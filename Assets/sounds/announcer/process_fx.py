#!/usr/bin/env python3
"""
Generate the in-game "announcer" effect from the dry VO clips.

    dry/*.wav  ->  fx/*.wav   (same filenames, stereo 48 kHz)

Recipe: a bright, present, lightly-compressed
voice kept mono-centered and forward, sitting over a big, dark, fast-fading
convolution-reverb tail (bowling-alley / arena wash).

Re-run any time the dry/ clips change; it overwrites fx/ and the IR.
Cross-platform (Windows build box included) -- only needs ffmpeg on PATH.

    python process_fx.py

Constraints:
  - The dry/ clips are already highpass+loudnorm'd (and thus 192 kHz). The voice
    loudness is baked in; we add tone + room ON TOP and finish with only a safety
    limiter. Do NOT loudnorm the dry+wet sum (it ducks the voice, kills presence).
  - ffmpeg's `afir` outputs SILENCE if you set wet=/dry= [SOURCE]; use bare
    `afir` (it emits the pure wet convolution) and set level yourself.
  - afir auto-gains the IR by peak (default gtype), so wet level is predictable
    via the explicit `volume` below. apad gives the tail room to ring out.
  - Reverb is symmetric L/R with the voice mono-centered, so it can never
    hard-pan to one side.
"""
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DRY = os.path.join(HERE, "dry")
FX = os.path.join(HERE, "fx")
IR = os.path.join(HERE, "announcer_reverb_ir.wav")  # committed; regenerated here

# --- reverb impulse response: exponential pink-noise decay -------------------
#   pink noise = dark/boomy; ~1.7 s decay; two decorrelated channels = width.
IR_DUR = 1.7    # tail length (seconds)
IR_LP = 2800    # lowpass on the tail (Hz) -> dark, won't mask consonants

# --- voice front-end (applied to the dry, BEFORE reverb) ---------------------
#   192k->48k, clean rumble, presence + air (bright), light comp, 2 dB headroom.
FRONT = (
    "aresample=48000,highpass=f=90,"
    "equalizer=f=3200:t=q:w=1.4:g=2.5,treble=g=2.5:f=5000,"
    "acompressor=threshold=-18dB:ratio=2.5:attack=8:release=140:makeup=1dB,"
    "volume=-2dB"
)
WET_DB = 15     # reverb level (dB) under the voice


def run(args):
    subprocess.run(args, check=True)


def build_ir():
    graph = (
        f"anoisesrc=d={IR_DUR}:c=pink:r=48000:a=0.9:seed=101[nl];"
        f"anoisesrc=d={IR_DUR}:c=pink:r=48000:a=0.9:seed=911[nr];"
        f"[nl]afade=t=in:st=0:d=0.004,afade=t=out:st=0:d={IR_DUR}:curve=exp,"
        f"highpass=f=80,lowpass=f={IR_LP}[L];"
        f"[nr]afade=t=in:st=0:d=0.004,afade=t=out:st=0:d={IR_DUR}:curve=exp,"
        f"highpass=f=80,lowpass=f={IR_LP}[R];"
        "[L][R]join=inputs=2:channel_layout=stereo[ir]"
    )
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
         "-filter_complex", graph, "-map", "[ir]", "-c:a", "pcm_s16le", IR])


def process(src, dst):
    graph = (
        f"[0:a]{FRONT},apad=pad_dur=2.0,pan=stereo|c0=c0|c1=c0,asplit=2[dry][conv];"
        f"[conv][1:a]afir,volume={WET_DB}dB[wet];"
        "[dry][wet]amix=inputs=2:normalize=0[mx];"
        "[mx]alimiter=limit=0.97,"
        "areverse,silenceremove=start_periods=1:start_threshold=-60dB:start_silence=0.1,"
        "areverse[out]"
    )
    run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
         "-i", src, "-i", IR, "-filter_complex", graph,
         "-map", "[out]", "-ar", "48000", "-c:a", "pcm_s16le", dst])


def main():
    os.makedirs(FX, exist_ok=True)
    build_ir()
    clips = sorted(glob.glob(os.path.join(DRY, "*.wav")))
    if not clips:
        sys.exit(f"no dry clips found in {DRY}")
    for src in clips:
        process(src, os.path.join(FX, os.path.basename(src)))
    print(f"Processed {len(clips)} clips -> {FX}")


if __name__ == "__main__":
    main()
