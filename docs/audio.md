# Audio

Sample provenance and licences are in `Assets/sounds/SOURCES.txt` and
`Assets/sounds/announcer/SOURCES.txt`. Every clip is loaded at runtime with `SoundFile.Load` into
a `SoundEvent` built in C#; the project has no `.sound` assets.

## Ball

`BallAudio` rides the ball (`BallController` creates it). It plays `sounds/ball/throw0.wav` once
when the ball crosses `LaneBuilder.FoulLineWorldX` and re-arms once the ball is back behind it.
The throw pool holds only pin-free clips, or a random pick plays pins on the throw.

The roll is synthesised: filtered noise written to a positional `SoundStream`, silent until the
throw, with level from speed, a once-per-revolution pulse, and a wobble from
`BallController.SteerEffort01`. Speed comes from the world-position delta, not rigidbody velocity,
so every client hears the same roll; a proxy's rigidbody velocity can read ~0 [SOURCE].

## Pins

`LaneBuilder` puts one `PinImpactAudio` bank on each lane and a `PinImpactListener` on each pin.
A listener classifies the contact (pin, ball, structure) and reports it; the bank gates it
(speed, impulse, per-frame cap, coalescing by time and position; ball hits bypass the cap and
coalescing), then plays clack/thud samples from `sounds/pins/`. Pin-on-pin contacts report from
both pins and are deduplicated by coalescing.

Pin physics run only on the bowler's machine, so the bank sends each play through
`GameDirector.NetPinImpact` (unreliable broadcast) and every client calls `PlayRaw`. Offline, it
plays locally. `GameDirector.PinAudioQuietUntil` silences settling contacts after a rack.

`ClackVariants` and `ThudVariants` must equal the file counts in `Assets/sounds/pins/`:
`SoundFile.Load` returns a non-null handle that plays silence for a missing file [SOURCE].

## Reverb bus

`AlleyReverb`, on the `World` object in `main.scene` (which overrides `Wet`), creates a `Pins` mixer
as a child of `Game` and inserts a `DspProcessor` on it. Pin sounds target that mixer, so only
pins are wet; ball, announcer, UI and music stay dry. `Wet` and `DryLevel` are independent
absolute levels, back-solved into mixer volume and the processor's dry/wet `Mix`; their sum must
stay ≤ 1. The processor is inserted directly because the `DspVolume` path is listener-gated and
disabled in the editor [SOURCE]. Presets with a delay line (`cavern.*`, `outside.*`) echo; use a
diffuse reverb-only preset.

## Announcer

`GameDirector` classifies each settled ball into a call id and broadcasts `Announce`. Each client
plays it through the lane's `AnnouncerAudio` only when that lane is `Lane.ForLocalPlayer`, as a
2D (`UI = true`) sound. Each call loads two takes, `sounds/announcer/fx/<call>_0.wav` and `_1.wav`,
into one event so playback alternates randomly. `AnnouncerAudio.Calls`, `reslice.py`'s `CALLS`
and the files in `dry/` and `fx/` name the same 25 calls.

### Regenerating the clips

Both scripts need `ffmpeg` and `ffprobe` on PATH and run from any directory.

1. `python3 Assets/sounds/announcer/reslice.py [out_dir]` cuts `~/vo.m4a` (the source take, not in
   the repo; every call spoken twice in `CALLS` order) into `dry/<call>_<take>.wav`: mono, highpass,
   loudnorm. It exits listing the segments if silence detection does not find exactly 50; tune
   `NOISE`/`SILD`.
2. `python3 Assets/sounds/announcer/process_fx.py` rebuilds `announcer_reverb_ir.wav` and writes
   `fx/` from `dry/`: voice EQ and compression over a convolution tail, stereo 48 kHz. `fx/` is
   what the game plays.

`_review_dry.wav` and `_review_fx.wav` are listening aids; the game does not load them.

## Music

The soundtrack is `Libraries/gamah.skafinity`, vendored source of the skafinity library
(github.com/gamah/skafinity, `sbox-library/Skafinity/`). Update it by syncing from upstream;
never edit the vendored copy. Its `Code/Engine/**` and public types are the contract.

`LocalHud` creates a `SkafinityPlayer` on the `Music` mixer and a `SkafinityMusicPanel` created
disabled. The engine scales only a mixer named `Music` by the music-volume preference, and skips
that scaling in the editor [SOURCE]. `MusicScreen` drives both: N enables and opens the panel and
frees the cursor, closing disables it (a disabled panel shows no ♪ button to capture the
pointer); M toggles `SkafinityPlayer.Enabled`, which is the mute. `MusicScreen.IsOpen` and `Muted`
feed `BallCamera` and `HudHints`.

The board is themed by assigning `SkafinityTheme.Accent` from `WorldPrefs.ThemeColor` whenever the
hex changes. AUTO and an unparseable hex pass null, which is the library's neutral palette, not
terryball's amber.

## Voice

Proximity voice uses `TerryVoice`, a `Voice` subclass that `AlleyServer.SpawnAvatar` puts on
every avatar in `Manual` mode. All state is client-local in `VoicePrefs` cookies:
`terryball.voice.enabled` (default off), `terryball.voice.muted` (SteamIDs as `long[]`) and
`terryball.help_seen`.

- **Hearing** is receiver-side: `TerryVoice.ShouldHearVoice` drops everything while voice is off
  and drops muted senders.
- **Transmit** is owner-side: each frame `VoiceScreen` sets the local `TerryVoice` to `AlwaysOn`
  with the `voice` push-to-talk binding when on, or `Manual`, not listening, with the unbound
  `NoVoiceInput` when off. The player's s&box voice mode still decides whether the mic sends.
- **Range**: each frame `VoiceScreen` writes `WorldPrefs.VoiceRangeRoaming` to every
  `TerryVoice`'s `Distance`, with a linear falloff and volume 2. Falloff is computed on the
  receiver, so this is the local player's hearing range. The engine's default falloff is
  front-loaded [SOURCE].

Keys (`VoiceScreen`): G toggles voice while walking (G exits the ball while bowling); B opens the
mute roster (`VoicePanel`), navigated with arrows, Enter/Space to mute, Esc/B to close. While the
roster is open `VoiceScreen` clears the `chat` action and turns off the walker's
`UseInputControls`, restoring them on close.
