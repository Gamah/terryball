using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Live-captures the ball's opaque occupant (Terry) as a silhouette texture: a side-on ORTHOGRAPHIC camera
/// that renders ONLY the tagged occupant (whitelist) into a render target, framed exactly to the ball's bounds
/// (2*radius, centred on the ball). BallShadowTintPP projects that texture along each coloured light, so Terry
/// casts his ACTUAL shape as a shadow - his real, live pose - for any light direction.
///
/// The framing (centred on the ball, height 2*radius, world-up) matches what the shader assumes, so no capture
/// matrix needs to cross over - the shader projects using the ball's own centre/radius. Lives at the scene root.
/// </summary>
[Title( "Occupant Silhouette Capture" )]
[Category( "Terryball" )]
[Icon( "accessibility_new" )]
public sealed class OccupantSilhouetteCapture : Component
{
	/// <summary>Render-target square resolution (px).</summary>
	[Property] public int Resolution { get; set; } = 256;

	/// <summary>Tag the capture camera renders (and only that). BallController tags Terry with this.</summary>
	[Property] public string OccupantTag { get; set; } = "occupant";

	/// <summary>The captured silhouette (Terry lit on black). Read by BallShadowTintPP.</summary>
	public Texture SilhouetteTexture { get; private set; }

	private CameraComponent _cam;
	private BallController _ball;

	protected override void OnEnabled()
	{
		SilhouetteTexture = Texture.CreateRenderTarget()
			.WithSize( Resolution, Resolution )
			.WithFormat( ImageFormat.RGBA8888 )
			.Create( "TerrySilhouette" );

		var camGo = new GameObject( true, "OccupantSilhouetteCam" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		_cam = camGo.Components.Create<CameraComponent>();
		_cam.Orthographic = true;
		_cam.BackgroundColor = Color.Black;                                    // black => luminance is the silhouette mask
		_cam.ClearFlags = ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil;
		_cam.RenderTags.Add( OccupantTag );                                    // whitelist: ONLY the occupant renders
		_cam.RenderTarget = SilhouetteTexture;
		_cam.ZNear = 1f;
		_cam.ZFar = 5000f;
		_cam.Priority = -100;                                                  // render into the RT before the main view samples it
	}

	protected override void OnUpdate()
	{
		if ( !_cam.IsValid() ) return;
		if ( !_ball.IsValid() )
			_ball = Scene.GetAllComponents<BallController>().FirstOrDefault( b => b.IsValid() );
		if ( !_ball.IsValid() ) return;

		var center = _ball.WorldPosition;
		var r = _ball.Radius;

		// Side-on: sit off the ball's +Y, look back at it, world-up so Terry stays upright in-frame. Ortho, so
		// distance doesn't change scale - only the height (2*radius) frames him to the ball's bounds.
		var camPos = center + Vector3.Left * (r * 6f);
		_cam.WorldPosition = camPos;
		_cam.WorldRotation = Rotation.LookAt( (center - camPos).Normal, Vector3.Up );
		_cam.OrthographicHeight = r * 2f;
	}

	protected override void OnDisabled()
	{
		SilhouetteTexture?.Dispose();
		SilhouetteTexture = null;
	}
}
