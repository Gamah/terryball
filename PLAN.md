# PLAN: ball shadows — remaining work

The ball-shadow effect is **shipped and working** (branch merged to master). This file now tracks the
**followups** that remain, not the original build.

## What's already done (shipped)

A screen-space post-process (`Code/BallShadowTintPP.cs` + `Assets/shaders/ballshadow_tint.shader`,
`Stage.AfterOpaque`) that **projects each ball's surface colour into its real shadow**:

- Per shadowed pixel, reconstruct world pos from depth, read the real sun shadow term
  (`DirectionalLightShadow::GetVisibility` — grading + penumbra ride through), and for each `ShadowBall`
  (marker component) sample its **colour map** at the entry+exit surface points along the sun ray. A chord
  through one colour tints that colour; a chord through two greys toward their mean.
- Multiple balls stack **subtractively** (up to 4 slots); an opaque occupant (Terry, `HasOpaqueOccupant`)
  reads deep grey via the occlusion cut.
- **Extra coloured lights** (point lights, up to 2) each cast an **analytic** ball shadow tinted by
  ball-colour × light-colour (`ApplyLight`). The sun keeps its real engine shadow; extra lights are
  analytic only. A green `GreenApproachLight` (scene root, parked over the spawn point by `ApproachLight`)
  demos this.
- Tuning knobs on the component: Strength, Colorize, Saturation, ColorMip, TerryCut, OccupantDark,
  CascadeResolution/Distance (`r.shadows.csm.*` for CSM sharpness). DebugMode enum isolates the pipeline
  (kept intentionally — useful for the work below).
- Current playing ball is a red/blue **pokeball** (`glassball_poke.vmat` + `pokeball_color.png`).

Hard-won constraints: **shader source must stay ASCII and brace-balanced** or the vfx parser crashes on
load. Colour maps must be resolved **in the post-process** (a runtime `Texture.Load` in `OnEnabled` returns
null). See the memory notes.

---

## Active plan

### 1. Run the effect on the lane-monitor cameras

Today only the scene's **main camera** has `BallShadowTintPP`. The 4 lane-monitor feed cameras are created
in code (`Code/LaneMonitorCamera.cs`, `Components.Create<CameraComponent>()`) and have no post-process, so
those feeds show plain grey shadows — an in-game inconsistency.

- Attach a `BallShadowTintPP` to each monitor camera when it's created (it's a `BasePostProcess`, so it's
  auto-collected per camera). Mirror the main camera's settings, or expose them once and share.
- Verify: the coloured shadow shows in all 4 feeds, and per-camera re-gathering of balls/lights is cheap
  enough (profile if the feeds stutter).

### 2. Auto-couple the shadow colour to the ball (kill the two-place setup)

The ball's colour currently lives in **two** places that can drift: the material (`glassball_poke.vmat`
`TextureColor`) and `ShadowBall.ColorMapPath`. The goal is **one source**, texture/shader-agnostic.

- Preferred (robust): make `ShadowBall.ColorMap`/`Path` the single source and drive the **ball's material
  from it** at runtime (per-instance `TextureColor` override via the renderer's `SceneObject.Attributes`),
  so setting the ball's colour map updates both the ball and its shadow. Verify the glass shader honours a
  per-instance `TextureColor` override.
- Full-agnostic (spike): read the ball's colour straight off its `ModelRenderer`'s material at runtime
  (`Material.Attributes` `g_vColorTint` + averaging/keeping the colour texture) so *any* ball just works
  with zero config. Uncertain API — prototype before committing.

---

## Stretch goals

3. **Extra-light shadow correctness.** The extra (coloured) light shadow is analytic-only: no penumbra, no
   Terry silhouette, and it doesn't respect other occluders/surfaces — it's a flat projected ellipse, so on
   the gutter/walls/pins it can bleed where the real light wouldn't reach. Fine on the flat approach; needs
   depth/occluder awareness (or its own shadow map) to be correct elsewhere.
4. **Green light behaviour.** It's pinned to the spawn point, so its shadow detaches once the ball rolls
   away — fine as a start-of-frame beauty element. Decide if it should track the ball, fade out on release,
   or stay purely decorative.
5. **Rolling swirl.** The colour map is sampled in ball-local space, so the shadow's colours rotate with
   the roll (correct, but can look busy at speed). Option to damp/freeze the rotation past some speed.
6. **Split alignment / catseye.** The analytic spherical UV doesn't match the model's real UV, so the
   shadow's colour seam won't perfectly line up with the ball's visible seam, and the pole smears into a
   "catseye" at the shadow centre (liked, kept). Match the model UV if exact registration is ever needed.

---

## Explicitly ruled out (don't re-tread)

- **Projected decals** — reconstruct the shadow, no real detail; fighting the projection across gutter
  surfaces was a losing battle.
- **Analytic floor `shadow_receiver.shader`** — analytic ellipse only, needed per-surface materials
  ("massive failure").
- **Opaque `ShadowsOnly` proxy sphere** — flattens the glass's graded shadow.
- **Native coloured/deep shadow maps or directional cookies** — Source 2's shadow rasteriser is closed;
  only the sampling HLSL is editable, which is what this uses.
