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

## Prior art — how other engines do this (and what it tells us)

- **Canonical algorithm: Translucent Shadow Maps** (Dachsbacher & Stamminger, 2003). Render from the
  light an extra buffer of how light is attenuated/tinted as it passes the translucent surface, then
  modulate the receiver by it. The per-light **transmittance buffer** below is the colored subset of
  exactly this — so we're on a well-trodden path, not inventing one.
- **Unreal**: "Colored Translucent Shadows" tint the shadow by the material's **Base Color**, scaled by
  **Opacity** (0..1) — identical to our *light × transmittance* model. **Big caveat: UE only supports it
  with _static_ lighting (Lightmass); Lumen/dynamic GI does _not_.** Takeaway for us: even a AAA engine
  punts colored translucent shadows to the **baked** path. Our ball **moves**, so baking is off the
  table → a fully dynamic version is genuinely advanced. That's a strong argument to **ship the soft-decal
  fallback first** as the baseline and treat the transmittance buffer as the stretch goal.
- **Unity (HDRP + DXR)**: "Color Shadow" on ray-traced shadows — the shadow ray accumulates tint as it
  passes transparent surfaces (`Transmittance Color`). Clean and correct, but costs real ray-tracing
  budget and RTX-class hardware. See *Alternative: ray-traced* below — s&box does expose an RT accel
  structure, so it's not impossible here.

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

## Alternative: ray-traced colored shadows

Unity's approach: with ray-traced shadows enabled, the shadow ray accumulates `Transmittance Color`
as it passes the transparent ball, giving correct colored shadows "for free" *if you're already
paying for RT shadows*. s&box **does** expose a ray-tracing acceleration structure to shaders
(`common/classes/Raytracing.hlsl`, `_accelStruct`) and an RT mesh-trace path in the engine, so this
isn't off the table. But: RT shadows cost real GPU and want RTX-class hardware, and we are **not**
currently doing RT shadows — so this would add the full RT-shadow cost just for the ball's tint.
Record it, prototype only if the transmittance-buffer path proves too fiddly *and* there's GPU budget.

## Recommended build order

1. **Soft-decal fallback first (ship it).** A purpose-made **soft radial decal** (proper alpha
   falloff, multiply blend) projected on the lane under the ball, tracked each frame, faded as the
   ball lifts off. *Not* the flattened glass-sphere smear we removed (that used the full glass shader
   and looked wrong) — a dedicated soft decal reads close to a real tinted shadow, costs almost
   nothing, and needs no custom receiver shader. Given even Unreal keeps colored translucent shadows
   in the *baked* path, this is the pragmatic baseline. To honor *light × ball*, the decal's tint can
   be modulated by the dominant light's color so it isn't a fixed green.
2. **Transmittance buffer (the stretch goal)** — the per-light approach above, for the genuinely
   correct multi-light result. Bigger lift (custom receiver shader); attempt once the baseline ships.
3. **Ray-traced** — only with GPU budget to spare.

## Tuning / quality knobs (from the UE notes)
- Shadow **sharpness** depends on the receiver's resolution, the **light's source angle/size** (softer
  with a wider source), and the transmittance sample quality — expose these rather than hardcoding.
- **Opacity drives tint strength**: transmittance ≈ the ball color filtered by glass opacity (UE's
  "Base Color scaled by Opacity", "Thin Translucent" output). Clear texels pass more light (brighter,
  more saturated tint); opaque texels darken toward a gray block.

## Key references
- **Translucent Shadow Maps**, Dachsbacher & Stamminger 2003 — the canonical algorithm this implements.
- UE "Using Colored Translucent Shadows" (light × base-color × opacity; **static lighting only**).
- Unity/HDRP "Color Shadow" on ray-traced shadows (the RT alternative).
- `glass.shader` `S_MODE_DEPTH` block — proof the cast side is colorless.
- `common/classes/Light.hlsl` `Light::Shadows()` — scalar visibility, the receiver side.
- `common/classes/Raytracing.hlsl` — the RT accel structure, for the ray-traced alternative.
- `common/lightbinner.hlsl` — `TransmissiveEnabled` light flag + cookie (`GetLightCookie`); a
  light cookie/gobo is a *third* option but tints the whole light pool, not just the ball footprint.
- `Code/FloorBuilder.cs` — where the lane floor (the receiver) is built; needs the custom material.
- `Code/LaneBuilder.cs` — lane bounds for framing the RT; floor top is `z = 0` in lane space.
- `Code/BallController.cs` — the ball; `galaxyball_color.png` / `glassball.vmat` are its look.

## Done when
The ball's shadow on the lane is visibly green-tinted, tracks the ball, doesn't bleed onto other
lanes, has a toggle, and doesn't measurably hurt frame time — **and it still works (no code
changes) after adding another shadow-casting light to the scene.**
