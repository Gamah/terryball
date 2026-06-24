using Sandbox;
using Sandbox.Citizen;

namespace Terryball;

/// <summary>
/// The hamster-ball mechanic.
///
/// A large glass ball (Rigidbody + SphereCollider) with Terry (the Citizen) riding the
/// INSIDE surface. WASD (camera-relative) makes Terry "run" up the inner wall like a
/// hamster; gravity always pulls him back toward the bottom, so he settles there on his
/// own. Terry's offset from the bottom is turned into the roll torque an off-centre weight
/// there would produce (NOT by moving the ball's mass-centre — that sank the ball through
/// the floor); the torque vanishes once he's back at the bottom, so releasing input coasts.
///
/// Terry is purely visual here (no Terry↔ball collision is simulated, by design); his run
/// animation is driven from how fast he's actually moving through the world. The standalone
/// <see cref="TerryController"/> stub is kept in the repo for other reuse.
/// </summary>
public sealed class BallController : Component
{
	[Property, Group( "Ball" )] public float Radius { get; set; } = 100f;
	[Property, Group( "Ball" )] public float Friction { get; set; } = 1.0f;
	/// <summary>Rolling resistance: how fast the ball's spin bleeds off when it isn't being
	/// driven. The drive only ever adds forward torque, so this is the only thing that slows a
	/// coasting ball — too low and a single tap rolls almost forever. Higher also lowers top
	/// speed, so re-check the mph HUD / bump <see cref="DriveGain"/> if a hard throw feels weak.</summary>
	[Property, Group( "Ball" )] public float AngularDamping { get; set; } = 0.6f;
	/// <summary>Mass in kg. A regulation bowling ball is ~6.8 kg (15 lb).</summary>
	[Property, Group( "Ball" )] public float Mass { get; set; } = 6.8f;

	[Property, Group( "Occupant" )] public float TerryScale { get; set; } = 1.5f;

	/// <summary>How hard input drives the occupant up the wall (rad/s²-ish).</summary>
	[Property, Group( "Drive" )] public float RunAccel { get; set; } = 8f;
	/// <summary>How hard gravity pulls the occupant back toward the bottom while driving.</summary>
	[Property, Group( "Drive" )] public float RestoreAccel { get; set; } = 1f;
	/// <summary>Damping on the occupant's motion along the surface while driving.</summary>
	[Property, Group( "Drive" )] public float Damping { get; set; } = 3f;
	/// <summary>When you release the controls, the occupant has to slide back to the bottom
	/// before the roll torque dies — until then the ball keeps being driven. A strong restore
	/// with near-critical damping (≈2·√restore) snaps him back fast without overshoot, so
	/// letting go promptly settles Terry and stops the drive.</summary>
	[Property, Group( "Drive" )] public float ReleaseRestore { get; set; } = 25f;
	[Property, Group( "Drive" )] public float ReleaseDamping { get; set; } = 10f;
	/// <summary>Fraction of Radius the off-centre weight sits at (0..1) — the roll lever.</summary>
	[Property, Group( "Drive" ), Range( 0f, 1f )] public float MassOffsetFactor { get; set; } = 0.7f;
	/// <summary>Stylized multiplier on the roll torque. Impact speed scales ~√(this), so
	/// ~6.25× ≈ 2.5× faster. Tune against the MPH readout. (Raised alongside AngularDamping:
	/// top speed ∝ DriveGain/AngularDamping, so more rolling resistance needs more drive.)</summary>
	[Property, Group( "Drive" )] public float DriveGain { get; set; } = 20f;

	[Property, Group( "Debug" )] public bool ShowSpeed { get; set; } = true;
	/// <summary>Real ball size used only to report a bowling-scale mph on the HUD.</summary>
	[Property, Group( "Debug" )] public float RealBallInches { get; set; } = 8.5f;

	private Rigidbody _body;
	private GameObject _terry;
	private CitizenAnimationHelper _anim;
	private Vector3 _terryPrevPos;

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

		_anim = _terry.Components.GetOrCreate<CitizenAnimationHelper>();
		_anim.Target = renderer;
		_anim.IsGrounded = true;

		PlaceTerry();
		_terryPrevPos = _terry.WorldPosition;
	}

	protected override void OnFixedUpdate()
	{
		var dt = Time.Delta;
		if ( dt <= 0f ) return;

		// Camera-relative run direction on the horizontal plane.
		var move = Input.AnalogMove; // x = forward, y = left
		var wish = Vector3.Zero;
		var cam = Scene.Camera;
		if ( cam.IsValid() )
		{
			var rot = cam.WorldRotation;
			var f = rot.Forward.WithZ( 0f );
			var l = rot.Left.WithZ( 0f );
			if ( f.Length > 0.001f ) f = f.Normal;
			if ( l.Length > 0.001f ) l = l.Normal;
			wish = f * move.x + l * move.y;
		}
		if ( wish.Length > 1f ) wish = wish.Normal;

		// Spherical-pendulum dynamics on the inner surface.
		// Gravity's tangential component pulls the occupant downhill (toward the bottom);
		// input pushes it up-slope in the wish direction. Both are projected onto the
		// sphere's tangent plane at the current position.
		var tGrav = Vector3.Down - Vector3.Down.Dot( _dir ) * _dir;
		var tInput = wish - wish.Dot( _dir ) * _dir;

		// With no input, pull the occupant back to the bottom hard so the roll torque dies
		// quickly — otherwise he coasts around the wall and the ball keeps driving after release.
		var hasInput = wish.Length > 0.01f;
		var restore = hasInput ? RestoreAccel : ReleaseRestore;
		var damp = hasInput ? Damping : ReleaseDamping;

		var accel = restore * tGrav + RunAccel * tInput - damp * _vel;
		_vel += accel * dt;
		_dir = (_dir + _vel * dt).Normal;
		_vel -= _vel.Dot( _dir ) * _dir; // keep velocity tangent to the sphere

		// Roll the ball with the gravitational torque an off-centre weight at Terry's
		// position would create — WITHOUT moving the mass centre. (Updating MassCenterOverride
		// every frame injected spurious motion as Terry settled, sinking the ball through the
		// floor.) τ = r × (m·g); it's zero when Terry is at the bottom, so the ball settles.
		var rOffset = _dir * (Radius * MassOffsetFactor);
		var torque = Vector3.Cross( rOffset, Mass * Scene.PhysicsWorld.Gravity ) * DriveGain;
		_body.ApplyTorque( torque );

		// Keep the body awake while input is held or it's still moving.
		if ( wish.Length > 0.01f || _body.Velocity.LengthSquared > 1f )
			_body.Sleeping = false;
	}

	protected override void OnUpdate()
	{
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
}
