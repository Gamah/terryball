using Sandbox;
using Sandbox.Citizen;
using System;
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

	/// <summary>When set, the ball ignores BOTH input and braking and just coasts on its own
	/// momentum (physics only). The round director flips this on once the ball crosses the throw
	/// line so the throw is committed to physics and you can watch it hit the pins.</summary>
	public bool ControlsLocked { get; set; }

	// --- Occupant (Terry) visual climb -------------------------------------------------
	// Terry riding the inner wall is purely visual now — it no longer drives the ball. He
	// "runs" up-slope toward wishDir and gravity settles him back to the bottom.

	/// <summary>How hard input drives Terry up the wall (visual only).</summary>
	[Property, Group( "Occupant" )] public float RunAccel { get; set; } = 8f;
	/// <summary>Gravity pull settling Terry toward the bottom while driving (visual only).</summary>
	[Property, Group( "Occupant" )] public float RestoreAccel { get; set; } = 1f;
	/// <summary>Damping on Terry's motion along the surface while driving (visual only).</summary>
	[Property, Group( "Occupant" )] public float Damping { get; set; } = 3f;
	/// <summary>On release, Terry is pulled back to the bottom hard with near-critical damping
	/// (≈2·√restore) so he drops back down promptly without overshoot (visual only).</summary>
	[Property, Group( "Occupant" )] public float ReleaseRestore { get; set; } = 25f;
	[Property, Group( "Occupant" )] public float ReleaseDamping { get; set; } = 10f;

	[Property, Group( "Debug" )] public bool ShowSpeed { get; set; } = true;
	/// <summary>Real ball size used only to report a bowling-scale mph on the HUD.</summary>
	[Property, Group( "Debug" )] public float RealBallInches { get; set; } = 8.5f;

	private Rigidbody _body;
	private GameObject _terry;
	private SkinnedModelRenderer _terryRenderer;
	private CitizenAnimationHelper _anim;
	private Vector3 _terryPrevPos;

	// In-ball Terry is dressed in the ACTIVE bowler's avatar so everyone sees who's up riding inside.
	// Re-dressed (on every client) whenever the spotlight moves to a different bowler.
	private Guid _dressedBowler = Guid.Empty;
	private bool _dressedAny;
	private CancellationTokenSource _dressCts;
	private GameDirector _director;
	// Resolve within our own lane (the director sharing our Lane root), never a scene-global scan, so
	// each lane's ball answers only to its own director.
	private GameDirector Director => _director.IsValid() ? _director : (_director = Lane.Of( this )?.Director);

	// Occupant state, in world space: _dir is the unit direction from ball centre to Terry
	// (starts straight down); _vel is his velocity along the sphere surface.
	private Vector3 _dir = Vector3.Down;
	private Vector3 _vel = Vector3.Zero;
	private Vector3 _faceDir = Vector3.Forward; // last meaningful heading (ball travel dir)
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

		// Body params go AFTER the collider: adding a collider shape rebuilds mass from
		// volume (BuildMass), which would otherwise clobber our MassOverride.
		_body.AngularDamping = AngularDamping;
		_body.MassOverride = Mass; // realistic bowling-ball mass vs the pins
		_body.EnhancedCcd = true;  // don't tunnel through the pins at speed

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

		_anim = _terry.Components.GetOrCreate<CitizenAnimationHelper>();
		_anim.Target = renderer;
		_anim.IsGrounded = true;

		PlaceTerry();
		_terryPrevPos = _terry.WorldPosition;
	}

	// Keep the in-ball Terry wearing whoever is currently up (runs on every client, bowler or
	// spectator, so all four feeds show the active bowler's avatar inside the ball).
	private void UpdateBowlerDress()
	{
		var id = Director?.ActiveBowlerId ?? Guid.Empty;

		// An unoccupied ball (nobody up — lobby, between turns, game over) shows no Terry inside.
		if ( _terry.IsValid() )
			_terry.Enabled = id != Guid.Empty;

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

		_dressCts?.Cancel();
		_dressCts = new CancellationTokenSource();
		try
		{
			await clothing.ApplyAsync( _terryRenderer, _dressCts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception e )
		{
			Log.Warning( $"[terryball] in-ball dressing failed: {e.Message}" );
		}
	}

	protected override void OnDisabled() => _dressCts?.Cancel();

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

		// --- Terry's visual climb (no longer drives the ball) --------------------------
		// Spherical-pendulum on the inner surface: input pushes Terry up-slope toward wishDir,
		// gravity's tangential component pulls him to the bottom, damping settles him. A
		// snappier restore on release drops him back down promptly.
		var tGrav = Vector3.Down - Vector3.Down.Dot( _dir ) * _dir;
		var tInput = wish - wish.Dot( _dir ) * _dir;
		var restore = hasInput ? RestoreAccel : ReleaseRestore;
		var damp = hasInput ? Damping : ReleaseDamping;
		var accel = restore * tGrav + RunAccel * tInput - damp * _vel;
		_vel += accel * dt;
		_dir = (_dir + _vel * dt).Normal;
		_vel -= _vel.Dot( _dir ) * _dir; // keep velocity tangent to the sphere

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

		// Drive Terry's run animation from how fast he's actually moving through the world
		// (ball roll + his own climbing), so the legs match the speed you see.
		if ( _anim.IsValid() && Time.Delta > 0f )
		{
			var worldVel = (_terry.WorldPosition - _terryPrevPos) / Time.Delta;
			_anim.WithVelocity( worldVel );
		}
		_terryPrevPos = _terry.WorldPosition;

		if ( ShowSpeed && _body.IsValid() )
		{
			// Speed in units, plus a real-world mph treating the ball's diameter as 8.5".
			var v = _body.Velocity.Length;
			var inchesPerUnit = RealBallInches / (Radius * 2f);
			var mph = v * inchesPerUnit / 17.6f; // 1 mph = 17.6 in/s
			DebugOverlay.Text( WorldPosition + Vector3.Up * (Radius + 90f),
				$"{mph:F1} mph   ({v:F0} u/s)", 24, TextFlag.Center, Color.White );
		}
	}

	private void PlaceTerry()
	{
		if ( !_terry.IsValid() ) return;

		// Terry's origin is at his feet, so plant his feet just inside the shell.
		var innerR = Radius - 2f;
		_terry.WorldPosition = WorldPosition + _dir * innerR;

		// Face the direction the ball is actually rolling ("if he can" — keep the last
		// heading while it's nearly stationary so he doesn't spin). "up" is the inward
		// normal (-_dir), so he leans into the curve as he climbs.
		var up = -_dir;
		var travel = _body.IsValid() ? _body.Velocity : Vector3.Zero;
		travel -= travel.Dot( up ) * up; // onto his standing plane
		if ( travel.Length > 5f )
			_faceDir = travel.Normal;

		var fwd = _faceDir - _faceDir.Dot( up ) * up;
		fwd = fwd.Length > 0.001f ? fwd.Normal : Vector3.Forward;
		_terry.WorldRotation = Rotation.LookAt( fwd, up );
	}

	// DEBUG: log every ball↔pin contact and its impulse. This is the crux diagnostic — if the ball
	// "isn't affected" by the pins, either no contacts fire (contact generation failing) or they fire
	// with near-zero Impulse. Both are visible here. NormalSpeed = closing speed along the contact
	// normal; Impulse = the actual momentum exchanged at the contact.
	public void OnCollisionStart( Collision c ) => LogPinContact( "START", c );
	public void OnCollisionUpdate( Collision c ) => LogPinContact( "UPDATE", c );

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
