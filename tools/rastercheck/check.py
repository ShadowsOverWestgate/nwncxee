#!/usr/bin/env python3
"""Alloy's TGA decode (and its DDS unpack for the textures the engine forces uncompressed) against Pillow's,
file by file; PNGs of both (and of what the engine uploads for a colour-mapped file) land in the output
directory. Needs Pillow and the .NET SDK.

    tools/rastercheck/check.py [-o OUTDIR] file.tga|file.dds ...
"""
import argparse, io, os, struct, subprocess, sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

def engine_today(raw, w, h):
    """What the stock engine uploads for a colour-mapped TGA: the 8-bit indices (RLE-expanded if type 9),
    then its R/B swap at a 1-byte stride, which shifts them two pixels left; shown as greyscale."""
    idl, typ = raw[0], raw[2]
    clen, cbits = int.from_bytes(raw[5:7], "little"), raw[7]
    src, n = raw[18 + idl + clen * (cbits >> 3):], w * h
    out, s = bytearray(), 0
    while len(out) < n:
        if typ != 9: out += src[s:s + n]; break
        head = src[s]; count = (head & 0x7f) + 1
        if head & 0x80: out += bytes([src[s + 1]]) * count; s += 2
        else: out += src[s + 1:s + 1 + count]; s += 1 + count
    out = out[:n] + b"\0\0"          # the swap reads two bytes into the engine's tail
    for i in range(n): out[i], out[i + 2] = out[i + 2], out[i]
    return Image.frombytes("L", (w, h), bytes(out[:n]))

def dds_reference(raw):
    """Pillow reads only the Microsoft header: a legacy BioWare one (w, h, channels, level-0 size, alpha mean)
    is rewrapped as level 0 alone behind a DXT1/DXT5 Microsoft header."""
    if raw[:4] == b"DDS ":
        return Image.open(io.BytesIO(raw))
    w, h, ch, size = struct.unpack_from("<4I", raw)
    hdr = bytearray(128)
    struct.pack_into("<4sIIIIIII", hdr, 0, b"DDS ", 124, 0x1007 | 0x80000, h, w, size, 0, 1)
    struct.pack_into("<II4s", hdr, 76, 32, 4, b"DXT5" if ch == 4 else b"DXT1")
    struct.pack_into("<I", hdr, 108, 0x1000)
    return Image.open(io.BytesIO(bytes(hdr) + raw[20:20 + size]))

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-o", default=os.path.expanduser("~/cx-rastercheck"))
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    os.makedirs(a.o, exist_ok=True)
    subprocess.run(["dotnet", "build", "-v", "q", "-nologo", HERE], check=True, stdout=subprocess.DEVNULL)
    exe = [os.path.join(HERE, "bin/Debug/net10.0/rastercheck")]
    bad = 0
    for f in a.files:
        name = os.path.splitext(os.path.basename(f))[0]
        rawout = os.path.join(a.o, name + ".alloy.raw")
        verdict, w, h, px, typ, *why = subprocess.run(exe + [f, rawout], check=True, capture_output=True, text=True).stdout.split(" ")
        dds = typ == "dds"
        w, h, px, typ = int(w), int(h), int(px), (0 if dds else int(typ))
        ref = dds_reference(open(f, "rb").read()) if dds else Image.open(f)
        ref.load()
        mode = {1: "L", 3: "RGB", 4: "RGBA"}.get(px)
        if verdict != "Pixels" or mode is None:
            print(f"{name}: Alloy does not decode it ({verdict}: {' '.join(why).strip()})"); bad += 1; continue
        ours = Image.frombytes(mode, (w, h), open(rawout, "rb").read())
        # The engine never honours the top-origin bit: rows stay in file order. Pillow shows them upright.
        top = dds or open(f, "rb").read(18)[17] & 0x20
        shown = ours if top else ours.transpose(Image.FLIP_TOP_BOTTOM)
        same = shown.tobytes() == ref.convert(mode).tobytes()
        shown.save(os.path.join(a.o, name + ".alloy.png"))
        ref.convert(mode).save(os.path.join(a.o, name + ".reference.png"))
        extra = ""
        if typ in (1, 9):
            e = engine_today(open(f, "rb").read(), w, h)
            (e if top else e.transpose(Image.FLIP_TOP_BOTTOM)).save(os.path.join(a.o, name + ".engine-today.png"))
            extra = ", engine-today.png = what stock NWN uploads"
        elif typ == 11:
            extra = ", stock NWN refuses type 11: the texture never loads"
        elif dds and "beyond" in " ".join(why):
            extra = ", stock NWN misreads this header (0 channels, data at +128): only Alloy loads it"
        elif dds:
            extra = ", stock NWN never reads a DDS for a cube, grid or controller texture"
        print(f"{name}: {'DDS ' + ' '.join(why).replace(' beyond', '').strip() if dds else f'type {typ}'} {w}x{h} -> {mode}: {'MATCHES Pillow' if same else 'DIFFERS from Pillow'}{extra}")
        bad += not same
        os.remove(rawout)
    print(f"-> {a.o}")
    return 1 if bad else 0

sys.exit(main())
