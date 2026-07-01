using Sandbox;
using Sandbox.Citizen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Terryball;

/// <summary>
/// The hamster-ball mechanic.
///
/// A large glass ball (Rigidbody + SphereCollider) with Terry (the Citizen) riding the
/// INSIDE surface.
///
/// Control: WASD is camera-relative and names a target travel direction. Each tick we steer
/// the ball's horizontal velocity toward wishDir * <see cref="MaxSpeed"/> with a proportional
/// force applied at the ball's centre (friction then spins it up to roll). With no key held
/// the target is zero, so the same controller becomes a brake that opposes the current
/// velocity — the ball coasts to a stop and never accelerates on its own.
///
/// Terry is purely visual: he "runs" up the inner wall toward the wish direction and gravity
/// settles him to the bottom, but his motion no longer drives the ball (it used to, via an
/// off-centre-weight torque). His run animation is driven from how fast he's actually moving
/// through the world, and no Terry↔ball collision is simulated (by design). The standalone
/// <see cref="TerryController"/> stub is kept in the repo for other reuse.
/// </summary>
public sealed class BallController : Component, Component.ICollisionListener
{
	[Property, Group( "Ball" )] public float Radius { get; set; } = 100f;
	/// <summary>Surface friction of the ball. Kept LOW because a real lane/pin is slick (oiled): with
	/// high friction the ball's topspin GRIPS the pins and walks UP them (it climbs like a topspin ball
	/// on a wall — the upward kick comes from tangential friction, not the collision). Low friction lets
	/// it drive through low. The ball is force-driven, so it doesn't need grip to move.</summary>
	[Property, Group( "Ball" )] public float Friction { get; set; } = 0.3f;
	/// <summary>Restitution (bounciness) of the ball's surface. By the collision algebra (M=6.8 vs
	/// m=1.6), HIGH restitution makes the ball lose MORE speed to the pin (keeps ~63% at e=0.95 vs
	/// ~81% at e=0) AND makes the pin fly faster — both wanted. Low restitution makes ball and pin
	/// move together (ball barely slows = plows). So keep this high; the steamroll was a contact/CCD
	/// problem, not restitution.</summary>
	[Property, Group( "Ball" )] public float Elasticity { get; set; } = 0.3f;
	/// <summary>Pit catch strength: once the ball passes the back of the pin deck it's over the pit, and
	/// its speed bleeds at this rate (per-second exponential decay). A deterministic "cushion" so the
	/// ball dumps its energy and settles in the pit instead of pinging off the dead back wall and
	/// rocketing back up the lane — independent of any collision/restitution quirk. 0 = off.</summary>
	[Property, Group( "Ball" )] public float PitDamping { get; set; } = 6f;
	/// <summary>Passive rolling resistance (per-second decay of spin). Braking is active now —
	/// releasing the keys applies a <see cref="BrakeGain"/> counter-force whose effective decay
	/// rate is BrakeGain/Mass — so this MUST stay well below that or it silently dominates the
	/// coast and overrides the brake. It mainly exists to bleed off lone vertical (yaw) spin the
	/// brake can't see. Higher also lowers top speed under drive.</summary>
	[Property, Group( "Ball" )] public float AngularDamping { get; set; } = 0.05f;
	/// <summary>Mass in kg. A regulation bowling ball is ~6.8 kg (15 lb), but that makes the ball too
	/// dominant ("meaty") over the pins. Detached down to reduce the ball:pin ratio so it deflects
	/// more and clears fewer pins. NOTE: the live value is serialized in the scene — change it there,
	/// the code default only applies to freshly-added components.</summary>
	[Property, Group( "Ball" )] public float Mass { get; set; } = 5.3f;

	[Property, Group( "Occupant" )] public float TerryScale { get; set; } = 1.5f;

