using Sandbox;
using System;

namespace Terryball;

/// <summary>
/// The GUIDED ("Aim") throw mode — an alternative to the free hamster-ball drive, toggled per-client in
/// the lane controls (<see cref="GameDirector.AimMode"/>). When it's on and the local player is the bowler up, this
/// takes over the run-up: instead of driving the ball with WASD, the ball is parked just behind the foul
/// line and the player lines the throw up in four timed steps, each committed with a click or Space:
///
///   1. LINE   — the ball shuttles left/right across the lane; lock in the lateral start line.
///   2. AIM    — an arrow pinned to the ball sweeps left/right; lock in the aim direction.
///   3. POWER  — a meter fills and empties; lock in the throw speed.
///   4. SPIN   — a meter sweeps left/right; lock in how much the ball hooks.
///
/// then the ball is launched with that velocity + spin and committed to physics. The hook is a stylized
/// oiled-lane sim in <see cref="BallController"/> (slides straight through the oiled front, breaks toward
/// the pocket over the dry back end) so a big spin curves the ball late instead of flinging it straight
/// into the gutter off the foul line.
///
/// The mode itself is a lane-wide setting on the director (<see cref="GameDirector.AimMode"/>, synced), so
/// every bowler on a lane rolls the same way; this component is the purely CLIENT-LOCAL driver for it. It's
/// built on the local HUD root (never networked), resolves the lane the local player is on each frame via
/// <see cref="Lane.ForLocalPlayer"/>, and only ever acts on the bowler's own machine — the launched ball is
/// the lane's networked ball, so the throw replicates to every spectator automatically. Non-bowler clients
/// run this but stay dormant.
/// </summary>
public sealed class ThrowAimer : Component
{
	private enum Phase { Idle, Line, Aim, Power, Spin, Launched }
	private Phase _phase = Phase.Idle;

	// --- Timing sweeps (ping-pong rates, cycles/sec) -----------------------------------
	[Property, Group( "Timing" )] public float LineSpeed { get; set; } = 0.9f;
	[Property, Group( "Timing" )] public float AimSpeed { get; set; } = 0.7f;
	[Property, Group( "Timing" )] public float PowerSpeed { get; set; } = 0.85f;
	[Property, Group( "Timing" )] public float SpinSpeed { get; set; } = 0.8f;

	// --- Ranges ------------------------------------------------------------------------
	/// <summary>Max aim deflection from straight-down-lane, degrees (± this).</summary>
	[Property, Group( "Aim" )] public float MaxAimDegrees { get; set; } = 9f;
	/// <summary>Lowest committed throw speed (u/s) at an empty power meter.</summary>
	[Property, Group( "Launch" )] public float MinLaunchSpeed { get; set; } = 3200f;
	/// <summary>Top committed throw speed (u/s) at a full power meter.</summary>
	[Property, Group( "Launch" )] public float MaxLaunchSpeed { get; set; } = 6200f;
	/// <summary>Fraction of the no-slip roll rate (v/R) the ball launches with. A real thrown ball comes off
	/// the hand SKIDDING on the oiled front and only revs up to a true roll on the dry back end — so we launch
	/// it under-rolling and let the lane's friction spin it up, instead of forcing a full-grip roll off the
	/// line (which read as the ball "spinning as if it had full friction"). 1 = full roll, 0 = pure skid.</summary>
	[Property, Group( "Launch" )] public float LaunchRollFraction { get; set; } = 0.5f;
	/// <summary>Side-rotation rate (rad/s) a full ±1 spin puts on the ball, ABOUT THE TRAVEL AXIS — a real hook
	/// ball's tilted rotation axis. This is the genuine cause of the hook: it makes the ball's contact patch
	/// slip sideways, and friction on the dry back end turns that into the curve (BallController). So the spin
	/// you see rolling on the ball IS what breaks it — and the peak hook the ball revs out to is roughly this
	/// times the ball radius (u/s of sideways speed). Combined with the partial forward roll it reads as a
	/// canted axis, not a flat top-spin.</summary>
	[Property, Group( "Launch" )] public float SideRotationRate { get; set; } = 8f;

