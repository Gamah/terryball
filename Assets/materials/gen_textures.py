#!/usr/bin/env python3
"""
Stylised, seamless-tileable surface textures for the Terryball lanes.

Everything here is generated from noise + math (no photo sources), so the output
is original and CC0-clean. Each material emits three maps at 1024x1024:

    <name>_color.png   sRGB albedo
    <name>_normal.png  tangent-space normal (OpenGL, +Y up)
    <name>_rough.png   linear roughness (grey)

Maps are periodic (they wrap), so they tile without a visible seam. The lane
builder tiles them per-box from world size, so absolute density is set in C#;
here we only decide the *look* of one tile.

Run:  python3 Assets/materials/gen_textures.py
"""

import numpy as np
from PIL import Image
import os

N = 1024
OUT = os.path.join(os.path.dirname(__file__), "generated")
os.makedirs(OUT, exist_ok=True)

# ---------------------------------------------------------------- noise helpers

def _smooth(t):
    return t * t * (3.0 - 2.0 * t)

def wrap_noise(grid, rng):
    """Periodic value noise: random GxG lattice, bilinearly interpolated up to N
    with wrap-around, so opposite edges match exactly."""
    g = rng.random((grid, grid)).astype(np.float32)
    xs = np.linspace(0, grid, N, endpoint=False, dtype=np.float32)
    x0 = np.floor(xs).astype(int)
    fx = _smooth(xs - x0)
    x0m = x0 % grid
    x1m = (x0 + 1) % grid
    # rows then cols
    r = g[x0m][:, x0m] * (1 - fx)[:, None] + g[x1m][:, x0m] * fx[:, None]
    out = r * (1 - fx)[None, :] + (g[x0m][:, x1m] * (1 - fx)[:, None]
                                  + g[x1m][:, x1m] * fx[:, None]) * fx[None, :]
    return out

def fbm(rng, octaves=5, base=4, gain=0.5, lac=2):
    """Fractal sum of wrapping value noise. Grid sizes stay integer so each
    octave is periodic over the full image."""
    out = np.zeros((N, N), np.float32)
    amp, tot, grid = 1.0, 0.0, base
    for _ in range(octaves):
        out += amp * wrap_noise(grid, rng)
        tot += amp
        amp *= gain
        grid = int(grid * lac)
    return out / tot

def norm01(a):
    a = a - a.min()
    m = a.max()
    return a / m if m > 1e-6 else a

def height_to_normal(h, strength=2.0):
    """Tangent-space normal from a periodic height field (wrapping gradients)."""
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5 * strength
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5 * strength
    nz = np.ones_like(h)
    ln = np.sqrt(gx * gx + gy * gy + nz * nz)
    n = np.stack([-gx / ln, gy / ln, nz / ln], -1)  # +Y up (OpenGL)
    return ((n * 0.5 + 0.5) * 255).astype(np.uint8)

def save(name, color, height, rough, nstrength=2.0):
    Image.fromarray((np.clip(color, 0, 1) * 255).astype(np.uint8), "RGB").save(
        os.path.join(OUT, f"{name}_color.png"))
    Image.fromarray(height_to_normal(height, nstrength), "RGB").save(
        os.path.join(OUT, f"{name}_normal.png"))
    Image.fromarray((np.clip(rough, 0, 1) * 255).astype(np.uint8), "L").save(
        os.path.join(OUT, f"{name}_rough.png"))
    print(f"  {name}")

def lerp(a, b, t):
    t = t[..., None]
    return np.array(a) * (1 - t) + np.array(b) * t

Y, X = np.mgrid[0:N, 0:N].astype(np.float32) / N  # 0..1 coords

# ---------------------------------------------------------------- wood (planks)

