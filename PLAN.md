# PLAN: recolour ball shadows from the balls' own colours (screen-space, per-light, agnostic)

**Goal.** Balls are translucent coloured galaxy marbles. Their shadows should read **the ball's colour**
— as if each light were filtered through that ball's glass — instead of grey. The effect must be:

- **light-count agnostic** — works for 1 directional sun today, or N point/spot/colored lights later;
- **colour agnostic & live** — the tint is **not** a hardcoded property. It is derived, **in realtime**,
  from **whatever gives the ball its current colour** (see "Where the tint comes from"). Green today
  because the material is green; change the material and the shadow follows;
- **ball-count agnostic** — N balls, each contributing its own tint;
- **physically mixed** — where colours overlap, they combine as a **subtractive filter** (each occluding
  ball multiplicatively tints the light passing through it; unblocked lights keep lighting normally);
- **scoped to balls** — pins / Terry / lane structure keep normal grey shadows;
- **detail-preserving** — the real shadow's soft penumbra, the Terry silhouette, and the graded grey
  levels the glass shader casts all ride through.

## The core insight (unchanged from the prior working idea)

Every earlier failed approach **reconstructed** a shadow (projected decal, analytic floor ellipse). A
reconstruction can't contain detail it didn't generate. **We do not reconstruct — we recolour the real
shadow that's already on screen.** We take:

- **shape/detail** from the engine's real shadow term (fractional → graded greys, penumbra, Terry ride
  through), and
- **"which ball, which light" attribution** from cheap analytic ray/sphere tests per light.

## Decisions locked in (2026-07-01)

1. **Per-light iteration (agnostic).** Do **not** hardcode the single directional sun. Iterate lights via
   the engine's generic light path and read each light's own shadow term + colour, then attribute the
   recolour per light. (See "Engine API" — this likely forces an `ISceneCustomPass` rather than a pure
   post-process, because the light-binner cluster + shadow atlases must be bound.)
2. **Spike first.** Ship the single-sun red-tint spike (below) to confirm the shadow/light bindings are
   available in our pass, THEN generalize to N lights / N balls. No building on an unproven binding.
3. **Physical subtractive mixing.** A point inside several balls' shadows gets its passing light
   multiplicatively tinted by each occluding ball's colour (green·amber glass → murky); lights not
   blocked at that point keep lighting it. This is the real coloured-shadow / coloured-glass behaviour.
4. **Tint is live, sourced from the ball's colour** — not a `[Property] Color`. See next section.

## Where the tint comes from — sample the texel the light ray passed through

The ball has **no single colour scalar**; its green is the `glassball.vmat` material — `TextureColor` =
`galaxyball_color.png` (green nebula) lifted by `g_vColorTint` `[0.82,1.0,0.88]`. Rather than collapse
that to one representative colour (an average), we tint **per ray**: a coloured-glass shadow *is* the
light tinted by the exact glass it passed through, so the galaxy's swirls project into the shadow.

Per shadowed pixel, per light: the shadow ray is `worldPos → light`. Ray/sphere-intersect it against the
ball, take the **light-facing hit** (`t_far`, where the light enters the glass), transform that point into
**ball-local** space (using the pushed ball transform), map it to a spherical UV, and sample the galaxy
texture (× live `g_vColorTint`) there. That texel **is** the tint filter for that pixel — no average.

Decisions:
- **Sample space = ball-local** → the swirl **rotates with the rolling ball** (physically correct, lively).
  One-line toggle to sample in world space (frozen swirl) if it reads too noisy at speed.
- **Sample point = `t_far`** (light-facing surface). Can upgrade to a `t_near`/`t_far` blend for depth later.
- **UV** is synthesized analytically (spherical), so it won't match the model's unwrap exactly — irrelevant
  for an abstract swirl map; it reads as believable coloured swirl.
- `g_vColorTint` is read **each frame**, so live material recolours track the same frame.

## Engine API (grounded in ../sbox-public)

- `game/addons/base/Assets/shaders/common/classes/Light.hlsl`: generic light iteration —
  `Light::Count(screenPos)`, `Light::From(worldPos, posSs, i, lightmapUV)`, and per-light
  `Light::Shadows(worldPos, screenPos)` (dispatches to `DirectionalLightShadow` / `ProjectedShadow` /
  `ProjectedShadowCube` by type), `Light::GetLightColor`, `GetLightDirection`, `GetLightPosition`,
  `GetLightAttenuation`. Backed by the light-binner cluster (`common/lightbinner.hlsl`).
- `game/core/shaders/Shadows/DirectionalLightShadow.hlsl` — `GetVisibility(worldPos, screenPos)` (sun).
- `common/classes/Depth.hlsl` — `GetWorldPosition(screenPos)` (confirmed usable in a post pass).

## The algorithm (per screen pixel)

```
worldPos   = Depth::GetWorldPosition(screenPos)
detail     = 1 - <real shadow term>          // aggregate (spike) or per-light Light::Shadows; FRACTIONAL,
                                             // carries graded greys + Terry silhouette + penumbra
// accumulate a subtractive tint over lights and balls:
tintFilter = float3(1,1,1)                   // multiplicative filter applied to shadowed colour
for each light L (Light::Count / Light::From):
    toL     = direction/position of L (GetLightDirection/GetLightPosition)
    for each ball B (pushed centre/radius/orientation):
        occ = softRaySphere(worldPos, toL, B.center, B.radius)   // 0..1 does B block L here?
        hit = rayFarHit(worldPos, toL, B)                        // light-facing surface point (t_far)
        uv  = sphereUV( toBallLocal(hit, B) )                    // ball-local → spherical UV
        tex = SampleGalaxy(uv).rgb * g_vColorTint                // the exact texel the light passed through
        tintFilter = lerp(tintFilter, tintFilter * tex, occ)     // subtractive stack, colour-agnostic
color = lerp(color, color * tintFilter, detail)                  // recolour only the real shadow
```