	// Locked-in values as each step is committed.
	private float _lineY;     // world Y of the chosen start line
	private float _aimDeg;    // chosen aim deflection
	private float _power01;   // chosen power 0..1
	private float _startX;    // world X the ball is held at (just behind the foul line)
	private float _surfaceZ;  // world Z of the ball centre resting on the lane surface
	private float _centerY;   // lane centre-line Y
	private float _yRange;    // half the lateral shuttle range
	private bool _holding;    // ball currently frozen by us (gravity off) — must be released if we bail

	// --- Live refs (re-resolved each frame; they follow the player across lanes) --------
	private Lane ActiveLane => Lane.ForLocalPlayer( Scene );

	protected override void OnUpdate()
	{
		var lane = ActiveLane;
		var director = lane?.Director;
		var ball = lane?.Ball;
		var builder = lane?.Builder;
		var localAv = TerryAvatar.Local( Scene );

		var bowling = director?.LocalIsBowling ?? false;
		var carried = localAv?.Carried ?? false;
		var rolling = director?.State == GameDirector.RoundState.Rolling;
		var over = director?.GameOver ?? false;

		var active = (director?.AimMode ?? false) && bowling && carried && rolling && !over
			&& ball.IsValid() && builder.IsValid();

		if ( !active )
		{
			// Left the aim window (throw released, bailed, walked off, mode toggled off): make sure the ball
			// isn't left frozen with gravity off, then go dormant.
			if ( _holding && ball.IsValid() ) { ball.EndHold(); _holding = false; }
			HideArrow();
			_phase = Phase.Idle;
			return;
		}

		// Freeze the sequence (but keep holding the ball) while a panel/chat has the cursor, so a click meant
		// for that UI doesn't also lock a step.
		var blocked = ChatPanel.IsOpen || MusicScreen.IsOpen || (localAv?.ControlsOpen ?? false);

		// A fresh roll just armed: set up the geometry and start lining up.
		if ( _phase == Phase.Idle || _phase == Phase.Launched )
		{
			if ( _phase == Phase.Launched ) return; // committed throw still coasting to the throw line
			BeginSequence( builder, ball );
		}

		var advance = !blocked && AdvancePressed();

		switch ( _phase )
		{
			case Phase.Line:
			{
				var holdY = _centerY + SignedPing( LineSpeed ) * _yRange;
				HoldBall( ball, holdY );
				if ( advance ) { _lineY = holdY; _phase = Phase.Aim; }
				break;
			}
			case Phase.Aim:
			{
				HoldBall( ball, _lineY );
				_aimDeg = SignedPing( AimSpeed ) * MaxAimDegrees;
				if ( advance ) _phase = Phase.Power;
				break;
			}
			case Phase.Power:
			{
				HoldBall( ball, _lineY );
				_power01 = Ping( PowerSpeed );
				if ( advance ) _phase = Phase.Spin;
				break;
			}
			case Phase.Spin:
			{
				HoldBall( ball, _lineY );
				var spin = SignedPing( SpinSpeed );
				if ( advance ) { Launch( ball, spin ); return; }
				break;
			}
		}

		DrawGuide( ball, builder );
	}

	// Ensure a mode-toggle-off or teardown while mid-line-up never strands the ball gravity-off.
	protected override void OnDisabled()
	{
		var ball = ActiveLane?.Ball;
		if ( _holding && ball.IsValid() ) ball.EndHold();
		_holding = false;
		DestroyArrow();
		_phase = Phase.Idle;
	}

	private void BeginSequence( LaneBuilder builder, BallController ball )
	{
		_centerY = builder.RunUpWorldPosition.y;
		// A hair above the surface: gravity is off during the hold, so a small gap keeps the frozen ball from
		// fighting lane contact without it visibly floating. It drops onto its line the instant it's launched.
		_surfaceZ = builder.PlatformTopWorldZ + ball.Radius + 2f;
		_startX = builder.FoulLineWorldX - ball.Radius * 1.5f; // just behind the foul line
		var laneHalf = builder.LaneWidthInches * 0.5f * builder.UnitsPerInch;
		_yRange = MathF.Max( 0f, laneHalf - ball.Radius );

		_lineY = _centerY;
		_aimDeg = 0f;
		_power01 = 0f;
		ball.ControlsLocked = true; // ignore free-drive input while we line the throw up
		_phase = Phase.Line;
	}

