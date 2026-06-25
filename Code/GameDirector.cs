using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Runs a single throw's lifecycle and scores the pins, the way a modern alley's pin-detection
/// camera does: a pin counts as DOWN once it's either tipped past <see cref="TiltThresholdDegrees"/>
/// from vertical OR shoved off its spot by more than <see cref="DisplacementThresholdInches"/> — a
/// pin merely nudged but still standing on its spot stays UP.
///
/// Flow:
///   Rolling  — the player drives the ball normally.
///   (ball centre crosses the lane's throw line, a few feet short of the pins)
///   Watching — the ball's input AND braking are cut so it coasts on momentum into the pins, and
///              the camera pulls back/up for an overview of the hit. Held for <see cref="WatchSeconds"/>.
///   (reset)  — pins are re-racked on their spots, the ball is teleported back to the run-up and
///              handed back to the player, and we return to Rolling.
///
/// <see cref="PinStatuses"/> and <see cref="State"/> are read live by the top-left overlay panel.
/// </summary>
public sealed class GameDirector : Component
{
	/// <summary>Hard CAP (seconds) on the watch view before re-racking. We normally cut it short the
	/// moment the action settles (see the Settle* knobs); this is just the backstop for the rare case
	/// where something jitters forever.</summary>
	[Property] public float WatchSeconds { get; set; } = 5f;

	/// <summary>End the watch early once the ball and every standing pin have been quieter than the
	/// Settle thresholds for this long (seconds). Short, so a clean rack snaps back near-instantly.</summary>
	[Property, Group( "Settle" )] public float SettleQuietSeconds { get; set; } = 0.35f;
	/// <summary>Never settle early before at least this long has passed — cheap insurance against a
	/// one-frame fluke at the instant the watch arms.</summary>
	[Property, Group( "Settle" )] public float MinWatchSeconds { get; set; } = 0.3f;
	/// <summary>A body counts as "still" below this linear speed (u/s). Pins cascade at hundreds–
	/// thousands of u/s, a settled one is ~0, so there's a wide margin.</summary>
	[Property, Group( "Settle" )] public float SettleSpeedThreshold { get; set; } = 25f;
	/// <summary>And below this angular speed (rad/s) — catches a pin still spinning in place.</summary>
	[Property, Group( "Settle" )] public float SettleAngularThreshold { get; set; } = 1.0f;

	/// <summary>Physics substeps for the world. Re-asserted every frame because the engine replaces
	/// the PhysicsWorld after OnStart, so setting it once didn't stick (the probe was reading 1).
	/// Resolves the fast 10-pin cascade instead of smearing the ball through it in coarse steps.</summary>
	[Property] public int PhysicsSubSteps { get; set; } = 8;

	/// <summary>Tip past this many degrees off vertical and the pin reads as knocked down.</summary>
	[Property] public float TiltThresholdDegrees { get; set; } = 30f;
	/// <summary>Shoved this many real inches off its spot (horizontally) and the pin reads as down,
	/// even if it's somehow still upright.</summary>
	[Property] public float DisplacementThresholdInches { get; set; } = 4f;

	public enum RoundState { Rolling, Watching }
	public RoundState State { get; private set; } = RoundState.Rolling;

	/// <summary>A pin's number (1–10) and whether it currently reads as knocked down.</summary>
	public readonly record struct PinStatus( int Number, bool Down );

	private readonly List<PinStatus> _statuses = new();
	/// <summary>Live per-pin standing/down state, ordered by pin number, for the overlay.</summary>
	public IReadOnlyList<PinStatus> PinStatuses => _statuses;
	/// <summary>Count of pins currently down — handy for the overlay header / build hash.</summary>
	public int DownCount => _statuses.Count( s => s.Down );

	private LaneBuilder _lane;
	private BallController _ball;
	private BallCamera _camera;

