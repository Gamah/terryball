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

Standard stained-glass-shadow technique. Two new pieces: a render-to-texture of the ball's
transmittance from the light's POV, and a lane-floor shader that multiplies its lighting by it.

1. **Transmission RT, rendered from the Sun's direction.**
   - The lane shadow caster is the `Sun` DirectionalLight (see `PLAN.md` Milestone 0 / the
     `main.scene`). Build an **orthographic** view aligned to the sun direction, framed over
     the lane play area (roughly the lane bounds from `LaneBuilder`).
   - Render **only the ball** into an `RGBA` RT: where the ball covers a texel, store its green
     **transmittance** color (tint toward `galaxyball_color` greens, alpha = coverage/opacity);
     clear to white (1,1,1) = "fully lets light through" everywhere else.
   - s&box hooks to evaluate: `CommandList` / a `RenderHook` / `Graphics.RenderToTexture`, or a
     dedicated off-screen `CameraComponent` with a render target + a layer/tag mask so only the
     ball draws. A custom material on the ball for this pass keeps it cheap (no need for the full
     glass shader — just output green×coverage).
   - Bind the RT + its world→light matrix as **global shader attributes** (e.g.
     `Graphics.GlobalAttributes` or `Scene` attributes) so receiver shaders can read them.

2. **Lane-floor receiver shader samples the buffer.**
   - The lane floor currently uses a stock material (`FloorBuilder.cs` builds it). Give it a
     **custom shader** (fork the standard/`complex` lit shader, or a thin post-add) that, after
     normal lighting, projects the pixel's world pos into the transmission RT and **multiplies**
     the lit color by the sampled green-transmittance. Outside the ball's footprint the sample is
     white → no change.
   - Only the lane needs this; pins/Terry can keep stock shaders.

### Gotchas / unknowns to resolve early
- **Directional light = no single position.** Frame the ortho RT to the lane, not a point light
  cone. Keep the RT matrix in sync with the `Sun` each frame (or assume the sun is static and bake
  the matrix once — confirm the sun doesn't move).
- Confirm s&box exposes a clean way to render a single object to an RT from script (camera+tag mask
  is most likely; verify against `sbox-public` Camera/RenderHook APIs before committing to it).
- Confirm you can bind a global texture + matrix that a `.shader` reads (global attributes).
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
lanes, has a toggle, and doesn't measurably hurt frame time.
