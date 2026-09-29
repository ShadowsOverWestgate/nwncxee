#!/usr/bin/env python3
"""Attach each snd_footstep event to a foot, and measure its offset from the real touchdown.

Touchdown = the start of the foot's weight-bearing window (backward speed crosses 50 % of the
cycle's ground speed). Both come from the composed skeleton; nothing is assumed about parity.

    ./events.py --stock pmh0 walk run cwalkf cwalkb cwalkl cwalkr pausewalkl pausewalkr \
                pausewalkfl pausewalkfr runfl runfr pauseturn walkinj walkdead dodges dodgelr
"""
import sys
import fk


def windows(on, L):
    n = len(on)
    starts = [i for i in range(n) if on[i] and not on[i - 1]]
    ends = [i for i in range(n) if on[i] and not on[(i + 1) % n]]
    return [(s * L / n, None) for s in starts], [(e * L / n) for e in ends]


def main():
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = fk.build(root, stock)
    print(f"# {root} ({'stock' if stock else 'live'})")
    print("anim\tresolves\tlength\tevent_t\tevent\tfoot\ttouchdown_t\tevent_minus_touchdown\tliftoff_t\tstance_s")
    for nm in names:
        if nm not in rm:
            print(f"# !! {nm} missing")
            continue
        owner, a = rm[nm]
        r = fk.analyse(skel, a, nm)
        L = a.length
        if "onL" not in r:
            print(f"# !! {nm}: no ground speed (stationary?)")
            continue
        td = {}
        for f, on in (("L", r["onL"]), ("R", r["onR"])):
            st, en = windows(on, L)
            td[f] = ([s for s, _ in st], en)
        ev = sorted(set((round(t, 4), n.lower()) for t, n in a.events))
        for t, n in ev:
            best = None
            for f in "LR":
                for s in td[f][0]:
                    d = (t - s) % L
                    d = d if d <= L / 2 else d - L
                    if best is None or abs(d) < abs(best[2]):
                        # pick the lift-off that follows this touchdown
                        lo = min(td[f][1], key=lambda e: (e - s) % L) if td[f][1] else float("nan")
                        best = (f, s, d, lo, (lo - s) % L)
            if best is None:
                print(f"{nm}\t{owner}\t{L:.4f}\t{t:.4f}\t{n}\t?\t\t\t\t")
            else:
                f, s, d, lo, dur = best
                print(f"{nm}\t{owner}\t{L:.4f}\t{t:.4f}\t{n}\t{f}\t{s:.4f}\t{d:+.4f}\t{lo:.4f}\t{dur:.4f}")


if __name__ == "__main__":
    main()
