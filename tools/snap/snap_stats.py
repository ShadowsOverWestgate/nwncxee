#!/usr/bin/env python3
"""CX-374: read one `/alloy snap` set (cx_snap/N_{opaque,sky,pre}.pfm + N_final.ppm), print channel stats
and write stretched PNGs + amplified stage diffs beside them, so a sub-code-value sky fault is a number
and a picture rather than an impression. Usage: snap_stats.py <dir> <N> [--stretch 20]"""
import sys, os, re
import numpy as np
from PIL import Image

def read_pfm(p):
    with open(p, 'rb') as f:
        assert f.readline().strip() == b'PF'
        w, h = map(int, f.readline().split()); scale = float(f.readline())
        a = np.frombuffer(f.read(), dtype='<f4' if scale < 0 else '>f4').reshape(h, w, 3)
    return a[::-1].astype(np.float64)   # bottom-up -> top-down

def read_ppm(p):
    return np.asarray(Image.open(p).convert('RGB')).astype(np.float64) / 255.0

def stats(name, a):
    m = a.reshape(-1, 3).mean(0); s = a.reshape(-1, 3).std(0)
    mn = a.reshape(-1, 3).min(0); mx = a.reshape(-1, 3).max(0)
    print(f"{name:10s} mean {m[0]:.5f} {m[1]:.5f} {m[2]:.5f}  std {s[0]:.5f} {s[1]:.5f} {s[2]:.5f}"
          f"  min {mn[0]:.4f} {mn[1]:.4f} {mn[2]:.4f}  max {mx[0]:.4f} {mx[1]:.4f} {mx[2]:.4f}")

def save(name, a, gain=None):
    x = a
    if gain is None:   # percentile stretch
        lo, hi = np.percentile(x, 0.5), np.percentile(x, 99.5)
        x = (x - lo) / max(hi - lo, 1e-9)
    else: x = x * gain
    Image.fromarray((np.clip(x, 0, 1) * 255).astype(np.uint8)).save(name)

def encode(a):   # linear -> sRGB
    a = np.clip(a, 0, 1)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * a ** (1 / 2.4) - 0.055)

d, n = sys.argv[1], sys.argv[2]
imgs = {}
for st in ('opaque', 'sky', 'pre'):
    p = os.path.join(d, f'{n}_{st}.pfm')
    if os.path.exists(p): imgs[st] = read_pfm(p)
p = os.path.join(d, f'{n}_final.ppm')
if os.path.exists(p): imgs['final'] = read_ppm(p)
for k, v in imgs.items():
    stats(k, v); save(os.path.join(d, f'{n}_{k}_stretch.png'), v)
    if k != 'final': stats(k + '>srgb', encode(v))
pairs = [('opaque', 'sky'), ('sky', 'pre')]
for a, b in pairs:
    if a in imgs and b in imgs:
        dlt = imgs[b] - imgs[a]
        stats(f'{b}-{a}', dlt)
        save(os.path.join(d, f'{n}_diff_{b}_{a}.png'), np.abs(dlt), gain=None)
if 'pre' in imgs and 'final' in imgs and imgs['pre'].shape == imgs['final'].shape:
    dlt = imgs['final'] - encode(imgs['pre']); stats('final-srgb(pre)', dlt)
    save(os.path.join(d, f'{n}_diff_final_pre.png'), np.abs(dlt))
