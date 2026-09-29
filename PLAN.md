# PLAN.md

One flat table, ranked highest first. **A shipped row is deleted, not ticked**; when you close
one, put what a future session would get wrong without it into `CLAUDE.md`. Rows are
intentions, not specifications: a better idea beats the row, so change the row.

| # | Row |
|---|---|
| 100 | **Lighting renders too dark on the current engine.** Engine source read 2026-09-29 (upstream `sbox-public` `b5464937`) against the 2026-08-04 head: `Light`/`DirectionalLight` components and serialization, ambient (`DirectionalLight.SkyColor` + `AmbientLight` → `SceneWorld.AmbientLightColor` in `Scene.PreCameraRender`), tonemapping and exposure are unchanged. Prime suspect is `12b791d8` (UI shader core): panels now blend in gamma space, so dark translucent backgrounds render ~2× darker (`rgba(0,0,0,0.72)` over mid-grey: 0.27 → 0.14 sRGB). Test `ui_gamma_blend 0` in the console [SOURCE: only seen read on the managed-renderer path]; if it clears, retune panel background colours/alphas. If the 3D world is still dark (`main.scene` sun `SkyColor` is 0.25,0.32,0.35, scaled by `SkyBrightnessApplier`), toggle the sun's `Shadows` and `r.shadows.contact.enabled 0` — if either restores light, suspect the shadow commits `8f20bf94`, `bf0b5627`/`1965d08d`, `0a22829c`, `798e68ec`. Adding an `AmbientLight` is the engine's recommended ambient either way. Same row in rotaliate-client and gambit; fix all three together. `ballshadow_tint.shader` calls the 2-argument `DirectionalLightShadow::GetVisibility`, which `bf0b5627` kept as an overload deriving the receiver normal from screen-space derivatives — valid only in uniform control flow |
