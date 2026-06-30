# Plan: green-tinted shadow under the galaxy ball

Status: **not started.** Picked up from a tuning session where we added the green galaxy
texture + translucency to the ball (`glassball.vmat`, `galaxyball_color.png`) and tried,
then **gutted**, a faked blob-shadow. This doc is the brief for doing it *properly* next.

## Goal

When the green translucent ball is lit by the lane `Sun`, the shadow it casts on the lane
should read **green-tinted** (the ball's hue), not flat gray — matching the way the world
already goes green when seen *through* the ball.

## Why the obvious approaches don't work (don't re-discover this)

- The green-through-the-ball look is **screen-space refraction** in `glass.shader`'s forward
  pass (samples the scene behind, tints by albedo). It has nothing to do with shadows.
- Shadows are a **depth/visibility** pass into a **shared shadow atlas**. A caster's depth
  shader only writes occlusion; there is no color channel. Receivers sample the atlas and get
  back a **single scalar** — see `Light::Shadows()` returning a `float` in
  `sbox-public/game/addons/base/Assets/shaders/common/classes/Light.hlsl` (~L113), and the
  glass shadow pass `OpaqueFadeDepth(...); return 1` in
  `sbox-public/game/addons/base/Assets/shaders/glass.shader` (~L172, the `S_MODE_DEPTH` block).
- Therefore you **cannot** "hijack the ball's shadow cast and add hue." Color must be added on
  the **receiving** surface (the lane floor), not the caster.

## The real approach: a transmission ("colored shadow") buffer

Standard stained-glass-shadow technique. Two new pieces: per-light render-to-textures of the
ball's transmittance from each light's POV, and a lane-floor shader that multiplies **each
light's** contribution by that light's transmittance sample.

### Color model: light hue × ball hue (combine, don't replace)

The shadow color is **not** "the ball's green." It's the light's own color **filtered through** the
ball — i.e. `transmittedRadiance_i = lightColor_i × ballTransmittance`, exactly like stained glass.
This falls out for free from the per-light design below, because the receiver multiplies *that
light's radiance* (which already carries the light's hue) by the ball's transmittance:

- The buffer stores the ball's **transmittance** (how much of each RGB channel *passes*), **not** a
  pre-baked shadow color. `white (1,1,1)` = fully transparent (light passes unchanged),
  `black (0,0,0)` = fully opaque (a normal gray shadow), a **green** value = passes green, blocks
  red/blue. Derive it from `galaxyball_color` × the glass translucency (opaque texels → darker
  transmittance, clear texels → greener/brighter).
- Result combines hues automatically: **white** sun → green shadow; **red** light → near-black
  (green glass eats red); **yellow** light → olive; a **green** light → bright green. No special
  per-light tint logic — the multiply does the mixing.
- This means a light's color and the ball's color genuinely *mix* in the footprint, and it stays
  correct when lights of different colors overlap (each contributes `lightColor_i × transmittance`).

> **Source-agnostic by design.** Do *not* hardwire the `Sun`. The mechanism must work for an
> arbitrary set of scene lights (a sun today, spots/points added later). Drive everything off
> "the shadow-casting lights that affect this lane," discovered at runtime — never a named light.
> The natural seam is the engine's own per-light loop (`lightbinner.hlsl` / `Light::Shadows()`),
> where colored transmission becomes "multiply this light's visibility scalar by this light's
> transmittance color." One light or ten, the receiver code is the same loop body.

