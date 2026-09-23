#!/usr/bin/env python3
"""Prototype: classify each 2x2 grass-atlas cell as BLADE (geometric blades can replace it) or CARD (keep the
alpha-tested card). Biased toward CARD: a flower drawn as blades is visible, grass drawn as a card is not.

  classify.py <atlas>...    # cx-grassmip-N-L0.ppm (+ sibling .pgm alpha), or any RGBA DDS/TGA/PNG
  classify.py --selfcheck

Root side is auto-detected (the edge with more coverage); the dumps are GL row order, so roots sit at row 0.
"""
import sys, colorsys
from pathlib import Path
import numpy as np
from PIL import Image

CUT = 0.5            # alpha threshold, as the blade shader's cutout
FOREIGN_MAX = 0.03   # share of opaque texels that are saturated non-grass hues, or near-white
BLOB_MAX = 0.04      # share of tip-side opaque texels that survive an erosion a blade is too thin for
RUN_MAX = 0.05       # mean opaque run width, as a fraction of the cell width
EXTENT_MIN = 0.35    # share of rows that carry any coverage
GRASS_HUE = (35, 175)  # degrees: straw yellow through green; low-saturation browns pass regardless


def load(path):
    p = Path(path)
    img = Image.open(p)
    if p.suffix == ".ppm" and p.with_suffix(".pgm").exists():
        rgb = np.asarray(img.convert("RGB"), np.float32) / 255
        a = np.asarray(Image.open(p.with_suffix(".pgm")).convert("L"), np.float32) / 255
        return rgb, a
    img = img.convert("RGBA")
    px = np.asarray(img, np.float32) / 255
    return px[..., :3], px[..., 3]


def erode(mask, r):
    out = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out &= np.roll(np.roll(mask, dy, 0), dx, 1)
    return out


def features(rgb, a):
    h, w = a.shape
    m = a > CUT
    n = m.sum()
    if n == 0:
        return None
    edge = max(1, h // 10)
    if m[-edge:].sum() > m[:edge].sum():   # put the root at row 0
        rgb, a, m = rgb[::-1], a[::-1], m[::-1]

    rows = m.any(1)
    extent = rows.mean()
    # mean width of horizontal opaque runs: blades are many narrow runs, a blob is a few wide ones
    d = np.diff(np.pad(m.astype(np.int8), ((0, 0), (1, 1))), axis=1)
    starts, ends = np.nonzero(d == 1), np.nonzero(d == -1)
    run = (ends[1] - starts[1]).mean() / w

    c = rgb[m]
    hsv = np.array([colorsys.rgb_to_hsv(*t) for t in c[:: max(1, len(c) // 20000)]])
    hue, sat, val = hsv[:, 0] * 360, hsv[:, 1], hsv[:, 2]
    foreign = ((sat > 0.35) & ((hue < GRASS_HUE[0]) | (hue > GRASS_HUE[1]))) | ((val > 0.85) & (sat < 0.2))
    foreign = foreign.mean()

    # a blob that survives erosion by ~1/64 of the cell; tip side only, the root knot is where blades converge
    r = max(1, w // 64)
    tip = m[int(h * 0.3):]
    blob = erode(tip, r).sum() / max(1, tip.sum())

    # root-to-tip colour ramp, 8 bins, alpha-weighted: what a geometric blade would be tinted with
    ramp = []
    for band in np.array_split(np.arange(h), 8):
        wgt = a[band][..., None] * m[band][..., None]
        s = wgt.sum()
        ramp.append((rgb[band] * wgt).sum((0, 1)) / s if s > 0 else np.zeros(3))
    return dict(cov=n / m.size, extent=extent, run=run, foreign=foreign, blob=blob, ramp=np.array(ramp))


def verdict(f):
    if f is None:
        return "EMPTY", ["no opaque texels"]
    why = []
    if f["foreign"] > FOREIGN_MAX: why.append(f"foreign colour {f['foreign']:.3f}")
    if f["blob"] > BLOB_MAX: why.append(f"blob {f['blob']:.3f}")
    if f["run"] > RUN_MAX: why.append(f"wide runs {f['run']:.3f}")
    if f["extent"] < EXTENT_MIN: why.append(f"short {f['extent']:.2f}")
    return ("CARD" if why else "BLADE"), why


def cells(rgb, a):
    h, w = a.shape
    for i, (y, x) in enumerate([(0, 0), (0, 1), (1, 0), (1, 1)]):
        sl = np.s_[y * h // 2:(y + 1) * h // 2, x * w // 2:(x + 1) * w // 2]
        yield i, rgb[sl], a[sl]


def report(path):
    rgb, a = load(path)
    print(f"{path}  {a.shape[1]}x{a.shape[0]}")
    for i, crgb, ca in cells(rgb, a):
        f = features(crgb, ca)
        v, why = verdict(f)
        if f is None:
            print(f"  cell {i}: {v}")
            continue
        print(f"  cell {i}: {v:5}  cov {f['cov']:.3f} ext {f['extent']:.2f} run {f['run']:.3f} "
              f"foreign {f['foreign']:.3f} blob {f['blob']:.3f}" + (f"  <- {', '.join(why)}" if why else ""))
        if v == "BLADE":
            print("          ramp root->tip " + " ".join("#%02x%02x%02x" % tuple((c * 255).astype(int)) for c in f["ramp"]))


def selfcheck():
    rng = np.random.default_rng(1)
    n = 128

    def blades():
        rgb, a = np.zeros((n, n, 3), np.float32), np.zeros((n, n), np.float32)
        for _ in range(30):
            x, lean, top = rng.uniform(10, n - 10), rng.uniform(-0.3, 0.3), int(rng.uniform(0.5, 0.95) * n)
            for y in range(top):
                cx = int(x + lean * y)
                if 0 <= cx < n - 1:
                    a[y, cx:cx + 2] = 1
                    rgb[y, cx:cx + 2] = (0.25, 0.45 + 0.2 * y / n, 0.1)
        return rgb, a

    g_rgb, g_a = blades()
    assert verdict(features(g_rgb, g_a))[0] == "BLADE", verdict(features(g_rgb, g_a))

    f_rgb, f_a = blades()
    yy, xx = np.mgrid[:n, :n]
    for cx, cy in [(30, 100), (70, 110), (100, 95)]:
        disc = (yy - cy) ** 2 + (xx - cx) ** 2 < 36
        f_a[disc], f_rgb[disc] = 1, (0.85, 0.15, 0.3)
    v, why = verdict(features(f_rgb, f_a))
    assert v == "CARD" and any("foreign" in w for w in why) and any("blob" in w for w in why), why

    w_rgb, w_a = f_rgb.copy(), f_a.copy()   # white flowers: colour test alone must not be the only guard
    for cx, cy in [(30, 100), (70, 110), (100, 95)]:
        w_rgb[(yy - cy) ** 2 + (xx - cx) ** 2 < 36] = (0.95, 0.95, 0.9)
    assert verdict(features(w_rgb, w_a))[0] == "CARD"

    flipped = verdict(features(g_rgb[::-1], g_a[::-1]))[0]
    assert flipped == "BLADE", flipped
    print("selfcheck ok")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args or args == ["--selfcheck"]:
        selfcheck()
    for p in (x for x in args if x != "--selfcheck"):
        report(p)
