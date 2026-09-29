# Ball

One `BallController` per lane (the `Ball` child of each `Lane` in `main.scene`). Live tuning values
are in the scene, not the code defaults.

## Scale

The ball's diameter (`2 × Radius` units) is 8.5" by definition. `LaneBuilder` derives the whole
lane's units-per-inch from it, and `BallController.UnitsToMph`/`MphToUnits` use the same mapping
(`RealBallInches`), so the HUD speed and `RollAtMph` stay true if `Radius` is retuned.

## Build and ownership

- The ball builds lazily on the first frame its lane is in use (`GameDirector.LaneActive`); until
  then it is an empty networked object.
- Build order matters: the lane's collision tag (`Lane.CollisionTag`, see [game.md](game.md)) goes on
  before the collider is created, because shapes take the tags at build time [SOURCE]. `MassOverride`
  is set after the collider, because adding a shape rebuilds mass from volume [SOURCE].
- The ball has `EnhancedCcd`; pins do not. The engine skips CCD between two bodies that both have it
  (`Rigidbody.EnhancedCcd` doc, `engine/Sandbox.Engine/Scene/Components/Collider/Rigidbody.cs`).
- Only the client whose turn it is simulates (`GameDirector.LocalIsBowling`); everyone else receives
  the networked transform.
- The glass shell (`BallVisual`) and in-ball Terry are `NetworkMode.Never` children that every client
  builds and skins itself. A replicated copy would carry the default material, because a runtime
  `MaterialOverride` does not replicate [SOURCE].

## Free drive

WASD is camera-relative. `OnFixedUpdate` applies a force at the ball's centre proportional to
`targetVel − vel`: `DriveGain` toward `wish × MaxSpeed` with input, `BrakeGain` toward zero without.
Ground friction spins the ball up to roll. The ball never self-accelerates.

`ControlsLocked` stops both drive and brake; the ball coasts on physics alone. The director sets it
when the ball's centre crosses `LaneBuilder.ThrowLineWorldX` (and also turns on
`BallCamera.Watching`). At that crossing the ball latches `ThrowLineMph` ([Sync]) for the HUD.

Past `PinDeckEndWorldX` with controls locked, `PitDamping` decays velocity exponentially so the ball
settles in the pit instead of rebounding up the lane.

## Oil and hook

Only an aimed throw hooks. `HookAmount` is nonzero only after `Launch`; it is forced to 0 whenever
controls are unlocked.

- Oil: progress `p` runs 0 at `FoulLineWorldX` to 1 at `PinDeckWorldCenter.x`. Grip is 0 up to
  `OilFrontFraction`, then smoothsteps to 1.
- Slip: the lateral component of `v + ω × (−R·ẑ)`, i.e. travel plus the contact patch's surface
  velocity from spin.
- Friction: Coulomb, up to `HookAccel × grip`, opposing the slip, zero inside `HookSlipDeadband`.
  It is applied at the contact point, so the same force that curves the path revs the side spin
  down. The hook is emergent from spin and self-limits once the ball rolls on its new line.
- While an aimed throw coasts, angular damping drops to `HookCoastAngularDamping` so the revs survive
  the oiled front.

The ball's side spin at launch comes from `ThrowAimer.SideRotationRate`, not from `HookAmount`.

## Terry inside

Terry is visual only: no collision with the ball, no effect on its motion.

- Placed every frame with his pelvis bone on the ball centre, facing horizontal travel.
- Enabled only while the active bowler's `TerryAvatar.Carried` is true (the crane has scooped them in).
- Dressed in the active bowler's avatar via `ClothingContainer` (local user or their connection).
- Animation from ball speed: walk→run ramp up to `RollAtMph`, then the Citizen `Roll` special move.
  `PlaybackRate` also scales the animgraph's own pose crossfade, so the rate dips to
  `TransitionPlaybackRate` for `TransitionTime` around each run↔roll swap.
- Tagged `occupant` for `OccupantSilhouetteCapture` (see [rendering.md](rendering.md)).

## Crane

`SetCraneMode(true)` drops the collider and gravity and zeroes motion while the director flies the
ball kinematically (`CraneMoveTo`). `CraneColliderOff` is [Sync] and mirrored in `OnUpdate`, so every
client's collider drops, not just the owner's.

