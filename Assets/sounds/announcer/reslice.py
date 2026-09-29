#!/usr/bin/env python3
"""Re-slice the announcer dry/ clips from the original continuous take.

    ~/vo.m4a  ->  dry/<call>_<take>.wav   (50 clips: 25 calls x 2 takes)

The take is one ~67s recording, each call performed TWICE in a row, in the
script order below. We split ONLY on the gaps between calls (within-phrase gaps
like "open frame" / "four bagger" are shorter, so they stay together) and cut
each segment with a little head/tail padding so word endings aren't clipped.

Then run process_fx.py to rebuild fx/ from these.

    python reslice.py            # writes straight into dry/
    python reslice.py out_dir    # or stage elsewhere first

Cross-platform; only needs ffmpeg/ffprobe on PATH.
"""
import os, re, subprocess, sys, tempfile

SRC = os.path.expanduser("~/vo.m4a")          # the original recording (kept out of the repo)
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "dry")

# script order — each call performed TWICE in a row (take 0, take 1)
CALLS = ["one","two","three","four","five","six","seven","eight","nine",
         "strike","spare","gutter","miss","open-frame","split","double","turkey",
         "four-bagger","five-bagger","six-pack","nice","so-close","good-try",
         "pocket","perfect-game"]

NOISE, SILD = "-35dB", 0.30   # split on between-call gaps only (within-phrase gaps are ~0.18s)
HEAD, TAIL = 0.06, 0.20       # padding (s): a touch before the onset, a bit after so the tail survives


def main():
    if not os.path.exists(SRC):
        sys.exit(f"original take not found: {SRC}")
    with tempfile.TemporaryDirectory() as tmp:
        mono = os.path.join(tmp, "mono.wav")
        subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-y",
            "-i",SRC,"-ac","1","-ar","48000",mono], check=True)
        dur = float(subprocess.run(["ffprobe","-v","error","-show_entries","format=duration",
            "-of","csv=p=0",mono], capture_output=True, text=True).stdout.strip())
        det = subprocess.run(["ffmpeg","-hide_banner","-nostats","-i",mono,
            "-af",f"silencedetect=noise={NOISE}:d={SILD}","-f","null","-"],
            capture_output=True, text=True).stderr
        starts = [float(x) for x in re.findall(r"silence_start: ([\d.]+)", det)]
        ends   = [float(x) for x in re.findall(r"silence_end: ([\d.]+)", det)]

    # a speech segment runs from a silence_end to the next silence_start
    segs = sorted({(e, min([s for s in starts if s > e + 1e-3]))
                   for e in ends if any(s > e + 1e-3 for s in starts)})
    if len(segs) != len(CALLS) * 2:
        for i,(a,b) in enumerate(segs): print(f"  {i:2d} {a:7.3f}-{b:7.3f} ({b-a:.3f}s)")
        sys.exit(f"got {len(segs)} segments, need {len(CALLS)*2} — adjust NOISE/SILD")

    os.makedirs(OUT, exist_ok=True)
    for i,(a,b) in enumerate(segs):
        dst = os.path.join(OUT, f"{CALLS[i//2]}_{i%2}.wav")
        subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-y",
            "-ss",f"{max(0.0,a-HEAD):.3f}","-to",f"{min(dur,b+TAIL):.3f}","-i",SRC,
            "-ac","1","-af","highpass=f=85,loudnorm=I=-16:TP=-1.5:LRA=11",
            "-c:a","pcm_s16le", dst], check=True)
    print(f"wrote {len(segs)} clips to {OUT}/ — now run process_fx.py to rebuild fx/")


if __name__ == "__main__":
    main()
