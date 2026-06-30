# Tinted ball-shadow — what's been tried (and learned)

Goal: a **green-tinted shadow** under the translucent green galaxy ball. This file records the
approaches attempted in one long session so the next attempt doesn't re-tread them. The active plan
is in [PLAN.md](PLAN.md).

## Hard engine facts (verified against ../sbox-public — don't re-discover)

- **Engine shadows are a colourless visibility scalar** (lit vs. not). There is no "shadow colour"
  property to set anywhere, and **no built-in shadow tint** on lights or globally.
- **The glass ball casts almost no shadow.** `glass.shader`'s `S_MODE_DEPTH` block computes a Fresnel
  opacity and `OpaqueFadeDepth(flOpacity, …)` **dithers the shadow depth away by opacity** (~L173). So
  for the ball to cast a real grayscale shadow you must add an **opaque `ShadowsOnly` proxy sphere**
  (`ModelRenderer.RenderType = ShadowRenderType.ShadowsOnly`) at the ball's size. This is what produced
  the clean gray shadow in the reference screenshot.
- **Light cookies are SPOT-LIGHT ONLY** (`SpotLight.Cookie`). Directional & point lights expose none.
  `GetCookieUV` (lightbinner.hlsl) is a perspective cone projection; the engine multiplies the cookie
  RGB×A into the spot's colour through the cone.
- **Spot shadow sharpness is governed by the spot `Radius`** (the shadow-frustum far plane). An
  oversized radius spreads the shadow map over a huge frustum and the shadow **washes out**. Keep
  `Radius` just past the lit pool.
- **Spot shadows fade out by distance (~1000 units).** At this lane's huge scale (units-per-inch
  ≈ 23.5, ball = 200u), lights placed at "realistic inches" land **thousands of units up** and their
  shadows fall out of range. Lights must sit low (a few hundred units) for shadows to render.
- **Many overlapping lights wash depth shadows** — a point shadowed by one light is lit by its
  neighbours, so dense rigs give soft/no shadows.
- **Receiver shadow term is callable in a pixel shader:**
  `DirectionalLightShadow::GetVisibility( worldPos, screenPos )` (1 = lit, 0 = shadowed), available to
  any PS that includes `common/pixel.hlsl`.

## Approaches attempted

1. **Spot rig + per-light cookie** (was branch `glass-shadow-lighting`). Replaced the Sun with a rig of
   cool-white **4800 K spot lights** (a lane run every ~12 ft + a pin key light + settee corner spots),
   then a `LaneShadowCookies` component that each frame projected the ball (sphere → disc) into every
   spot's **cookie** to tint that light green under the ball. Per-light (so overlap can't wash it),
   multi-ball venn via multiply-blend. Engine-wise viable, but **abandoned as overkill** — it drags in
   the whole spot rig and a projection-matching risk. (That branch also: widened/deepened the settee,
   doubled the backboard, collapsed the lane hierarchy into folders, dimmed ambient.)

2. **Floor shadow-tint shader** (was branch `floor-shadow-tint`). A custom `shadow_receiver.shader`
   (forked from `bark.shader`) that shades normally, then **multiplies a green hue wherever
   `DirectionalLightShadow::GetVisibility` is low** → any grayscale shadow auto-colours. Plus the ball
   `ShadowsOnly` proxy. Applied to the `LaneBed`. Light- and caster-agnostic. **Abandoned ("all wrong").**

## Current good state (the reference screenshot)

The `ShadowsOnly` proxy sphere + the directional **Sun** casts a clean, soft, correctly-angled **gray**
elliptical shadow to the lower-left of the ball on the lane/approach surface. Shape, softness and
direction are all good — it only lacks colour. **Tinting THIS shadow green is the task.**

## Key references

- `../sbox-public` (engine source), `../sbox-docs` (shader docs: `rendering/shaders/material.md`,
  `shading-model.md`).
- `glass.shader` `S_MODE_DEPTH` — why the ball won't cast.
- `common/utils/Material.CommonInputs.hlsl` + `common/pixel.hlsl` — includes a custom lit shader needs;
  `Material::From(i)` → `ShadingModelStandard::Shade(m)`.
- `Material.Create(name, shader)` + `Material.Set(param, …)`; `ModelRenderer.MaterialOverride`.
- `Code/BallController.cs` (glass shell visual), `Code/LaneBuilder.cs` (lane surfaces via
  `MakeBox`/`MakeDecal` on `models/dev/box.vmdl`).