The analytic ray/sphere tests decide **which light/ball owns the recolour** (attribution + mask);
`detail` (the real fractional shadow) decides **how strong** the recolour is at that pixel, so Terry and
the penumbra ride through. For the spike we collapse this to: one light, one ball, `tint = red`, no
material read — just prove `detail`/`GetVisibility` is readable in the pass.

## What we KEEP from the glass shadow

The glass ball casts its **own graded shadow** via `glass.shader`'s depth pass (Fresnel opacity +
`OpaqueFadeDepth` dithers it into the sun shadow map — dense rim, thin centre, solid Terry). That grading
is the detail we preserve. If the recoloured shadow reads too faint, strengthen opacity /
`OpaqueFadeDepth` in the `S_MODE_DEPTH` block — **never** swap in a flat opaque `ShadowsOnly` proxy
(uniform binary shadow flattens the grading).

## Data flow

`BallController` (only the local lane's ball, as before) pushes each frame via
`Scene.RenderAttributes` — now **arrays** for N balls (fixed small cap, e.g. 8; simpler than a
StructuredBuffer and plenty):

- `BallShadowCenters[]` (float3), `BallShadowRadii[]` (float), `BallShadowRots[]` (float4 quat, for
  ball-local sampling), `BallShadowCount` (int; 0 → shader bails, frame untouched), plus a scalar
  `BallShadowStrength`.
- The **galaxy colour texture** (`Texture2D`) + live `g_vColorTint` (float4), bound once/per-frame. One
  shared texture today; per-ball texture arrays only if balls ever diverge.

Light data (positions/dirs/colours/shadows) is read **inside the shader** from the engine's light binner —
not pushed from C#.

## Pieces to build

- **`BallController`**: push per-ball centre / radius / orientation arrays + bind the galaxy texture and
  live `g_vColorTint` each frame. No average, no authored tint. Resolve nothing about lights (shader-side).
- **Pass component** (`BallShadowTintPP`): start as a post-process; if the spike shows light-binner /
  shadow atlases aren't bound in post, switch to an `ISceneCustomPass` that runs **right after opaque**
  (bindings guaranteed there). Must be on the **4 lane-monitor cameras** + the main view.
- **`Assets/shaders/ballshadow_tint.shader`**: fullscreen shader. Template `postprocess_bloom.shader`
  (fullscreen VS + `postprocess/common.hlsl`), `Depth.hlsl` for world reconstruction, and `Shadow.hlsl` /
  `Light.hlsl` for the shadow term + light iteration.

## Risks / unknowns (verify in this order)

1. **Are the shadow term + light binner readable in the pass?** World reconstruction in post is confirmed;
   the **shadow atlases + light-binner cluster being bound during our pass is the open question** — and
   per-light iteration needs *both*. → the spike de-risks exactly this. If not bound in a post-process,
   fall back to `ISceneCustomPass` after opaque.
2. **Reconstructing per-light contribution in screen space.** We only have the composited colour, not each
   light's contribution. The algorithm sidesteps this by using analytic per-light ball occlusion for
   *attribution* and the real fractional shadow only for *strength*; validate this reads right with 2
   coloured lights (the end goal) once bindings work.
3. **Dither shimmer** at sparse-dither edges — controllable via shadow filtering / glass opacity.
4. **Multi-camera** ordering / pass present on every feed.
5. **Shaders compile only in-editor** — first pass may need syntax fixes (watch for magenta/no-op).

## De-risk spike (do this FIRST)

Post-process that tints **any** shadowed pixel **red** (`color *= red` where `1 - <shadow term> > 0`),
no ball mask, no per-light loop, no material read. If red lands on the shadow in-editor, the binding
works and the rest (per-light loop + analytic masks + live material tint + array plumbing) is
incremental. If not, switch to the `ISceneCustomPass` fallback and re-test before anything else.

## Explicitly ruled out (see info.md — don't re-tread)

- **Projected decals** (this branch): reconstructs the shadow → no real detail; fighting projection across
  gutter/lane/wall/trough surfaces is a losing battle.
- **Analytic floor `shadow_receiver.shader`** (old `green-ball-shadow`, "massive failure"): analytic
  ellipse only, needed per-surface material assignment.
- **Opaque `ShadowsOnly` proxy sphere**: flat binary shadow, destroys the Fresnel grading.
- **Per-light spot-cookie rig**: overkill.
- **Native coloured/deep shadow maps / directional cookies**: impossible — Source 2's shadow-map
  rasteriser is native/closed; only the *sampling* HLSL is editable (which is what this plan uses).

## Done when

Every ball's shadow reads **its own live colour** on every surface it falls across (driven by the ball's
material, updating in realtime), with the real shadow's grading / penumbra / Terry silhouette preserved;
overlapping coloured shadows mix as a subtractive filter; the effect is **light-count and ball-count
agnostic** (verified with 2 coloured lights on 2 coloured balls); pins/Terry keep grey shadows; it's a
single screen-space pass (no decal, no proxy, no per-surface material, no spot rig); frame time unaffected.