	private void HoldBall( BallController ball, float y )
	{
		ball.ControlsLocked = true;
		ball.HoldAt( new Vector3( _startX, y, _surfaceZ ) );
		_holding = true;
	}

	private void Launch( BallController ball, float spin )
	{
		var dir = Rotation.FromYaw( _aimDeg ).Forward; // +yaw = aim toward +Y (screen-left)
		var speed = MathX.Lerp( MinLaunchSpeed, MaxLaunchSpeed, _power01 );
		var velocity = dir * speed;

		// Forward roll about the lateral axis, but only a FRACTION of the no-slip rate (v/R): the ball leaves
		// the hand skidding on the oiled front and the lane's friction revs it toward a true roll, instead of
		// spinning as if it were full-grip off the line. Then a real SIDE ROTATION about the travel axis — the
		// hook ball's tilted spin axis. That side rotation is the physical cause of the curve: BallController
		// turns the sideways contact-patch slip it creates into the hook on the dry back end. The −dir sign
		// makes +spin (meter "HOOK LEFT") roll the ball toward +Y and break it that way. Forward roll + side
		// rotation together = a canted axis. HookAmount just arms the hook (a free-drive ball leaves it 0).
		var noSlip = speed / MathF.Max( 1f, ball.Radius );
		var rollAxis = dir.Cross( Vector3.Up );            // horizontal, perpendicular to travel
		var angular = rollAxis * (noSlip * LaunchRollFraction)
			- dir * (spin * SideRotationRate);             // side rotation about travel = the canted hook axis

		ball.Launch( velocity, angular, spin );
		ball.ControlsLocked = true; // committed — no steering; the director arms watch on the throw-line cross
		_holding = false;
		HideArrow(); // the throw's away — drop the aim guide off the lane
		_phase = Phase.Launched;
	}

	// --- Input -------------------------------------------------------------------------
	private static bool AdvancePressed()
		=> Input.Pressed( "attack1" ) || Input.Pressed( "Jump" ) || Input.Keyboard.Pressed( "space" );

	// --- Ping-pong helpers -------------------------------------------------------------
	// Ping: a 0→1→0 triangle at `cyclesPerSec`. SignedPing: the same swept over −1→1→−1.
	private static float Ping( float cyclesPerSec )
	{
		var t = (Time.Now * cyclesPerSec * 2f) % 2f;
		if ( t < 0f ) t += 2f;
		return t < 1f ? t : 2f - t;
	}
	private static float SignedPing( float cyclesPerSec ) => Ping( cyclesPerSec ) * 2f - 1f;

	// --- Visuals -----------------------------------------------------------------------
	private void DrawGuide( BallController ball, LaneBuilder builder )
	{
		// Lay the arrow flat on the lane surface, just ahead of the ball, like a real lane's targeting arrows.
		var origin = new Vector3( ball.WorldPosition.x, ball.WorldPosition.y, builder.PlatformTopWorldZ + 3f );

		// The aim/preview arrow: straight down-lane while lining up, swept once aiming, curved by the live
		// spin in the spin step. Length grows with the committed power.
		var yaw = _phase == Phase.Line ? 0f : _aimDeg;
		var previewSpin = _phase == Phase.Spin ? SignedPing( SpinSpeed ) : 0f;
		var lengthFrac = _phase switch
		{
			Phase.Power => 0.35f + 0.65f * _power01,
			Phase.Spin => 0.35f + 0.65f * _power01,
			_ => 0.6f,
		};
		var baseLen = ball.Radius * 14f;
		var color = _phase switch
		{
			Phase.Line => new Color( 0.3f, 0.9f, 1f ),
			Phase.Aim => new Color( 1f, 0.85f, 0.2f ),
			Phase.Power => Color.Lerp( new Color( 0.3f, 1f, 0.3f ), new Color( 1f, 0.3f, 0.2f ), _power01 ),
			Phase.Spin => new Color( 0.75f, 0.45f, 1f ),
			_ => Color.White,
		};

		UpdateArrowMesh( origin, yaw, baseLen * lengthFrac, color, previewSpin );

		// A ground line clear across the lane at the ball's chosen/lining-up start line, so the lateral
		// position reads clearly against the boards.
		var yNow = _phase == Phase.Line ? ball.WorldPosition.y : _lineY;
		var half = builder.LaneWidthInches * 0.5f * builder.UnitsPerInch;
		var z = _surfaceZ - ball.Radius + 1f;
		DebugOverlay.Line( new Vector3( _startX, yNow - half, z ), new Vector3( _startX, yNow + half, z ),
			color.WithAlpha( 0.5f ), overlay: false );

		// On-screen step prompt + meter.
		DrawHud();
	}

