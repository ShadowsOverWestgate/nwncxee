#!/usr/bin/env python3
"""CX-117 off-device: the ENGINE's PLT composite against a CPU port of the GLSL that ships.

Two independent implementations, not two spellings of one (review r1 finding 9):

  * `engine_image_decode` is a port of `ImageDecode` @0x4b6580 as the engine WRITES it -- it builds the
    15 x 1024-byte stack LUT by copying `paletteimage[rows[i] << 10 ..]` into row i, then indexes that LUT
    with `(layer * 256 + intensity) * 4`. A copy and an index, in that order, as the decompile has it.
  * `glsl_*` is a port of `managed/Alloy/Shaders/cx_plt.glsl`: CxPltFetch's addressing and rounding,
    CxPltBilinear's four taps and its `uv * dim - 0.5` offset, CxPltResolve's LOD, trilinear blend and LOD
    cut -- plus `build_mips`, a port of PltImage.BuildMips' majority-layer / mean-intensity reduction.

The master palette is built the way `AurSetPalette` @0x485190 builds it: seven palette TGAs, each flipped in
Y and copied with a FLAT memcpy (`ImageCopy`) into row slot*256 of one 256x1792 buffer.

What it asserts:

  1. every shipped PLT parses under the header Alloy declares, and no pixel names a layer the file does not;
  2. the GLSL resolve at texel centres, LOD 0, equals the engine's composite BYTE FOR BYTE -- over every
     texel of every PLT, for three scheme-row sets;
  3. off-centre, the GLSL's bilinear over RESOLVED colours equals bilinear over the engine's COMPOSITE at the
     same uv -- which is the "the same arithmetic a filtered composite would have performed" claim, measured;
  4. the mip reduction is the majority rule, the trilinear blend is the lerp of its two levels, and past
     CX_PLT_LOD_CUT no second level is blended in;
  5. REPEAT addressing wraps and CLAMP addressing clamps, at the texel the shader fetches.

    tools/pltcheck/check.py [--data DIR] [--limit N] [--samples N] [--rows a,b,...]

Exit 0 clean, 1 if anything disagrees. Needs the installed game and numpy.
"""
import argparse, os, struct, sys, collections
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import paths  # noqa: E402
DEFAULT_DATA = os.path.join(paths.get("NWN_DIR") or "", "data")
PLT_RESTYPE, TGA_RESTYPE = 6, 3
HEADER = 24
MAGIC = 0x20544C50          # 'PLT '
SLOTS, ROWS_PER_SLOT, PAL_W, BPP = 7, 256, 256, 4
MASTER_ROWS = SLOTS * ROWS_PER_SLOT
ROW_BYTES = PAL_W * BPP
# cx_plt.glsl's own constants; the C# side pins them with TextureSlots.Check
CX_PLT_LAYERS = 10
CX_PLT_LOD_CUT = 4.0
ENGINE_LUT_ROWS = 15        # ImageDecode's stack cache; SetScheme allocates exactly this many ints
PALETTES = ["pal_skin01", "pal_hair01", "pal_armor01", "pal_armor02",
            "pal_cloth01", "pal_leath01", "pal_tattoo01"]


# ---------------------------------------------------------------- resources

def load_keys(data):
    """Every KEY in `data`, merged: (resref, restype) -> (bif index, entry index)."""
    bifs, keys = [], {}
    for name in sorted(os.listdir(data)):
        if not name.endswith(".key"):
            continue
        b = open(os.path.join(data, name), "rb").read()
        if b[:8] != b"KEY V1  ":
            continue
        nbif, nkey, off_file, off_key = struct.unpack_from("<4I", b, 8)
        base = len(bifs)
        for i in range(nbif):
            _sz, fn_off, fn_sz, _drv = struct.unpack_from("<IIHH", b, off_file + i * 12)
            bifs.append(b[fn_off:fn_off + fn_sz].rstrip(b"\0").decode("latin1").replace("\\", "/"))
        for i in range(nkey):
            at = off_key + i * 22
            rr = b[at:at + 16].rstrip(b"\0").decode("latin1").lower()
            rt, rid = struct.unpack_from("<HI", b, at + 16)
            keys[(rr, rt)] = (base + (rid >> 20), rid & 0xFFFFF)
    return bifs, keys


