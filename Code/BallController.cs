using Sandbox;

namespace Terryball;

/// <summary>
/// The hamster-ball mechanic.
///
/// A large glass ball (Rigidbody + SphereCollider) with a primitive cube riding the
/// INSIDE surface. WASD (camera-relative) makes the cube "run" up the inner wall like a
/// hamster; gravity always pulls it back toward the bottom, so it settles there on its
/// own. The cube's offset is fed into the ball's mass-center, and real physics rolls the
/// ball forward as it tries to bring that centre of gravity back under the contact point.
///
/// The cube is purely visual (no cube↔ball collision is simulated, by design). Swapping
/// the cube for Terry later is just a matter of changing the occupant model + a run anim;
/// the driver below is model-agnostic. See <see cref="TerryController"/> (kept for reuse).
/// </summary>
public sealed class BallController : Component
{
	[Property, Group( "Ball" )] public float Radius { get; set; } = 100f;
	[Property, Group( "Ball" )] public float Friction { get; set; } = 1.0f;
	[Property, Group( "Ball" )] public float AngularDamping { get; set; } = 0.5f;

	[Property, Group( "Occupant" )] public float CubeSize { get; set; } = 26f;
	[Property, Group( "Occupant" )] public Color CubeColor { get; set; } = new Color( 0.92f, 0.45f, 0.20f, 1f );

	/// <summary>How hard input drives the occupant up the wall (rad/s²-ish).</summary>
	[Property, Group( "Drive" )] public float RunAccel { get; set; } = 8f;
	/// <summary>How hard gravity pulls the occupant back toward the bottom.</summary>
	[Property, Group( "Drive" )] public float RestoreAccel { get; set; } = 1f;
	/// <summary>Damping on the occupant's motion along the surface.</summary>
	[Property, Group( "Drive" )] public float Damping { get; set; } = 3f;
	/// <summary>Fraction of Radius the mass-centre shifts toward the occupant (0..1).</summary>
	[Property, Group( "Drive" ), Range( 0f, 1f )] public float MassOffsetFactor { get; set; } = 0.5f;

	private Rigidbody _body;
	private GameObject _cube;

	// Occupant state, in world space: _dir is the unit direction from ball centre to the
	// occupant (starts straight down); _vel is its velocity along the sphere surface.
	private Vector3 _dir = Vector3.Down;
	private Vector3 _vel = Vector3.Zero;
	private bool _built;

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Physics: dynamic sphere that grips the floor so it rolls instead of sliding.
		_body = Components.GetOrCreate<Rigidbody>();
		_body.AngularDamping = AngularDamping;
		_body.OverrideMassCenter = true;
		_body.MassCenterOverride = Vector3.Down * (Radius * MassOffsetFactor);

		var col = Components.GetOrCreate<SphereCollider>();
		col.Radius = Radius;
		col.Friction = Friction;

		// Visual: scaled dev sphere with the glass material so the cube inside shows through.
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

		// Occupant: a primitive cube. Its world transform is driven every frame, so the
		// ball's spin never carries it — it rides the inner surface independently.
		_cube = new GameObject( true, "Cube" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		var cubeRenderer = _cube.Components.GetOrCreate<ModelRenderer>();
		cubeRenderer.Model = Model.Load( "models/dev/box.vmdl" );
		cubeRenderer.Tint = CubeColor;

		var boxSize = cubeRenderer.Model.Bounds.Size; // dev box is unit-ish; scale to CubeSize
		_cube.LocalScale = new Vector3(
			CubeSize / boxSize.x,
			CubeSize / boxSize.y,
			CubeSize / boxSize.z );

		PlaceCube();
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

		var accel = RestoreAccel * tGrav + RunAccel * tInput - Damping * _vel;
		_vel += accel * dt;
		_dir = (_dir + _vel * dt).Normal;
		_vel -= _vel.Dot( _dir ) * _dir; // keep velocity tangent to the sphere

		// Feed the occupant's offset into the ball's mass-centre (in body-local space, so it
		// tracks correctly as the ball rolls). Off-centre CG → gravity torque → the ball rolls.
		var localOffset = WorldRotation.Inverse * (_dir * (Radius * MassOffsetFactor));
		_body.MassCenterOverride = localOffset;

		// Keep the body awake while the CG is off the bottom or input is held.
		if ( wish.Length > 0.01f || _dir.Dot( Vector3.Down ) < 0.999f )
			_body.Sleeping = false;
	}

	protected override void OnUpdate()
	{
		PlaceCube();
	}

	private void PlaceCube()
	{
		if ( !_cube.IsValid() ) return;

		var innerR = Radius - CubeSize * 0.5f - 2f;
		_cube.WorldPosition = WorldPosition + _dir * innerR;

		// Stand the occupant on the wall: "up" is the inward normal (-_dir), facing its travel
		// direction. For a cube this is cosmetic; for Terry later it gives a natural lean.
		var up = -_dir;
		var fwd = _vel.Length > 0.01f ? _vel.Normal : Vector3.Forward;
		fwd -= fwd.Dot( up ) * up;
		fwd = fwd.Length > 0.001f ? fwd.Normal : Vector3.Forward;
		_cube.WorldRotation = Rotation.LookAt( fwd, up );
	}
}
