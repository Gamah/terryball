# UI

## Local HUD

`LocalHudSystem` (a `GameObjectSystem`) creates one `LocalHUD` object per client in every play
scene, `NetworkMode.Never` and `NotSaved`. `LocalHud` builds every screen-space panel onto it in
code: the lane HUD, the podium screens, `SkyBrightnessApplier`, `ThrowAimer`, the music player and
board, voice, and `HudHints`. Nothing on it replicates, so mute, music, theme and open-panel state
are per client. A panel that needs a lane resolves it with `Lane.ForLocalPlayer`, so the HUD
follows the player between lanes.

## Podiums

Each podium is a component carrying only an `InteractRange`; the UI is a panel on the local HUD.

| Podium | Station | Where | Opens |
| --- | --- | --- | --- |
| LANE | `LaneStation` | each lane's settee | `LaneControls` |
| BALL | `BallDesignerStation` | settee and concourse | `BallDesigner` |
| HELP | `HelpStation` | settee and concourse | `HowToPlay` |
| TELE | `TeleportStation` | concourse | `TeleportPanel` |
| SETTINGS | `WorldSettingsStation` | concourse | `WorldSettings` |
| MORE | `MoreStation` | concourse | `MorePanel` |

`LaneBuilder` builds the settee podiums, `SpawnAreaBuilder` the concourse ones; both set
`InteractRange`, so the component defaults are not the live values.

`TerryAvatar.UpdateControlsStation` owns engagement. With nothing open, the nearest station in
range across all six kinds wins the walk-up prompt (`Near*`) and the `use` press (E). An open
screen frees the cursor and closes on E, Esc, its CLOSE button (`RequestClose*`), or walking out
of range. Riding the ball closes every screen. Adding a podium kind means a station component, a
`Nearest*` scan and a kind branch in `TerryAvatar`, and a panel on `LocalHud`.

`VoiceScreen` opens `HowToPlay` once on first run via `RequestOpenHelp`, which marks it sticky so
the range check does not close it; `VoicePrefs.HelpSeen` records that it was shown.

`TeleportPanel` lists every `Lane` ordered by world Y as "Lane n" and calls
`TerryAvatar.TeleportToLane`, which drops the player on that lane's settee.

`MorePanel` holds the outbound links (`DiscordButton`, `GamesButton`, `SkafinityButton`). Game
code has no API to open a URL, so each link prints its URL in full and copies it with
`Clipboard.SetText`; the displayed string and the copied one are the same constant. Each helper
keeps its own `SinceCopied` clock.

### Station signs

`StationSign` is the WorldPanel label over each podium. Its markup is sized to
`StationSign.PanelPixels` and `WorldWidth`, which both builders read; one font size fits the
longest label (SETTINGS) with `nowrap`, so a longer label clips. The border is the podium's
identity colour, passed by the builder; an empty `Accent` (MORE) takes the world theme.

## World settings

`WorldPrefs` is client-local and `Cookie`-backed: every getter reads its cookie, every setter
writes it and bumps `Revision`. Nothing in it is networked.

| Setting | Cookie key | Consumer |
| --- | --- | --- |
| `ThemeColor` (`#rrggbb`, `""` = AUTO) | `terryball.world.theme` | `WorldTheme`, `MusicScreen` |
| `SkyBrightness` (0.2–1.5) | `terryball.world.sky_brightness` | `SkyBrightnessApplier` |
| `VoiceRangeRoaming` (300–5000u) | `terryball.world.voice_range` | `VoiceScreen` |

`SkyBrightnessApplier` re-applies when `Revision` changes (its last-applied starts at -1, so it
applies once on load); `WorldSettings` folds `Revision` into `BuildHash`, themed panels fold
`WorldTheme.Hash`. `VoiceScreen` re-asserts the voice range every frame. `SkyBrightnessApplier` scales the authored
Sun and skybox colours, captured once, by the setting. The sky floor is 0.2 because the Sun is the
alley's only light.

## WorldTheme

`WorldTheme` derives the shared panel chrome (fills, borders, dividers, accent, cell) from one
accent: `WorldPrefs.ThemeColor`, or amber `#f1c40f` on AUTO. The text ramp is neutral white-alpha.
Panels bind these as inline `style=` values; a compiled `<style>` block cannot follow a runtime
colour. Podium identity colours are not themed. Panels themed from it: `WorldSettings`,
`MorePanel`, `StationSign`, `LaneConsole`, `PinReadout`. The music board is themed separately (see
[audio.md](audio.md#music)).

## Hints

`HudHints` is the one bottom-left keycap stack: Chat, M, N, G, B, and R while not bowling. It has
no state and no pointer events. The Chat keycap reads the binding with `Input.GetButtonOrigin`;
M/N/G/B/R are raw key checks in `MusicScreen`, `VoiceScreen` and `TerryAvatar`, so the hint text
is spelled out and has to match them. It hides while the music board is open or the lane controls
have the cursor. `VoicePanel`'s roster sits above it (`bottom: 100px`); the two offsets move
together.

## Chat

Chat is the engine's overlay (feed and typing box, the `Chat` action). `ChatPanel` draws nothing;
it exists for `ChatPanel.IsOpen`, which is always false and is still read by the input gates in
`BallCamera`, `TerryAvatar`, `GameDirector`, `ThrowAimer`, `MusicScreen` and `VoiceScreen`.
