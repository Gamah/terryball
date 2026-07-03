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
	/// <summary>How far down-lane (in ball radii) the aim arrow floats ahead of the ball, so an opaque ball
	/// doesn't hide it. The arrow starts this far in front, pointing the way you're aiming.</summary>
	[Property, Group( "Aim" )] public float ArrowFrontGapRadii { get; set; } = 4f;
	/// <summary>Lowest committed throw speed (u/s) at an empty power meter.</summary>
	[Property, Group( "Launch" )] public float MinLaunchSpeed { get; set; } = 3200f;
	/// <summary>Top committed throw speed (u/s) at a full power meter.</summary>
	[Property, Group( "Launch" )] public float MaxLaunchSpeed { get; set; } = 6200f;
	/// <summary>Fraction of the no-slip roll rate (v/R) the ball launches with. A real thrown ball comes off
	/// the hand SKIDDING on the oiled front and only revs up to a true roll on the dry back end — so we launch
	/// it under-rolling and let the lane's friction spin it up, instead of forcing a full-grip roll off the
	/// line (which read as the ball "spinning as if it had full friction"). 1 = full roll, 0 = pure skid.</summary>
	[Property, Group( "Launch" )] public float LaunchRollFraction { get; set; } = 0.5f;
	/// <summary>Side-rotation rate (rad/s) a full ±1 spin puts on the ball at launch, ABOUT THE TRAVEL AXIS — a
	/// real hook ball's tilted rotation axis, and the genuine cause of the hook. The ball leaves the hand
	/// spinning this fast; on the dry back end friction converts that side rotation into the sideways break and
	/// REVS THE SPIN DOWN as it goes (BallController applies the hook at the contact point). So the spin you
	/// see is what breaks the ball, it's clearly spinning off the hand, and it rolls out as it sets — like a
	/// real hook. Bigger = more revs and a bigger break. (The realized sideways break is roughly a third of
	/// this × the radius once it revs out, so it takes a lot of revs for a big hook — hence the high value.)</summary>
	[Property, Group( "Launch" )] public float SideRotationRate { get; set; } = 32f;

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
			HideMeter();
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
		DestroyMeter();
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
		HideMeter(); // and the step plaque
		_phase = Phase.Launched;

		if ( ActiveLane?.Director?.LogThrow ?? false )
		{
			var sideRevs = spin * SideRotationRate;
			var lineOff = (_lineY - _centerY) / MathF.Max( 1f, ball.Radius );
			Log.Info( $"[terryball][throw] LAUNCH aim={_aimDeg:F1}° power={_power01 * 100f:F0}% spin={spin:+0.00;-0.00} " +
				$"speed={speed:F0}u/s line={lineOff:+0.0;-0.0}R  fwdRoll={noSlip * LaunchRollFraction:F1}rad/s " +
				$"sideRot={sideRevs:F1}rad/s (~{MathF.Abs( sideRevs ) * ball.Radius:F0}u/s peak-slip)  " +
				$"vel={velocity} ang={angular}" );
		}
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
		// The aim/preview arrow: straight down-lane while lining up, swept once aiming, curved by the live
		// spin in the spin step. Length grows with the committed power.
		var yaw = _phase == Phase.Line ? 0f : _aimDeg;

		// Lay the arrow flat on the lane surface and float it a ways DOWN-LANE ahead of the ball (along the aim
		// direction), so an opaque ball doesn't hide it — like a real lane's targeting arrows out in front.
		var gap = Rotation.FromYaw( yaw ).Forward * (ball.Radius * ArrowFrontGapRadii);
		var origin = new Vector3( ball.WorldPosition.x, ball.WorldPosition.y, builder.PlatformTopWorldZ + 3f ) + gap;
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

		// The step meter — a first-party mesh floating above the ball (where the debug speed readout used to
		// be), with a text label naming the step. Replaces the old bottom-of-screen ASCII meter.
		DrawStepMeter( ball );
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

	/// <summary>Where "perfect" sits on the power/spin meters (0..1). The physics is tuned so this fill is the
	/// ideal pocket throw, leaving the top of the meter as room to overcook it — so maxing out is a mistake,
	/// not the goal. Marked with a bright tick on the meter.</summary>
	[Property, Group( "Aim" )] public float SweetSpot { get; set; } = 0.8f;

	// --- Step meter (a first-party procedural mesh pinned in front of the ball) ---------
	// A flat plaque billboarded to face the camera, floating a little down-lane in front of the ball and fixed
	// on the lane centre-line (it does NOT follow the ball's lateral shuttle): a dark track, a single-colour
	// fill that sweeps with the live step value (a centred both-ways marker for the signed SPIN step), a bright
	// sweet-spot tick on POWER/SPIN, and a row of four pips showing which of the four steps you're on. A short
	// text LABEL over it names the step (no % — the meter IS the number). Built like the aim arrow — a
	// client-local ModelRenderer (NetworkMode.Never, a procedural model can't replicate), only the local bowler
	// sees it — but on the vertex_color material so the one mesh carries the track / fill / tick colours.
	private GameObject _meterGo;
	private ModelRenderer _meterRenderer;

	private void HideMeter()
	{
		if ( _meterRenderer.IsValid() ) _meterRenderer.Enabled = false;
	}

	private void DestroyMeter()
	{
		_meterGo?.Destroy();
		_meterGo = null;
		_meterRenderer = null;
	}

	/// <summary>How far down-lane (in ball radii) the step meter floats IN FRONT of the held ball, and how high
	/// it floats above the lane surface. It's pinned to the lane centre-line at this spot — it does NOT follow
	/// the ball's lateral shuttle.</summary>
	[Property, Group( "Aim" )] public float MeterFrontGap { get; set; } = 4f;
	[Property, Group( "Aim" )] public float MeterFloatHeight { get; set; } = 1.2f;

	private void DrawStepMeter( BallController ball )
	{
		// Per-step display value: a 0..1 fill (or a signed −1..1 marker for SPIN), whether it carries a
		// sweet-spot tick, the step index, and the label. Directional steps (LINE/AIM/SPIN) are negated so the
		// meter reads the SAME way round as the screen — the camera sits behind the ball, so screen-right is −Y,
		// and moving the ball/hook toward screen-left moves the marker toward the LEFT of the meter.
		float fill; var signed = false; float sweet = -1f; int step; string label;
		switch ( _phase )
		{
			case Phase.Line: fill = (-SignedPing( LineSpeed ) + 1f) * 0.5f; step = 1; label = "STEP 1/4   ·   LINE"; break;
			case Phase.Aim: fill = (-SignedPing( AimSpeed ) + 1f) * 0.5f; step = 2; label = "STEP 2/4   ·   AIM"; break;
			case Phase.Power: fill = _power01; sweet = SweetSpot; step = 3; label = "STEP 3/4   ·   POWER"; break;
			case Phase.Spin: fill = -SignedPing( SpinSpeed ); signed = true; sweet = SweetSpot; step = 4; label = "STEP 4/4   ·   SPIN"; break;
			default: HideMeter(); return;
		}

		// Pin the plaque to a FIXED spot: lane centre-line (_centerY), a little down-lane in front of the held
		// ball (_startX), floating above the lane. It stays put while the ball shuttles left/right underneath.
		var r = ball.Radius;
		var center = new Vector3( _startX + r * MeterFrontGap, _centerY, _surfaceZ + r * MeterFloatHeight );
		var cam = Scene.Camera;
		var toCam = cam.IsValid() ? (cam.WorldPosition - center) : Vector3.Backward;
		toCam = toCam.Length > 0.01f ? toCam.Normal : Vector3.Backward;
		var right = Vector3.Up.Cross( toCam );
		right = right.Length > 0.01f ? right.Normal : Vector3.Forward;
		var up = toCam.Cross( right ).Normal;

		UpdateMeterMesh( r, center, right, up, toCam, fill, signed, sweet, step );

		// Text label above the plaque naming the step, plus the commit hint below it. World-space text billboards
		// to the camera on its own. No percentage — the meter shows the value.
		DebugOverlay.Text( center + up * (r * 0.75f), label, 18, TextFlag.Center, Color.White );
		var hint = sweet >= 0f ? "Click / Space to set   ·   the tick is perfect (past it = overcooked)"
			: "Click / Space to set";
		DebugOverlay.Text( center - up * (r * 0.5f), hint, 13, TextFlag.Center, new Color( 1f, 1f, 1f, 0.75f ) );
	}

	private void UpdateMeterMesh( float r, Vector3 center, Vector3 right, Vector3 up, Vector3 toCam,
		float fill, bool signed, float sweet, int step )
	{
		if ( !_meterRenderer.IsValid() )
		{
			_meterGo = new GameObject( true, "AimMeter" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
			_meterGo.NetworkMode = NetworkMode.Never;
			_meterRenderer = _meterGo.Components.GetOrCreate<ModelRenderer>();
		}

		var halfW = r * 1.2f;             // track half-width
		var halfH = r * 0.16f;            // track half-height
		var fillH = halfH * 0.70f;        // fill inset within the track
		var tickHW = r * 0.03f;           // sweet-spot tick half-width
		var pipHalf = r * 0.055f;         // step-pip half-size
		var pipV = halfH + r * 0.18f;     // pip row height above the track centre
		var pipSpread = halfW * 0.5f;     // how far the four pips fan out

		// One consistent colour scheme for every step — the step is named in the label and shown by the pips,
		// so the meter itself doesn't change colour as you go.
		var track = new Color32( 46, 51, 64 );
		var pipOff = new Color32( 70, 76, 92 );
		var tickCol = new Color32( 255, 255, 255 );
		var fillCol = new Color32( 90, 200, 255 ); // cyan accent

		var vb = new VertexBuffer();
		vb.Init( true );
		var count = 0;
		var plane = center; // bumped toward the camera per layer so stacked quads never z-fight

		Vertex MV( Vector3 p, Color32 c ) { var v = new Vertex( p, c ); v.Normal = Vector3.Up; return v; }
		void Quad( float u0, float u1, float v0, float v1, Color32 c )
		{
			Vector3 P( float u, float v ) => plane + right * u + up * v;
			var b = count;
			vb.Add( MV( P( u0, v0 ), c ) ); vb.Add( MV( P( u1, v0 ), c ) );
			vb.Add( MV( P( u1, v1 ), c ) ); vb.Add( MV( P( u0, v1 ), c ) );
			count += 4;
			vb.AddRawIndex( b ); vb.AddRawIndex( b + 1 ); vb.AddRawIndex( b + 2 );
			vb.AddRawIndex( b ); vb.AddRawIndex( b + 2 ); vb.AddRawIndex( b + 3 );
			vb.AddRawIndex( b ); vb.AddRawIndex( b + 2 ); vb.AddRawIndex( b + 1 ); // back-face
			vb.AddRawIndex( b ); vb.AddRawIndex( b + 3 ); vb.AddRawIndex( b + 2 );
		}

		// Track.
		plane = center;
		Quad( -halfW, halfW, -halfH, halfH, track );

		// Fill: from the left edge for a 0..1 bar, or out from the centre for the signed SPIN meter.
		plane = center + toCam * 0.6f;
		if ( signed )
		{
			var s = fill.Clamp( -1f, 1f );
			var end = s * halfW;
			Quad( MathF.Min( 0f, end ), MathF.Max( 0f, end ), -fillH, fillH, fillCol );
		}
		else
		{
			var end = -halfW + fill.Clamp( 0f, 1f ) * (halfW * 2f);
			Quad( -halfW, end, -fillH, fillH, fillCol );
		}

		// Sweet-spot tick(s) + centre mark, brought fully forward so they read over the fill.
		plane = center + toCam * 1.2f;
		if ( sweet >= 0f )
		{
			if ( signed )
			{
				var at = sweet.Clamp( 0f, 1f ) * halfW;
				Quad( at - tickHW, at + tickHW, -halfH, halfH, tickCol );
				Quad( -at - tickHW, -at + tickHW, -halfH, halfH, tickCol );
				Quad( -tickHW * 0.7f, tickHW * 0.7f, -fillH, fillH, new Color32( 200, 205, 220 ) ); // centre
			}
			else
			{
				var at = -halfW + sweet.Clamp( 0f, 1f ) * (halfW * 2f);
				Quad( at - tickHW, at + tickHW, -halfH, halfH, tickCol );
			}
		}

		// Four step pips above the track — filled in the accent colour up to the current step, dim beyond.
		plane = center + toCam * 0.6f;
		for ( var i = 0; i < 4; i++ )
		{
			var u = MathX.Lerp( -pipSpread, pipSpread, i / 3f );
			Quad( u - pipHalf, u + pipHalf, pipV - pipHalf, pipV + pipHalf, i < step ? fillCol : pipOff );
		}

		_meterGo.WorldPosition = Vector3.Zero; // vertices are world-space
		_meterGo.WorldRotation = Rotation.Identity;
		_meterGo.WorldScale = Vector3.One;
		_meterRenderer.Enabled = true;
		_meterRenderer.Tint = Color.White; // per-vertex colour is multiplied by this tint in the shader

		// vertex_color.vmat's shader reads the mesh's per-vertex COLOR (vColor * instance tint); the default
		// complex/white shader ignores it (it drives colour from the tint alone), so the meter needs this one
		// to show its dark track / phase-coloured fill / white tick as distinct colours in a single mesh.
		var mesh = new Mesh( Material.Load( "materials/default/vertex_color.vmat" ) );
		mesh.CreateBuffers( vb );
		var mb = new ModelBuilder();
		mb.AddMesh( mesh );
		_meterRenderer.Model = mb.Create();
	}
}
