# CLAUDE.md — Terryball

An s&box bowling alley: Terry rides inside a glass ball down procedurally built lanes, and players
share the alley over s&box networking, taking turns on each lane's roster.

General s&box engine lore (the API whitelist, panel layout, scene and networking rules, project
setup, vendoring) lives in `~/.claude/sbox.md` and is not repeated here.

---

## The code is the truth; prose is a claim about it

Docs, comments and docstrings are read by the next session as fact. A false or stale sentence
costs more than a missing one, so:

- **When prose and code disagree, the code wins.** Fix or delete the prose in the same change
  that notices it. Never reason from prose you have not checked against the code it describes.
- **Canonical contracts outrank everything else**, this file included:
  - **skafinity's `Code/Engine/**`** for the music (the vendored `gamah.skafinity` library).
- **Third-party behaviour we observed rather than read in a published contract is `[SOURCE]`**:
  inferred from an implementation, a changelog, a forum answer or an experiment, and liable to
  change with any release. Re-derive it rather than trusting the sentence. A published contract
  (the s&box docs, sbox-public source) is cited by link or path instead.

## Writing docs, comments and docstrings

A sentence stays only if all three hold:

1. **It is true of the code as it stands**, and checkable against the code beside it.
2. **The code cannot say it**: the constraint a value or a shape is built around, a behaviour
   outside the repo (s&box, Steam), or an interface other code depends on.
3. **It does not go stale on its own.**

That rules out:

- **History.** No "used to", "was N", earlier attempts, reversed decisions, dates, or branch
  names. `git log` holds it.
- **Symptoms and incidents.** State the constraint in the present tense, not the failure that
  taught it.
- **Measurements as story.** One number may stand where a constant would otherwise look
  arbitrary. No timestamps, run logs, hardware, build numbers used as evidence, or time estimates.
- **Pointers to things that get deleted or reused**: `PLAN.md` rows or ranks, issue status,
  sessions, branches.
- **Closed questions and negative results.** Code that does not exist needs no explanation. Keep
  one only if a reader would otherwise rebuild it, and then as one line.
- **Future-shaping** ("later", "until we ship", "COMING SOON"). State boundaries as present
  facts; intentions are `PLAN.md` rows.

Mechanism goes in a docstring beside the code. A file in `docs/` holds what spans files:
contracts, invariants, how the pieces fit, how to operate them. When cutting, delete rather than
reword; do not append what you just learned to the end of a doc, edit the section that owns the
fact.

---

## Where things are written down

| file | what is in it |
| --- | --- |
| `docs/game.md` | Lanes, the lane lobby, Play and Practice rules, a turn step by step, pin judging and respotting, the lane displays, announcer calls. |
| `docs/networking.md` | Authority (host, lane director, bowler up), claiming and releasing a lane, ball and pin ownership, the RPCs, leavers, what is synced and what stays local. |
| `docs/ball.md` | Ball scale and physics (drive, oil and hook), Terry inside the ball, the crane, the aimed throw, `BallCamera`, ball designs. |
| `docs/alley.md` | The procedural build: scene roots, unit scales, lane layout, build timing, model and material assignment rules, regenerating textures. |
| `docs/rendering.md` | Engine shadow facts, the coloured ball shadow (`ShadowBall`, `BallShadowTintPP`, the shader), sky brightness. |
| `docs/ui.md` | The local HUD, podiums and what each opens, station signs, `WorldPrefs`, `WorldTheme`, hints, chat. |
| `docs/audio.md` | Ball and pin audio, the reverb bus, the announcer and regenerating its clips, skafinity music, voice. |
| `PLAN.md` | The ranked table of work to start. Nothing else in the repo may cite it. |

Design decisions are ours to make, and a settled one is written into the `docs/` file that owns
it so the next session builds on it instead of re-deciding.

---

## Stack

| Layer | Choice |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels, screen-space and `WorldPanel` |
| Networking | s&box peer networking; up to 16 players (`terryball.sbproj`) |
| Storage | `FileSystem.Data` (persistent local JSON) |
| Music | `gamah.skafinity`, vendored as source under `Libraries/` |

### Engine source and docs

`../sbox-docs/docs/` is the manual and `../sbox-public` the engine source: read them instead of
recalling an API.

## History is written to be public

The repo is private and may be opened, which publishes its whole history. A secret committed
and then deleted is still public.

- **Never commit a secret.** No tokens (a game server token included), auth tickets, or real
  player ids in fixtures or logs.
- **Commit messages are for strangers.** Explain the change, not the session it came from.
- **Provenance stays clean.** Every asset is ours (CC0, recorded) or documented with its licence.
