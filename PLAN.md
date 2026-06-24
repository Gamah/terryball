# Terryball — Plan

An s&box game. Terry (the Citizen) on a flat floor. The smallest possible playable
scene first; the control mechanic is the real design work and is deliberately left
open below.

> First time on this machine: don't open `terryball.sbproj` directly. Open the s&box
> editor → **New Project** → Game (Empty), pointed at this folder, and let the editor
> register the package and regenerate its `.csproj`/`.slnx`. (Same gotcha as the
> sibling Rotaliate project.)

---

## Milestone 0 — Scene that runs (scaffolded here)

A bare scene, no authored geometry:

- **`Assets/scenes/main.scene`** — `World` (FloorBuilder), `Terry`
  (TerryController + Citizen model), a `Sun` DirectionalLight, and a fixed `Camera`.
- **`Code/FloorBuilder.cs`** — builds one flat floor at runtime. Collider on the
  uniform root, visual on a `box.vmdl` child scaled by model bounds (s&box freezes
  physics if a BoxCollider rides a non-uniform scale).
- **`Code/TerryController.cs`** — **stub**: gravity + ground clamp only, **no input**.

Goal: press Play, Terry stands on the floor, nothing falls through. That's it.

---

## Milestone 1 — Camera

A follow camera looking at Terry (third-person, slight downward pitch). Keep it dumb
until the control mechanic exists, because the camera frame and the input frame are
coupled (see below) — don't over-invest here first.

---

## Input mechanic (the actual design problem)

### The Super Monkey Ball reference — what we are pointing AT, not copying

For shared vocabulary, the Super Monkey Ball model is:

- The player **never touches the ball directly.** Input tilts the *world/stage*.
- The ball is a passive rigid body; it rolls because **gravity** pulls it down the
  slope you created by tilting the floor.
- Two analog axes = pitch/roll of the entire stage, clamped to a small max angle.
- All challenge is *indirect*: you steer by sculpting the surface under the ball, and
  momentum/inertia do the rest. There is no jump, no thrust, no direct velocity.

That "you move the floor, not the avatar; physics does the steering" loop is the
**high-level shape** Terryball gestures at: a momentum toy where input is once-removed
from the avatar.

### ⚠️ Terryball will diverge from Super Monkey Ball — fundamentally, and by the user's call

This is a deliberate, load-bearing point, not a detail to be optimized away:

**The user owns the input mechanic, and their direction will differ from Super Monkey
Ball at the root — not in tuning (tilt angle, friction, camera) but in kind.** Treat
SMB only as a familiar starting reference for conversation. Do **not** implement an
SMB-style "tilt-the-stage, gravity-rolls-the-ball" controller as a default and wait to
be corrected. The fundamental divergence is expected to live in one or more of:

- **What the input acts on** — possibly Terry directly, or some third object, rather
  than the world tilt.
- **What produces motion** — possibly direct force/thrust/animation, not gravity-down-
  a-slope.
- **The avatar itself** — Terry is a *bipedal Citizen*, not a sphere; "rolling" may not
  be the verb at all.
- **The frame of reference** — input may be avatar-relative or camera-relative rather
  than a single global stage tilt.

So: **stop at the stub and get the mechanic from the user before building it.** The
`TerryController.OnUpdate` TODO is the seam. When the direction is settled, that's the
only file that has to change to bring the scene to life.

Open questions to put to the user before writing any controller code:
1. Does input move Terry, the world, or a third thing?
2. Is motion physics-driven (forces/gravity) or kinematic (direct position/velocity)?
3. Avatar-relative or camera-relative steering?
4. Win/lose or fail states at all, or pure sandbox toy for now?

---

## Milestone 2 — Hamster-ball mechanic (implemented)

Mechanic decided with the user and built. Terry is **kept for reuse** (the `Terry`
GameObject is disabled in `main.scene`; `TerryController.cs` is untouched). He is
replaced in the active scene by a large **glass ball** with a **primitive cube** riding
the inside.

- **`Code/BallController.cs`** — the core. Builds, at runtime: a `Rigidbody` +
  `SphereCollider` ball (dev sphere model + `Assets/materials/glassball.vmat`), and a
  primitive cube occupant on the inner surface.
  - **Cube driver**: a spherical-pendulum model. WASD (camera-relative) adds a "run"
    acceleration up the inner wall; gravity's tangential component always pulls the cube
    back toward the bottom; damping settles it. Holding W ≈ 1 s climbs ~90° (then the run
    direction loses leverage near the equator, so it asymptotes there — by design).
  - **Ball roll**: the cube's offset drives the Rigidbody's `MassCenterOverride`. Real
    physics rolls the ball forward to bring that CG back under the contact point; releasing
    input lets the cube fall to the bottom and the ball coast to rest (`AngularDamping`).
  - No cube↔ball collision is simulated (intentional). Swapping cube → Terry later is just
    model + run-anim; the driver is model-agnostic. Occupant is oriented "standing on the
    wall" (up = inward normal) to make that swap natural.
  - All feel constants are `[Property]` — tune `RunAccel`/`RestoreAccel`/`Damping`/
    `MassOffsetFactor` in the editor.
- **`Code/BallCamera.cs`** — third-person follow, **horizon-locked** (never rolls); mouse
  yaws around the ball and pitches up/down. Movement input is taken relative to this
  camera's facing.

Goal: press Play, mouse to look, WASD to run the cube up the wall, watch the ball roll and
settle. Tune to taste.

## Out of scope for now

Levels/goals, multiplayer, UI/HUD, audio, scoring. Floor + Terry + an agreed input
mechanic first.
