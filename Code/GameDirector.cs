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
	/// <summary>How long (seconds) to hold the pulled-back watch view before re-racking. Hardcoded
	/// behaviour for now — see the brief; exposed so it's still tunable in-scene.</summary>
	[Property] public float WatchSeconds { get; set; } = 5f;

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
	private bool _probed;      // one-shot physics probe logged?
	private float _impactSpeed; // ball speed at the throw line, to compare against the deck-exit speed
	private bool _exitLogged;   // logged the deck-exit speed for this throw?
	private float _peakPinSpin; // peak pin angular speed seen this throw (rad/s)
	private float _peakPinVel;  // peak pin linear speed seen this throw (u/s)

	protected override void OnStart()
	{
		_lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault();
		_ball = Scene.GetAllComponents<BallController>().FirstOrDefault();
		_camera = Scene.GetAllComponents<BallCamera>().FirstOrDefault();

		// DEBUG/feel: raise physics substeps so the fast 10-pin cascade is actually resolved instead
		// of the ball plowing through it in one coarse step. This is world-global but only buys
		// accuracy (costs CPU) — it doesn't change behaviour for players/other modes like gravity would.
		Scene.PhysicsWorld.SubSteps = 8;

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
	}

	protected override void OnUpdate()
	{
		if ( !_lane.IsValid() || !_ball.IsValid() ) return;

		Probe();
		Score();

		switch ( State )
		{
			case RoundState.Rolling:
				// Arm watch-mode the moment the ball's centre passes the throw line.
				if ( _ball.WorldPosition.x >= _lane.ThrowLineWorldX )
					EnterWatch();
				break;

			case RoundState.Watching:
				// DEBUG: track how violently the pins are actually spinning/flying this throw.
				foreach ( var p in _lane.Pins )
				{
					var prb = p.Object.IsValid() ? p.Object.Components.Get<Rigidbody>() : null;
					if ( !prb.IsValid() ) continue;
					_peakPinSpin = MathF.Max( _peakPinSpin, prb.AngularVelocity.Length );
					_peakPinVel = MathF.Max( _peakPinVel, prb.Velocity.Length );
				}

				// DEBUG: sample the ball's speed the moment it clears the back of the rack — how much
				// momentum did 10 pins actually take out of it? (God-mode if it keeps most of it.)
				if ( !_exitLogged && _ball.WorldPosition.x >= _lane.PinDeckEndWorldX )
				{
					_exitLogged = true;
					var bb = _ball.Components.Get<Rigidbody>();
					var exit = bb.IsValid() ? bb.Velocity.Length : 0f;
					var retained = _impactSpeed > 1f ? exit / _impactSpeed * 100f : 0f;
					Log.Info( $"[terryball][exit] cleared the rack: speed={exit:F0} u/s  " +
						$"retained={retained:F0}% of impact({_impactSpeed:F0})  downSoFar={DownCount}  " +
						$"peakPinSpin={_peakPinSpin:F1} rad/s  peakPinVel={_peakPinVel:F0} u/s" );
				}
				if ( Time.Now - _watchStart >= WatchSeconds )
					Reset();
				break;
		}
	}

	private void EnterWatch()
	{
		State = RoundState.Watching;
		_watchStart = Time.Now;
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

	private void Reset()
	{
		State = RoundState.Rolling;
		_ball.ControlsLocked = false;
		if ( _camera.IsValid() ) _camera.Watching = false;

		// Re-rack: drop every pin back onto its spot, upright and dead still.
		foreach ( var p in _lane.Pins )
		{
			if ( !p.Object.IsValid() ) continue;
			p.Object.WorldPosition = p.Home;
			p.Object.WorldRotation = Rotation.Identity;
			var rb = p.Object.Components.Get<Rigidbody>();
			if ( rb.IsValid() )
			{
				rb.Velocity = Vector3.Zero;
				rb.AngularVelocity = Vector3.Zero;
			}
		}

		// Teleport the ball back to the run-up and hand control back to the player.
		PlaceBallHome();
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
			if ( p.Object.IsValid() )
			{
				var tilted = p.Object.WorldRotation.Up.Dot( Vector3.Up ) < cosTilt;
				var offsetSq = (p.Object.WorldPosition - p.Home).WithZ( 0f ).LengthSquared;
				down = tilted || offsetSq > maxOffsetSq;
			}
			_statuses.Add( new PinStatus( p.Number, down ) );
		}
	}
}