	private Vector3 _ballHome;
	private Rotation _ballHomeRot;
	private float _watchStart;
	private float _quietSince;  // when the scene first went still this watch (-1 = currently moving)
	private bool _probed;      // one-shot physics probe logged?
	private float _impactSpeed; // ball speed at the throw line, to compare against the deck-exit speed
	private bool _exitLogged;   // logged the deck-exit speed for this throw?
	private float _peakPinSpin; // peak pin angular speed seen this throw (rad/s)
	private float _peakPinVel;  // peak pin linear speed seen this throw (u/s)

	// --- Game / frame state -------------------------------------------------------------
	// A bowling game is 10 frames of 1–2 balls (the 10th gets a 3rd on a strike/spare). We
	// track one PlayerScore per player — a list, so adding players later is a data change, not
	// a rewrite — but seed exactly one for now. The active player rolls _frame; with a single
	// player AdvanceFrame just steps the frame, with several it'd rotate players first.

	/// <summary>One player's scratch: the pin counts knocked on each ball, grouped by frame.</summary>
	public sealed class PlayerScore
	{
		public string Name { get; set; }
		/// <summary>Ten frames; each holds the pins felled per ball (1–2 balls, or 3 in the 10th).</summary>
		public readonly List<List<int>> Frames = new();
		public PlayerScore( string name )
		{
			Name = name;
			for ( var i = 0; i < 10; i++ ) Frames.Add( new List<int>() );
		}
	}

	private readonly List<PlayerScore> _players = new();
	private int _current;            // whose turn (index into _players)
	private int _frame;              // current frame 0..9 for the active player
	private int _ballStartDown;      // pins already down entering the current ball (swept from earlier balls this frame)
	private readonly HashSet<int> _sweptPins = new(); // pins cleared off the deck this frame (down on an earlier ball)
	private bool _gameOver;

	/// <summary>True once the 10th frame is complete — the overlay shows GAME OVER / press P.</summary>
	public bool GameOver => _gameOver;

	protected override void OnStart()
	{
		_lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault();
		_ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
		_camera = Scene.GetAllComponents<BallCamera>().FirstOrDefault();

		if ( _ball.IsValid() )
		{
			// Rack the ball in the run-up area on the lane platform (sitting on the surface), not at
			// the world origin. Fall back to wherever it was placed if the lane isn't up yet.
			_ballHome = _lane.IsValid()
				? _lane.RunUpWorldPosition + Vector3.Up * _ball.Radius
				: _ball.WorldPosition;
			_ballHomeRot = _ball.WorldRotation;

			// Put it there now, too, so the first throw starts from the run-up under the camera.
			PlaceBallHome();
		}

		// One player for now; the list + active-player index are here so multiplayer is a data
		// change later, not a rewrite. The pins were just racked by the LaneBuilder, so a fresh
		// game starts on frame 1 with a full deck.
		_players.Clear();
		_players.Add( new PlayerScore( "Player 1" ) );
		_current = 0;
		_frame = 0;
		_ballStartDown = 0;
		_sweptPins.Clear();
		_gameOver = false;
	}

