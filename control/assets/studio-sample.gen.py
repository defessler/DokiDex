#!/usr/bin/env python
"""Generate control/assets/studio-sample.png — the DokiGen Studio design-mode preview.

This is a *placeholder* artwork (no GPU / no real model) used only by the panel's --design / --render
sample state, so the Studio's happy-path renders with a coherent inline result instead of an empty frame.
It is the visual analogue of MainViewModel's canned StatusDoc: illustrative, in-brand, deterministic.

Aesthetic: the "DokiGen Void" palette — near-black field, a coiling neon koi/dragon ribbon graded
teal -> hot-core -> gold, volumetric glow, embers, film grain, a faint hex-sigil watermark.
Run:  python control/assets/studio-sample.gen.py
"""
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W = H = 1024
rng = np.random.default_rng(7)          # deterministic

# ---- palette (linear-ish 0..1) ----
BG_CORE = np.array([0.040, 0.066, 0.098])   # dark teal heart of the field
BG_EDGE = np.array([0.010, 0.014, 0.024])   # void at the corners
TEAL    = np.array([0.21, 0.88, 0.96])      # #35E0F0 emitting cyan (tail)
HOT     = np.array([0.92, 0.98, 1.00])      # white-hot mid-core
GOLD    = np.array([0.95, 0.80, 0.49])      # #E8C77A etched gold (head)

# ---- background: radial vignette ----
yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
cx, cy = W * 0.53, H * 0.47
r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (W * 0.72)
t = np.clip(r, 0, 1)[..., None]
img = BG_CORE * (1 - t) + BG_EDGE * t       # HxWx3 float

emission = np.zeros((H, W, 3), dtype=np.float32)   # additive light, blurred later

def stamp(px, py, rad, col, intensity):
    """Add a soft radial disc of `col` light at (px,py) into the emission layer."""
    x0, x1 = max(0, int(px - rad)), min(W, int(px + rad) + 1)
    y0, y1 = max(0, int(py - rad)), min(H, int(py + rad) + 1)
    if x0 >= x1 or y0 >= y1:
        return
    lx, ly = np.mgrid[y0:y1, x0:x1].astype(np.float32)
    d2 = ((lx - py) ** 2 + (ly - px) ** 2) / (rad * rad)
    fall = np.clip(1 - d2, 0, 1) ** 1.7            # soft-edged disc
    emission[y0:y1, x0:x1] += (fall[..., None] * col) * intensity

def lerp3(a, b, u):
    return a * (1 - u) + b * u

def body_color(s):
    """teal -> brief hot core -> gold along the body parameter s in [0,1].
    Biased so the tail stays legibly teal and the head reaches gold (no white-out)."""
    if s < 0.5:
        return lerp3(TEAL, HOT, (s / 0.5) ** 1.7)     # hold teal through the tail
    return lerp3(HOT, GOLD, ((s - 0.5) / 0.5) ** 0.7)  # reach gold early in the head

# ---- faint rain: thin near-vertical teal streaks, well behind the ribbon ----
rain_col = lerp3(TEAL, np.array([0.45, 0.55, 0.66]), 0.55)
for _ in range(44):
    rx, ry = rng.uniform(0, W), rng.uniform(-40, H * 0.7)
    length = rng.uniform(46, 130)
    slant = rng.uniform(-0.5, -0.18)
    for k in range(int(length / 4)):
        stamp(rx + slant * k * 4, ry + k * 4, rng.uniform(0.9, 1.7), rain_col, 0.02)

# ---- the coiling ribbon: a decaying spiral = a curled koi/dragon body ----
N = 900
ts = np.linspace(0, 1, N)
theta = ts * math.pi * 2.55 + 0.7
spiral = 1 - 0.58 * ts                                   # radius decays inward -> coil
bx = cx + spiral * np.cos(theta) * W * 0.31 + W * 0.035 * np.sin(ts * 9.0)
by = cy + spiral * np.sin(theta) * H * 0.31 + H * 0.028 * np.cos(ts * 7.0)
girth = np.sin(math.pi * ts) ** 0.7                      # thick middle, thin head/tail

for i in range(N):
    s = ts[i]
    col = body_color(s)
    rad = 6 + 34 * girth[i]
    stamp(bx[i], by[i], rad, col, 0.10)                  # soft body mass (saturated)
    stamp(bx[i], by[i], rad * 0.28, HOT, 0.055)          # thin bright core (kept low to not white-out)

# ---- embers drifting off the body ----
for _ in range(120):
    i = int(rng.integers(0, N))
    ang = rng.uniform(0, 2 * math.pi)
    dist = rng.uniform(10, 95) * (0.4 + girth[i])
    ex, ey = bx[i] + math.cos(ang) * dist, by[i] + math.sin(ang) * dist
    col = lerp3(body_color(ts[i]), GOLD, rng.uniform(0, 0.6))
    stamp(ex, ey, rng.uniform(2.5, 7.5), col, rng.uniform(0.12, 0.4))

# ---- volumetric glow: blur the emission at several scales, add back ----
def blur(a, sigma):
    im = Image.fromarray(np.clip(a, 0, 6) / 6, mode="F") if False else None  # (per-channel below)
    out = np.zeros_like(a)
    for c in range(3):
        ch = Image.fromarray((np.clip(a[..., c], 0, 8) * 32).astype(np.uint8))
        ch = ch.filter(ImageFilter.GaussianBlur(sigma))
        out[..., c] = np.asarray(ch, dtype=np.float32) / 32
    return out

glow = blur(emission, 7) * 0.9 + blur(emission, 22) * 0.7 + blur(emission, 54) * 0.5
img = img + emission * 0.9 + glow

# ---- faint hex-sigil watermark (matches Palette.xaml SigilGeo) ----
sig = Image.new("RGBA", (W, H), (0, 0, 0, 0))
sd = ImageDraw.Draw(sig)
R = W * 0.30
hexpts = [(cx + R * math.cos(math.pi / 6 + k * math.pi / 3),
           cy + R * math.sin(math.pi / 6 + k * math.pi / 3)) for k in range(6)]
gold_rgba = (232, 199, 122, 26)
sd.line(hexpts + [hexpts[0]], fill=gold_rgba, width=2)
up = [(cx, cy - R), (cx + R * 0.866, cy + R * 0.5), (cx - R * 0.866, cy + R * 0.5)]
dn = [(cx, cy + R), (cx + R * 0.866, cy - R * 0.5), (cx - R * 0.866, cy - R * 0.5)]
sd.line(up + [up[0]], fill=gold_rgba, width=2)
sd.line(dn + [dn[0]], fill=gold_rgba, width=2)
sig = sig.filter(ImageFilter.GaussianBlur(0.6))
img = img * (1 - np.asarray(sig)[..., 3:4] / 255 * 0.0)   # keep field; sigil added as light below
img = img + np.asarray(sig)[..., :3].astype(np.float32) / 255 * (np.asarray(sig)[..., 3:4] / 255)

# ---- grain + filmic tone-map + corner darkening ----
grain = rng.normal(0, 1, (H, W, 1)).astype(np.float32) * 0.012
img = img + grain
img = img / (1 + img)                                      # Reinhard roll-off (no harsh clipping)
img = img * (1 - 0.34 * np.clip(r[..., None] - 0.45, 0, 1)) # deepen the corners
img = np.clip(img, 0, 1)

out = Image.fromarray((img ** (1 / 1.9) * 255 + 0.5).astype(np.uint8), "RGB")  # gamma to sRGB-ish
out.save("control/assets/studio-sample.png")
print("wrote control/assets/studio-sample.png", out.size)
