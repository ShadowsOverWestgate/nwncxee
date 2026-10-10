#!/usr/bin/env python3
"""Walk a model's setsupermodel chain and report where each animation name resolves.

    ./chain.py pmh0 pfh0            # live (the HAKS chain first)
    ./chain.py --stock pmh0 pfh0    # vanilla install only
"""
import sys
import mdlres, mdlparse


def walk(root, stock=False, limit=16):
    """[(resref, source, Model)] from root down the setsupermodel chain."""
    out, seen, cur = [], set(), root.lower()
    while cur and cur not in ("null", "") and cur not in seen and len(out) < limit:
        seen.add(cur)
        try:
            txt, src = mdlres.text(cur, stock)
        except KeyError as e:
            out.append((cur, "MISSING", None))
            break
        m = mdlparse.parse(txt)
        out.append((cur, src, m))
        cur = m.supermodel
    return out


def resolve_map(ch):
    """animation name -> (model resref, Anim). First model in the chain wins."""
    res = {}
    for resref, src, m in ch:
        if m is None:
            continue
        for a in m.animorder:
            res.setdefault(a, (resref, m.anims[a]))
    return res


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    for root in [a for a in sys.argv[1:] if not a.startswith("--")]:
        ch = walk(root, stock)
        print(f"=== {root} ({'stock' if stock else 'live'}) ===")
        for resref, src, m in ch:
            n = len(m.anims) if m else 0
            geom = len(m.nodes) if m else 0
            print(f"  {resref:<18} from {src:<14} anims={n:<4} nodes={geom:<4} "
                  f"scale={m.animationscale if m else '-'} -> {m.supermodel if m else '?'}")
        rm = resolve_map(ch)
        print(f"  total distinct animation names: {len(rm)}")
