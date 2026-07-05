using Sandbox;
using System;
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
	/// <summary>Render-target square resolution (px). Higher = smoother silhouette edge (less blocky).</summary>
	[Property] public int Resolution { get; set; } = 1024;

	/// <summary>Tag the capture camera renders (and only that). BallController tags Terry with this.</summary>
	[Property] public string OccupantTag { get; set; } = "occupant";

	/// <summary>Tag we stamp onto every WorldPanel and exclude from the capture. WorldPanels IGNORE the
	/// RenderTags whitelist (they render into any camera), so the only way to keep the lane's HUD panels out of
	/// Terry's silhouette is to exclude them explicitly - their GameObject tags propagate to the panel scene
	/// object, which the camera's ExcludeTags then filters.</summary>
	[Property] public string PanelExcludeTag { get; set; } = "no_silhouette";

	/// <summary>The captured silhouette (Terry lit on black). Read by BallShadowTintPP.</summary>
	public Texture SilhouetteTexture { get; private set; }

	private CameraComponent _cam;
	private BallController _ball;

	protected override void OnEnabled()
	{
		// Headless (dedicated server) has no render device — RenderToTexture null-derefs
		// (CCameraRenderer::RenderToTexture -> ACCESS_VIOLATION 0x0). This capture is client-only visual, so skip it.
		if ( Application.IsHeadless ) return;

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
		_cam.RenderTags.Add( OccupantTag );                                    // whitelist: ONLY the occupant renders (3D geometry)
		_cam.RenderExcludeTags.Add( PanelExcludeTag );                         // ...and never the HUD WorldPanels (tagged in OnUpdate)
		// ZNear/ZFar are re-derived per frame in OnUpdate as a thin slab around the ball (see there).
	}

	// How far off the ball's side the camera sits, in ball radii. Ortho, so this only sets the near/far slab.
	private const float CamDistRadii = 3f;

	protected override void OnUpdate()
	{
		if ( !_cam.IsValid() || SilhouetteTexture is null ) return;
		if ( !_ball.IsValid() )
			_ball = Scene.GetAllComponents<BallController>().FirstOrDefault( b => b.IsValid() );
		if ( !_ball.IsValid() ) return;

		// WorldPanels bypass the RenderTags whitelist, so stamp them all with the exclude tag (idempotent) - it
		// propagates to their panel scene object, which the capture's ExcludeTags then keeps out of the render.
		// Cheap: a handful of panels, and Tags.Add is a no-op once present.
		foreach ( var wp in Scene.GetAllComponents<WorldPanel>() )
			wp.GameObject.Tags.Add( PanelExcludeTag );

		var center = _ball.WorldPosition;
		var r = _ball.Radius;

		// Side-on: sit off the ball's +Y, look back at it, world-up so Terry stays upright in-frame. Ortho, so
		// distance doesn't change scale - only the height (2*radius) frames him to the ball's bounds.
		var dist = r * CamDistRadii;
		var camPos = center + Vector3.Left * dist;
		_cam.WorldPosition = camPos;
		_cam.WorldRotation = Rotation.LookAt( (center - camPos).Normal, Vector3.Up );
		_cam.OrthographicHeight = r * 2f;

		// Clip to a thin slab that hugs just the ball's own depth. WorldPanels (lane monitors, the PINS
		// backstop, the announcer banner) IGNORE the RenderTags whitelist, so without this the wide far plane
		// swallows any panel sitting across the lane in the capture column and prints it into the shadow. The
		// slab is [dist - r, dist + r] (+5% pad so Terry's limbs aren't clipped) => nothing past the ball renders.
		_cam.ZNear = MathF.Max( 1f, dist - r * 1.05f );
		_cam.ZFar = dist + r * 1.05f;

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
