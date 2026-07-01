using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Live-captures the ball's opaque occupant (Terry) as a silhouette texture: a side-on ORTHOGRAPHIC camera
/// renders ONLY the tagged occupant (whitelist) into a render target, framed exactly to the ball's bounds
/// (2*radius, centred on the ball). BallShadowTintPP projects that texture along each coloured light, so Terry
/// casts his ACTUAL shape as a shadow - his real, live pose - for any light direction.
///
/// The camera is a DISABLED CameraComponent driven by an on-demand <see cref="CameraComponent.RenderToTexture"/>
/// call each frame - NOT a live scene camera. A live/enabled second camera hijacks the main screen/HUD (it goes
/// through OnEnabled -> UpdateMainCamera and joins the screen render list); a disabled one never enters that path
/// (AddToRenderList early-outs on !Active) yet still renders on demand via RenderToTexture. That is the crucial
/// difference from the earlier attempt that had to be reverted.
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

		// A DISABLED camera: it never joins the screen render list or becomes the main camera (no hijack), but
		// RenderToTexture still renders it on demand. Created with startEnabled=false so its OnEnabled ->
		// UpdateMainCamera never runs.
		var camGo = new GameObject( true, "OccupantSilhouetteCam" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		_cam = camGo.Components.Create<CameraComponent>( false ); // startEnabled: false => stays out of the main render path
		_cam.Orthographic = true;
		_cam.BackgroundColor = Color.Black;                                    // black => luminance is the silhouette mask
		_cam.ClearFlags = ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil;
		_cam.RenderTags.Add( OccupantTag );                                    // whitelist: ONLY the occupant renders
		_cam.ZNear = 1f;
		_cam.ZFar = 5000f;
	}

	protected override void OnUpdate()
	{
		if ( !_cam.IsValid() || SilhouetteTexture is null ) return;
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

		// On-demand render of just the occupant into the RT. Called from OnUpdate (game thread, Graphics inactive)
		// so RenderToTexture self-initialises the scene camera - no live screen camera, no hijack.
		_cam.RenderToTexture( SilhouetteTexture );
	}

	protected override void OnDisabled()
	{
		if ( _cam.IsValid() )
			_cam.GameObject.Destroy();
		_cam = null;

		SilhouetteTexture?.Dispose();
		SilhouetteTexture = null;
	}
}