1. **A transmittance RT per relevant light** (not one global buffer).
   - Enumerate the scene's shadow-casting lights that reach the lane at runtime; for **each**,
     derive its light-space view-projection from the light itself, by type — **directional** →
     orthographic aligned to its direction, framed to the lane bounds (`LaneBuilder`);
     **spot** → its perspective cone; **point** → a cube / the engine's point-shadow convention.
     Pull these from the light's transform/params, so adding a light "just works."
   - For each, render **only the ball** into an `RGBA` RT: where the ball covers a texel, store its
     green **transmittance** (tint toward `galaxyball_color` greens, alpha = coverage/opacity);
     clear to white (1,1,1) = "fully lets light through." A cheap custom ball material for this
     pass (output green×coverage) avoids the full glass shader.
   - **Budget the count.** N RTs for N lights gets expensive — cap it (e.g. the K nearest/strongest
     shadow-casters per lane); lights beyond the cap fall back to a normal gray shadow. Make K a
     tunable. Pack the RTs into an **atlas/array** indexed the same way the light loop indexes lights.
   - s&box hooks to evaluate: `CommandList` / `RenderHook` / `Graphics.RenderToTexture`, or an
     off-screen `CameraComponent` + render target with a layer/tag mask so only the ball draws.
   - Bind the RT array + each light's world→light matrix as **global shader attributes**, keyed by
     light index, so the receiver can look up "light i's transmittance buffer + matrix."

2. **Lane-floor receiver shader multiplies per light.**
   - The lane floor uses a stock material (`FloorBuilder.cs`). Give it a **custom shader** (fork the
     standard/`complex` lit shader, or a thin post-add). In its lighting loop, for **each** light
     `i`, after the normal shadow visibility, project the world pos into light `i`'s transmittance
     buffer and **multiply that light's radiance** by the sampled transmittance — `lightColor_i ×
     transmittance` (see *Color model* above). White sample → unchanged; this is what mixes the
     light's hue with the ball's. Source-agnostic payoff: a loop over whatever lights exist, no
     special case for the sun.
   - Lights without a transmittance buffer (beyond the cap, or non-casters) sample as white → behave
     exactly as today. Only the lane needs this shader; pins/Terry keep stock.

### Gotchas / unknowns to resolve early
- **No named lights anywhere.** Discover shadow-casters at runtime and key buffers by light index,
  so the feature survives lights being added/removed/moved. If a light moves, rebuild its matrix
  that frame; if static, you may cache — but detect that generically, don't assume "the sun."
- **Per-type projections.** Directional = ortho framed to the lane; spot = perspective cone; point =
  cube/6-face (or skip point lights in v1 and note it). Read projection from the light, not a constant.
- **Cost scales with light count** — hence the K-light cap + atlas. Decide K and the selection metric
  (distance/intensity) up front.
- Confirm s&box exposes: rendering a single object to an RT from script (camera+tag mask likely);
  binding a global **texture array + matrix array** a `.shader` can index; and reading the engine's
  active-light list/order from script so the receiver indices match the cast indices.
- Receiver shader is the risky part — forking a lit shader in s&box is fiddly. Budget time for it.
- Make it **toggleable** and lane-scoped so it can't tank perf or leak across the 4 lanes.

## Fallback if the "real" path stalls

The spatially-correct cheap fake: a **soft radial green decal** (proper alpha falloff, multiply
blend) projected on the lane under the ball, tracked each frame and faded as the ball lifts off.
This is *not* the flattened glass-sphere smear we removed (that looked wrong because it used the
full glass shader); a purpose-made soft decal reads much closer to a real tinted shadow. Cheap,
no custom receiver shader. Use this if the transmission buffer proves too deep for the session.

## Key references
- `glass.shader` `S_MODE_DEPTH` block — proof the cast side is colorless.
- `common/classes/Light.hlsl` `Light::Shadows()` — scalar visibility, the receiver side.
- `common/lightbinner.hlsl` — `TransmissiveEnabled` light flag + cookie (`GetLightCookie`); a
  light cookie/gobo is a *third* option but tints the whole light pool, not just the ball footprint.
- `Code/FloorBuilder.cs` — where the lane floor (the receiver) is built; needs the custom material.
- `Code/LaneBuilder.cs` — lane bounds for framing the RT; floor top is `z = 0` in lane space.
- `Code/BallController.cs` — the ball; `galaxyball_color.png` / `glassball.vmat` are its look.

## Done when
The ball's shadow on the lane is visibly green-tinted, tracks the ball, doesn't bleed onto other
lanes, has a toggle, and doesn't measurably hurt frame time — **and it still works (no code
changes) after adding another shadow-casting light to the scene.**
