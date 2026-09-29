using Sandbox;
using System;

namespace Terryball;

/// <summary>
/// The GUIDED ("Aim") throw mode — an alternative to the free hamster-ball drive, toggled per-client in
/// the lane controls (<see cref="GameDirector.AimMode"/>). When it's on and the local player is the bowler up, this
/// takes over the run-up: instead of driving the ball with WASD, the ball is parked just behind the foul
/// line and the player lines the throw up in four HAND-POSITIONED steps, each committed with a click or Space
/// (and right-click / Backspace steps back to redo one):
///
///   1. LINE   — slide the ball left/right across the lane (A/D or slide the mouse); set the start line.
///   2. AIM    — swing the aim arrow left/right (A/D or the mouse); set the aim direction.
///   3. POWER  — raise/lower the throw speed left/right (A/D or the mouse); slide right to fill.
///   4. SPIN   — tilt the hook left/right (A/D or the mouse); set how much the ball hooks.
///
/// then the ball is launched with that velocity + spin and committed to physics. The hook is a stylized
/// oiled-lane sim in <see cref="BallController"/> (slides straight through the oiled front, breaks toward
/// the pocket over the dry back end) so a big spin curves the ball late instead of flinging it straight
/// into the gutter off the foul line.
///
/// Every step is a live 0..1 "caret" the player positions by hand — no auto sweep — so the four steps all
/// feel identical under the same movement-key / mouse sensitivity. The mode itself is a lane-wide setting on
/// the director (<see cref="GameDirector.AimMode"/>, synced), so every bowler on a lane rolls the same way;
/// this component is the purely CLIENT-LOCAL driver for it. It's built on the local HUD root (never
/// networked), resolves the lane the local player is on each frame via <see cref="Lane.ForLocalPlayer"/>, and
/// only ever acts on the bowler's own machine — the launched ball is the lane's networked ball, so the throw
/// replicates to every spectator automatically. Non-bowler clients run this but stay dormant.
/// </summary>
public sealed class ThrowAimer : Component
{
	private enum Phase { Idle, Line, Aim, Power, Spin, Launched }
	private Phase _phase = Phase.Idle;

	// --- Manual control (all four steps are positioned by hand, not swept) --------------
	/// <summary>How fast the movement keys (A/D) nudge a step, as fractions
	/// of the 0..1 caret track per second — so every step feels the same regardless of lane width.</summary>
	[Property, Group( "Aim" )] public float ManualKeySpeed { get; set; } = 0.9f;
	/// <summary>How far a step's caret moves per degree of mouse movement (fraction of the 0..1 track). The
	/// mouse is the primary control; this is the shared sensitivity across all four steps.</summary>
	[Property, Group( "Aim" )] public float ManualMouseSpeed { get; set; } = 0.02f;

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
	/// line. 1 = full roll, 0 = pure skid.</summary>
	[Property, Group( "Launch" )] public float LaunchRollFraction { get; set; } = 0.5f;
	/// <summary>Side-rotation rate (rad/s) a full ±1 spin puts on the ball at launch, ABOUT THE TRAVEL AXIS — a
	/// real hook ball's tilted rotation axis, and the genuine cause of the hook. The ball leaves the hand
	/// spinning this fast; on the dry back end friction converts that side rotation into the sideways break and
	/// REVS THE SPIN DOWN as it goes (BallController applies the hook at the contact point). So the spin you
	/// see is what breaks the ball, it's clearly spinning off the hand, and it rolls out as it sets — like a
	/// real hook. Bigger = more revs and a bigger break. (The realized sideways break is roughly a third of
	/// this × the radius once it revs out, so it takes a lot of revs for a big hook — hence the high value.)</summary>
	[Property, Group( "Launch" )] public float SideRotationRate { get; set; } = 32f;

	/// <summary>Where "perfect" sits on the power/spin meters (0..1). The physics is tuned so this fill is the
	/// ideal pocket throw, leaving the top of the meter as room to overcook it — so maxing out is a mistake,
	/// not the goal. Marked with a bright tick on the meter.</summary>
	[Property, Group( "Aim" )] public float SweetSpot { get; set; } = 0.8f;