## Aimed throw

`ThrowAimer` and `ThrowAimHud` live on the client-local `LocalHud`. The aimer resolves the local
player's lane each frame (`Lane.ForLocalPlayer`) and acts only when `GameDirector.AimMode` (lane-wide,
synced) is on, the local player is bowling and carried, the round is `Rolling`, and the game is not over.

1. Setup: ball held (`HoldAt`, gravity off) at `FoulLineWorldX − 1.5R`, 2 units above the platform,
   `ControlsLocked` on.
2. Four steps, each a 0..1 caret moved by A/D or mouse yaw (0 = screen-left): LINE (lateral start),
   AIM (±`MaxAimDegrees`), POWER (`MinLaunchSpeed`..`MaxLaunchSpeed`), SPIN (−1..+1). Click/Space
   commits a step; right-click/Backspace steps back. Input freezes while chat, music or controls are open.
3. `Launch`: velocity along the aim; angular velocity = forward roll at `LaunchRollFraction × v/R`
   minus `dir × spin × SideRotationRate` (side rotation about the travel axis). `HookAmount = spin`.
4. The committed meter stays up until the throw-line crossing ends the `Rolling` state.

Leaving the active window mid-line-up calls `EndHold` and unlocks controls, so the ball is never
stranded gravity-off. The aim arrow is a procedural mesh on a `NetworkMode.Never` child, visible only
to the bowler.

Meter state is published two ways: as properties on `ThrowAimer` for `ThrowAimHud` (same object), and
as `[Sync]` `Aim*Net` fields on the bowler's `TerryAvatar` for the spectator meter (`AimFeedBanner` over
the settee feed board).

## Camera

One `BallCamera` for the whole alley (on Lane 1's `Camera` object), following
`Lane.ForLocalPlayer`. It drives the view only while the local player is bowling and carried;
otherwise the `PlayerController` owns the camera and `BallCamera.OnUpdate` leaves it alone.

- Play: third-person behind the ball, horizon-locked, mouse yaw/pitch. Look freezes while chat or
  music is open.
- Lining up an aimed throw (`ThrowAimer.LiningUp`): yaw pinned down-lane (+X), `AimPitch`, `AimHeight`;
  the lateral anchor eases onto the ball's Y. In free drive the anchor follows Y exactly.
- `Watching`: blends to a vantage `WatchBack` behind and `WatchHeight` above the pin deck.
- `OnPreRender` runs every frame and eases the hand-off between the walking view and the ball view over
  `TransitionSeconds` on each `Carried` edge.

## Ball designs

`BallDesign` is a `BallPattern` (HalfHalf, Polkadot, Cubes, Galaxy) plus primary and accent hues.

- Persistence: client-local `balldesign.json` in `FileSystem.Data` (`BallDesign.Load`/`Save`). The
  designer applies edits live without saving; its Save button and closing the designer
  (`TerryAvatar.CloseDesigner`) save.
- Sync path: `TerryAvatar.ApplyDesign` sets the avatar's `[Sync]` design fields and calls
  `GameDirector.SetBallDesignRpc` (`[Rpc.Owner]`), re-asserted periodically so it survives a director
  handover. The director stashes designs per connection and publishes the active bowler's to its
  `[Sync]` `ActiveBall*` fields. The ball skins from the director, not the avatar, because the
  director's fields reach late joiners.
- Skin: `BallController.SkinToBowler` regenerates only when the design changes. `BuildColorMap` makes a
  512×256 equirectangular texture (the dev sphere's UV layout) that goes to the ball's own copy of
  `materials/glassball_poke.vmat` (`g_tColor`) and to `ShadowBall.ColorMap`, so shell and shadow match.
  Alpha is forced to 255 because the glass reads it as opacity.
- Designer: `BallDesignerStation` podiums on each settee and on the concourse open `BallDesigner.razor`.
  Its preview (`BallPreviewPanel`) renders its own `SceneWorld` through `ScenePanel`, not a
  `CameraComponent`, so it cannot take over the main view; it uses a copy of the same glass material
  and a copy of `main.scene`'s sun, skybox and envmap.
