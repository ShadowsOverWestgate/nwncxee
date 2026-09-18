#!/usr/bin/env python3
"""Hak/nwsync MDL census: what node types and flags ship in content Alloy must draw.

Sources: directories of .mdl, .hak/.erf/.mod containers, and nwsync manifests
(`nwsync:<sha1-prefix>`). Binary models go through NeverBlender's pure-Python
decompiler (NEVERBLENDER env or ~/repos/neverblender); ASCII models are scanned as-is.
Output: one TSV row per mesh node on stdout, tallies on stderr.

    tools/mdlcensus/census.py "$HOME/.local/share/Neverwinter Nights"/hak/*.hak > haks.tsv
    tools/mdlcensus/census.py nwsync:fde1 --grep bench
"""
import argparse, collections, os, re, sqlite3, struct, sys, zlib
from compression import zstd

sys.path.insert(0, os.environ.get("NEVERBLENDER", os.path.expanduser("~/repos/neverblender/NeverBlender")))
import nvb_decompile  # noqa: E402

NWN_HOME = os.path.expanduser("~/.local/share/Neverwinter Nights")
RESTYPE_MDL = 2002
MESH_NODES = {"trimesh", "skin", "danglymesh", "animmesh", "aabb"}
NODE_KEYS = ["bitmap", "materialname", "render", "shadow", "tilefade", "renderhint", "lightmapped",
             "beaming", "rotatetexture", "transparencyhint", "alpha", "twosidedtex", "verts", "faces",
             "tangents", "constraints", "weights", "colors"]


def erf_entries(path):
    """Yield (resref, restype, bytes) from a HAK/ERF/MOD container."""
    with open(path, "rb") as f:
        hdr = f.read(160)
        count, key_off, res_off = struct.unpack_from("<I", hdr, 16)[0], *struct.unpack_from("<II", hdr, 24)
        f.seek(key_off); keys = f.read(24 * count)
        f.seek(res_off); res = f.read(8 * count)
        for i in range(count):
            resref, _, restype = struct.unpack_from("<16sIH", keys, 24 * i)
            off, size = struct.unpack_from("<II", res, 8 * i)
            if restype == RESTYPE_MDL:
                f.seek(off); yield resref.rstrip(b"\0").decode("latin1").lower(), restype, f.read(size)


def nwsync_entries(prefix):
    """Yield (resref, restype, bytes) for one manifest's models; NSYC blob = 24-byte header + zstd/zlib/raw."""
    meta = sqlite3.connect(f"{NWN_HOME}/nwsync/nwsyncmeta.sqlite3")
    data = sqlite3.connect(f"{NWN_HOME}/nwsync/nwsyncdata_0.sqlite3")
    rows = meta.execute("select resref, resref_sha1 from manifest_resrefs where manifest_sha1 like ? and restype=?",
                        (prefix + "%", RESTYPE_MDL)).fetchall()
    for resref, sha in rows:
        blob = data.execute("select data from resrefs where sha1=?", (sha,)).fetchone()
        if blob is None: continue
        b = blob[0]
        if b[:4] == b"NSYC":
            alg = struct.unpack_from("<I", b, 8)[0]
            b = {0: lambda x: x, 1: zlib.decompress, 2: zstd.decompress}[alg](b[24:])
        yield resref.lower(), RESTYPE_MDL, b


def dir_entries(path):
    for root, _, files in os.walk(path):
        for fn in files:
            if fn.lower().endswith(".mdl"):
                with open(os.path.join(root, fn), "rb") as f:
                    yield fn[:-4].lower(), RESTYPE_MDL, f.read()


def to_ascii(data):
    """Binary -> NeverBlender decompile; ASCII -> as-is. Returns (text, was_binary)."""
    if data[:4] != b"\0\0\0\0":
        return data.decode("latin1"), False
    r = nvb_decompile.BinaryMdlReader("<mem>")
    r.data = data
    r._read_file_header(); r._read_model_header()
    return r.get_ascii(), True


def scan(text):
    """Yield (model_props, [node dicts]) from ASCII MDL text."""
    model = {"classification": "", "supermodel": ""}
    nodes, cur = [], None
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line: continue
        tok = line.split()
        key = tok[0].lower()
        if key == "node" and len(tok) >= 3:
            cur = {"type": tok[1].lower(), "name": tok[2]}; nodes.append(cur); continue
        if key == "endnode": cur = None; continue
        if key == "newanim": break
        if key == "classification": model["classification"] = tok[1] if len(tok) > 1 else ""
        elif key == "setsupermodel": model["supermodel"] = tok[2] if len(tok) > 2 else ""
        elif cur is not None and key in NODE_KEYS:
            cur[key] = tok[1] if len(tok) > 1 else "1"
    return model, nodes


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("sources", nargs="+", help="dir | .hak/.erf/.mod | nwsync:<sha1-prefix>")
    ap.add_argument("--grep", help="only models whose resref matches this regex")
    ap.add_argument("--all-nodes", action="store_true", help="emit dummy/light/emitter/reference rows too")
    a = ap.parse_args()
    pat = re.compile(a.grep) if a.grep else None
    cols = ["source", "model", "binary", "classification", "supermodel", "type", "name"] + NODE_KEYS
    print("\t".join(cols))
    tally, errors, models = collections.Counter(), [], 0
    for src in a.sources:
        if src.startswith("nwsync:"): it = nwsync_entries(src[7:])
        elif os.path.isdir(src): it = dir_entries(src)
        else: it = erf_entries(src)
        label = os.path.basename(src)
        for resref, _, data in it:
            if pat and not pat.search(resref): continue
            models += 1
            try:
                text, binary = to_ascii(data)
                model, nodes = scan(text)
            except Exception as e:  # ponytail: one bad model must not kill the census
                errors.append(f"{label}/{resref}: {type(e).__name__}: {str(e)[:80]}"); continue
            tally["models"] += 1
            tally["binary" if binary else "ascii"] += 1
            tally[f"class={model['classification'].lower() or '-'}"] += 1
            for n in nodes:
                tally[f"node={n['type']}"] += 1
                if n["type"] in MESH_NODES:
                    for k in ("render", "shadow", "tilefade", "renderhint", "lightmapped", "beaming",
                              "rotatetexture", "transparencyhint", "twosidedtex"):
                        if k in n and n[k] not in ("0", "None"):
                            # ponytail: the binary lightmapped byte is noise past 0/1; bucket it
                            tally[f"{k}={n[k] if k != 'lightmapped' else 'nonzero'}"] += 1
                    if n.get("bitmap", "").lower() in ("", "null"): tally["bitmap=null"] += 1
                    if "materialname" in n: tally["has-mtr"] += 1
                    if "tangents" in n: tally["has-tangents"] += 1
                    if n.get("verts", "0") == "0": tally["verts=0"] += 1
                if a.all_nodes or n["type"] in MESH_NODES:
                    row = [label, resref, "1" if binary else "0", model["classification"], model["supermodel"],
                           n["type"], n["name"]] + [n.get(k, "") for k in NODE_KEYS]
                    print("\t".join(row))
    err = sys.stderr
    print(f"# {models} models seen, {len(errors)} failed to parse", file=err)
    for k, v in sorted(tally.items(), key=lambda kv: (kv[0].split('=')[0], -kv[1])):
        print(f"{k}\t{v}", file=err)
    for e in errors[:50]: print("! " + e, file=err)


if __name__ == "__main__":
    main()