	protected override void OnUpdate()
	{
		if ( !_lane.IsValid() || !_ball.IsValid() ) return;

		// Re-assert every frame: the engine swaps the PhysicsWorld after OnStart, so a one-time set
		// silently reverted to 1. This keeps the cascade properly substepped on the live world.
		Scene.PhysicsWorld.SubSteps = PhysicsSubSteps;

		// Press P at any time to start a brand-new game: frame 1, full rack, ball in hand.
		if ( Input.Keyboard.Pressed( "P" ) )
		{
			NewGame();
			return;
		}

		Probe();
		Score();

		switch ( State )
		{
			case RoundState.Rolling:
				// Arm watch-mode the moment the ball's centre passes the throw line (unless the
				// game's already over and the ball is parked).
				if ( !_gameOver && _ball.WorldPosition.x >= _lane.ThrowLineWorldX )
					EnterWatch();
				break;

			case RoundState.Watching:
				// Walk the pins once: track peak motion (debug) AND whether the whole rack is still.
				var pinsStill = true;
				foreach ( var p in _lane.Pins )
				{
					var prb = p.Object.IsValid() && p.Object.Enabled ? p.Object.Components.Get<Rigidbody>() : null;
					if ( !prb.IsValid() ) continue; // swept/disabled pins don't count
					var spin = prb.AngularVelocity.Length;
					var vel = prb.Velocity.Length;
					_peakPinSpin = MathF.Max( _peakPinSpin, spin );
					_peakPinVel = MathF.Max( _peakPinVel, vel );
					if ( vel > SettleSpeedThreshold || spin > SettleAngularThreshold )
						pinsStill = false;
				}

				// DEBUG: sample the ball's speed the moment it clears the back of the rack — how much
				// momentum did 10 pins actually take out of it? (God-mode if it keeps most of it.)
				if ( !_exitLogged && _ball.WorldPosition.x >= _lane.PinDeckEndWorldX )
				{
					_exitLogged = true;
					_ball.Absorbed = true; // pit swallows it: no back-wall rebound, no depenetration launch
					var bb = _ball.Components.Get<Rigidbody>();
					var exit = bb.IsValid() ? bb.Velocity.Length : 0f;
					var retained = _impactSpeed > 1f ? exit / _impactSpeed * 100f : 0f;
					Log.Info( $"[terryball][exit] cleared the rack: speed={exit:F0} u/s  " +
						$"retained={retained:F0}% of impact({_impactSpeed:F0})  downSoFar={DownCount}  " +
						$"peakPinSpin={_peakPinSpin:F1} rad/s  peakPinVel={_peakPinVel:F0} u/s" );
				}
				// The ball must also be still — absorbed in the pit, or stopped/deflected to rest. This
				// is the guard against settling before impact: while the ball is coasting in it's fast,
				// so the scene is never "quiet" until the throw has actually played out.
				var ballBody = _ball.Components.Get<Rigidbody>();
				var ballStill = _ball.Absorbed ||
					(ballBody.IsValid() && ballBody.Velocity.Length <= SettleSpeedThreshold && ballBody.AngularVelocity.Length <= SettleAngularThreshold);

				if ( pinsStill && ballStill )
				{
					if ( _quietSince < 0f ) _quietSince = Time.Now;
				}
				else
				{
					_quietSince = -1f;
				}

				var watched = Time.Now - _watchStart;
				var settledEarly = _quietSince >= 0f && watched >= MinWatchSeconds &&
					Time.Now - _quietSince >= SettleQuietSeconds;

				if ( settledEarly || watched >= WatchSeconds )
					ProcessSettledBall();
				break;
		}
	}

	private void EnterWatch()
	{
		State = RoundState.Watching;
		_watchStart = Time.Now;
		_quietSince = -1f;                  // not yet still (ball is coasting in fast)
		_ball.ControlsLocked = true;        // ignore input + braking: coast into the pins
		if ( _camera.IsValid() ) _camera.Watching = true; // back off and up
		_exitLogged = false;
		_peakPinSpin = 0f;
		_peakPinVel = 0f;

		// DEBUG: ball state the instant it crosses the throw line (impact conditions).
		var bb = _ball.Components.Get<Rigidbody>();
		if ( bb.IsValid() )
		{
			_impactSpeed = bb.Velocity.Length;
			var mph = _impactSpeed * (_ball.RealBallInches / (_ball.Radius * 2f)) / 17.6f;
			Log.Info( $"[terryball][impact] throw-line cross: speed={_impactSpeed:F0} u/s (~{mph:F1} mph)  " +
				$"angVel={bb.AngularVelocity.Length:F1}  vel={bb.Velocity}" );
		}
	}