	// --- Live + locked state -----------------------------------------------------------
	// _caret01 is the live 0..1 position of the CURRENT step (0 = screen-left / empty), nudged each frame by the
	// keys/mouse. On commit it's stored into the per-step locked caret below (also used for the ghost tracks),
	// and the physical value (_lineY / _aimDeg / _power01 / _spin) is derived from the caret.
	private float _caret01;
	private float _lineCaret = 0.5f, _aimCaret = 0.5f, _powerCaret, _spinCaret = 0.5f;

	private float _lineY;     // world Y of the chosen start line (derived from _lineCaret)
	private float _aimDeg;    // chosen aim deflection
	private float _power01;   // chosen power 0..1
	private float _spin;      // chosen hook, -1..1 (+1 = hook toward screen-left)
	private float _startX;    // world X the ball is held at (just behind the foul line)
	private float _surfaceZ;  // world Z of the ball centre resting on the lane surface
	private float _centerY;   // lane centre-line Y
	private float _yRange;    // half the lateral range the ball can be placed across
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
			// isn't left frozen with gravity off — and if we were still MID-LINE-UP (holding, not launched),
			// hand control back to free drive so toggling off Aim mid-throw doesn't strand the ball locked and
			// un-drivable. (A launched throw sets _holding=false, so its coasting lock is untouched here.)
			if ( _holding && ball.IsValid() ) { ball.EndHold(); ball.ControlsLocked = false; _holding = false; }
			HideArrow();
			AimHudVisible = false;
			AimCommitted = false;
			PublishBoard( false );
			_phase = Phase.Idle;
			return;
		}

		// Freeze the sequence (but keep holding the ball) while a panel/chat has the cursor, so a click meant
		// for that UI doesn't also lock a step — and so aiming input doesn't leak while typing.
		var blocked = ChatPanel.IsOpen || MusicScreen.IsOpen || (localAv?.ControlsOpen ?? false);

		// A fresh roll just armed: set up the geometry and start lining up.
		if ( _phase == Phase.Idle || _phase == Phase.Launched )
		{
			if ( _phase == Phase.Launched ) return; // committed throw still coasting to the throw line
			BeginSequence( builder, ball );
		}

		var advance = !blocked && AdvancePressed();

		// Right-click / Backspace steps BACK one step, so a bad lock can be redone: Aim→Line, Power→Aim,
		// Spin→Power (Line is the first step, nothing before it). The re-entered step resumes from the value you
		// had locked there. Never both back and forward in the same frame.
		if ( !blocked && !advance && BackPressed() )
		{
			var target = _phase switch
			{
				Phase.Aim => Phase.Line,
				Phase.Power => Phase.Aim,
				Phase.Spin => Phase.Power,
				_ => _phase,
			};
			if ( target != _phase ) EnterStep( target );
		}

		switch ( _phase )
		{
			case Phase.Line:
			{
				// Slide the start line left/right by hand; +Y is screen-left (caret → 0).
				if ( !blocked ) NudgeHorizontal();
				_lineY = _centerY + Signed( _caret01 ) * _yRange;
				HoldBall( ball, _lineY );
				if ( advance ) { _lineCaret = _caret01; EnterStep( Phase.Aim ); }
				break;
			}
			case Phase.Aim:
			{
				// Swing the aim arrow left/right; +deg = aim toward +Y (screen-left).
				if ( !blocked ) NudgeHorizontal();
				_aimDeg = Signed( _caret01 ) * MaxAimDegrees;
				HoldBall( ball, _lineY );
				if ( advance ) { _aimCaret = _caret01; EnterStep( Phase.Power ); }
				break;
			}
			case Phase.Power:
			{
				// Raise/lower the throw speed left/right, same as the other steps (slide right to fill).
				if ( !blocked ) NudgeHorizontal();
				_power01 = _caret01;
				HoldBall( ball, _lineY );
				if ( advance ) { _powerCaret = _caret01; EnterStep( Phase.Spin ); }
				break;
			}
			case Phase.Spin:
			{
				// Tilt the hook left/right; +spin hooks toward screen-left.
				if ( !blocked ) NudgeHorizontal();
				_spin = Signed( _caret01 );
				HoldBall( ball, _lineY );
				if ( advance ) { _spinCaret = _caret01; Launch( ball, _spin ); return; }
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
		AimHudVisible = false;
		PublishBoard( false );
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

		// Reset every step to its neutral: LINE/AIM/SPIN centred (caret 0.5), POWER empty (caret 0).
		_lineCaret = 0.5f;
		_aimCaret = 0.5f;
		_powerCaret = 0f;
		_spinCaret = 0.5f;
		_lineY = _centerY;
		_aimDeg = 0f;
		_power01 = 0f;
		_spin = 0f;
		ball.ControlsLocked = true; // ignore free-drive input while we line the throw up
		EnterStep( Phase.Line );
	}

	// Enter a step, seeding the live caret from that step's stored value — its neutral the first time, or the
	// value you'd locked if you're stepping back into it (or re-forward after a back).
	private void EnterStep( Phase p )
	{
		_phase = p;
		_caret01 = p switch
		{
			Phase.Line => _lineCaret,
			Phase.Aim => _aimCaret,
			Phase.Power => _powerCaret,
			Phase.Spin => _spinCaret,
			_ => _caret01,
		};
	}

	// Caret 0..1 (0 = screen-left) → signed −1..1 (+1 = screen-left), the natural axis for the physical values.
	private static float Signed( float caret01 ) => 1f - 2f * caret01;

	// Horizontal manual control, shared by all four steps: A/D and sliding the mouse left/right. Screen-left =
	// caret 0, so moving left — A (AnalogMove.y +1) or mouse-left (AnalogLook.yaw +) — drives the caret toward 0
	// (and moving right fills toward 1, e.g. more power).
	private void NudgeHorizontal()
	{
		_caret01 = (_caret01
			- Input.AnalogMove.y * (ManualKeySpeed * Time.Delta)
			- Input.AnalogLook.yaw * ManualMouseSpeed).Clamp( 0f, 1f );
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

		// Leave the step HUD up, frozen on the committed throw — the current meter shows the locked SPIN and the
		// three ghost tracks keep LINE/AIM/POWER above it — so you can read what you threw while the ball coasts
		// in. It's dismissed by the normal !active teardown the instant the ball crosses the throw line (the
		// director flips to Watching → the pin action), which is the existing clean cutoff.
		AimStep = 4; AimStepLabel = "SPIN";
		AimCaret01 = _spinCaret; // committed spin position
		AimSweet01 = SweetSpot; AimTwoSided = true;
		AimCommitted = true;
		AimHudVisible = true;
		PublishBoard( true ); // mirror the committed throw onto the settee feed board for spectators

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

	// Right-click or Backspace to walk back a step.
	private static bool BackPressed()
		=> Input.Pressed( "attack2" ) || Input.Keyboard.Pressed( "backspace" );

	// --- Visuals -----------------------------------------------------------------------
	private void DrawGuide( BallController ball, LaneBuilder builder )
	{
		// The aim/preview arrow: straight down-lane while lining up the line, swung by hand while aiming, curved
		// by the live spin in the spin step. Length grows with the committed power.
		var yaw = _phase == Phase.Line ? 0f : _aimDeg;

		// Lay the arrow flat on the lane surface and float it a ways DOWN-LANE ahead of the ball (along the aim
		// direction), so an opaque ball doesn't hide it — like a real lane's targeting arrows out in front.
		var gap = Rotation.FromYaw( yaw ).Forward * (ball.Radius * ArrowFrontGapRadii);
		var origin = new Vector3( ball.WorldPosition.x, ball.WorldPosition.y, builder.PlatformTopWorldZ + 3f ) + gap;
		var previewSpin = _phase == Phase.Spin ? _spin : 0f;
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

		// Publish the live step state for the bottom-of-screen HUD panel (ThrowAimHud) to render.
		UpdateAimHud();
	}

	// --- Aim arrow (a REAL rendered mesh, not debug lines) -----------------------------
	// A flat, solid arrow (wide shaft ribbon + triangular head) laid on the lane surface, curving laterally by
	// `spin` toward its tip (the hook preview). Built as a procedural ModelRenderer on a client-local child
	// (NetworkMode.Never — a procedural model can't replicate; see LaneBuilder's targeting arrows), rebuilt
	// each frame as the aim/power/spin change. Only the local bowler ever sees it.
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
		// Needs a real tangent: a zero one shades black [SOURCE]. fwd is perpendicular to the up normal.
		Vertex V( Vector3 p ) => new Vertex( p, n, fwd, new Vector4( 0f, 0f, 0f, 0f ) );

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

	// --- Step meter state (read by the bottom-of-screen HUD panel, ThrowAimHud) ---------
	// This component only DRIVES the guided sequence; the meter itself is a real first-party HUD panel drawn
	// at the bottom of the screen. Each active frame we publish the live step state here and ThrowAimHud
	// renders it — no world-space overlay. All directional steps are mapped so 0 = screen-LEFT.
	/// <summary>True while the bowler is actively lining up an aimed throw (the four steps), before the throw is
	/// committed. The camera reads this to pin a steady down-lane yaw (the mouse drives the aim, not free-look)
	/// while it eases laterally onto the ball's line.</summary>
	public bool LiningUp => _phase is Phase.Line or Phase.Aim or Phase.Power or Phase.Spin;

	/// <summary>A committed earlier step, drawn as a dimmed "ghost" track above the current meter so you can see
	/// what you already locked in. <see cref="Caret01"/> is the locked position (0..1, 0 = screen-left);
	/// <see cref="HasCenter"/> marks the directional steps (LINE/AIM) that show a lane-centre reference.</summary>
	public readonly record struct AimGhost( string Label, float Caret01, bool HasCenter );

	private readonly System.Collections.Generic.List<AimGhost> _ghosts = new();
	public System.Collections.Generic.IReadOnlyList<AimGhost> AimGhosts => _ghosts;

	public bool AimHudVisible { get; private set; }
	public int AimStep { get; private set; }             // 1..4
	public string AimStepLabel { get; private set; } = "";
	public float AimCaret01 { get; private set; }        // live caret position, 0..1 (0 = screen-left)
	public float AimSweet01 { get; private set; } = -1f; // sweet-spot fraction 0..1, or <0 for none (LINE/AIM)
	public bool AimTwoSided { get; private set; }        // SPIN: centred, hooks either way
	public bool AimCommitted { get; private set; }       // throw's away — frozen summary, no interaction hints

	private void UpdateAimHud()
	{
		AimCommitted = false; // this path only runs while actively lining up
		// Publish the current step's live meter state. The caret IS the step's live 0..1 hand position (0 =
		// screen-left / empty), so it maps straight through.
		switch ( _phase )
		{
			case Phase.Line: AimCaret01 = _caret01; AimSweet01 = -1f; AimTwoSided = false; AimStep = 1; AimStepLabel = "LINE"; break;
			case Phase.Aim: AimCaret01 = _caret01; AimSweet01 = -1f; AimTwoSided = false; AimStep = 2; AimStepLabel = "AIM"; break;
			case Phase.Power: AimCaret01 = _caret01; AimSweet01 = SweetSpot; AimTwoSided = false; AimStep = 3; AimStepLabel = "POWER"; break;
			case Phase.Spin: AimCaret01 = _caret01; AimSweet01 = SweetSpot; AimTwoSided = true; AimStep = 4; AimStepLabel = "SPIN"; break;
			default: AimHudVisible = false; return;
		}

		// The up-to-three already-committed steps, oldest first, shown as ghost tracks above the live one.
		_ghosts.Clear();
		if ( _phase >= Phase.Aim ) _ghosts.Add( new AimGhost( "LINE", _lineCaret, true ) );
		if ( _phase >= Phase.Power ) _ghosts.Add( new AimGhost( "AIM", _aimCaret, true ) );
		if ( _phase >= Phase.Spin ) _ghosts.Add( new AimGhost( "POWER", _powerCaret, false ) );

		AimHudVisible = true;
		PublishBoard( true ); // mirror the live line-up onto the settee feed board for spectators
	}

	// Mirror the local step-meter state onto the bowler's networked avatar so the per-lane settee feed board
	// (MonitorFeed) can draw the SAME LINE/AIM/POWER/SPIN meter for spectators — the bowler owns their avatar,
	// so these [Sync] fields replicate to everyone watching. Cleared (active=false) on teardown so the board
	// meter appears and disappears exactly when the bowler's own HUD does.
	private void PublishBoard( bool active )
	{
		var av = TerryAvatar.Local( Scene );
		if ( !av.IsValid() ) return;
		av.AimHudActive = active;
		if ( !active ) return;
		av.AimStepNet = AimStep;
		av.AimCaretNet = AimCaret01;
		av.AimSweetNet = AimSweet01;
		av.AimTwoSidedNet = AimTwoSided;
		av.AimCommittedNet = AimCommitted;
		av.AimLineCaretNet = _lineCaret;
		av.AimAimCaretNet = _aimCaret;
		av.AimPowerCaretNet = _powerCaret;
	}

}
