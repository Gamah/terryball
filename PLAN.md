# PLAN: tint the ball's shadow green (via a floor shader)

**Goal.** The ball already casts a clean, soft, correctly-angled **gray** shadow on the lane (the
reference screenshot — it comes from an opaque `ShadowsOnly` proxy sphere under the directional Sun, see
[info.md](info.md)). Take **that** shadow and tint it **green**, where:

- the green is read from a **property on the ball** (a single `Color`) — so changing it changes the
  shadow, and a (future) differently-coloured ball casts a differently-coloured shadow. **One colour per
  ball** is fine (a multi-coloured galaxy ball still casts a single-colour shadow).
- **only the ball's shadow** is tinted — pins / Terry / lane structure keep normal **gray** shadows.
- the mechanism is a **custom lane/floor shader** (no spot-light rig, no projected decals).

Decisions already made (this session): scope = ball-only; colour = from the ball; mechanism = floor
shader; abandoned the per-light spot-cookie and the "tint every shadow" shader (see info.md).

## What we keep

- The ball's invisible **`ShadowsOnly` proxy sphere** (same size as the ball). The glass shell can't cast
  (its depth pass dithers occlusion away), so the proxy is what makes the gray shadow we're colouring.

## The core challenge — "ball only"

The engine's directional shadow map is shared by every caster and stores **no caster identity**, so a
shader that just tints "wherever this surface is shadowed" also tints pin/Terry shadows. We need a
**ball-specific signal** the shader can test. Recommended path, then simpler fallbacks:

### Recommended — a ball-shadow MASK that gates the tint

1. **Mask render (per frame).** Render only the ball's proxy from the **Sun's direction** into a small
   render target = where the ball blocks the Sun. Also compute its world→light projection matrix. (This
   is **one** RT and **one** projection — far lighter than the abandoned per-light cookie.)
2. **Bind globally.** Push the mask texture + matrix + the ball's tint colour as **global shader
   attributes** (`Graphics.Attributes`), so any lane material can read them.
3. **Lane shader.** A forked standard lit shader (`shadow_receiver.shader`) that:
   - shades normally (so surfaces look unchanged),
   - reads the engine's **directional shadow** for the nice soft shadow **shape**,
   - samples the **ball-mask** at this world position,
   - tints toward the ball's colour **only where the surface is shadowed AND the ball-mask covers it**.
   → the ball's shadow goes green (keeping the engine's soft shape); pin/Terry shadows stay gray.

### Simpler fallbacks (if the mask proves fiddly)

- **A. Tint by location.** Apply the tint shader only to surfaces the ball roams alone (building floor,
  ramp, approach, lane bed) and **not** the pin deck. Then "tint every shadow here" ≈ ball-only, because
  no pins stand there. Gap: the ball's shadow isn't tinted once it reaches the deck.
- **B. Ball is the only caster.** Tint every directional shadow, but disable shadow-casting on
  pins/Terry/structure so the ball is the only thing casting. Simplest, but those objects lose their
  shadows entirely.

## Pieces to build

- **`BallController`**: a `[Property] Color ShadowTint` (default a galaxy green). Each frame push it to a
  global shader attribute so the lane shader reads the live ball colour. Keep/add the `ShadowsOnly` proxy.
- **Ball-shadow-mask component** (recommended path only): renders the ball proxy into an RT from the
  Sun's direction (read the `DirectionalLight`'s rotation at runtime) and binds RT + matrix globally.
- **`Assets/shaders/shadow_receiver.shader`**: forked from the engine's standard lit shader
  (`bark.shader` is a clean template — uses `Material::From` → `ShadingModelStandard::Shade`). Adds the
  shadow-tint blend (gated by the ball-mask in the recommended path). Reads the global tint colour.
- **`LaneBuilder` / `FloorBuilder`**: assign this material to every surface the ball rolls over (building
  floor, ramp, approach shoulders, lane bed, pin deck), preserving each surface's base colour.

## Tuning knobs

- `BallController.ShadowTint` (colour), tint strength, and mask softness/resolution.

## Risks / notes

- **Shaders only compile in the editor** — can't verify here, so the first pass may need syntax fixes
  (watch for a magenta/error material on the lane = compile failure).
- The mask needs the **Sun's direction** at runtime (read from the `DirectionalLight` component).
- All lane surfaces are built procedurally on `models/dev/box.vmdl`; the shader must respect each
  surface's flat colour.

## Done when

The ball's shadow reads **green** on every surface it rolls over, the green is driven by the ball's
`ShadowTint` property (change it → the shadow changes), **pins/Terry keep gray shadows**, there's no
spot-light rig and no decal, and frame time is unaffected.
