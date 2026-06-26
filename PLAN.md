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

Mechanic decided with the user and built. A standalone `TerryController.cs` stub is
**kept for reuse** (the disabled `Terry` GameObject in `main.scene`). The active scene is
a large **glass ball** with **Terry (the Citizen)** riding the inside.

- **`Code/BallController.cs`** — the core. Builds, at runtime: a `Rigidbody` +
  `SphereCollider` ball (dev sphere model + `Assets/materials/glassball.vmat`), and Terry
  (SkinnedModelRenderer + `CitizenAnimationHelper`) on the inner surface.
  - **Driver**: a spherical-pendulum model. WASD (camera-relative) adds a "run"
    acceleration up the inner wall; gravity's tangential component always pulls Terry
    back toward the bottom; damping settles him. Holding W ≈ 1 s climbs ~90° (then the run
    direction loses leverage near the equator, so it asymptotes there — by design).
  - **Ball roll**: each frame the ball gets the gravitational torque an off-centre weight
    at Terry's position would produce — `ApplyTorque(r × m·g)` — so it's zero when Terry is
    at the bottom and the ball settles. (We do *not* move the mass centre per-frame; doing
    so injected spurious motion that sank the ball through the floor as Terry settled.)
    Releasing input lets Terry fall to the bottom and the ball coasts to rest
    (`AngularDamping`).
  - **Animation**: run cycle is driven from Terry's actual world-space speed (ball roll +
    his climbing), so the legs match the visible speed. He's oriented "standing on the
    wall" (up = inward normal) for a natural lean.
  - No Terry↔ball collision is simulated (intentional).
  - All feel constants are `[Property]` — tune `RunAccel`/`RestoreAccel`/`Damping`/
    `MassOffsetFactor`/`TerryScale` in the editor.
- **`Code/FloorBuilder.cs` + `Assets/materials/floorgrid.vmat`** — the floor uses a tiled
  grid material so the ball's speed is readable as it rolls.
- **`Code/BallCamera.cs`** — third-person follow, **horizon-locked** (never rolls); mouse
  yaws around the ball and pitches up/down. Movement input is taken relative to this
  camera's facing.

Goal: press Play, mouse to look, WASD to run the cube up the wall, watch the ball roll and
settle. Tune to taste.

## Milestone 3 — Bowling lane (implemented)

