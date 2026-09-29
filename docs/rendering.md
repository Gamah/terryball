# Rendering

## Engine facts

Checked against `../sbox-public`. Paths are relative to it.

- Engine shadows carry no colour. A receiver's shadow term is a scalar visibility
  (`DirectionalLightShadow::GetVisibility( worldPos, screenPos )`, 1 = lit, 0 = shadowed, in
  `game/core/shaders/Shadows/DirectionalLightShadow.hlsl`, reached through `common/Shadow.hlsl`), and no
  light component has a shadow-colour or tint property.
- The glass shader barely casts. Its `S_MODE_DEPTH` pass computes a Fresnel opacity and calls
  `OpaqueFadeDepth`, which dithers shadow depth away by that opacity
  (`game/addons/base/Assets/shaders/glass.shader`, ~L177–186). A glass object that needs a solid shadow
  needs an opaque proxy with `ModelRenderer.RenderType = ShadowRenderType.ShadowsOnly`.
- Light cookies are spot-only on components: `SpotLight.Cookie`
  (`engine/Sandbox.Engine/Scene/Components/Light/SpotLight.cs`). `DirectionalLight` and `PointLight`
  expose none. `GetCookieUV` in `game/addons/base/Assets/shaders/common/lightbinner.hlsl` is a perspective
  cone projection.
- A spot light's shadow frustum runs from 1 to `Radius`
  (`engine/Sandbox.Engine/Systems/Render/Shadows/ShadowMapper.Projected.cs`), so an oversized `Radius`
  spreads the shadow map thin and blurs the shadow.
- Sun shadow resolution is set by the global convars `r.shadows.csm.maxresolution` (default 2048) and
  `r.shadows.csm.distance` (default 15000), in `engine/Sandbox.Engine/Systems/Render/Shadows/ShadowMapper.cs`.
- Many overlapping shadow-casting lights wash out depth shadows: a point shadowed from one light is lit
  by its neighbours.

## Coloured ball shadows

The ball's shadow shows the ball's own colours, spatially: a red/blue ball casts a red/blue shadow.
Three pieces:

- `ShadowBall` (on each ball, added by `BallController`): radius, colour map, tint, and whether an
  opaque occupant rides inside. `BallController` hands it the same design texture the glass shell renders.
  `ColorMapPath` is resolved by the post-process rather than in `OnEnabled`, where `Texture.Load` can
  return null [SOURCE].
- `BallShadowTintPP` (a `BasePostProcess` on the scene camera): gathers up to `MaxBalls` (4) active
  `ShadowBall`s and up to `MaxLights` `PointLight`s, sets attributes, and blits
  `shaders/ballshadow_tint.shader` at `Stage.AfterOpaque`, where the shadow-map and light constants are
  still bound [SOURCE]. It also sets the two sun-cascade convars above (`CascadeResolution`,
  `CascadeDistance`), which are global.
- `ballshadow_tint.shader`: a fullscreen pass that reconstructs world position from depth.

Per pixel, for the sun:

1. For each ball, an analytic mask: does the ray toward the sun pass within the ball's radius?
   The ball's shadow shape is this analytic disc, not the engine's shadow map (the glass casts almost
   nothing there).
2. Colour: the ball's map sampled at both points where that ray crosses the sphere (inverse-rotated by
   the ball's world rotation, equirectangular UV), averaged. Overlapping balls multiply (subtractive).
3. Occupant: under a ball with `HasOpaqueOccupant`, where the engine's real sun shadow is above
   `TerryCut`, the colour goes to `OccupantDark`. Terry is opaque, so he does cast a real engine shadow.
4. Desaturate by `Saturation`, then blend multiply ↔ colorize by `Colorize`, scaled by the mask and
   `Strength`.

Per point light, the same analytic ball-colour shadow filtered by the light's colour and faded to zero at
its `Radius`, plus Terry's silhouette: `OccupantSilhouetteCapture` renders the `occupant`-tagged Terry
side-on to a texture (a disabled `CameraComponent` driven by `RenderToTexture`, so it never joins the
screen render list [SOURCE]), and the shader billboards that cutout toward each light through the ball
centre.

Contracts between the C# and the shader:

- Four unrolled ball slots (`ShadowBallA/T/R/Tex0..3`); `MaxBalls` must match. An empty slot has radius 0.
- `struct ShadowLight` (two `float4`: position + range, colour) must match `ShadowLightData`.
- `DebugMode` values match the shader's `g_nDebugMode` branches (6 = final).
- The shader reads `ColorBuffer` with `SrgbRead( false )`: at `AfterOpaque` it is linear HDR, and the pass writes
  every pixel back, so an sRGB decode darkens the whole frame.
- The shader source must be ASCII with balanced section braces, or the vfx parser fails to load it [SOURCE].
- The occupant capture frames `centre ± radius` with feet at the bottom; the shader assumes the same frame.

## Sky brightness

`SkyBrightnessApplier` (on the client-local `LocalHud`) applies `WorldPrefs.SkyBrightness` (a per-client
cookie set at the SETTINGS pedestal). It captures each `DirectionalLight`'s `LightColor` and `SkyColor`
and each `SkyBox2D`'s `Tint` the first time it sees them, and always writes authored × scale (RGB only),
so repeated applies never compound. It reapplies when `WorldPrefs.Revision` changes and on a 0.5 s sweep
that catches lights created after the first apply. Nothing replicates; each client dims only its own view.
