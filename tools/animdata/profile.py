#!/usr/bin/env python3
"""Phase profile of a locomotion cycle: 20 bins of ankle height, backward speed, hip height.

Answers "does the content itself accelerate/decelerate inside the cycle" without any engine term:
the implied ground speed is the backward speed of whichever ankle is currently bearing weight.

    ./profile.py --stock pmh0 walk run
"""
import sys
import chain, fk

BINS = 20


def main():
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = fk.build(root, stock)
    for nm in names:
        owner, a = rm[nm]
        r = fk.analyse(skel, a, nm)
        L = a.length
        step = fk.N // BINS
        ev = sorted(set((round(t, 3), n.lower()) for t, n in a.events))
        print(f"=== {nm} @{owner} length {L:.3f}s  events " +
              ", ".join(f"{t:.3f}" for t, _ in ev))
        print(f"{'phase':>6} {'t':>6} {'Lz':>6} {'Rz':>6} {'-vLy':>7} {'-vRy':>7} "
              f"{'ground':>7} {'bearing':>8} {'rootz':>7}")
        dt = L / fk.N
        vLy = fk.deriv(r["ank"]["lfoot_g"]["y"], dt)
        vRy = fk.deriv(r["ank"]["rfoot_g"]["y"], dt)
        for b in range(BINS):
            i = b * step
            bearing = ("L" if r["onL"][i] else "") + ("R" if r["onR"][i] else "") or "-"
            print(f"{b/BINS:6.2f} {i*dt:6.3f} {r['ank']['lfoot_g']['z'][i]:6.3f} "
                  f"{r['ank']['rfoot_g']['z'][i]:6.3f} {-vLy[i]:7.3f} {-vRy[i]:7.3f} "
                  f"{max(-vLy[i], -vRy[i]):7.3f} {bearing:>8} {r['root']['z'][i]:7.4f}")
        print()


if __name__ == "__main__":
    main()
