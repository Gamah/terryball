# PLAN: tint the ball's shadow green (recolour the REAL shadow, screen-space)

**Goal.** The ball is a translucent green galaxy marble. We want the shadow it casts on the lane to read
**green** — as if sunlight were filtered through the green glass — instead of grey. The green must:

- come from a **property on the ball** (a single `Color`), so changing it changes the shadow;
- apply to **only the ball's shadow** — pins / Terry / lane structure keep normal grey shadows;
- **preserve the real shadow's detail** (see below): its soft penumbra, the Terry silhouette inside it,
  and the **graded grey levels** the glass shader casts.

## The core insight (why this plan is different from every prior attempt)

Every previous approach **reconstructed** a shadow (a projected decal, an analytic ray/sphere ellipse in a
floor shader). By definition a reconstruction can't contain detail it didn't itself generate — so none of
them matched the real shadow. **This plan does not reconstruct the shadow. It recolours the real one.**

The real shadow already has everything we want and it's already on screen. We just shift its hue, and we
mask *where* we shift it to "the sphere's shadow only." We take:

- **the shape/detail** from the engine's real directional shadow term (`GetVisibility`), and
- **the "belongs to the sphere?" mask** from a cheap analytic ray/sphere test.

This is the "screen-space shader that tints the shadow belonging to the sphere" idea, done as a
**post-process** pass so it needs **no per-surface material assignment** (the thing that made the old
`shadow_receiver` floor-shader painful).

## What we KEEP — and what we REVERSE from the old plan

- **KEEP** the glass ball casting its **own graded shadow.** `glass.shader`'s depth pass computes a
  **Fresnel opacity** and `OpaqueFadeDepth(...)` dithers the ball into the sun shadow map by it — dense at
  the rim, thin through the centre, plus Terry's solid silhouette. That grading (the "different levels of
  grey") is exactly the detail we're preserving.
- **REVERSE the old plan / info.md:** do **NOT** add an opaque `ShadowsOnly` proxy sphere. A solid proxy
  casts a *uniform binary* shadow, which would **flatten and destroy** the Fresnel grading we like. If the
  glass shadow reads too faint after recolouring, strengthen it by nudging the **opacity / `OpaqueFadeDepth`
  in `glass.shader`'s `S_MODE_DEPTH` block** — that keeps the grading while making it denser. Never swap in
  a flat proxy.

## The algorithm (per screen pixel, in the post-process)

```
worldPos   = Depth::GetWorldPosition(screenPos)                 // reconstruct from depth  (CONFIRMED usable in a post pass)
realShadow = 1 - DirectionalLightShadow::GetVisibility(worldPos, screenPos)
                                                               // the REAL sun shadow term — FRACTIONAL, so the
                                                               // graded greys + Terry silhouette + penumbra ride through
ballMask   = softRaySphere(worldPos, sunDir, ballCenter, ballRadius)
                                                               // 0..1: is this point in the sphere's shadow volume?
tint       = realShadow * ballMask
color      = lerp(color, color * ballTintColor, tint)          // multiplicative hue shift; keeps luminance/grading
```

Because `GetVisibility` returns a **fractional** value (dither resolved through PCF), the green strength
**tracks the grey level** — faint shadow → faint green, dense rim → strong green, Terry → full green. The
analytic sphere test is only a **mask** (where to recolour), never the shape (what to recolour), and it
generously encloses the ball+Terry footprint (Terry rides inside the sphere).

## Data flow

`BallController` pushes, each frame, via **global render attributes** (`Scene.RenderAttributes`):

- `BallShadowCenter` (float3, ball world centre)
- `BallShadowRadius`  (float, 0 = disabled → shader bails and the frame is untouched)
- `BallShadowToSun`   (float3, = -`DirectionalLight.Forward`, read from the scene sun)
- `BallShadowTint`    (float4, rgb = ball colour, a = strength)

Only the **local lane's** ball pushes (as the old shader_receiver did), so one ball drives the tint.

## Pieces to build

- **`BallController`**: a `[Property] Color ShadowTint` (default galaxy green) + a small update that pushes
  the four attributes above each frame (resolve the `DirectionalLight` once, cache it). No proxy sphere.
- **Post-process component** (`BallShadowTintPP` or similar): an `ISceneCustomPass` / post-process effect
  added to each rendering camera. Binds the colour + depth, runs the fullscreen shader. Must be present on
  the **4 lane-monitor cameras** and the main view (attributes are global, so the shader side is shared).
- **`Assets/shaders/ballshadow_tint.shader`**: a post-process shader. Template: `postprocess_bloom.shader`
  (fullscreen VS + `postprocess/common.hlsl`), plus `common/classes/Depth.hlsl` for `GetWorldPosition`
  (CONFIRMED: `postprocess_standard_p1.shader` does exactly this) and `common/Shadow.hlsl` for
  `DirectionalLightShadow::GetVisibility`.

## Risks / unknowns (verify in this order)

1. **Is `GetVisibility` readable in a post-process pass?** World-reconstruction in a post pass is confirmed;
   the sun **shadow-map + light constants being bound during a post pass is the one open question.**
   → **De-risk spike first** (below). If it's NOT bound, fall back to an **`ISceneCustomPass` that runs
   right after opaque**, where shadow bindings are guaranteed, sampling depth via `CommandList` grab.
2. **Dither shimmer.** PCF smooths the glass dither, so the broad gradient (rim/centre/Terry) is faithful,
   but a very sparse dither can shimmer at edges — controllable via shadow filtering or glass opacity.
3. **Multi-camera** ordering / that the pass is on every camera feed.
4. **Shaders only compile in-editor** — first pass may need syntax fixes (watch for magenta/no-op).

## De-risk spike (do this before the full version)

Add the post-process that tints **any** shadowed pixel **red** (`color *= red` where `1-GetVisibility > 0`),
no ball mask, no green. If red lands on the shadow in-editor, `GetVisibility` works in post and the rest
(green + `ballMask` + strength + the `Scene.RenderAttributes` plumbing) is straightforward. If not, switch
to the `ISceneCustomPass` fallback and re-test.

## Explicitly ruled out (don't re-tread — see info.md)

- **Projected decals** (branch `ball-shadow-green-tint`): reconstructs the shadow, so it can't carry the
  real detail, and fighting the projection across the gutter's lane/wall/trough surfaces is a losing battle.
- **Analytic floor `shadow_receiver.shader`** (old branch `green-ball-shadow`, "massive failure"): analytic
  ellipse only (no real detail), and needed per-surface material assignment.
- **Per-light spot-cookie rig**: abandoned as overkill.
- **Native colored/deep shadow maps or directional cookies**: not possible — the Source 2 shadow-map
  rasteriser is native/closed; only the *sampling* HLSL is editable, which is what this plan uses.

## Done when

The ball's shadow reads **green** on every surface it falls across, driven by the ball's `ShadowTint`
property, with the **real shadow's grading, penumbra, and Terry silhouette preserved**; pins/Terry keep grey
shadows; it's a single screen-space pass (no decal, no proxy, no per-surface material, no spot rig); and
frame time is unaffected.
