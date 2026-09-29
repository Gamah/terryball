// ScenePanel's low-level SceneCamera/World accessors are [Obsolete] but are still the way to render a
// manual SceneWorld to a panel — and crucially they DON'T create a scene CameraComponent, so this preview
// can't hijack the main game view/controls (which a second runtime camera does). Suppress the warnings.
#pragma warning disable CS0612
#pragma warning disable CS0618

using Sandbox;
using Sandbox.UI;

namespace Terryball;

/// <summary>
/// A live 3-D preview of a <see cref="BallDesign"/> for the designer screen. It renders a slowly
/// spinning ball into a UI panel using its OWN <see cref="SceneWorld"/> and a low-level
/// <see cref="SceneCamera"/> (via <see cref="ScenePanel"/>) — NOT a scene <c>CameraComponent</c>, so it
/// can't hijack the main game view/controls the way a second runtime camera does. Feed it a design with
/// <see cref="SetDesign"/> and it rebuilds the ball's colour map to match, instantly.
/// </summary>
public sealed class BallPreviewPanel : ScenePanel
{
	private SceneWorld _world;
	private SceneModel _ball;
	private Material _material;
	private Texture _colorMap;
	private BallDesign _current;
	private bool _hasDesign;
	private float _spin;

	public BallPreviewPanel()
	{
		_world = new SceneWorld();

		// Reproduce the GAME scene's environment exactly (its Sun + 2D Skybox + Envmap probe) so the glass
		// shell reads the same translucent, sky-reflecting way it does in-world — a bare SceneWorld gives
		// glass nothing to reflect and it renders near-black.
		_ = new SceneSkyBox( _world, Material.Load( "materials/skybox/skybox_day_01.vmat" ) );
		_ = new SceneCubemap( _world, Texture.Load( "textures/cubemaps/default2.vtex" ),
			BBox.FromPositionAndSize( Vector3.Zero, int.MaxValue ) );
		// The scene's Sun: rotation + colour copied from main.scene's DirectionalLight.
		_ = new SceneDirectionalLight( _world, new Rotation( -0.27f, 0.27f, 0.65f, 0.65f ),
			new Color( 0.94419f, 0.97767f, 1f ) ) { ShadowsEnabled = false };
		_world.AmbientLightColor = new Color( 0.2532f, 0.32006f, 0.35349f ); // the Sun's SkyColor term

		// The REAL ball material — a copy of the in-world glass shell (translucency intact) so the preview
		// is an exact copy of the actual ball. SetDesign swaps its colour map to the chosen design.
		_material = Material.Load( "materials/glassball_poke.vmat" ).CreateCopy();

		_ball = new SceneModel( _world, "models/dev/sphere.vmdl", Transform.Zero );
		_ball.SetMaterialOverride( _material );

		// Frame the dev sphere (radius ~32): camera pulled back on -X looking at the origin.
		Camera.World = _world;
		Camera.Position = new Vector3( -110, 0, 8 );
		Camera.Rotation = Rotation.FromYaw( 0 );
		Camera.FieldOfView = 45;
		Camera.BackgroundColor = Color.Transparent; // let the skybox fill the backdrop
		Camera.ZNear = 1f;
		Camera.ZFar = 100000f; // the skybox sits at the far plane
	}

	/// <summary>Rebuild the preview ball's colour map to <paramref name="d"/> (no-op if unchanged).</summary>
	public void SetDesign( BallDesign d )
	{
		if ( _hasDesign && d.Equals( _current ) ) return;
		_current = d;
		_hasDesign = true;

		_colorMap?.Dispose();
		_colorMap = d.BuildColorMap();
		_material?.Set( "g_tColor", _colorMap );
	}

	public override void Tick()
	{
		// Idle spin so the whole ball can be inspected.
		_spin += RealTime.Delta * 35f;
		if ( _ball.IsValid() )
			_ball.Rotation = Rotation.FromYaw( _spin ) * Rotation.FromPitch( -12 );

		base.Tick(); // renders the world to the panel texture
	}

	public override void Delete( bool immediate = false )
	{
		_ball?.Delete();
		_world?.Delete();
		_colorMap?.Dispose();
		_ball = null;
		_world = null;
		_colorMap = null;
		base.Delete( immediate );
	}
}
