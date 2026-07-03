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
	/// <summary>Revs a full ±1 spin adds as a tilt of the ball's roll axis toward the travel direction
	/// (rad/s) — a real hook ball spins about an axis canted forward, not a flat top-spin. Reads as the ball
	/// visibly turning over toward the hook side; the actual curve is the oiled-lane hook in BallController.</summary>
	[Property, Group( "Launch" )] public float SideSpinRate { get; set; } = 16f;

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
		// spinning as if it were full-grip off the line. The side-spin cants that roll axis forward (about the
		// travel direction) — a real hook ball's tilted axis — so it visibly turns over toward the hook side.
		// The actual curve is the oiled-lane hook, armed via HookAmount = spin.
		var noSlip = speed / MathF.Max( 1f, ball.Radius );
		var rollAxis = dir.Cross( Vector3.Up );            // horizontal, perpendicular to travel
		var angular = rollAxis * (noSlip * LaunchRollFraction)
			+ dir * (spin * SideSpinRate);                 // barrel/tilt about travel = the canted hook axis

		ball.Launch( velocity, angular, spin );
		ball.ControlsLocked = true; // committed — no steering; the director arms watch on the throw-line cross
		_holding = false;
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
		var origin = ball.WorldPosition + Vector3.Up * (ball.Radius * 0.25f);

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

		DrawArrow( origin, yaw, baseLen * lengthFrac, color, previewSpin );

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

	// A bold, FILLED flat arrow (wide shaft ribbon + solid triangular head) along `yawDeg`, curving laterally
	// by `spin` toward its tip (a hook preview). DebugOverlay.Line has no thickness, so the shaft is filled as
	// a band of parallel offset lines and the head as a fan — both drawn on-top (overlay) so the whole thing
	// reads clearly over the lane instead of as one thin scratch.
	private void DrawArrow( Vector3 origin, float yawDeg, float length, Color color, float spin )
	{
		var fwd = Rotation.FromYaw( yawDeg ).Forward;
		var lat0 = Vector3.Up.Cross( fwd );         // matches the hook's +/− convention
		var bend = spin * length * 0.28f;           // how far the tip curves

		const int segs = 16;
		var pts = new Vector3[segs + 1];
		for ( var i = 0; i <= segs; i++ )
		{
			var s = i / (float)segs;
			pts[i] = origin + fwd * (length * s) + lat0 * (bend * s * s);
		}

		// Ground-plane lateral at point i (perpendicular to the LOCAL tangent), so the ribbon keeps a constant
		// width even where the shaft curves.
		Vector3 LatAt( int i )
		{
			var a = pts[Math.Max( 0, i - 1 )];
			var b = pts[Math.Min( segs, i + 1 )];
			var t = (b - a).WithZ( 0f );
			t = t.Length > 0.001f ? t.Normal : fwd;
			return Vector3.Up.Cross( t );
		}

		var halfW = MathF.Max( 18f, length * 0.05f );    // shaft half-width
		var headLen = length * 0.26f;
		var headHalf = MathF.Max( 30f, length * 0.12f ); // arrowhead half-width (wider than the shaft)

		// Shaft: a filled ribbon of parallel offset polylines across the width.
		const int fill = 12;
		for ( var k = 0; k <= fill; k++ )
		{
			var off = MathX.Lerp( -halfW, halfW, k / (float)fill );
			for ( var i = 0; i < segs; i++ )
			{
				var a = pts[i] + LatAt( i ) * off;
				var b = pts[i + 1] + LatAt( i + 1 ) * off;
				DebugOverlay.Line( a, b, color, overlay: true );
			}
		}

		// Head: a solid triangle at the tip, filled by a fan of lines from the tip across its base edge.
		var tip = pts[segs];
		var tipDir = (pts[segs] - pts[segs - 1]).WithZ( 0f );
		tipDir = tipDir.Length > 0.001f ? tipDir.Normal : fwd;
		var side = Vector3.Up.Cross( tipDir );
		var baseC = tip - tipDir * headLen;
		var bl = baseC + side * headHalf;
		var br = baseC - side * headHalf;

		const int hf = 14;
		for ( var k = 0; k <= hf; k++ )
			DebugOverlay.Line( tip, Vector3.Lerp( bl, br, k / (float)hf ), color, overlay: true );
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
