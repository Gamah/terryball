# Terryball

An s&box game: Terry (the Citizen) on a flat floor. A momentum toy whose control
mechanic gestures at the *shape* of Super Monkey Ball (input once-removed from the
avatar) but **diverges from it fundamentally** — the actual mechanic is the user's
call and is intentionally not yet built.

See **[PLAN.md](PLAN.md)** — start there. The current scene runs but Terry has no
input (`Code/TerryController.cs` is a stub on purpose).

## Layout

```
terryball.sbproj          # open via the s&box editor's New Project (see PLAN.md)
Assets/scenes/main.scene  # floor + Terry + sun + camera
Code/FloorBuilder.cs      # runtime flat floor
Code/TerryController.cs   # STUB — input mechanic deliberately empty
```