	// DEBUG (one-shot): dump mass / inertia / gravity so we can see whether MassOverride rescaled
	// the inertia tensor and how the lane's scale compares to world gravity. Logged once.
	private void Probe()
	{
		if ( _probed ) return;
		if ( _lane.Pins.Count == 0 ) return;
		_probed = true;

		var g = Scene.PhysicsWorld.Gravity;
		var scaleCorrectG = 386f * _lane.UnitsPerInch; // real 1g in this lane's units
		Log.Info( $"[terryball][probe] world.Gravity={g} (|g|={g.Length:F0} u/s²)  " +
			$"scale-correct≈{scaleCorrectG:F0} u/s²  ratio={scaleCorrectG / MathF.Max( 1f, g.Length ):F1}x  " +
			$"unitsPerInch={_lane.UnitsPerInch:F2}  subSteps={Scene.PhysicsWorld.SubSteps}" );
		// Landmarks so the [coast] x-trace can be read against where the pins actually sit.
		Log.Info( $"[terryball][probe] x-landmarks: throwLine={_lane.ThrowLineWorldX:F0}  " +
			$"deckCenter={_lane.PinDeckWorldCenter.x:F0}  deckEnd={_lane.PinDeckEndWorldX:F0}  " +
			$"ballRadius={_ball.Radius:F0}" );

		var bb = _ball.Components.Get<Rigidbody>();
		if ( bb.IsValid() )
		{
			var r = _ball.Radius;
			var expect = 0.4f * bb.Mass * r * r; // solid sphere ⅖mr²
			Log.Info( $"[terryball][probe] BALL mass={bb.Mass:F2}kg gravScale={bb.GravityScale:F2}  " +
				$"inertia={bb.InertiaTensor}  expect(⅖mr²)≈{expect:F0}  massCenter={bb.MassCenter}" );
		}

		var pin = _lane.Pins[0].Object;
		var pb = pin.IsValid() ? pin.Components.Get<Rigidbody>() : null;
		if ( pb.IsValid() )
			Log.Info( $"[terryball][probe] PIN1 mass={pb.Mass:F2}kg gravScale={pb.GravityScale:F2}  " +
				$"inertia={pb.InertiaTensor}  massCenter={pb.MassCenter}" );
	}

	// DEBUG: after the rack has settled (end of watch), dump each pin's final state so we can see the
	// leave pattern AND why each "down" pin is down — actually fell over (tilt) vs merely shoved off
	// its spot while still upright (disp). Distinguishes a too-efficient cascade from over-lenient
	// scoring before we change anything.
	private void LogLeave()
	{
		var cosTilt = MathF.Cos( MathX.DegreeToRadian( TiltThresholdDegrees ) );
		var standing = new List<int>();
		foreach ( var p in _lane.Pins )
		{
			if ( !p.Object.IsValid() ) continue;
			var up = p.Object.WorldRotation.Up.Dot( Vector3.Up ).Clamp( -1f, 1f );
			var tilt = MathX.RadianToDegree( MathF.Acos( up ) );
			var disp = (p.Object.WorldPosition - p.Home).WithZ( 0f ).Length / _lane.UnitsPerInch;
			var tilted = up < cosTilt;
			var shoved = disp > DisplacementThresholdInches;
			if ( !tilted && !shoved ) standing.Add( p.Number );
			Log.Info( $"[terryball][leave] Pin{p.Number} {(tilted || shoved ? "DOWN" : "UP  ")} " +
				$"tilt={tilt:F0}° disp={disp:F1}in  reason={(tilted ? "fell" : shoved ? "shoved-but-upright" : "-")}" );
		}
		Log.Info( $"[terryball][leave] standing={string.Join( ",", standing )}  ({standing.Count} left)" );
	}

	// The watch has ended and the pins have settled: book this ball's pinfall onto the active
	// player's frame, then decide what comes next — another ball this frame (respot the pins
	// still standing), a fresh frame (full re-rack), or the end of the game.
	private void ProcessSettledBall()
	{
		LogLeave();

		// Pins down now (Score() already marks both physics-down pins AND ones swept on an
		// earlier ball this frame), minus the ones that were already down entering this ball,
		// is exactly what THIS ball knocked over.
		var downNow = _statuses.Count( s => s.Down );
		var knocked = Math.Clamp( downNow - _ballStartDown, 0, 10 );

		var player = _players[_current];
		var frame = player.Frames[_frame];
		frame.Add( knocked );

		DecideNextBall( frame );
	}

