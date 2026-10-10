#!/usr/bin/env python3
"""Faithful port of AurTextureCalculateHashID @0x4857b0 (nwmain-linux). Red if any two distinct
texture resrefs share a hash: Alloy's texcache keys on the hash alone, the engine strcasecmp's after it.
Usage: aurhash-collisions.py nwn_base.key nwn_retail.key ~/.local/share/"Neverwinter Nights"/hak/*.hak [names.txt]
Result 2026-09-13: 2,202 colliding values over base+one persistent world's haks, 1,719 over another's nwsync manifest."""
import sys, struct, glob, os, collections
M32 = 0xFFFFFFFF
P = 0x7fa35f15
def modp(u):  # (uint)((ulong)u * 0x20173909 >> 0x3c) * -0x7fa35f15 + u, all 32-bit
    q = ((u * 0x20173909) >> 0x3c) & M32
    return (u + q * (-P)) & M32
def aurhash(name, schemes=()):
    h = 0
    for ch in name.encode('latin1'):
        c = ch if ch < 128 else ch - 256
        if 65 <= c <= 90: c += 32
        u = (c & M32) if h == 0 else (c * h) & M32
        h = modp(u)
    for i, s in enumerate(schemes):
        h = modp(((i + 1 + s) * h) & M32)
    return h
TEX = {3: 'tga', 2033: 'dds', 6: 'plt'}
def key_names(path):
    b = open(path, 'rb').read()
    n, off = struct.unpack_from('<II', b, 12)[0], struct.unpack_from('<I', b, 20)[0]
    for i in range(n):
        r, t = struct.unpack_from('<16sH', b, off + i * 22)
        if t in TEX: yield r.split(b'\0')[0].decode('latin1').lower(), TEX[t]
def erf_names(path):
    with open(path, 'rb') as f:
        hdr = f.read(32); n, koff = struct.unpack_from('<I', hdr, 16)[0], struct.unpack_from('<I', hdr, 24)[0]
        f.seek(koff); b = f.read(n * 24)
    for i in range(n):
        r, _, t = struct.unpack_from('<16sIH', b, i * 24)
        if t in TEX: yield r.split(b'\0')[0].decode('latin1').lower(), TEX[t]
if __name__ == '__main__':
    assert aurhash('') == 0
    names = collections.defaultdict(set)
    for p in sys.argv[1:]:
        it = key_names(p) if p.endswith('.key') else erf_names(p) if p.endswith('.hak') else ((l.strip().lower(), 'txt') for l in open(p))
        for nm, t in it:
            if nm: names[nm].add(f'{t}:{os.path.basename(p)}')
    by = collections.defaultdict(list)
    for nm in names: by[aurhash(nm)].append(nm)
    coll = {h: v for h, v in by.items() if len(v) > 1}
    print(f'{len(names)} distinct names, {len(coll)} colliding hash values')
    for h, v in sorted(coll.items(), key=lambda kv: -len(kv[1]))[:40]:
        print(f'  0x{h:08x}: ' + ', '.join(f'{n} [{"|".join(sorted(names[n]))}]' for n in sorted(v)))
    sys.exit(1 if coll else 0)
