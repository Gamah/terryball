# PLAN.md

One flat table, ranked highest first. **A shipped row is deleted, not ticked**; when you close
one, put what a future session would get wrong without it into `CLAUDE.md` or `docs/`. Rows are
intentions, not specifications: a better idea beats the row, so change the row.

| # | Row |
|---|---|
| 100 | **Lighting renders too dark on the current engine.** Read against upstream `sbox-public` `b5464937` on 2026-09-29. The 3D world and the HUD are two separate questions. **World:** ambient (sun `SkyColor` + `AmbientLight`, summed in `Scene.PreCameraRender`), tonemapping and exposure are unchanged, and `ballshadow_tint.shader` only touches ball footprints. Toggle the sun's `Shadows` and `r.shadows.contact.enabled 0`; if either brings the light back, suspect the shadow commits `8f20bf94`, `bf0b5627`, `0a22829c`, `798e68ec`. Otherwise raise `main.scene`'s sun `SkyColor` (0.25,0.32,0.35, scaled by `SkyBrightnessApplier`) or move it to an `AmbientLight`; the two add together, and `SkyColor` is the obsolete path. **HUD:** `12b791d8` blends panels in sRGB space like a browser, so translucent dark backgrounds come out darker (`rgba(0,0,0,0.72)` over mid-grey: 0.27 → 0.14). `ui_gamma_blend 0` is read only by the experimental `r_managed_scene` renderer, so it cannot be tested on the default path; retune the alphas by eye instead. Same row in rotaliate-client and gambit; fix all three together |
