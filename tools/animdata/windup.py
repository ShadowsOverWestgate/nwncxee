#!/usr/bin/env python3
"""Does the CONTENT wind up / wind down? Three tests, all on the composed skeleton.

  1. loop continuity  - ankle/root speed just before the wrap vs just after (a start-from-rest
                        cycle would show a jump);
  2. half-cycle symmetry - first half vs second half of the implied ground speed (an authored
                        acceleration would break the symmetry);
  3. in-cycle ripple  - min/max of the implied ground speed over the bearing part, and where.

    ./windup.py --stock pmh0 walk run
"""
import sys
import fk
import mdlparse


def main():
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = fk.build(root, stock)
    for nm in names:
        owner, a = rm[nm]
        r = fk.analyse(skel, a, nm)
        L, N = a.length, fk.N
        sp = r["speed_profile"]
        # 1. wrap continuity: compare the mean over the last 5 % and the first 5 % of the cycle
        k = max(1, N // 20)
        head, tail = sum(sp[:k]) / k, sum(sp[-k:]) / k
        # per-key continuity of the raw controllers at the wrap
        jumps = []
        for bone, n in a.nodes.items():
            if len(n.poskeys) > 1:
                d = max(abs(n.poskeys[0][c] - n.poskeys[-1][c]) for c in (1, 2, 3))
                if d > 1e-4:
                    jumps.append(f"{bone} pos {d:.4f}")
            if len(n.orikeys) > 1:
                qa = mdlparse.aa_to_quat(*n.orikeys[0][1:5])
                qb = mdlparse.aa_to_quat(*n.orikeys[-1][1:5])
                dd = abs(sum(x * y for x, y in zip(qa, qb)))
                if dd < 0.9999:
                    jumps.append(f"{bone} ori {2 * 57.2958 * (1 - dd):.3f}deg")
        # 2. half-cycle symmetry
        h = N // 2
        first, second = sp[:h], sp[h:]
        mf, ms = sum(first) / len(first), sum(second) / len(second)
        # 3. ripple, already in analyse
        lo, hi = r.get("speed_ripple", (float("nan"),) * 2)
        imin = min(range(N), key=lambda i: sp[i] if (r["onL"][i] or r["onR"][i]) else 1e9)
        imax = max(range(N), key=lambda i: sp[i] if (r["onL"][i] or r["onR"][i]) else -1e9)
        print(f"=== {nm} @{owner} length {L:.3f}")
        print(f"  wrap: mean ground speed over last 5 % {tail:.3f} vs first 5 % {head:.3f} "
              f"(delta {100*(head-tail)/max(head,tail,1e-9):+.1f} %)")
        print(f"  first-key == last-key mismatches: {len(jumps)}"
              + (("  e.g. " + ", ".join(jumps[:4])) if jumps else "  (cycle closes exactly)"))
        print(f"  half-cycle means: {mf:.3f} vs {ms:.3f} ({100*(ms-mf)/max(mf,1e-9):+.1f} %)")
        print(f"  bearing-phase ground speed {lo:.3f} (t={imin*L/N:.3f}) .. {hi:.3f} "
              f"(t={imax*L/N:.3f}), ripple {100*(hi-lo)/max(hi,1e-9):.0f} % of peak")
        print(f"  leg-bone key spacing: " + ", ".join(
            f"{b}:{len(a.nodes[b].orikeys)}" for b in
            ("lthigh_g", "lshin_g", "lfoot_g", "rthigh_g", "rshin_g", "rfoot_g") if b in a.nodes))


if __name__ == "__main__":
    main()