- **`Code/LaneBuilder.cs`** (on the `Lane` object) builds, at runtime, a bowling alley
  scaled so the ball reads as a real 8.5" bowling ball. It derives units-per-inch from the
  ball's actual size (200 u = 8.5" → ~23.5 u/in), then builds in real dimensions:
  - Two cube **walls** a lane-width apart (41.5" → ~976 u gap), `WallHeight` 150 u tall,
    running down the lane (+X).
  - **10 cube pins**, each a `Rigidbody`, in the regulation triangle (12" centres) at the
    foul-line-to-head-pin distance (60 ft → ~16,940 u down +X). The ball knocks them over.
  - All real dims are `[Property]` in inches; walls/pins follow the FloorBuilder rule
    (collider on the uniform root, visual on a non-uniform child).

## Note: scale vs. feel (deliberate, stylized)

The ball is **200 units** (sized so Terry fits inside), and we *treat* that as a real 8.5"
bowling ball for building the lane. But a realistic bowling **speed** (~15–20 mph) at that
scale works out to **~8,300 u/s** — a 200-unit ball spinning ~40 rev/s, which the gentle
mass-center-offset drive can't reach and the physics wouldn't simulate stably anyway.

**Decision:** keep the current chaotic, indirect control scheme and treat the simulation
as **stylized, not literally accurate**. The crash is tuned by *feel* (satisfying scatter),
not to hit a real mph. The on-screen MPH readout (`BallController.ShowSpeed`, derived from
`RealBallInches`) is a fun gauge, not a target. Tuning knobs: `AngularDamping`,
`MassOffsetFactor`, `RunAccel`/`Damping`, ball `Mass` vs pin `PinMass`.

## Milestone 4 — Multiplayer lane director (implemented, UNVERIFIED in-editor)

The `GameDirector` is now a host-authoritative lane that runs the whole alley for up to 16
players. Built blind against the engine source — **not yet compiled/run in the s&box editor.**

- **Bootstrap + avatars** (`GameDirector` + `TerryAvatar`): auto-hosts on load and spawns a
  walking `PlayerController` Citizen per connection on the settee. `GameNetworkType` is now
  `Multiplayer` and client object-spawn/refresh/destroy permissions are locked off (host is the
  sole spawner).
- **Lobby/roster** (`LaneControls.razor`): players Join/Leave (own avatar flag); the host runs
  START/STOP. Game state (phase, per-player encoded frames, current player/frame, game-over) is
  `[Sync(FromHost)]` on the director (which is `NetworkMode.Object` in the scene), so every
  client's scoreboard + settee monitor match. Turn rotation cycles bowlers; "{name} is up" banner.
- **Throws — approach B** (active bowler is authoritative): the bowler runs the whole throw on
  their own local ball + pins, reports each settled ball to the host (`ReportBall` → `HostBookBall`)
  and streams ball+pin transforms to spectators (`StreamThrow` → `ApplyStreamedThrow`). Walking
  Terry hides while you're in the ball.
- **Camera-feed screen** (`LaneMonitorCamera` + `MonitorFeed.razor`): the settee has two overhead
  screens — scoreboard (left) and a live chase-cam of the active ball (right), rendered to a texture.

### Known gaps / risk areas to check first in-editor
- Director `[Sync]`/scoreboard actually replicating (scene-object networking).
- RPC string args + transform-stream fidelity (20 Hz, no interpolation).
- Render-target camera producing a feed; perf of the extra per-client scene render.
- Clean hand-off when a *remote* player's turn ends.

## Milestone 6 — Per-lane authority, Terry-scale settee, walk-up controls, real avatars (implemented, UNVERIFIED in-editor)

- **Per-lane authority** (`Lane.cs`): every lane is now a self-contained subtree under one `Lane 1`
  root carrying a `Lane` component (geometry, ball, camera, director, feed-cam are its children). All
  cross-component lookups go through `Lane.Of(this)` / `Lane.Local(scene)` instead of scene-global
  `GetAllComponents<…>().FirstOrDefault()`, so each director is authoritative only for ITS ball/pins/
  active-bowler. `LaneBuilder` now builds in lane-LOCAL space (and exposes its landmarks through the
  lane root transform via `ToWorld`), so a second lane is just the same subtree dropped at an offset.
  Only one lane is instantiated; the lookups are written for N. (Avatar→lane assignment and per-lane
  HUD gating are the remaining N-lane seams, noted in code.)
- **Terry-scale settee** (`LaneBuilder`): the settee + benches + overhead monitors are sized to the
  walking Citizen (real inches × a citizen units-per-inch derived from `BowlerHeightUnits`), not the
  giant ball — the people standing on it are the ruler now. (The big backstop pin board stays
  lane-scale; it's read by the ball from down-lane.)
- **Settee screens**: from the seated bowler's view (facing +x, so +Y is "left" in s&box) the LEFT
  overhead screen is the live ball-camera feed (`MonitorFeed`) and the RIGHT is the scoreboard
  (`LaneConsole`).
- **Walk-up lane controls** (`LaneStation` + `TerryAvatar` + `LaneControls.razor`): screen-space
  buttons can't be clicked under the FPS look-lock, so a control podium/`LaneStation` sits on the
  settee; walking within range shows a "Press E" prompt, and E frees the cursor
  (`Mouse.Visibility = Visible` + `PlayerController.UseLookControls = false`) and reveals the JOIN/
  LEAVE/START/STOP strip. E/Escape/walking away restores look + cursor. (Same engage pattern as
  rotaliate's settings boards.)
- **Full player avatars** (`TerryAvatar` + `BallController`): each walking Terry — and the Terry
  riding inside the ball — is dressed in its player's complete Steam avatar, **including workshop /
  Steam-inventory (community-market) cosmetics**. Clothing objects aren't networked, so every client
  dresses every avatar locally: own = `CreateFromLocalUser`, others = `CreateFromConnection`, always
  via `ApplyAsync` (downloads missing items; sync `Apply` drops them). The in-ball Terry re-dresses to
  the active bowler whenever the spotlight moves.

### Known gaps / risk areas to check first in-editor
- The networked `Director` (NetworkMode.Object) is now a CHILD of the non-networked `Lane 1` scene
  object — confirm `[Sync]`/scoreboard still replicates (both are stable scene objects, so it should).
- Cursor actually frees on E for the STOCK `PlayerController` (verified the lever is
  `Mouse.Visibility = Visible`, but confirm clicks register on the buttons).
- Avatar `ApplyAsync` downloading workshop/inventory items for remote players; dressing the in-ball
  Terry on turn changes without hitches.
- Settee/monitor feel at the new Citizen scale next to the giant lane.

## Out of scope for now

Levels/goals, audio, polish. Multiplayer scaffolding is in (Milestones 4–6) but needs an
in-editor compile + smoke test before further work.
