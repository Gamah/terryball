# Alley

The alley is built in code at runtime. `main.scene` authors only the roots: `World` (`FloorBuilder`,
`AlleyServer`), four `Lane` roots at y = −4500, −1500, 1500, 4500 (each with `LaneGeometry`/`LaneBuilder`,
`Ball`, `Director`), the sun and skybox, and `SpawnArea` (`SpawnAreaBuilder` + its `SpawnPoint` child).
All built geometry is `NotSaved` and client-local: every client builds an identical copy.

## Units

- Lane scale: `LaneBuilder` sizes everything in real inches × `u`, where `u = ball diameter /
  BallDiameterInches` (200 u / 8.5" ≈ 23.5 u/in with the scene's `Radius` 100). Exposed as
  `UnitsPerInch`.
- Person scale: the settee, podiums, monitors and concourse use `cu = BowlerHeightUnits /
  BowlerHeightInches` (1 u/in, the Citizen as a 6 ft person). The lane is ~23× a person.
- Pin gravity: `Rigidbody.GravityScale = 386·u / |world g| × PinGravityScale`, i.e. a real 1 g at lane
  scale. Pins only; world gravity is untouched.

## Layout

Lane-local, +x down-lane, floor top at z = 0, foul line is the x datum for every real dimension.

- Along x: floor gap (`SpawnGapUnits`) → ramp (`RampSlopeDegrees`) up to the platform top `H` →
  15 ft approach → foul line → 60 ft to the head pin → pin deck (+`PinDeckTailInches`) → pit →
  backboard deck. The throw line sits `ThrowLineDistanceInches` before the head pin.
- Across y: lane bed, gutters (recessed `GutterDepthInches`), barrier curbs, buffer margin. The approach
  shoulders fill to platform width before the foul line; gutters, barriers and buffers start at it.
- The lane bed is one collider from the back of the approach to the end of the deck, so there is no
  seam at the foul line. Boards, foul/throw lines and arrows are collider-less decals.
- The pit floor is a slab clamped a hair above z = 0. Pit and backboard use `BackstopElasticity` /
  `BackstopFriction` (dead, slick) so the ball settles.
- The backboard deck's top (`H + BackboardHeightInches`) is the settee floor. The settee runs away from
  the lane (+x) and faces back up-lane (`SetteeFaceRotation`). On it: two benches, the LANE / BALL / HELP
  podiums with their stations, the overhead feed (bowler's left) and scoreboard (right) screens, the aim
  banner, the backstop pin readout and the announcer call-out.
- `SpawnAreaBuilder` lays a ground-level concourse behind all settees, one ramp from it up to each
  settee deck, and the concourse podiums (BALL, HELP, TELE, SETTINGS, MORE). It moves `SpawnPoint`
  between BALL and HELP, facing the lanes.

Consumers compare against world-space landmarks that `LaneBuilder` pushes through the lane root
transform: `RunUpWorldPosition`, `FoulLineWorldX`, `ThrowLineWorldX`, `PinDeckWorldCenter`,
`PinDeckEndWorldX`, `PlatformTopWorldZ`, `SetteeFloorCenter`, `SetteeDeck*`. Ball and director logic
compare world x, and `SpawnAreaBuilder` assumes all lanes share orientation, so lanes must differ only in
y. `SpawnAreaBuilder`'s own GameObject must stay at identity (it treats local as world).

## Build timing

- `LaneBuilder.OnEnabled` only enqueues `BuildNow` on `BuildScheduler`, which runs one job per frame
  after load. A synchronous build of every lane blocks a joining client through the network handshake
  long enough to time it out.
- `EnsureScale` and `EnsurePinModel` run on demand, so a pin that replicates in before its lane's turn in
  the queue can still configure itself.
- `SpawnAreaBuilder` waits until every lane has published `SetteeDeckTopWorldZ`, then builds once.
  `AlleyServer` calls `EnsureBuilt` before spawning a player so the spawn point is already placed.
- `FloorBuilder` builds in `OnEnabled` (`ExecuteInEditor`).

## Procedural models and networking

These rules come from observed client behaviour [SOURCE]:

- A runtime `ModelBuilder` model assigned during `OnEnabled` shows the ERROR model on a joining client.
  Assign it in `OnStart` or later, as a fresh null→model change: re-assigning the same reference is a
  no-op. The arrows get theirs at the end of `BuildNow` (with an `OnStart` fallback); pins get theirs in
  `ConfigurePin`, called from `Pin.OnStart` on every client.
- A runtime child of the networked lane root defaults to `NetworkMode.Snapshot` and replicates. Anything
  every client builds itself and that cannot serialize (procedural arrows, `StationSign`/`WorldPanel`
  screens, the ball shell and Terry) is `NetworkMode.Never`, or clients see a duplicate (ERROR model or
  an empty panel).

Pin spawning and ownership are in [networking.md](networking.md). `HostSpawnPins` also requires
`Networking.IsActive`, because `IsHost` is true with no session
(`engine/Sandbox.Engine/Systems/Networking/Networking.cs`) and a joining client would otherwise rack its
own pins under the host's.

Pin model: the render lathe is a monotone cubic through the USBC silhouette at 48 radial segments,
with non-zero tangents (a zero tangent makes the lit shader's TBN NaN). Collision is one convex hull
per coarse silhouette slab at 24 segments, because the hull cooker can reject denser hulls and silently
drop the pin's physics [SOURCE].

## Surface materials

Boxes are `models/dev/box.vmdl`, whose UVs run 0..1 per face. Each textured box gets a material copy with
`g_vTexCoordScale` set so the texture repeats at a fixed world size (`tileWorld` units per tile) across
its dominant face; `LaneBuilder.Tiled` caches copies by repeat count. The box tint multiplies over the
texture.

## Generated textures

`Assets/materials/gen_textures.py` generates every surface texture from seeded noise (no photo sources,
CC0). Each material gets `<name>_color.png`, `_normal.png` (OpenGL, +Y up) and `_rough.png`, 1024²
and seamlessly tiling, in `Assets/materials/generated/`; the `.vmat` files beside the script reference
them. Seeds are fixed, so a rerun reproduces the same maps.

    python3 Assets/materials/gen_textures.py    # needs numpy and Pillow

Wood is invariant along X (the lane length): the lane bed tiles it many times down its length, and any
variation along X would show as a band at each repeat.