_bif_cache = {}


def read_res(data, bifs, keys, resref, restype):
    bi, ri = keys[(resref.lower(), restype)]
    path = os.path.join(data, os.path.basename(bifs[bi]))
    if path not in _bif_cache:
        if not os.path.exists(path):
            return None
        _bif_cache[path] = open(path, "rb").read()
    b = _bif_cache[path]
    if b[:8] != b"BIFFV1  ":
        return None
    _nvar, _nfix, off = struct.unpack_from("<3I", b, 8)
    _id, o, sz, _rt = struct.unpack_from("<4I", b, off + ri * 16)
    return b[o:o + sz]


# ------------------------------------------------- Alloy's declared PLT format

def parse_plt(raw):
    if len(raw) < HEADER:
        return None, "shorter than the 24-byte header"
    if struct.unpack_from("<I", raw)[0] != MAGIC:
        return None, "the first dword is not 'PLT '"
    layers, comp, w, h = (struct.unpack_from("<I", raw, 8)[0], struct.unpack_from("<I", raw, 12)[0],
                          struct.unpack_from("<I", raw, 16)[0], struct.unpack_from("<I", raw, 20)[0])
    if not (0 < w <= 0x2000 and 0 < h <= 0x2000):
        return None, f"{w}x{h} is outside the engine's 1..0x2000"
    if not (0 < layers <= 0x20):
        return None, f"{layers} layers, outside the engine's 1..0x20"
    want = w * h * 2
    if comp:
        pairs, why = unrle(raw[HEADER:], want)
        if pairs is None:
            return None, why
    else:
        if len(raw) - HEADER < want:
            return None, f"{len(raw) - HEADER} bytes of pairs for {w}x{h}, which needs {want}"
        pairs = raw[HEADER:HEADER + want]
    return (w, h, layers, bool(comp), pairs), None


def unrle(src, want):
    """CResPLT::ReadRLEPLT @0x1136560: count = (ctl & 0x7f) + 1, top bit = repeat the next byte."""
    out, s = bytearray(), 0
    while len(out) < want:
        if s >= len(src):
            return None, f"the RLE stream ends after {len(out)} of {want} bytes"
        ctl = src[s]; s += 1
        n = min((ctl & 0x7F) + 1, want - len(out))
        if ctl & 0x80:
            if s >= len(src):
                return None, "the RLE stream ends on a run byte"
            out += bytes([src[s]]) * n; s += 1
        else:
            if s + n > len(src):
                return None, f"an RLE literal run of {n} overruns the stream"
            out += src[s:s + n]; s += n
    return bytes(out), None


# ------------------------------------------- AurSetPalette's master palette

def decode_tga32(raw):
    """A 32-bit uncompressed TGA as the engine's resource path yields it: BGRA on disk, R and B swapped."""
    idlen, _cmaptype, imtype = raw[0], raw[1], raw[2]
    cmap_len, cmap_bits = struct.unpack_from("<H", raw, 5)[0], raw[7]
    w, h = struct.unpack_from("<HH", raw, 12)
    bits = raw[16]
    if imtype != 2 or bits != 32:
        return None, None, None, f"type {imtype} / {bits} bits is not an uncompressed 32-bit TGA"
    off = 18 + idlen + cmap_len * (cmap_bits >> 3)
    px = bytearray(raw[off:off + w * h * 4])
    b = bytes(px[0::4])
    px[0::4] = px[2::4]
    px[2::4] = b
    return bytes(px), w, h, None