	// Bowling's flow control. Frames 1–9: a first-ball strike ends the frame, otherwise you get
	// a second ball at the pins you left. The 10th frame is the special case — a strike or spare
	// buys a third ball, and a cleared deck (double-strike, or the spare ball) re-racks fresh.
	private void DecideNextBall( List<int> frame )
	{
		var tenth = _frame == 9;
		var ball = frame.Count; // balls thrown so far this frame (1, 2 or 3)

		if ( !tenth )
		{
			var strike = frame[0] == 10;
			if ( strike || ball >= 2 )
				AdvanceFrame();                 // strike (1 ball) or open/spare (2 balls): frame done
			else
				ContinueFrame( fresh: false );  // second ball at the standing pins
			return;
		}

		// --- 10th frame ---
		if ( ball == 1 )
		{
			// A strike clears the deck, so the bonus ball is on a fresh rack; otherwise it's the
			// ordinary second ball at what's left.
			ContinueFrame( fresh: frame[0] == 10 );
		}
		else if ( ball == 2 )
		{
			var earnedThird = frame[0] == 10 || frame[0] + frame[1] >= 10; // strike or spare
			if ( !earnedThird )
			{
				EndGame();
			}
			else
			{
				// Third ball on a fresh rack only if the deck was just cleared: a second strike
				// after an opening strike, or the spare ball (which by definition cleared it).
				var fresh = frame[0] == 10 ? frame[1] == 10 : true;
				ContinueFrame( fresh );
			}
		}
		else // third ball thrown — the game is done
		{
			EndGame();
		}
	}

	// Advance the active player to their next frame and rack a fresh deck. (With multiple
	// players this is where you'd rotate to the next player before stepping the frame.)
	private void AdvanceFrame()
	{
		_frame++;
		if ( _frame >= 10 )
		{
			EndGame();
			return;
		}
		FullRack();
		_ballStartDown = 0;
		BeginRolling();
	}

	// Stay in the same frame for another ball. fresh=true racks a clean deck (10th-frame bonus
	// after a cleared rack); fresh=false sweeps the pins this ball downed and respots the rest.
	private void ContinueFrame( bool fresh )
	{
		if ( fresh )
		{
			FullRack();
			_ballStartDown = 0;
		}
		else
		{
			SweepAndRespot();
			_ballStartDown = _sweptPins.Count; // everything still down is now swept off the deck
		}
		BeginRolling();
	}

	private void EndGame()
	{
		_gameOver = true;
		State = RoundState.Rolling;
		if ( _camera.IsValid() ) _camera.Watching = false;
		_ball.Absorbed = false;
		_ball.ControlsLocked = true; // park the ball; no more throws until P starts a new game
		PlaceBallHome();

		var cum = ScoreThrough( _players[_current], out var have );
		var final = 0;
		for ( var f = 0; f < 10; f++ ) if ( have[f] ) final = cum[f];
		Log.Info( $"[terryball][game] complete — final score {final}" );
	}

	// Hand control back and roll the next ball from the run-up.
	private void BeginRolling()
	{
		State = RoundState.Rolling;
		_ball.ControlsLocked = false;
		_ball.Absorbed = false;
		if ( _camera.IsValid() ) _camera.Watching = false;
		PlaceBallHome();
	}

	// Press-P new game: clear every player's card, full rack, back to frame 1 ball 1.
	private void NewGame()
	{
		foreach ( var p in _players )
			foreach ( var f in p.Frames )
				f.Clear();
		_current = 0;
		_frame = 0;
		_gameOver = false;
		FullRack();
		_ballStartDown = 0;
		BeginRolling();
		Log.Info( "[terryball][game] new game — racked, frame 1" );
	}

	// Drop every pin back onto its spot, enabled, upright and dead still — a clean rack.
	private void FullRack()
	{
		_sweptPins.Clear();
		foreach ( var p in _lane.Pins )
			RespotPin( p );
	}

