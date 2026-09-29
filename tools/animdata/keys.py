#!/usr/bin/env python3
"""Per-bone controller census for one animation: key counts, key times, spacing, controller kind.

    ./keys.py --stock pmh0 walk run pause1 pauseturn
"""
import sys
import chain

BONES = ["rootdummy", "pelvis_g", "torso_g", "neck_g", "head_g",
         "lthigh_g", "lshin_g", "lfoot_g", "rthigh_g", "rshin_g", "rfoot_g",
         "lbicep_g", "lforearm_g", "rbicep_g", "rforearm_g"]


def spacing(ts):
    if len(ts) < 2:
        return ""
    d = [ts[i + 1] - ts[i] for i in range(len(ts) - 1)]
    lo, hi = min(d), max(d)
    return f"{lo:.4f}..{hi:.4f} ({'uniform' if hi - lo < 1e-4 else 'varied'})"


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch = chain.walk(root, stock)
    rm = chain.resolve_map(ch)
    for nm in names:
        if nm not in rm:
            print(f"!! {nm} missing")
            continue
        owner, a = rm[nm]
        print(f"=== {nm} @{owner}  length {a.length:.4f}  transtime {a.transtime:.4f}  "
              f"animroot {a.animroot}  nodes {len(a.nodes)}")
        for b in BONES:
            n = a.nodes.get(b)
            if n is None:
                print(f"  {b:<12} (absent)")
                continue
            pt = [k[0] for k in n.poskeys]
            ot = [k[0] for k in n.orikeys]
            print(f"  {b:<12} pos {len(pt):>3} keys  {spacing(pt):<24} "
                  f"ori {len(ot):>3} keys  {spacing(ot)}")
            if b == "rootdummy" and pt:
                print(f"      pos times: " + " ".join(f"{t:.3f}" for t in pt))
                print(f"      pos z    : " + " ".join(f"{k[3]:.4f}" for k in n.poskeys))
            if b in ("lthigh_g", "rthigh_g") and ot:
                print(f"      ori times: " + " ".join(f"{t:.3f}" for t in ot))
        print()