def wood(name, seed, c_light, c_dark, c_seam, planks=6, rough_lo=0.18, rough_hi=0.4):
    rng = np.random.default_rng(seed)
    # Planks run along X; seams are periodic in Y.
    plank_v = (Y * planks) % 1.0
    plank_id = np.floor(Y * planks).astype(int)
    # Full organic 2D wood grain (the original rich look): a few soft grain lines plus
    # a low-freq base tone. On its own this BANDS when tiled down the lane — the same
    # blotches recur at every repeat AND line up across every board at once, reading as
    # a stripe across the whole lane. Fix: slide EACH plank along its length by its own
    # amount, so the dark features stagger board-to-board and never form a coherent band
    # (each board keeps its full grain). A per-LANE offset can't do this — it shifts
    # every board the same, so they stay aligned. Everything wraps, so the tile stays
    # seamless; the per-plank roll's row-to-row jump is hidden under the seam groove.
    base_tone = fbm(rng, octaves=4, base=3)
    warp = fbm(rng, octaves=4, base=5) - 0.5          # flowing wander, ~[-.5, .5]
    lines = 18                                        # grain lines across a whole tile
    ripple = np.sin((Y * lines + warp * 3.0) * 2 * np.pi) * 0.5 + 0.5
    grain = norm01(base_tone * 0.6 + ripple * 0.22)
    grain = 0.32 + 0.55 * grain                       # compress tonal range -> soft contrast
    # Per-plank lengthwise roll (columns = X = lane length): shift each plank's whole
    # grain block by a random column offset, so neighbouring boards don't share the
    # same lengthwise features. plank_id is constant along a row, so this is a per-row
    # column roll.
    row_plank = plank_id[:, 0] % planks
    plank_off = (rng.random(planks) * N).astype(int)
    cols = (np.arange(N)[None, :] - plank_off[row_plank][:, None]) % N
    grain = grain[np.arange(N)[:, None], cols]
    # Per-plank tone offset so neighbours differ. Periodic over `planks` (3 is coprime
    # to 5 and 7) so the first/last plank tones match and the tile wraps in Y too.
    tone = (np.sin(2 * np.pi * 3 * plank_id.astype(np.float32) / planks) * 0.5 + 0.5)
    col = lerp(c_dark, c_light, grain)                 # (N,N,3)
    col = col * (0.82 + 0.18 * tone)[..., None]
    # Dark seam grooves between planks.
    seam = np.exp(-(plank_v ** 2) / (2 * 0.006 ** 2))
    seam += np.exp(-((plank_v - 1.0) ** 2) / (2 * 0.006 ** 2))
    seam = np.clip(seam, 0, 1)
    col = col * (1 - 0.55 * seam[..., None]) + np.array(c_seam) * (0.55 * seam[..., None])
    # Height: grain relief minus deep seams.
    h = grain * 0.5 - seam * 1.0
    rough = rough_lo + (rough_hi - rough_lo) * (1 - grain) + seam * 0.3
    save(name, col, h, np.clip(rough, 0, 1), nstrength=0.9)  # gentler relief -> less ribbed

# ---------------------------------------------------------------- gutter plastic

def gutter(name, seed):
    rng = np.random.default_rng(seed)
    base = np.array([0.07, 0.075, 0.09])
    streak = fbm(rng, octaves=5, base=2)
    # Long grooves down the gutter length (X).
    grooves = (np.sin(Y * np.pi * 30) * 0.5 + 0.5)
    grooves = grooves ** 3
    fleck = fbm(rng, octaves=6, base=8)
    col = base[None, None, :] * (0.7 + 0.6 * streak[..., None])
    col = col * (0.8 + 0.4 * grooves[..., None])
    col += (fleck[..., None] - 0.5) * 0.03
    h = grooves * 0.6 + streak * 0.2
    rough = 0.22 + 0.15 * (1 - grooves) + 0.1 * fleck
    save(name, col, h, rough, nstrength=1.2)

# ---------------------------------------------------------------- brushed metal

def metal(name, seed, tint=(0.5, 0.52, 0.56)):
    rng = np.random.default_rng(seed)
    # Fine horizontal brush lines + broad anisotropic variation.
    brush = fbm(rng, octaves=6, base=(2)) * 0.0
    lines = rng.random((N, 1)).astype(np.float32)  # per-row (brushed along X)
    lines = np.repeat(lines, N, axis=1)
    lines = 0.5 + 0.5 * np.sin(Y * np.pi * 240 + lines * 6)
    broad = fbm(rng, octaves=4, base=3)
    val = 0.55 + 0.25 * broad + 0.12 * (lines - 0.5)
    col = np.array(tint)[None, None, :] * val[..., None]
    h = lines * 0.15 + broad * 0.4
    rough = 0.3 + 0.15 * broad + 0.05 * (lines - 0.5)
    save(name, col, h, rough, nstrength=0.8)

# ---------------------------------------------------------------- retro carpet

