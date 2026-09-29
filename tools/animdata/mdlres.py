#!/usr/bin/env python3
"""Resolve an MDL resref the way the live NWN:EE client does, and hand back ASCII text.

Search order (highest first), mirroring the client's resman:
    1. user override/          (empty on this box, still checked)
    2. the module's hak list, in module order: sow_top, sow_over_01, sow_core_01, ...
    3. the game's ovr/ directory
    4. data/nwn_base.key -> *.bif

`--stock` restricts the search to 3+4 (what a vanilla client sees).

Binary MDLs go through NeverBlender's pure-Python decompiler, same as tools/mdlcensus/census.py.
"""
import os, pickle, struct, subprocess, sys, tempfile

sys.path.insert(0, os.environ.get("NEVERBLENDER", os.path.expanduser("~/repos/neverblender/NeverBlender")))
import nvb_decompile  # noqa: E402

NWN_ROOT = os.environ.get("NWN_ROOT", "~/gry/steam/Neverwinter Nights")
NWN_HOME = os.path.expanduser("~/.local/share/Neverwinter Nights")
HAK_ORDER = ["sow_top", "sow_over_01", "sow_core_01", "sow_core_02", "sow_part_01",
             "sow_appr_01", "sow_item_01", "sow_envi_01", "sow_vfxs_01"]
RESTYPE_MDL = 2002
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "extract", "hakindex.pkl")


def erf_index(path):
    """resref.lower() -> (offset, size) for every MDL in a HAK/ERF/MOD."""
    out = {}
    with open(path, "rb") as f:
        hdr = f.read(160)
        count = struct.unpack_from("<I", hdr, 16)[0]
        key_off, res_off = struct.unpack_from("<II", hdr, 24)
        f.seek(key_off); keys = f.read(24 * count)
        f.seek(res_off); res = f.read(8 * count)
        for i in range(count):
            resref, _, restype = struct.unpack_from("<16sIH", keys, 24 * i)
            if restype != RESTYPE_MDL:
                continue
            off, size = struct.unpack_from("<II", res, 8 * i)
            out[resref.rstrip(b"\0").decode("latin1").lower()] = (off, size)
    return out


_haks = None


def hak_indexes():
    global _haks
    if _haks is not None:
        return _haks
    if os.path.exists(CACHE):
        with open(CACHE, "rb") as f:
            _haks = pickle.load(f)
            return _haks
    _haks = []
    for h in HAK_ORDER:
        p = os.path.join(NWN_HOME, "hak", h + ".hak")
        if os.path.exists(p):
            _haks.append((h, p, erf_index(p)))
    os.makedirs(os.path.dirname(CACHE), exist_ok=True)
    with open(CACHE, "wb") as f:
        pickle.dump(_haks, f)
    return _haks


def _resman_bytes(resref):
    """Pull <resref>.mdl out of the installed game (ovr/ then the BIFs) via neverwinter.nim."""
    with tempfile.TemporaryDirectory() as td:
        r = subprocess.run(["nwn_resman_extract", "--root", NWN_ROOT, "-d", td, resref + ".mdl"],
                           capture_output=True)
        p = os.path.join(td, resref + ".mdl")
        if not os.path.exists(p):
            p2 = os.path.join(td, resref.upper() + ".mdl")
            p = p2 if os.path.exists(p2) else None
        if p is None:
            raise KeyError(f"{resref}.mdl not in resman ({r.stderr[:200]!r})")
        with open(p, "rb") as f:
            return f.read()


def raw(resref, stock=False):
    """Bytes of <resref>.mdl plus the source label, resolved in client order."""
    resref = resref.lower()
    if not stock:
        ov = os.path.join(NWN_HOME, "override", resref + ".mdl")
        if os.path.exists(ov):
            with open(ov, "rb") as f:
                return f.read(), "override"
        for name, path, idx in hak_indexes():
            if resref in idx:
                off, size = idx[resref]
                with open(path, "rb") as f:
                    f.seek(off)
                    return f.read(size), name
    return _resman_bytes(resref), "stock"


def to_ascii(data):
    if data[:4] != b"\0\0\0\0":
        return data.decode("latin1")
    r = nvb_decompile.BinaryMdlReader("<mem>")
    r.data = data
    r._read_file_header(); r._read_model_header()
    return r.get_ascii()


_text_cache = {}


def text(resref, stock=False):
    """ASCII MDL text + source label, cached per process."""
    key = (resref.lower(), stock)
    if key not in _text_cache:
        data, src = raw(resref, stock)
        _text_cache[key] = (to_ascii(data), src)
    return _text_cache[key]


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    for a in [x for x in sys.argv[1:] if not x.startswith("--")]:
        t, src = text(a, stock)
        sys.stderr.write(f"# {a}: {src}, {len(t)} chars\n")
        sys.stdout.write(t)