	// --- Aim arrow (a REAL rendered mesh, not debug lines) -----------------------------
	// A flat, solid arrow (wide shaft ribbon + triangular head) laid on the lane surface, curving laterally by
	// `spin` toward its tip (the hook preview). Built as a procedural ModelRenderer on a client-local child
	// (NetworkMode.Never — a procedural model can't replicate; see LaneBuilder's targeting arrows), rebuilt
	// each frame as the aim/power/spin sweep. Only the local bowler ever sees it.
	private GameObject _arrowGo;
	private ModelRenderer _arrowRenderer;
	private Material _arrowMat;

	private void EnsureArrow()
	{
		if ( _arrowRenderer.IsValid() ) return;
		_arrowGo = new GameObject( true, "AimArrow" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		_arrowGo.NetworkMode = NetworkMode.Never;
		_arrowRenderer = _arrowGo.Components.GetOrCreate<ModelRenderer>();
		_arrowMat = Material.Load( "materials/default/white.vmat" );
	}

	private void HideArrow()
	{
		if ( _arrowRenderer.IsValid() ) _arrowRenderer.Enabled = false;
	}

	private void DestroyArrow()
	{
		_arrowGo?.Destroy();
		_arrowGo = null;
		_arrowRenderer = null;
	}

	private void UpdateArrowMesh( Vector3 origin, float yawDeg, float length, Color color, float spin )
	{
		EnsureArrow();
		// Mesh vertices are built in WORLD space, so pin the object at the identity transform.
		_arrowGo.WorldPosition = Vector3.Zero;
		_arrowGo.WorldRotation = Rotation.Identity;
		_arrowGo.WorldScale = Vector3.One;
		_arrowRenderer.Enabled = true;
		_arrowRenderer.Tint = color;

		var fwd = Rotation.FromYaw( yawDeg ).Forward;
		var lat0 = Vector3.Up.Cross( fwd );
		var bend = spin * length * 0.28f;                // how far the tip curves
		var halfW = MathF.Max( 18f, length * 0.05f );    // shaft half-width
		var headFrac = 0.26f;                            // last stretch of the length is the arrowhead
		var headHalf = MathF.Max( 30f, length * 0.12f ); // arrowhead half-width (wider than the shaft)
		var shaftEnd = 1f - headFrac;

		// Curved centerline point at 0..1 along the arrow, and the ground-plane lateral (perpendicular to the
		// local tangent) there, so the ribbon keeps a constant width around the curve.
		Vector3 Center( float s ) => origin + fwd * (length * s) + lat0 * (bend * s * s);
		Vector3 LatAt( float s )
		{
			var t = (Center( MathF.Min( 1f, s + 0.03f ) ) - Center( MathF.Max( 0f, s - 0.03f ) )).WithZ( 0f );
			t = t.Length > 0.001f ? t.Normal : fwd;
			return Vector3.Up.Cross( t );
		}

		var vb = new VertexBuffer();
		vb.Init( true );
		var n = Vector3.Up;
		Vertex V( Vector3 p ) => new Vertex( p, n, Vector3.Zero, new Vector4( 0f, 0f, 0f, 0f ) );

		// Shaft: a filled ribbon — a strip of cross-section pairs. Both windings are emitted so it's never culled.
		const int segs = 14;
		for ( var i = 0; i <= segs; i++ )
		{
			var s = shaftEnd * i / segs;
			var c = Center( s );
			var lat = LatAt( s );
			vb.Add( V( c + lat * halfW ) );
			vb.Add( V( c - lat * halfW ) );
		}
		for ( var i = 0; i < segs; i++ )
		{
			int a = i * 2, b = i * 2 + 1, c = i * 2 + 2, d = i * 2 + 3;
			vb.AddRawIndex( a ); vb.AddRawIndex( b ); vb.AddRawIndex( c );
			vb.AddRawIndex( c ); vb.AddRawIndex( b ); vb.AddRawIndex( d );
			vb.AddRawIndex( a ); vb.AddRawIndex( c ); vb.AddRawIndex( b ); // back-face
			vb.AddRawIndex( c ); vb.AddRawIndex( d ); vb.AddRawIndex( b );
		}

		// Head: a solid triangle from the base of the head out to the tip.
		var baseIdx = (segs + 1) * 2;
		var latEnd = LatAt( shaftEnd );
		var baseC = Center( shaftEnd );
		vb.Add( V( Center( 1f ) ) );                 // baseIdx    — tip
		vb.Add( V( baseC + latEnd * headHalf ) );    // baseIdx+1  — base left
		vb.Add( V( baseC - latEnd * headHalf ) );    // baseIdx+2  — base right
		vb.AddRawIndex( baseIdx ); vb.AddRawIndex( baseIdx + 1 ); vb.AddRawIndex( baseIdx + 2 );
		vb.AddRawIndex( baseIdx ); vb.AddRawIndex( baseIdx + 2 ); vb.AddRawIndex( baseIdx + 1 ); // back-face

		var mesh = new Mesh( _arrowMat );
		mesh.CreateBuffers( vb );
		var mb = new ModelBuilder();
		mb.AddMesh( mesh );
		_arrowRenderer.Model = mb.Create();
	}

	private void DrawHud()
	{
		var w = Screen.Width;
		var h = Screen.Height;
		var cx = w * 0.5f;

		string title;
		string meter;
		switch ( _phase )
		{
			case Phase.Line: title = "STEP 1/4  -  LINE"; meter = Bar( (SignedPing( LineSpeed ) + 1f) * 0.5f ); break;
			case Phase.Aim: title = "STEP 2/4  -  AIM"; meter = Bar( (SignedPing( AimSpeed ) + 1f) * 0.5f ); break;
			case Phase.Power: title = "STEP 3/4  -  POWER"; meter = Bar( _power01 ); break;
			case Phase.Spin: title = "STEP 4/4  -  SPIN"; meter = SpinBar( SignedPing( SpinSpeed ) ); break;
			default: return;
		}

		DebugOverlay.ScreenText( new Vector2( cx, h - 150f ), title, 22, TextFlag.Center, Color.White );
		DebugOverlay.ScreenText( new Vector2( cx, h - 120f ), meter, 20, TextFlag.Center, new Color( 0.8f, 0.9f, 1f ) );
		DebugOverlay.ScreenText( new Vector2( cx, h - 92f ), "Click / Space to set", 16, TextFlag.Center,
			new Color( 1f, 1f, 1f, 0.7f ) );
	}

	private static string Bar( float v01 )
	{
		v01 = v01.Clamp( 0f, 1f );
		const int n = 20;
		var f = (int)MathF.Round( v01 * n );
		return "[" + new string( '|', f ) + new string( '.', n - f ) + $"]  {v01 * 100f:F0}%";
	}

	private static string SpinBar( float signed )
	{
		signed = signed.Clamp( -1f, 1f );
		const int half = 10;
		var pos = (int)MathF.Round( (signed + 1f) * 0.5f * (half * 2) );
		var sb = new System.Text.StringBuilder( "[" );
		for ( var i = 0; i <= half * 2; i++ )
			sb.Append( i == pos ? '#' : (i == half ? '+' : '.') );
		sb.Append( "]  " );
		// +spin curves toward +Y, which reads as screen-LEFT from the behind-ball camera (camera-right is -Y).
		sb.Append( signed > 0.05f ? $"HOOK < LEFT {signed * 100f:F0}%"
			: signed < -0.05f ? $"HOOK > RIGHT {MathF.Abs( signed ) * 100f:F0}%" : "STRAIGHT" );
		return sb.ToString();
	}
}