def carpet(name, seed):
    """The hero graphic: a busy mid-century bowling-alley carpet. Repeating
    boomerang/atomic motifs in teal, magenta and gold on deep indigo."""
    rng = np.random.default_rng(seed)
    base = np.array([0.10, 0.06, 0.16])       # deep indigo
    teal = np.array([0.10, 0.62, 0.60])
    magenta = np.array([0.82, 0.16, 0.45])
    gold = np.array([0.92, 0.72, 0.22])
    col = np.broadcast_to(base, (N, N, 3)).copy()

    reps = 4                                   # motif cells per tile
    u = (X * reps) % 1.0
    v = (Y * reps) % 1.0
    cell = (np.floor(X * reps) + np.floor(Y * reps) * 7).astype(int)

    def boomerang(cx, cy, rot, thick, col_a):
        # A bent-arm boomerang: two rotated capsule arms meeting at an elbow.
        du = u - cx
        dv = v - cy
        ca, sa = np.cos(rot), np.sin(rot)
        ru = du * ca - dv * sa
        rv = du * sa + dv * ca
        arm1 = np.abs(rv - 0.35 * np.abs(ru)) < thick
        arm1 &= (np.abs(ru) < 0.22)
        m = arm1
        col[m] = col_a

    boomerang(0.30, 0.35, 0.4, 0.045, teal)
    boomerang(0.68, 0.62, 0.4 + np.pi, 0.045, magenta)
    # Scattered atomic dots keyed off the cell id so they vary per cell.
    for k in range(6):
        r = np.random.default_rng(seed + 100 + k)
        cx, cy = r.random(), r.random()
        dot = ((u - cx) ** 2 + (v - cy) ** 2) < (0.02 + 0.015 * (k % 2))
        col[dot] = [teal, gold, magenta][k % 3]

    # Alternate the base hue slightly per cell for a woven check feel.
    check = (cell % 2 == 0)
    col[check] = col[check] * 0.86 + base * 0.14

    # Carpet fuzz: fine noise over everything, and a low-freq shading.
    rng2 = np.random.default_rng(seed + 7)
    fuzz = fbm(rng2, octaves=6, base=16)
    shade = fbm(rng2, octaves=3, base=3)
    col = col * (0.82 + 0.22 * fuzz[..., None]) * (0.9 + 0.15 * shade[..., None])

    h = fuzz * 0.5 + shade * 0.2
    rough = np.full((N, N), 0.9, np.float32) - 0.05 * fuzz
    save(name, col, h, rough, nstrength=1.0)

# ---------------------------------------------------------------- pit (dead rubber)

def pit(name, seed):
    rng = np.random.default_rng(seed)
    base = np.array([0.05, 0.05, 0.06])
    scuff = fbm(rng, octaves=6, base=4)
    grain = fbm(rng, octaves=6, base=24)
    col = base[None, None, :] * (0.7 + 0.9 * scuff[..., None])
    col += (grain[..., None] - 0.5) * 0.04
    h = grain * 0.4 + scuff * 0.3
    rough = 0.8 + 0.12 * (1 - scuff)
    save(name, col, h, rough, nstrength=1.0)

# ---------------------------------------------------------------- terrazzo floor

def floor(name, seed, base=(0.86, 0.84, 0.80), chip_rough=0.3):
    """Graphic terrazzo: a base packed with coloured aggregate chips. `base` sets
    the matrix colour — pale for the concourse, near-black for the spawn/ramps
    (the same bright chips read as confetti dots on black). `chip_rough` is the
    chips' roughness: lower = glossier. Raise it to calm shiny confetti dots."""
    rng = np.random.default_rng(seed)
    base = np.array(base)
    col = np.broadcast_to(base, (N, N, 3)).copy()
    chip_cols = [np.array(c) for c in [
        [0.85, 0.30, 0.30], [0.25, 0.45, 0.75], [0.30, 0.65, 0.45],
        [0.95, 0.78, 0.25], [0.25, 0.25, 0.30], [0.75, 0.45, 0.75]]]
    h = np.zeros((N, N), np.float32)
    # Scatter many little polygon-ish chips (rendered as jittered discs).
    for i in range(900):
        r = rng
        cx, cy = r.random(), r.random()
        rad = 0.004 + 0.010 * r.random()
        c = chip_cols[i % len(chip_cols)]
        # wrap-aware distance
        dx = np.abs(X - cx); dx = np.minimum(dx, 1 - dx)
        dy = np.abs(Y - cy); dy = np.minimum(dy, 1 - dy)
        jitter = 0.7 + 0.6 * fbm(np.random.default_rng(seed + i), 2, 4) if i < 0 else 1.0
        m = (dx * dx + dy * dy) < rad * rad
        col[m] = c
        h[m] = 0.5
    # subtle speckle in the cement
    spk = fbm(rng, octaves=6, base=32)
    col = col * (0.94 + 0.10 * spk[..., None])
    rough = np.full((N, N), 0.45, np.float32) + 0.2 * spk
    # chips can be glossier than the matrix (chip_rough); raise it to calm the confetti
    rough[h > 0.4] = chip_rough
    save(name, col, h * 0.3, rough, nstrength=0.6)

# ---------------------------------------------------------------- go

if __name__ == "__main__":
    print("Generating textures ->", OUT)
    wood("lanewood",     11, c_light=[0.80, 0.58, 0.34], c_dark=[0.55, 0.36, 0.18],
         c_seam=[0.30, 0.19, 0.10], planks=7, rough_lo=0.12, rough_hi=0.30)
    wood("approachwood", 23, c_light=[0.62, 0.40, 0.26], c_dark=[0.42, 0.25, 0.15],
         c_seam=[0.24, 0.14, 0.09], planks=5, rough_lo=0.45, rough_hi=0.7)
    gutter("gutter", 31)
    metal("metal", 41, tint=(0.46, 0.48, 0.54))
    carpet("carpet", 53)
    pit("pit", 61)
    floor("floor", 71)
    # ramps + settees: bright chips on near-black, chips kept matte so the dots don't glare
    floor("floordark", 83, base=[0.05, 0.05, 0.06], chip_rough=0.6)
    print("done.")
