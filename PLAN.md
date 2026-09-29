# PLAN.md

One flat table, ranked highest first. **A shipped row is deleted, not ticked**; when you close
one, put what a future session would get wrong without it into `CLAUDE.md` or `docs/`. Rows are
intentions, not specifications: a better idea beats the row, so change the row.

| # | Row |
|---|---|
| 100 | **Lighting renders too dark on the current engine.** Read against upstream `sbox-public` `b5464937` on 2026-09-29. Ambient (sun `SkyColor` + `AmbientLight`, summed in `Scene.PreCameraRender`), tonemapping and exposure are unchanged, and `ballshadow_tint.shader` only touches ball footprints. Toggle the sun's `Shadows` and `r.shadows.contact.enabled 0`; if either brings the light back, suspect the shadow commits `8f20bf94`, `bf0b5627`, `0a22829c`, `798e68ec`. Otherwise raise `main.scene`'s sun `SkyColor` (0.25,0.32,0.35, scaled by `SkyBrightnessApplier`) or move it to an `AmbientLight`; the two add together, and `SkyColor` is the obsolete path. The HUD half is done (dark backdrop alphas retuned for sRGB blending). Same row in rotaliate-client and gambit; fix all three together |