def image_copy(dst, dst_off, src, w, h, bpp):
    """ImageCopy: a FLAT memcpy of w*h*bpp. Not a row-by-row blit -- the destination stride IS the source
    width, which is why only a 256-wide palette lands on the master's row boundaries."""
    n = w * h * bpp
    dst[dst_off:dst_off + n] = src[:n]


def build_master(data, bifs, keys):
    """AurSetPalette @0x485190, slot by slot: read, ImageFlipY, ImageCopy at slot << 18."""
    master = bytearray(MASTER_ROWS * ROW_BYTES)
    for slot, name in enumerate(PALETTES):
        raw = read_res(data, bifs, keys, name, TGA_RESTYPE)
        if raw is None:
            return None, f"{name} is not in the resource tables"
        px, w, h, why = decode_tga32(raw)
        if px is None:
            return None, f"{name}: {why}"
        flipped = bytearray(len(px))
        row = w * BPP
        for y in range(h):                                     # ImageFlipY
            flipped[y * row:(y + 1) * row] = px[(h - 1 - y) * row:(h - y) * row]
        image_copy(master, slot << 18, flipped, w, h, BPP)     # 0x12 = 18 -> 256 rows * 256 px * 4 B
    return bytes(master), None


# --------------------------------------------------- the ENGINE's composite

def engine_image_decode(pairs, w, h, master, rows, count):
    """ImageDecode @0x4b6580, as written: a 15 x 1024-byte stack LUT filled by copying palette row rows[i],
    then one 32-bit copy per pixel out of LUT[(layer * 256 + intensity) * 4]."""
    lut = np.zeros(ENGINE_LUT_ROWS * ROW_BYTES, dtype=np.uint8)
    for i in range(min(count, ENGINE_LUT_ROWS)):
        at = rows[i] << 10
        lut[i * ROW_BYTES:(i + 1) * ROW_BYTES] = np.frombuffer(master, np.uint8, ROW_BYTES, at)
    lut32 = lut.view(np.uint32)                                # ENGINE_LUT_ROWS * 256 entries
    p = np.frombuffer(pairs, np.uint8, w * h * 2).reshape(-1, 2)
    idx = p[:, 1].astype(np.int32) * 256 + p[:, 0].astype(np.int32)
    return lut32[idx]                                          # uint32 per pixel, little-endian RGBA


# --------------------------------- a CPU port of cx_plt.glsl (and PltImage's mips)

