#!/usr/bin/env python3
"""Locomotion inventory for one supermodel chain: where each name resolves, plus its shape.

    ./inventory.py --stock pmh0 > loco_pmh0_stock.tsv
    ./inventory.py pmh0                       # live (the HAKS chain)
    ./inventory.py --all --stock pmh0         # every animation, not just locomotion
"""
import re, sys
import chain

LOCO = re.compile(
    r"^(walk|run|cwalk|pausewalk|pauseturn|cturn|hturn|pause\d?|pausesh|pausebrd|pausepsn|pausetrd"
    r"|sidestep|step|turn|idle|walkinj|walkdead|getlow|getmid|dodge|mount)", re.I)

MAIN_BONES = ["rootdummy", "pelvis_g", "torso_g", "lthigh_g", "rthigh_g", "lshin_g", "rshin_g",
              "lfoot_g", "rfoot_g", "lbicep_g", "rbicep_g", "neck_g", "head_g"]


def dedup_events(ev):
    """The stock binary stores every event twice; collapse identical (time, name) pairs."""
    seen, out = set(), []
    for t, n in sorted(ev):
        k = (round(t, 4), n.lower())
        if k in seen:
            continue
        seen.add(k)
        out.append((t, n))
    return out


def anim_row(name, owner, a):
    ev = dedup_events(a.events)
    keyed = [n for n in a.nodes.values() if n.poskeys or n.orikeys]
    posk = [n.name.lower() for n in a.nodes.values() if len(n.poskeys) > 1]
    root = a.nodes.get("rootdummy")
    rootz = ""
    rootxy = ""
    if root and len(root.poskeys) > 1:
        zs = [k[3] for k in root.poskeys]
        rootz = f"{min(zs):.4f}..{max(zs):.4f}"
        d = (root.poskeys[-1][1] - root.poskeys[0][1], root.poskeys[-1][2] - root.poskeys[0][2],
             root.poskeys[-1][3] - root.poskeys[0][3])
        rootxy = f"{d[0]:+.3f},{d[1]:+.3f},{d[2]:+.3f}"
    percounts = ";".join(f"{b}:{len(a.nodes[b].orikeys) if b in a.nodes else 0}"
                         f"/{len(a.nodes[b].poskeys) if b in a.nodes else 0}" for b in MAIN_BONES)
    return [name, owner, f"{a.length:.4f}", f"{a.transtime:.4f}", a.animroot,
            str(len(a.nodes)), str(len(keyed)),
            ",".join(f"{t:.3f}:{n}" for t, n in ev),
            ",".join(posk), rootz, rootxy, percounts]


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    everything = "--all" in sys.argv
    roots = [a for a in sys.argv[1:] if not a.startswith("--")] or ["pmh0"]
    cols = ["anim", "resolves_to", "length", "transtime", "animroot", "nodes", "keyed_nodes",
            "events", "pos_keyed_nodes", "root_z_range", "root_net_dxyz", "orikeys/poskeys per bone"]
    print("\t".join(cols))
    for root in roots:
        ch = chain.walk(root, stock)
        rm = chain.resolve_map(ch)
        for name in sorted(rm):
            if not everything and not LOCO.match(name):
                continue
            owner, a = rm[name]
            print("\t".join(anim_row(name, owner, a)))
