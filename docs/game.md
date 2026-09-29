# The game

How bowling works across `GameDirector`, `TerryAvatar`, `Pin` and the lane panels. Tuning values
are `[Property]`s tuned in the scene; read `Assets/scenes/main.scene` for the live numbers.

## Lanes

Each lane is a `Lane` subtree holding its builder, ball, director, monitor camera and announcer.
Lane components find siblings with `Lane.Of`, never a scene-wide scan, and each `GameDirector`
runs only its own lane. A lane's ball and pins carry the tag `lane{Index}`; `Collision.config`
makes different lane tags ignore each other, so a ball only hits its own rack.

A player's lane (`TerryAvatar.ActiveLane`, `Lane.ForLocalPlayer`) is the lane they have joined,
else the lane whose station they are standing at. Every screen-space panel follows it.

## Lane lobby

A lane starts in `GamePhase.Lobby`. Standing within a `LaneStation`'s range and pressing E opens
`LaneControls` with the cursor freed.

- **JOIN / LEAVE** (lobby only) writes the local avatar's own `Joined` and `JoinedLaneId`.
- **Lobby picks** — Throw (FREE / AIM), Ball Crane (ON / OFF), Mode (PLAY / PRACTICE), Drill
  (Practice only). A player who is not the lane's director holds them as local pending picks and
  passes them into `StartGame`, which applies them before tip-off. The director's own toggles
  write straight through, mid-game included.
- **START** — any player, lobby only. Claims the lane's director (see `networking.md`) and tips off.
- **STOP** — director only. Clears the cards, drops to the lobby, re-racks, parks the ball and
  hands the director back to the host.
- **RESET** — director only. Tips off a fresh game on the same lane without visiting the lobby.

A lane is in use (`LaneActive`) while a game is on or anyone has joined it. The host spawns a
lane's pins only once it is in use; they then stay.

## Play mode

START rosters the avatars joined to this lane, up to `MaxPlayers`; with none joined it does
nothing. Bowlers take turns within a shared frame, then everyone steps to the next frame.

- Frames 1–9: a strike ends the frame; otherwise a second ball at the standing pins.
- Frame 10: a strike re-racks for ball 2; a strike or spare earns ball 3, on a fresh rack after a
  double strike or a spare, else at ball 2's leave.
- Scoring is standard ten-pin; a frame's running total shows once its bonus balls are in.
- After the last bowler's frame 10 the final board stays up and the ball is parked until STOP.
- With one bowler, the bowler stays in the ball between frames; with more, the crane sets each
  bowler back on the settee at the end of their turn.

## Practice mode

No frames and no scorecard: the roster is a throw queue, front = up. START opens the lane with an
empty queue. **GET IN LINE** (shown while a practice lane is running and you are not queued) joins
you to the lane and calls `JoinQueueRpc`. Each enqueue is good for one turn; the bowler is dropped
from the queue when it ends, and anyone no longer joined to the lane is culled. An empty queue
leaves the lane open and idle until STOP.

| Drill | Rack | Balls |
|---|---|---|
| Regular | full | up to 2 (a strike ends it) |
| Strike | full | 1 |
| Spare | random non-split leave from `PracticeLeaves.Spares` | 1 |
| Split | random split leave from `PracticeLeaves.Splits` | 1 |

`PracticeLeaves` checks at load that every split entry is a split and no spare entry is, and logs
a warning otherwise. Practice balls are booked and announced through the same path as Play; the
classifier treats a drill's first ball as a fresh rack.

## A turn

1. The director racks the deck while it owns it, then sets `ActiveBowlerId` and bumps `TurnSerial`.
2. The bowler's client sees the new serial and runs `BeginTurn`: it takes the ball and pins, then
   the crane fetches it (fly over, sink, scoop, fly to the run-up, roll). A bowler already in the
   ball is flown straight from the pit to the run-up.
3. **Rolling** — the player drives the ball. When its centre crosses the throw line, **Watching**
   locks controls so it coasts in, and the camera pulls back.
4. The watch ends once the pins are still and the ball is either past the pin deck or still, held
   for `SettleQuietSeconds` after `MinWatchSeconds`; `WatchSeconds` caps it.
5. The bowler counts the pins, reports them (`ReportBall` → `BookBall`), and applies the frame
   rules locally: either sweep-and-respot and fly the ball back to the run-up, or end the turn
   (crane drop-off at the seat, then `EndTurn` → `EndTurnRpc` → next bowler).

A bowler grabbed on the settee is set back on the same spot; one grabbed anywhere else is set down
at the settee middle. With Ball Crane off, every leg teleports instead of flying; the scoop, drop and
roll still happen. **G** (bowler, chat closed) calls `LeaveBallRpc`, which ends the turn with
whatever balls are already booked.

## Pins

A pin reads **down** when it is tipped past `TiltThresholdDegrees` from vertical, is more than
`DisplacementThresholdInches` off its spot horizontally, or was swept earlier in the frame. A pin
mid-rack reads **up**. A ball's pinfall is the down count minus the pins already down when it was
thrown.

`Pin.Phase` drives the respot animation on every client:

- **Down** (`Sweep`) — fades out where it fell, then its owner disables it.
- **Racking** (`RackTo`) — fades in if hidden and glides onto its spot. Pins start in a stagger,
  pin 10 first and the head pin last, and the whole batch returns to physics together once the
  last one lands.
- **Up** — normal physics. An instant rack (START, RESET, STOP) snaps straight to Up.

Pin impact sound is muted from a rack until `PinRackQuietSeconds` after the batch goes live.

## Surfaces

Every pin readout draws from `GameDirector.PinDownBits` and the `RackRows` layout (head pin at the
bottom, 10 on the left), so they agree on every client.

| Surface | Where | Shows |
|---|---|---|
| `PinStatusOverlay` | screen, top-left; only when the local player has joined a lane | rack state, and throw speed latched at the throw line (`BallController.ThrowLineMph`) |
| `ScoreBoard` | screen, top-right; only when joined | ten-frame card per bowler, or PRACTICE banner and queue |
| `LaneControls` | screen modal at a lane station | lobby, picks, card or queue, game buttons |
| `BowlerHud` | screen, while the local player is bowling | the G exit prompt |
| `PinReadout` | world, above the backstop | the rack, large; WATCHING during the watch |
| `AnnouncerCallout` | world, over the lane in front of the pins | the latest call's text for a few seconds, then fades |
| `LaneConsole` | world, settee score screen | the lane's card or practice queue; its bottom button strip is inert |
| `MonitorFeed` | world, settee feed screen | `LaneMonitorCamera`'s render: the active bowler's camera pulled back, or a chase of the ball when nobody is up |
| `AimFeedBanner` | world, above the feed screen | the active bowler's guided-throw meter (LINE / AIM / POWER / SPIN) while they set up an Aim throw |

### Announcer calls

The director classifies each booked ball (`ClassifyCall`) and broadcasts one call id. On a fresh
rack: `strike`, or `double` … `six-pack` and `perfect-game` for a strike run (counted in strike
balls, 10th-frame bonuses included); a single pocket strike is sometimes `pocket`; `gutter` for
none; `split` when the head pin is down and the standing pins form two or more disconnected
groups; `so-close` for 9 unless the lone standing pin is the 7 or 10; otherwise the count. At a
leave: `spare`, or `nice` for a converted split (always) or a 3+ pin spare (sometimes); `miss`
for none; else `good-try` or `open-frame`. Splits are circled on the scorecards.