	/// <summary>Ball speed — in the SAME bowling-scale mph the HUD shows — at or above which Terry switches
	/// from running into the Citizen's continuous Roll animation. His legs ramp up walk→run over 0..this,
	/// maxing out just as he tucks into the roll. Kept in mph so it tracks the gauge regardless of Radius.</summary>
	[Property, Group( "Occupant" )] public float RollAtMph { get; set; } = 11.5f;
	/// <summary>Top leg speed (u/s) Terry's run ramps to right before he rolls. The Citizen is ~1 unit/inch,
	/// so ≈167 u/s reads as a ~9.5 mph run; below it the Auto move-style eases him from a walk into a run.</summary>
	[Property, Group( "Occupant" )] public float RunTopSpeed { get; set; } = 167f;
	/// <summary>Animgraph playback multiplier while Terry is rolling. The Roll clip plays at a fixed baked
	/// cadence, so we drive its speed here.</summary>
	[Property, Group( "Occupant" )] public float RollPlaybackRate { get; set; } = 5.25f;
	/// <summary>Animgraph playback multiplier for his legs at the SLOW end (a near-stationary walk). The
	/// move-speed cadence alone is too slow, so we scale the whole walk→run ramp via the playback rate.</summary>
	[Property, Group( "Occupant" )] public float WalkPlaybackRate { get; set; } = 0.5f;
	/// <summary>Animgraph playback multiplier the run ramps UP to right at the roll threshold.</summary>
	[Property, Group( "Occupant" )] public float RunPlaybackRate { get; set; } = 4.5f;
	/// <summary>How far toward the roll threshold (0..1) Terry stays at his base WalkPlaybackRate before
	/// his legs start speeding up. e.g. 0.3 = no speed-up over the first 30% of the run-up, then ramp.</summary>
	[Property, Group( "Occupant" )] public float WalkRampStart { get; set; } = 0.3f;
	/// <summary>Seconds to crossfade the run↔roll change. We ease a blend over this window (flipping the
	/// special-move state at its mid-point and crossfading the playback rate) so the switch isn't a hard
	/// snap. The Citizen animgraph crossfades the actual pose on its own baked timing.</summary>
	[Property, Group( "Occupant" )] public float TransitionTime { get; set; } = 0.5f;
	/// <summary>Playback rate to dip to through the middle of the run↔roll swap. PlaybackRate also scales the
	/// animgraph's own pose crossfade, so easing to ~1× across the swap lets that crossfade play at its real
	/// (visible) baked duration instead of being sped up by the fast run/roll cadence rates.</summary>
	[Property, Group( "Occupant" )] public float TransitionPlaybackRate { get; set; } = 1f;

	// --- Ball steering (the control scheme) --------------------------------------------
	// WASD names a target travel direction; we steer the ball's horizontal velocity toward
	// wishDir * MaxSpeed with a proportional force at the ball's centre. With no key held the
	// target is zero, so the same controller brakes — the ball only ever loses speed when
	// nothing is pressed and never self-accelerates.

	/// <summary>Top travel speed (u/s) a full WASD push steers toward.</summary>
	[Property, Group( "Drive" )] public float MaxSpeed { get; set; } = 5625f;
	/// <summary>Proportional gain turning the velocity error (target − current) into a centre
	/// force while a key is held. Force = error · gain, so this is roughly "how many u/s² of
	/// accel per u/s you're off target." Higher = snappier accel/steering. Tune by feel.</summary>
	[Property, Group( "Drive" )] public float DriveGain { get; set; } = 15f;
	/// <summary>Proportional gain on the braking force applied when no key is held; it opposes
	/// the current velocity so the ball coasts to a stop. The coast's decay rate is
	/// BrakeGain/Mass per second, so with Mass ≈ 6.8 a gain of 1 barely slows the ball (≈5 s
	/// time-constant); 20 hauls the ball down hard, stopping it in well under a second. Tune by feel.</summary>
	[Property, Group( "Drive" )] public float BrakeGain { get; set; } = 20f;

	/// <summary>Ball world velocity (units/s); zero before the body exists. For the roll audio.</summary>
	public Vector3 Velocity => _body.IsValid() ? _body.Velocity : Vector3.Zero;
	/// <summary>The drive target top speed (units/s) — the roll audio's reference for "full speed".</summary>
	public float MaxDriveSpeed => MaxSpeed;
	/// <summary>0..1 turning effort the player is applying (perpendicular correction), 0 after release.</summary>
	public float SteerEffort01 { get; private set; }

	/// <summary>When set, the ball ignores BOTH input and braking and just coasts on its own
	/// momentum (physics only). The round director flips this on once the ball crosses the throw
	/// line so the throw is committed to physics and you can watch it hit the pins.</summary>
	public bool ControlsLocked { get; set; }

