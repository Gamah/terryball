# Networking

Who owns what, and how lane state reaches every client. Rules in `game.md`.

## Authority

Three tiers, each the owner of different objects:

| Tier | Owns | Does |
|---|---|---|
| Host | the session, every avatar spawn, every pin spawn, unclaimed directors | creates the lobby, spawns avatars and pins, sets ownership flags, runs each unclaimed lane's lobby |
| Director owner (per lane) | the lane's `GameDirector` object; the ball and pins between turns | roster, scoring, turn rotation, racking, every synced director field |
| Active bowler (per lane) | the lane's ball and ten pins during their turn | simulates the throw; reports each ball to the director |

`GameDirector.IsDirector` is "I own the director object, or there is no live session". Lane game
logic gates on it, never on `Networking.IsHost`.

### Session: `AlleyServer`

Exactly one, on the scene root. `INetworkListener` callbacks fire on every component implementing
it, so avatar spawning must not live on a per-lane component. `OnStart` creates a lobby when no
session exists, making a solo player the host. `OnActive` (host) builds the joiner's walking Terry
— `PlayerController`, a `Body` child with the citizen renderer, `TerryAvatar`, `TerryVoice` — and
`NetworkSpawn`s it owned by that connection. `OnDisconnected` destroys that connection's avatars.

### Claiming a lane

- The host flags each director and ball `OwnerTransfer.Takeover` and `NetworkOrphaned.Host`; pins
  are spawned with the same flags.
- **START** calls `TakeOwnership` on the director, then writes the synced state. Only START
  claims: the lobby toggles (`SetAimMode`, `SetCraneEnabled`, `SetMode`, `SetDrill`) are no-ops
  for a non-director.
- **STOP** calls `DropOwnership` (unless the director is the host), returning the lane to the host.
  Game over does not release it; the final board stays under the director until STOP.

### A turn's ownership

The ball and pins go director → bowler → director, never bowler → bowler:

1. The director takes the ball and pins back (`HostReclaimThrowOwnership`), racks, then publishes
   `ActiveBowlerId` and bumps `TurnSerial`.
2. The bowler's client takes the ball and pins (`TakeThrowOwnership`) and simulates the throw; the
   engine replicates the transforms to everyone else.
3. The bowler calls the director's `[Rpc.Owner]` methods through its avatar: `BookBall` per ball,
   then `EndTurnRpc`, or `LeaveBallRpc` for the G escape. The last two check
   `Rpc.Caller.Id == ActiveBowlerId`.

`TurnSerial` exists because the same bowler can be up twice in a row, which `ActiveBowlerId` alone
cannot signal.

### Director RPCs

| Method | Kind | Caller |
|---|---|---|
| `BookBall`, `EndTurnRpc`, `LeaveBallRpc` | `Rpc.Owner` | active bowler |
| `JoinQueueRpc` | `Rpc.Owner` | any player at a practice lane; rejects an avatar joined to another lane, tolerates one whose `JoinedLaneId` has not replicated yet |
| `SetBallDesignRpc` | `Rpc.Owner` | every avatar owner, on join, on each designer edit, and every 1.5 s |
| `Announce` | `Rpc.Broadcast` | director owner |
| `NetPinImpact` | `Rpc.Broadcast`, unreliable | the pins' owner; contact listeners fire only there, so exactly one client broadcasts |

### Handover and departure

- **Director owner leaves**: the director reverts to the host. Roster, cards and the ball-design
  cache are director-local and do not transfer. A Play lane left Playing with no roster drops to
  the lobby; Practice is exempt, since an empty queue is its idle state. Clients re-submit their
  ball design every 1.5 s, which refills the new owner's cache.
- **Active bowler leaves**: the ball and pins revert to the host. The director polls
  `Networking.Connections` for the active bowler and advances the turn when they are gone.

## What is synced

| Object | Synced fields |
|---|---|
| `GameDirector` | `Phase`, `Mode`, `Drill`, `AimMode`, `CraneEnabled`, `ActiveBowlerId`, `TurnSerial`, `RosterNames`, `RosterScores`, `RosterSplits`, `CurrentPlayer`, `CurrentFrame`, `GameComplete`, `ActiveBall*` |
| `TerryAvatar` | `DisplayName` (`FromHost`), `Joined`, `JoinedLaneId`, `Carried`, `BowlCamPos`/`Rot`, the `Aim*` meter fields, `Ball*` design fields |
| `Pin` | `Number`, `Home`, `LaneId`, `Phase`, `RackFrom`/`RackFromRot`, `RackFadeIn`, `RackDelay`, `RackReleaseAt`; transform and enabled state via ownership |
| `BallController` | `ThrowLineMph`, `CraneColliderOff`; transform via ownership |

`RosterScores` encodes a player's frames as `b,b;b;…` (balls comma-separated, frames
semicolon-separated); `RosterSplits` as `frame.ball,…`. Practice publishes names only.

**The ball's skin comes from the director, not the avatar.** A host-spawned avatar's owner write
can miss the join snapshot [SOURCE], while the director's fields reach late joiners. The director
mirrors the active bowler's submitted design onto `ActiveBall*`, falling back to their avatar's
fields, then the default. For the same reason `TerryAvatar` publishes its saved design on its
first networked `OnUpdate`, not in `OnStart`: a `[Sync]` write in `OnStart` becomes the baseline
and is never sent [SOURCE].

## Derived on every client

Computed locally from synced inputs, so every client gets the same answer without a write:

- `TerryAvatar.Bowling` — owner id equals the lane director's `ActiveBowlerId`. A non-host
  director cannot write another player's avatar, so this is never stored.
- `GameDirector.LaneActive` — Playing, or any avatar joined to the lane.
- `GameDirector.PinDownBits` — the bowler scores its own pins; spectators evaluate the replicated
  pins with the same test (`EvaluateDownBits`).
- `CalloutText` / `CalloutAt` — set by the `Announce` broadcast. The announcer clip plays only on
  clients whose own lane is that lane.

## Local only

- The screen HUD: `LocalHudSystem` builds one `LocalHud` per client on a `NetworkMode.Never` object.
- The lane's world panels, monitor cameras and `OccupantSilhouetteCapture`: `NetworkMode.Never` or
  created per client.
- Pin meshes: procedural models do not replicate [SOURCE], so every client attaches its own copy
  in `Pin.OnStart` via `LaneBuilder.ConfigurePin`. Pins spawn at the scene root and find their lane
  by `LaneId`, because a networked object under a non-networked parent does not reach late joiners
  [SOURCE].
- Avatar clothing: each client dresses every avatar itself.
- Physics collision filtering by lane tag.