	// Sweep the pins this ball knocked down (disable them so they're off the deck and can't foul
	// the next ball) and respot the ones still standing back onto their exact spots.
	private void SweepAndRespot()
	{
		foreach ( var s in _statuses )
		{
			var p = _lane.Pins.FirstOrDefault( pp => pp.Number == s.Number );
			if ( !p.Object.IsValid() ) continue;
			if ( s.Down )
			{
				_sweptPins.Add( p.Number );
				p.Object.Enabled = false; // off the deck
			}
			else
			{
				RespotPin( p ); // nudged-but-standing pin returns to its home spot
			}
		}
	}

	// Place one pin on its home spot, enabled, upright and motionless.
	private void RespotPin( LaneBuilder.PinPlacement p )
	{
		if ( !p.Object.IsValid() ) return;
		p.Object.Enabled = true;
		p.Object.WorldPosition = p.Home;
		p.Object.WorldRotation = Rotation.Identity;
		var rb = p.Object.Components.Get<Rigidbody>();
		if ( rb.IsValid() )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}
	}

	// Drop the ball on its run-up spot, upright and dead still.
	private void PlaceBallHome()
	{
		_ball.WorldPosition = _ballHome;
		_ball.WorldRotation = _ballHomeRot;
		var ballBody = _ball.Components.Get<Rigidbody>();
		if ( ballBody.IsValid() )
		{
			ballBody.Velocity = Vector3.Zero;
			ballBody.AngularVelocity = Vector3.Zero;
		}
	}

	// Re-evaluate every pin against the alley's "still on its spot, still upright?" test.
	private void Score()
	{
		var cosTilt = MathF.Cos( MathX.DegreeToRadian( TiltThresholdDegrees ) );
		var maxOffset = DisplacementThresholdInches * _lane.UnitsPerInch;
		var maxOffsetSq = maxOffset * maxOffset;

		_statuses.Clear();
		foreach ( var p in _lane.Pins )
		{
			var down = true;
			// A pin swept off the deck earlier this frame stays down for the rest of the frame,
			// regardless of where its (disabled) body is sitting.
			if ( _sweptPins.Contains( p.Number ) )
			{
				down = true;
			}
			else if ( p.Object.IsValid() )
			{
				var tilted = p.Object.WorldRotation.Up.Dot( Vector3.Up ) < cosTilt;
				var offsetSq = (p.Object.WorldPosition - p.Home).WithZ( 0f ).LengthSquared;
				down = tilted || offsetSq > maxOffsetSq;
			}
			_statuses.Add( new PinStatus( p.Number, down ) );
		}
	}

	// --- Score view for the overlay -----------------------------------------------------
	// Read-only snapshots the razor scoreboard renders; computed here so the panel stays a dumb
	// view. One FrameView per frame (its ball marks + running total once scorable), one
	// PlayerView per player.

	/// <summary>One frame's display: the per-ball mark strings (X, /, -, digits) and the running
	/// cumulative total, which is null until the frame can actually be scored (its bonus is in).</summary>
	public sealed class FrameView
	{
		public List<string> Balls { get; init; } = new();
		public int? Score { get; init; }
		public bool Active { get; init; }
	}

	/// <summary>One player's row: name, ten frames, grand total and whether it's their turn.</summary>
	public sealed class PlayerView
	{
		public string Name { get; init; } = "";
		public List<FrameView> Frames { get; init; } = new();
		public int Total { get; init; }
		public bool Active { get; init; }
	}

	/// <summary>Build the scoreboard model for every player. Cheap; called on the panel's rebuild.</summary>
	public List<PlayerView> BuildScoreView()
	{
		var views = new List<PlayerView>();
		for ( var pi = 0; pi < _players.Count; pi++ )
		{
			var player = _players[pi];
			var cum = ScoreThrough( player, out var have );
			var frames = new List<FrameView>( 10 );
			var total = 0;
			for ( var fr = 0; fr < 10; fr++ )
			{
				if ( have[fr] ) total = cum[fr];
				frames.Add( new FrameView
				{
					Balls = BallMarks( player.Frames[fr], fr == 9 ),
					Score = have[fr] ? cum[fr] : null,
					Active = pi == _current && fr == _frame && !_gameOver,
				} );
			}
			views.Add( new PlayerView
			{
				Name = player.Name,
				Frames = frames,
				Total = total,
				Active = pi == _current,
			} );
		}
		return views;
	}

	/// <summary>Standard 10-pin cumulative scoring. Returns the running total per frame; the
	/// matching <paramref name="have"/> flag is false for any frame not yet fully scorable (e.g.
	/// a strike whose two bonus balls haven't been thrown).</summary>
	private static int[] ScoreThrough( PlayerScore player, out bool[] have )
	{
		// Flatten to a roll sequence (a frames-1–9 strike is the single value 10), which the
		// classic strike/spare look-ahead walks cleanly — including the 10th frame's bonus balls.
		var rolls = new List<int>();
		foreach ( var f in player.Frames )
			foreach ( var b in f )
				rolls.Add( b );

		var cum = new int[10];
		have = new bool[10];
		var running = 0;
		var i = 0;
		for ( var fr = 0; fr < 10 && i < rolls.Count; fr++ )
		{
			if ( fr == 9 )
			{
				// Last frame is just the sum of its balls, but only "have" it once it's complete:
				// 3 balls if the first ball is a strike or the first two make a spare, else 2.
				var need = rolls[i] == 10 || (i + 1 < rolls.Count && rolls[i] + rolls[i + 1] == 10) ? 3 : 2;
				if ( rolls.Count - i >= need )
				{
					for ( var k = i; k < rolls.Count; k++ ) running += rolls[k];
					cum[fr] = running;
					have[fr] = true;
				}
				break;
			}

			if ( rolls[i] == 10 ) // strike: 10 + next two balls
			{
				if ( i + 2 < rolls.Count ) { running += 10 + rolls[i + 1] + rolls[i + 2]; cum[fr] = running; have[fr] = true; i += 1; }
				else break;
			}
			else if ( i + 1 < rolls.Count && rolls[i] + rolls[i + 1] == 10 ) // spare: 10 + next ball
			{
				if ( i + 2 < rolls.Count ) { running += 10 + rolls[i + 2]; cum[fr] = running; have[fr] = true; i += 2; }
				else break;
			}
			else if ( i + 1 < rolls.Count ) // open frame, both balls thrown
			{
				running += rolls[i] + rolls[i + 1]; cum[fr] = running; have[fr] = true; i += 2;
			}
			else break; // only the first ball of an open frame so far
		}
		return cum;
	}

	private static string Sym( int n ) => n == 0 ? "-" : n.ToString();

	// The 1–3 mark strings shown in a frame's little boxes: X for a strike, / for a spare, - for
	// a miss, digits otherwise. The 10th frame has three boxes and its own strike/spare rules
	// per ball (a cleared deck re-racks, so a later ball can be a strike or spare in its own right).
	private static List<string> BallMarks( List<int> f, bool tenth )
	{
		if ( !tenth )
		{
			var b = new List<string> { "", "" };
			if ( f.Count >= 1 )
			{
				if ( f[0] == 10 ) { b[1] = "X"; }          // strike sits in the right box
				else
				{
					b[0] = Sym( f[0] );
					if ( f.Count >= 2 ) b[1] = f[0] + f[1] == 10 ? "/" : Sym( f[1] );
				}
			}
			return b;
		}

		var t = new List<string> { "", "", "" };
		if ( f.Count >= 1 ) t[0] = f[0] == 10 ? "X" : Sym( f[0] );
		if ( f.Count >= 2 )
			t[1] = f[0] == 10 ? (f[1] == 10 ? "X" : Sym( f[1] ))   // fresh rack after the strike
				: (f[0] + f[1] == 10 ? "/" : Sym( f[1] ));         // spare/open on the first rack
		if ( f.Count >= 3 )
		{
			// The third ball is on a fresh rack after a double-strike or a spare; otherwise it
			// continues the second ball's rack and can itself be a spare.
			var fresh = f[0] == 10 ? f[1] == 10 : true;
			t[2] = fresh ? (f[2] == 10 ? "X" : Sym( f[2] ))
				: (f[1] + f[2] == 10 ? "/" : Sym( f[2] ));
		}
		return t;
	}
}