	// --- Occupant (Terry) visual ride --------------------------------------------------
	// Terry rides CENTRED in the ball (purely visual — he doesn't drive it). He runs facing the
	// ball's travel direction and blends into a tucked forward somersault as it speeds up; see
	// RollAtMph / RunTopSpeed up top, OnUpdate (anim) and PlaceTerry.

	[Property, Group( "Debug" )] public bool ShowSpeed { get; set; } = true;
	/// <summary>Real ball size used only to report a bowling-scale mph on the HUD.</summary>
	[Property, Group( "Debug" )] public float RealBallInches { get; set; } = 8.5f;

	private Rigidbody _body;
	private SphereCollider _col;
	private GameObject _terry;
	private SkinnedModelRenderer _terryRenderer;
	private CitizenAnimationHelper _anim;

	// In-ball Terry is dressed in the ACTIVE bowler's avatar so everyone sees who's up riding inside.
	// Re-dressed (on every client) whenever the spotlight moves to a different bowler.
	private Guid _dressedBowler = Guid.Empty;
	private bool _dressedAny;
	private CancellationTokenSource _dressCts;
	private GameDirector _director;
	// Resolve within our own lane (the director sharing our Lane root), never a scene-global scan, so
	// each lane's ball answers only to its own director.
	private GameDirector Director => _director.IsValid() ? _director : (_director = Lane.Of( this )?.Director);

	// Occupant state, in world space: Terry rides CENTRED in the ball (not on the inner wall). _faceDir
	// is his last meaningful heading (the ball's travel direction); _bodyCentreUp is the feet→torso-centre
	// offset that hangs his middle on the ball centre.
	private Vector3 _faceDir = Vector3.Forward; // last meaningful heading (ball travel dir)
	private float _bodyCentreUp;                // feet→centre height (scaled), so he sits centred in the ball
	private float _transitionT;                 // seconds left in the current run↔roll transition window
	private bool _wasRolling;                   // edge-detect the roll state change to open that window
	private bool _built;

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Physics: dynamic sphere that grips the floor so it rolls instead of sliding.
		// Create the Rigidbody first, then the collider.
		_body = Components.GetOrCreate<Rigidbody>();

		var col = Components.GetOrCreate<SphereCollider>();
		col.Radius = Radius;
		col.Friction = Friction;
		col.Elasticity = Elasticity; // springy hit so the ball deflects off the pins instead of plowing
		_col = col;

		// Body params go AFTER the collider: adding a collider shape rebuilds mass from
		// volume (BuildMass), which would otherwise clobber our MassOverride.
		_body.AngularDamping = AngularDamping;
		_body.MassOverride = Mass; // realistic bowling-ball mass vs the pins
		_body.EnhancedCcd = true;  // don't tunnel through the pins at speed
		// Mute the engine's default surface collision sounds for the ball too (ball↔lane drop, gutter,
		// etc.) — they're the generic clunks; the pins carry their own procedural crash audio.
		_body.RigidbodyFlags |= RigidbodyFlags.DisableCollisionSounds;

		// Procedural roll rumble + foul-line throw sound.
		Components.GetOrCreate<BallAudio>();

		// Cast a green (galaxy) coloured shadow. The BallShadowTintPP post-process gathers every ShadowBall
		// and recolours its real shadow; radius tracks the ball, tint/texture default to the galaxy green.
		Components.GetOrCreate<ShadowBall>().Radius = Radius;

		// Visual: scaled dev sphere with the glass material so Terry inside shows through.
		var visual = new GameObject( true, "BallVisual" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		var shell = visual.Components.GetOrCreate<ModelRenderer>();
		shell.Model = Model.Load( "models/dev/sphere.vmdl" );
		shell.MaterialOverride = Material.Load( "materials/glassball.vmat" );

		var shellBounds = shell.Model.Bounds.Size.x; // dev sphere ≈ 64 across (r 32)
		visual.LocalScale = new Vector3( (Radius * 2f) / shellBounds );

		// Terry: a Citizen riding the inside. His world transform is driven every frame, so
		// the ball's spin never carries him — he rides the inner surface independently.
		_terry = new GameObject( true, "Terry" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		_terry.LocalScale = new Vector3( TerryScale );

		var renderer = _terry.Components.GetOrCreate<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );
		_terryRenderer = renderer;

		// Feet→torso-centre height: the Citizen's origin is at his feet, so half his height is roughly his
		// middle. We hang him so this point sits on the ball centre and tumble him about it.
		_bodyCentreUp = (renderer.Model?.Bounds.Size.z ?? 64f) * 0.5f * TerryScale;

		_anim = _terry.Components.GetOrCreate<CitizenAnimationHelper>();
		_anim.Target = renderer;
		_anim.IsGrounded = true;

		PlaceTerry();
	}