def build_mips(pairs, w, h):
    """PltImage.BuildMips: majority layer of the four children (a 2-2 tie decided by the destination texel's
    (x ^ y) & 1 parity, so neither tied layer dilates down the chain), mean intensity over the children
    carrying it. Returns [(w, h, ndarray(h, w, 2)), ...]."""
    cur = np.frombuffer(pairs, np.uint8, w * h * 2).reshape(h, w, 2).copy()
    chain = [(w, h, cur)]
    while w > 1 or h > 1:
        nw, nh = max(1, w >> 1), max(1, h >> 1)
        out = np.zeros((nh, nw, 2), np.uint8)
        for y in range(nh):
            y0, y1 = y * 2, min(y * 2 + 1, h - 1)
            for x in range(nw):
                x0, x1 = x * 2, min(x * 2 + 1, w - 1)
                kids = [cur[y0, x0], cur[y0, x1], cur[y1, x0], cur[y1, x1]]
                best, best_n, parity = int(kids[0][1]), 0, (x ^ y) & 1
                for k in kids:
                    cand = int(k[1])
                    n = sum(1 for q in kids if int(q[1]) == cand)
                    if n > best_n:
                        best, best_n = cand, n
                    elif n == best_n and cand != best:
                        if (parity == 0) if cand < best else (parity != 0):
                            best = cand
                hold = [int(k[0]) for k in kids if int(k[1]) == best]
                out[y, x, 0] = (sum(hold) + len(hold) // 2) // len(hold)
                out[y, x, 1] = best
        chain.append((nw, nh, out))
        cur, w, h = out, nw, nh
    return chain


def glsl_fetch(chain, level, px, py, master, rows, clamp_wrap):
    """CxPltFetch: wrap or clamp, nearest pair fetch, then texelFetch(uPltPal, ivec2(intensity, row))."""
    w, h, pairs = chain[level]
    if clamp_wrap:
        px, py = min(max(px, 0), w - 1), min(max(py, 0), h - 1)
    else:
        px -= w * (px // w)
        py -= h * (py // h)
    inten, layer = int(pairs[py, px, 0]), int(pairs[py, px, 1])
    layer = min(layer, CX_PLT_LAYERS - 1)
    at = rows[layer] * ROW_BYTES + inten * BPP
    return np.frombuffer(master, np.uint8, BPP, at).astype(np.float64)


def glsl_bilinear(chain, level, u, v, master, rows, clamp_wrap):
    """CxPltBilinear: `uv * dim - 0.5`, four resolved taps, mix(mix(..), mix(..))."""
    w, h, _ = chain[level]
    tx, ty = u * w - 0.5, v * h - 0.5
    px, py = int(np.floor(tx)), int(np.floor(ty))
    fx, fy = tx - np.floor(tx), ty - np.floor(ty)
    c00 = glsl_fetch(chain, level, px, py, master, rows, clamp_wrap)
    c10 = glsl_fetch(chain, level, px + 1, py, master, rows, clamp_wrap)
    c01 = glsl_fetch(chain, level, px, py + 1, master, rows, clamp_wrap)
    c11 = glsl_fetch(chain, level, px + 1, py + 1, master, rows, clamp_wrap)
    return (c00 * (1 - fx) + c10 * fx) * (1 - fy) + (c01 * (1 - fx) + c11 * fx) * fy


def glsl_resolve(chain, u, v, lod, master, rows, clamp_wrap):
    """CxPltResolve with the LOD stated (textureQueryLod is the hardware's)."""
    top = len(chain) - 1
    lod = min(max(lod, 0.0), float(top))
    lo = int(np.floor(lod))
    f = lod - lo
    c = glsl_bilinear(chain, lo, u, v, master, rows, clamp_wrap)
    if f > 0.004 and lod < CX_PLT_LOD_CUT and lo < top:
        c = c * (1 - f) + glsl_bilinear(chain, lo + 1, u, v, master, rows, clamp_wrap) * f
    return c


def composite_bilinear(comp32, w, h, u, v):
    """Bilinear over the ENGINE's finished composite under REPEAT -- what a filtered composite texture would
    return, which is what the resolve-then-filter order must reproduce."""
    img = comp32.view(np.uint8).reshape(h, w, 4).astype(np.float64)
    tx, ty = u * w - 0.5, v * h - 0.5
    px, py = int(np.floor(tx)), int(np.floor(ty))
    fx, fy = tx - np.floor(tx), ty - np.floor(ty)

    def at(x, y):
        return img[y % h, x % w]
    return ((at(px, py) * (1 - fx) + at(px + 1, py) * fx) * (1 - fy)
            + (at(px, py + 1) * (1 - fx) + at(px + 1, py + 1) * fx) * fy)


# ------------------------------------------------------------------- the run

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=DEFAULT_DATA)
    ap.add_argument("--limit", type=int, default=0, help="check only the first N PLTs")
    ap.add_argument("--samples", type=int, default=10,
                    help="PLTs to put through the bilinear / mip / LOD / wrap checks (each builds a mip chain)")
    ap.add_argument("--rows", default="1,274,520,531,1027,1039,1283,1295,1537,1537",
                    help="one scheme-row set (the live fighter's, by default)")
    a = ap.parse_args()
    if not os.path.isdir(a.data):
        print(f"pltcheck: no such data directory: {a.data}", file=sys.stderr)
        return 2
    bifs, keys = load_keys(a.data)
    master, why = build_master(a.data, bifs, keys)
    if master is None:
        print(f"pltcheck: FAIL -- the master palette could not be built: {why}")
        return 1
    print(f"pltcheck: master palette {PAL_W}x{MASTER_ROWS} from {len(PALETTES)} palettes "
          f"(AurSetPalette's ImageFlipY + flat ImageCopy at slot<<18)")
    m32 = np.frombuffer(master, np.uint32, MASTER_ROWS * PAL_W, 0).reshape(MASTER_ROWS, PAL_W)

    rowsets = [[int(x) for x in a.rows.split(",")],
               [0] * CX_PLT_LAYERS,
               [(l * 251) % MASTER_ROWS for l in range(CX_PLT_LAYERS)]]
    for rows in rowsets:
        while len(rows) < CX_PLT_LAYERS:
            rows.append(0)

    names = sorted(r for (r, t) in keys if t == PLT_RESTYPE)
    if a.limit:
        names = names[:a.limit]
    bad, checked, rle, layerhist, over = [], 0, 0, collections.Counter(), []
    centre_cmp, sampled = 0, 0
    sample_every = max(1, len(names) // max(a.samples, 1))

    for n, name in enumerate(names):
        raw = read_res(a.data, bifs, keys, name, PLT_RESTYPE)
        if raw is None:
            bad.append((name, "the BIF holding it is not installed"))
            continue
        got, why = parse_plt(raw)
        if got is None:
            bad.append((name, why))
            continue
        w, h, layers, comp, pairs = got
        checked += 1
        if comp:
            rle += 1
        layerhist[layers] += 1
        parr = np.frombuffer(pairs, np.uint8, w * h * 2).reshape(h, w, 2)
        top = int(parr[:, :, 1].max())
        if top >= layers:
            bad.append((name, f"a pixel names layer {top} of {layers} declared -- the engine reads stale stack"))
        if top >= CX_PLT_LAYERS:
            over.append((name, top))

        # (2) the GLSL resolve at TEXEL CENTRES, LOD 0, against the engine's composite. At a texel centre the
        # bilinear weights are exactly 0 and the LOD is 0, so this compares the engine's two-step LUT index
        # against the shader's own addressing and rounding over every texel, with no filtering in the way.
        lay = np.minimum(parr[:, :, 1].astype(np.int64), CX_PLT_LAYERS - 1)
        inten = parr[:, :, 0].astype(np.int64)
        for rows in rowsets:
            comp32 = engine_image_decode(pairs, w, h, master, rows, CX_PLT_LAYERS)
            shader32 = m32[np.asarray(rows, np.int64)[lay], inten].reshape(-1)
            centre_cmp += shader32.size
            if not np.array_equal(comp32, shader32):
                k = int(np.argmax(comp32 != shader32))
                flat = parr.reshape(-1, 2)
                bad.append((name, f"texel {k} (intensity {flat[k, 0]}, layer {flat[k, 1]}) engine "
                                  f"0x{comp32[k]:08x} vs shader 0x{shader32[k]:08x}"))
                break

        # (3)-(5) on a sample: the chain is O(w*h) in Python, so ten files, not fifteen hundred.
        if n % sample_every or sampled >= a.samples or w < 8 or h < 8:
            continue
        sampled += 1
        rows = rowsets[0]
        chain = build_mips(pairs, w, h)
        comp32 = engine_image_decode(pairs, w, h, master, rows, CX_PLT_LAYERS)

        # (3) off-centre: resolve-then-filter must equal filter-the-composite.
        worst = 0.0
        for (u, v) in [(0.3123, 0.7719), (0.5, 0.5), (0.001, 0.999), (0.25, 0.25), (0.8137, 0.1291)]:
            worst = max(worst, float(np.abs(glsl_bilinear(chain, 0, u, v, master, rows, False)
                                            - composite_bilinear(comp32, w, h, u, v)).max()))
        if worst > 1e-9:
            bad.append((name, "the GLSL bilinear over resolved colours differs from bilinear over the engine's "
                              f"composite by {worst:.3g}"))

        # (4) the mip rule, the trilinear blend and the LOD cut.
        w1, h1, m1 = chain[1]
        kid = chain[0][2]
        ref_bad = 0
        for y in range(min(h1, 8)):
            for x in range(min(w1, 8)):
                kids = [kid[y * 2, x * 2], kid[y * 2, min(x * 2 + 1, w - 1)],
                        kid[min(y * 2 + 1, h - 1), x * 2], kid[min(y * 2 + 1, h - 1), min(x * 2 + 1, w - 1)]]
                cnt = collections.Counter(int(k[1]) for k in kids)
                top = sorted(l for l in cnt if cnt[l] == max(cnt.values()))
                best = top[0] if ((x ^ y) & 1) == 0 else top[-1]   # the tie goes by the texel's parity
                hold = [int(k[0]) for k in kids if int(k[1]) == best]
                want = ((sum(hold) + len(hold) // 2) // len(hold), best)
                if (int(m1[y, x, 0]), int(m1[y, x, 1])) != want:
                    ref_bad += 1
        if ref_bad:
            bad.append((name, f"{ref_bad} mip-1 texels are not the majority-layer / mean-intensity reduction"))
        u, v = 0.3123, 0.7719
        tri = glsl_resolve(chain, u, v, 1.25, master, rows, False)
        lerp = (glsl_bilinear(chain, 1, u, v, master, rows, False) * 0.75
                + glsl_bilinear(chain, 2, u, v, master, rows, False) * 0.25)
        if float(np.abs(tri - lerp).max()) > 1e-9:
            bad.append((name, "the trilinear blend is not the lerp of its two levels"))
        if len(chain) > int(CX_PLT_LOD_CUT) + 1:
            cut = glsl_resolve(chain, u, v, CX_PLT_LOD_CUT + 0.25, master, rows, False)
            cut_lo = glsl_bilinear(chain, int(CX_PLT_LOD_CUT), u, v, master, rows, False)
            if float(np.abs(cut - cut_lo).max()) > 1e-9:
                bad.append((name, f"past CX_PLT_LOD_CUT={CX_PLT_LOD_CUT} a second level was still blended in"))

        # (5) addressing: REPEAT wraps, CLAMP clamps, at the texel the shader fetches.
        if not np.array_equal(glsl_fetch(chain, 0, -1, -1, master, rows, False),
                              glsl_fetch(chain, 0, w - 1, h - 1, master, rows, False)):
            bad.append((name, "REPEAT addressing did not wrap to the opposite edge"))
        if not np.array_equal(glsl_fetch(chain, 0, -1, -1, master, rows, True),
                              glsl_fetch(chain, 0, 0, 0, master, rows, True)):
            bad.append((name, "CLAMP addressing did not clamp to the first texel"))

    print(f"pltcheck: {checked} PLTs parsed ({rle} RLE), layer counts {dict(sorted(layerhist.items()))}")
    print(f"pltcheck: {centre_cmp} texel comparisons at texel centres, {len(rowsets)} scheme-row sets -- "
          f"{'the GLSL resolve and ImageDecode agree byte for byte' if not bad else 'see the findings below'}")
    print(f"pltcheck: {sampled} PLTs also checked off-centre (resolve-then-filter vs filter-the-composite), "
          f"for the mip reduction, the trilinear blend, the LOD cut and both wrap modes")
    if over:
        print(f"pltcheck: {len(over)} file(s) use a layer past {CX_PLT_LAYERS}, which Alloy clamps: {over[:5]}")
    if bad:
        print(f"pltcheck: FAIL -- {len(bad)} finding(s):")
        for name, why in bad[:40]:
            print(f"  {name}: {why}")
        return 1
    print("pltcheck: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
