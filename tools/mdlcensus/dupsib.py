#!/usr/bin/env python3
"""Offline twin of AlloyCensus.DupSibling: for every model, report a trimesh that has a LATER sibling
(same parent) with text-equal position/orientation/scale and equal verts+faces. Those are the parts
the #397 gate refuses at enrolment. Usage: dupsib.py <dir|hak|nwsync:prefix>... [--class DOOR] [--old]. Exit 1 when it would refuse anything.
#317 found the rule eating door leaves (two faces, same node transform, different positions); --old shows that."""
import sys, os, collections
POS = "--old" not in sys.argv   # --old: the pre-#317 rule (transform + counts only)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import census

def nodes_of(text):
    nodes, cur = [], None
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line: continue
        tok = line.split(); key = tok[0].lower()
        if key == "newanim": break
        if key == "node" and len(tok) >= 3: cur = {"type": tok[1].lower(), "name": tok[2], "render": "1"}; nodes.append(cur); continue
        if key == "endnode": cur = None; continue
        if key == "classification": cls = tok[1].upper() if len(tok) > 1 else ""; nodes.append({"type": "_cls", "name": cls}); continue
        if cur is not None and key in ("parent", "position", "orientation", "scale", "verts", "faces", "bitmap", "render"):
            cur[key] = " ".join(tok[1:])
            if key == "verts": cur["_vlines"] = int(tok[1]); cur["_v"] = []
        elif cur is not None and cur.get("_vlines", 0) > 0 and len(tok) == 3:
            cur["_v"].append(line); cur["_vlines"] -= 1
    return nodes

def twins(nodes):
    """(victim, winner) pairs: victim is EARLIER, winner a later renderable trimesh sibling, same xform+counts."""
    by_parent = collections.defaultdict(list)
    for n in nodes:
        if n["type"] in ("trimesh", "danglymesh", "skin", "animmesh") and n.get("render", "1") != "0":
            by_parent[n.get("parent", "").lower()].append(n)
    out = []
    for kids in by_parent.values():
        for i, a in enumerate(kids):
            ka = (a.get("position"), a.get("orientation"), a.get("scale", "1.0"), a.get("verts"), a.get("faces"))
            for b in kids[i + 1:]:
                kb = (b.get("position"), b.get("orientation"), b.get("scale", "1.0"), b.get("verts"), b.get("faces"))
                if ka == kb and (not POS or a.get("_v") == b.get("_v")): out.append((a, b)); break
    return out

def main():
    want = None
    if "--class" in sys.argv: want = sys.argv[sys.argv.index("--class") + 1].upper()
    args = [a for a in sys.argv[1:] if not a.startswith("--") and a.upper() != want]
    hits = 0; models = 0
    for src in args:
        if src.startswith("nwsync:"): it = census.nwsync_entries(src[7:])
        elif os.path.isdir(src): it = census.dir_entries(src)
        else: it = census.erf_entries(src)
        for name, _, data in it:
            try: text, _ = census.to_ascii(data)
            except Exception as ex: print(f"{name}\tPARSE-FAIL\t{ex}", file=sys.stderr); continue
            nodes = nodes_of(text)
            cls = next((n["name"] for n in nodes if n["type"] == "_cls"), "")
            if want and cls != want: continue
            models += 1
            for a, b in twins(nodes):
                hits += 1
                print(f"{name}\t{cls}\tvictim={a['name']}({a.get('bitmap')})\twinner={b['name']}({b.get('bitmap')})\tparent={a.get('parent')}\tpos={a.get('position')}\tv/f={a.get('verts')}/{a.get('faces')}")
    print(f"# {models} models scanned, {hits} parts DupSibling would refuse", file=sys.stderr)
    return 1 if hits else 0

if __name__ == "__main__": sys.exit(main())