	// Keep the in-ball Terry wearing whoever is currently up (runs on every client, bowler or
	// spectator, so all four feeds show the active bowler's avatar inside the ball).
	private void UpdateBowlerDress()
	{
		var id = Director?.ActiveBowlerId ?? Guid.Empty;

		// The Terry only appears once the crane has actually scooped the bowler IN (Carried) — so the
		// pickup flight is an empty ball, then Terry pops in on the scoop, and pops out again on the drop.
		// We still dress him off the active-bowler id below, which has him ready before the scoop lands.
		if ( _terry.IsValid() )
			_terry.Enabled = ActiveBowlerCarried( id );

		if ( id != _dressedBowler || !_dressedAny )
		{
			_dressedBowler = id;
			_dressedAny = true;
			_ = DressForBowler( id );
		}
	}

	/// <summary>Dress the in-ball Terry in the bowler <paramref name="id"/>'s full avatar (community-market
	/// cosmetics included). Empty id (nobody up) leaves the previous outfit on — the ball is parked then
	/// anyway. Our own turn reads the complete local avatar; a remote bowler is read from their connection
	/// (their owned items). ApplyAsync downloads any missing workshop/inventory items first.</summary>
	private async Task DressForBowler( Guid id )
	{
		if ( !_terryRenderer.IsValid() || id == Guid.Empty ) return;

		_dressCts?.Cancel();
		_dressCts = new CancellationTokenSource();
		try
		{
			// Build the avatar container, then apply. CreateFromLocalUser/CreateFromConnection can throw
			// from inside the engine (e.g. an inventory read faulting in RemoveUnownedItems), so the whole
			// thing is guarded — a transient cosmetics failure must not take down the bowler's turn.
			ClothingContainer clothing;
			if ( Connection.Local is not null && id == Connection.Local.Id )
			{
				clothing = ClothingContainer.CreateFromLocalUser();
			}
			else
			{
				var conn = Connection.Find( id );
				clothing = conn is not null ? ClothingContainer.CreateFromConnection( conn ) : null;
			}
			if ( clothing is null ) return;

			await clothing.ApplyAsync( _terryRenderer, _dressCts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception e )
		{
			Log.Warning( $"[terryball] in-ball dressing failed: {e.Message}" );
		}
	}

	// Is the active bowler's avatar currently scooped into the ball? Drives the in-ball Terry's visibility
	// so it matches the crane's body↔ball swap on every client (the flag is synced from the bowler).
	private bool ActiveBowlerCarried( Guid id )
	{
		if ( id == Guid.Empty ) return false;
		foreach ( var a in Scene.GetAllComponents<TerryAvatar>() )
			if ( a.IsValid() && a.GameObject.Network.Owner?.Id == id )
				return a.Carried;
		return false;
	}

	protected override void OnDisabled() => _dressCts?.Cancel();

	// --- Crane transport (the claw mechanic) -------------------------------------------
	// Between the run-up and the bowler's seat the director flies the ball kinematically (a claw that
	// fetches/returns the player). It sails the full length of the lane and over the freshly-set rack, so
	// we drop the collider + gravity for the trip and the director sets the position each frame.

	/// <summary>Enter/leave crane mode: drop the collider + gravity (so the flight doesn't scatter the
	/// rack or sag) and kill any residual motion; leaving restores normal physics for the roll.</summary>
	public void SetCraneMode( bool on )
	{
		if ( _col.IsValid() ) _col.Enabled = !on;
		if ( _body.IsValid() )
		{
			_body.Gravity = !on;
			if ( on )
			{
				_body.Velocity = Vector3.Zero;
				_body.AngularVelocity = Vector3.Zero;
				_body.Sleeping = false;
			}
		}
	}

	/// <summary>Kinematically place the ball at <paramref name="pos"/> for a crane leg, held dead still.</summary>
	public void CraneMoveTo( Vector3 pos )
	{
		WorldPosition = pos;
		if ( _body.IsValid() )
		{
			_body.Velocity = Vector3.Zero;
			_body.AngularVelocity = Vector3.Zero;
		}
	}

	protected override void OnFixedUpdate()
	{
		var dt = Time.Delta;
		if ( dt <= 0f ) return;

		// Only the client whose turn it is drives the ball; for everyone else it sits parked (its
		// transform is networked from the active bowler). Nobody up = nobody drives.
		if ( Director is not null && !Director.LocalIsBowling ) return;

		// Camera-relative run direction on the horizontal plane. While controls are locked (the
		// ball has crossed the throw line) we read no input at all — wish stays zero.
		var move = ControlsLocked ? Vector3.Zero : Input.AnalogMove; // x = forward, y = left
		var wish = Vector3.Zero;
		var cam = Scene.Camera;
		if ( !ControlsLocked && cam.IsValid() )
		{
			var rot = cam.WorldRotation;
			var f = rot.Forward.WithZ( 0f );
			var l = rot.Left.WithZ( 0f );
			if ( f.Length > 0.001f ) f = f.Normal;
			if ( l.Length > 0.001f ) l = l.Normal;
			wish = f * move.x + l * move.y;
		}
		if ( wish.Length > 1f ) wish = wish.Normal;

		// --- Steer the ball ------------------------------------------------------------
		// wishDir names where you want to go; steer the ball's horizontal velocity toward
		// wishDir * MaxSpeed. With no input the target is zero, so the controller brakes
		// instead — the ball never gains speed on its own.
		//
		// Drive with a force at the ball's CENTRE, not a roll torque. A ball driven only by
		// roll torque is friction-limited to ~1 g of forward accel (any extra torque just
		// spins the contact out), which felt painfully slow. A centre force accelerates the
		// COM directly and lets ground friction spin the ball up to roll — far stronger,
		// friction-independent acceleration that still tapers smoothly toward MaxSpeed.
		var hasInput = wish.Length > 0.01f;
		var vel = _body.Velocity.WithZ( 0f );
		var targetVel = hasInput ? wish.Normal * (MaxSpeed * wish.Length.Clamp( 0f, 1f )) : Vector3.Zero;
		var velError = targetVel - vel;
		var gain = hasInput ? DriveGain : BrakeGain;
		// Locked = neither drive nor brake: the ball coasts on momentum alone into the pins.
		if ( !ControlsLocked )
			_body.ApplyForce( velError * gain );

		// Steering effort for the roll-rumble audio: the part of the correction perpendicular to
		// travel — i.e. how hard the player is TURNING, not just accelerating straight. Zero once
		// the ball is released (ControlsLocked), so the roll smooths out after the throw.
		if ( ControlsLocked || !hasInput )
		{
			SteerEffort01 = 0f;
		}
		else
		{
			var dir = vel.Length > 1f ? vel.Normal : (wish.Length > 0.01f ? wish.Normal : Vector3.Forward);
			var lateral = velError - velError.Dot( dir ) * dir;
			SteerEffort01 = (lateral.Length / (MaxSpeed * 0.5f)).Clamp( 0f, 1f );
		}

		// Terry rides CENTRED in the ball now (see PlaceTerry) — the old inner-wall climb is gone.

		// Pit catch: once the ball is behind the pin deck (out over the pit) bleed its speed hard, so it
		// dumps energy into the cushion and settles instead of ricocheting off the dead back wall and
		// flying back up the lane. Frame-rate-independent exponential decay; only post-throw (coasting).
		var lane = Lane.Of( this )?.Builder;
		if ( PitDamping > 0f && ControlsLocked && lane.IsValid() && WorldPosition.x > lane.PinDeckEndWorldX )
		{
			var k = MathF.Exp( -PitDamping * dt );
			_body.Velocity *= k;
			_body.AngularVelocity *= k;
		}

		// Keep the body awake while input is held or it's still moving.
		if ( hasInput || _body.Velocity.LengthSquared > 1f )
			_body.Sleeping = false;

		// DEBUG: trace the coast through the rack every physics step — speed vs position, so we can see
		// whether the ball steps down at each pin contact or sails through unaffected.
		if ( ControlsLocked && _body.IsValid() && (Director?.LogPinCrash ?? false) )
		{
			var v = _body.Velocity;
			Log.Info( $"[terryball][coast] x={WorldPosition.x:F0} y={WorldPosition.y:F0} spd={v.Length:F0} " +
				$"vx={v.x:F0} vy={v.y:F0} vz={v.z:F0} angVel={_body.AngularVelocity.Length:F1} mass={_body.Mass:F2}" );
		}
	}

	protected override void OnUpdate()
	{
		UpdateBowlerDress();

		PlaceTerry();

		// Drive Terry's locomotion from the BALL's speed (he's centred, so he barely moves himself). He
		// eases from a walk up to a run as the ball speeds up, then tucks into the Citizen's continuous Roll
		// animation once it's really moving. ramp01 maps 0..rollSpeed → walk..run; past that he rolls.
		if ( _anim.IsValid() )
		{
			var speed = _body.IsValid() ? _body.Velocity.WithZ( 0f ).Length : 0f;
			// Roll threshold expressed in the HUD's bowling-scale mph, so "11.5 mph and up" means what the
			// gauge says (and survives a Radius change). Same mph mapping as the ShowSpeed readout below.
			var rollSpeed = MphToUnits( RollAtMph );
			var rolling = speed >= rollSpeed;
			var ramp01 = rollSpeed > 0f ? (speed / rollSpeed).Clamp( 0f, 1f ) : 0f;

			// Flip the roll state the instant the speed crosses the threshold and open a TransitionTime
			// window. The engine crossfades the pose itself, starting now — we hold the playback rate down
			// across this window (below) so that crossfade plays at a visible speed instead of being
			// fast-forwarded by the fast run/roll cadence rate.
			if ( rolling != _wasRolling )
			{
				_transitionT = TransitionTime;
				_wasRolling = rolling;
			}
			_transitionT = MathF.Max( 0f, _transitionT - Time.Delta );

			_anim.SpecialMove = rolling
				? CitizenAnimationHelper.SpecialMoveStyle.Roll
				: CitizenAnimationHelper.SpecialMoveStyle.None;

			// Drive playback rate directly: the move-speed cadence alone tops out at a jog (way too slow
			// against a fast ball). His legs hold at WalkPlaybackRate over the first WalkRampStart of the
			// run-up, then ramp WalkPlaybackRate→RunPlaybackRate the rest of the way; the settled roll plays
			// at RollPlaybackRate.
			if ( _terryRenderer.IsValid() )
			{
				var walkT = ((ramp01 - WalkRampStart) / MathF.Max( 1f - WalkRampStart, 0.0001f )).Clamp( 0f, 1f );
				var runRate = MathX.Lerp( WalkPlaybackRate, RunPlaybackRate, walkT );
				var settledRate = rolling ? RollPlaybackRate : runRate;

				// PlaybackRate scales the animgraph's OWN pose crossfade too (animNative.Update advances
				// transitions by delta×rate), so the fast cadence rate would snap the swap. inWindow is 1 at
				// the start of the transition — when the crossfade fires — and eases to 0 as the window
				// closes, so we hold the rate down at TransitionPlaybackRate (≈1×) while the pose crossfades,
				// then ramp back up to the fast settled cadence. Slow window lasts exactly TransitionTime.
				var inWindow = TransitionTime > 0f ? (_transitionT / TransitionTime).Clamp( 0f, 1f ) : 0f;
				_terryRenderer.PlaybackRate = MathX.Lerp( settledRate, TransitionPlaybackRate, inWindow );
			}

			// Feed his legs a ramped speed (Auto move-style picks walk vs run from it), facing travel so the
			// run/roll go the way the ball's rolling. Capped at RunTopSpeed (~9.5 mph) right as the roll takes over.
			_anim.WithVelocity( _faceDir * (RunTopSpeed * ramp01) );
		}

		if ( ShowSpeed && _body.IsValid() )
		{
			// Speed in units, plus a real-world mph treating the ball's diameter as RealBallInches.
			var v = _body.Velocity.Length;
			DebugOverlay.Text( WorldPosition + Vector3.Up * (Radius + 90f),
				$"{UnitsToMph( v ):F1} mph   ({v:F0} u/s)", 24, TextFlag.Center, Color.White );
		}
	}

	// Bowling-scale mph ↔ units, treating the ball's diameter as RealBallInches (1 mph = 17.6 in/s). The
	// HUD readout and the roll threshold both go through these so "11.5 mph" means exactly what's on screen.
	private float UnitsToMph( float units ) => units * (RealBallInches / (Radius * 2f)) / 17.6f;
	private float MphToUnits( float mph ) => mph * 17.6f * (Radius * 2f) / RealBallInches;

	// Hang Terry CENTRED in the ball, upright, facing the way it's travelling. The run/roll motion is all
	// in the animation (see OnUpdate) — his root just faces travel and sits on the ball centre.
	private void PlaceTerry()
	{
		if ( !_terry.IsValid() ) return;

		// Always face the direction the ball is actually rolling ("if he can" — keep the last heading while
		// it's nearly stationary so he doesn't spin in place). The Citizen's Roll animation rolls forward
		// along this facing, so facing travel keeps the roll going the right way.
		var travel = (_body.IsValid() ? _body.Velocity : Vector3.Zero).WithZ( 0f );
		if ( travel.Length > 5f )
			_faceDir = travel.Normal;
		var fwd = _faceDir.Length > 0.001f ? _faceDir.Normal : Vector3.Forward;

		_terry.WorldRotation = Rotation.LookAt( fwd, Vector3.Up );

		// Hang his PELVIS on the ball centre, not his feet. His origin is at his feet, but each clip (walk,
		// run, the curled roll) holds the pelvis a different height above that origin — pinning the feet let
		// that per-clip difference bob his whole body up/down through the transitions. So we read the pelvis's
		// current height above the origin and offset by THAT, which cancels the shift and keeps his middle
		// framed dead-centre regardless of which animation is playing. (Falls back to the static half-height
		// if the bone isn't resolvable yet.)
		var pelvisUp = _bodyCentreUp;
		if ( _terryRenderer.IsValid() && _terryRenderer.TryGetBoneTransformLocal( "pelvis", out var pelvisLocal ) )
			pelvisUp = pelvisLocal.Position.z * TerryScale;
		_terry.WorldPosition = WorldPosition - Vector3.Up * pelvisUp;
	}

	// DEBUG: log every ball↔pin contact and its impulse. This is the crux diagnostic — if the ball
	// "isn't affected" by the pins, either no contacts fire (contact generation failing) or they fire
	// with near-zero Impulse. Both are visible here. NormalSpeed = closing speed along the contact
	// normal; Impulse = the actual momentum exchanged at the contact.
	public void OnCollisionStart( Collision c )
	{
		RecordPinContact( c );
		LogPinContact( "START", c );
	}
	public void OnCollisionUpdate( Collision c ) => LogPinContact( "UPDATE", c );

	// Which pins THIS ball has directly touched this throw — used to tell a pocket hit (drove between
	// the 1 and the 2/3) from a Brooklyn/high hit. Cleared each ball by the director (ResetContacts).
	private readonly HashSet<int> _contacts = new();

	private void RecordPinContact( Collision c )
	{
		var pin = c.Other.GameObject.IsValid() ? c.Other.GameObject.Components.Get<Pin>() : null;
		if ( pin.IsValid() ) _contacts.Add( pin.Number );
	}

	/// <summary>True if this throw drove the pocket: the ball directly contacted the head pin (1) AND
	/// the 2 or 3 (either side of the pocket). A flush pocket strike, not a high hit or a wide miss.</summary>
	public bool PocketHit => _contacts.Contains( 1 ) && (_contacts.Contains( 2 ) || _contacts.Contains( 3 ));

	/// <summary>Clear the recorded pin contacts so the next throw starts fresh.</summary>
	public void ResetContacts() => _contacts.Clear();

	private void LogPinContact( string phase, Collision c )
	{
		if ( !(Director?.LogPinCrash ?? false) ) return; // gated behind the director's Debug checkbox

		var other = c.Other.GameObject;
		if ( !other.IsValid() || !other.Name.StartsWith( "Pin" ) ) return;

		var pinBody = c.Other.Body;
		var con = c.Contact;
		Log.Info( $"[terryball][contact:{phase}] {other.Name} impulse={con.Impulse:F1} normalSpd={con.NormalSpeed:F0} " +
			$"ballSpd={(_body.IsValid() ? _body.Velocity.Length : 0):F0} ballMass={(_body.IsValid() ? _body.Mass : 0):F2} " +
			$"pinSpd={(pinBody.IsValid() ? pinBody.Velocity.Length : 0):F0} pinMass={(pinBody.IsValid() ? pinBody.Mass : 0):F2} " +
			$"normal={con.Normal} pt={con.Point}" );
	}
}
