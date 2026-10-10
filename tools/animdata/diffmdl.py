#!/usr/bin/env python3
"""Compare the same model resolved live (the HAKS chain) vs stock: which animations differ, and how.

    ./diffmdl.py a_ba a_fa pmh0
"""
import math, sys
import mdlres, mdlparse

TOL = 5e-4          # ASCII re-export rounding


def cmp_anim(a, b):
    out = []
    if abs(a.length - b.length) > TOL:
        out.append(f"length {a.length:.4f} -> {b.length:.4f}")
    if abs(a.transtime - b.transtime) > TOL:
        out.append(f"transtime {a.transtime:.4f} -> {b.transtime:.4f}")
    if a.animroot != b.animroot:
        out.append(f"animroot {a.animroot} -> {b.animroot}")
    ea = sorted(set((round(t, 3), n.lower()) for t, n in a.events))
    eb = sorted(set((round(t, 3), n.lower()) for t, n in b.events))
    if ea != eb:
        out.append(f"events {ea} -> {eb}")
    if set(a.nodes) != set(b.nodes):
        out.append(f"nodes +{sorted(set(b.nodes)-set(a.nodes))[:4]} -{sorted(set(a.nodes)-set(b.nodes))[:4]}")
    worst_p = worst_o = 0.0
    nkeydiff = []
    for nm in sorted(set(a.nodes) & set(b.nodes)):
        na, nb = a.nodes[nm], b.nodes[nm]
        if len(na.poskeys) != len(nb.poskeys) or len(na.orikeys) != len(nb.orikeys):
            nkeydiff.append(f"{nm}({len(na.poskeys)}/{len(na.orikeys)}->{len(nb.poskeys)}/{len(nb.orikeys)})")
            continue
        for ka, kb in zip(na.poskeys, nb.poskeys):
            worst_p = max(worst_p, max(abs(x - y) for x, y in zip(ka, kb)))
        for ka, kb in zip(na.orikeys, nb.orikeys):
            # axis-angle is not unique; compare the rotation itself (angle between quaternions)
            qa, qb = mdlparse.aa_to_quat(*ka[1:5]), mdlparse.aa_to_quat(*kb[1:5])
            d = abs(sum(x * y for x, y in zip(qa, qb)))
            ang = 2 * math.degrees(math.acos(min(1.0, d)))
            worst_o = max(worst_o, ang, abs(ka[0] - kb[0]))
    if nkeydiff:
        out.append("keycount " + ",".join(nkeydiff[:6]))
    if worst_p > TOL:
        out.append(f"max|dpos| {worst_p:.5f}")
    if worst_o > 0.05:
        out.append(f"max rot delta {worst_o:.3f} deg")
    return out


if __name__ == "__main__":
    for r in sys.argv[1:]:
        try:
            ts, _ = mdlres.text(r, stock=True)
        except KeyError:
            print(f"{r}: no stock copy"); continue
        tl, src = mdlres.text(r, stock=False)
        ms, ml = mdlparse.parse(ts), mdlparse.parse(tl)
        print(f"=== {r}: stock({len(ms.anims)} anims) vs {src}({len(ml.anims)} anims)")
        print(f"    supermodel {ms.supermodel} -> {ml.supermodel}; "
              f"animscale {ms.animationscale} -> {ml.animationscale}")
        added = sorted(set(ml.anims) - set(ms.anims))
        removed = sorted(set(ms.anims) - set(ml.anims))
        if added:
            print(f"    added: {' '.join(added)}")
        if removed:
            print(f"    removed: {' '.join(removed)}")
        ndiff = 0
        for nm in sorted(set(ms.anims) & set(ml.anims)):
            d = cmp_anim(ms.anims[nm], ml.anims[nm])
            if d:
                ndiff += 1
                print(f"    {nm}: " + "; ".join(d))
        print(f"    {ndiff} of {len(set(ms.anims) & set(ml.anims))} shared animations differ "
              f"beyond tol {TOL}")
