#!/usr/bin/env python3
"""Per-phase FK dump of the leg chain (model space): hip/knee/ankle positions + joint angles.

    ./legchain.py --stock pmh0 walk run > ../legchain_pmh0.txt
"""
import math, sys
import fk, mdlparse

BONES = ["rootdummy", "pelvis_g", "lthigh_g", "lshin_g", "lfoot_g",
         "rthigh_g", "rshin_g", "rfoot_g", "torso_g"]
BINS = 20


def pitch(q):
    """Bone's -Z (down the bone) leaning toward +Y, in degrees: the sagittal joint angle."""
    d = mdlparse.qrot(q, (0, 0, -1))
    return math.degrees(math.atan2(d[1], -d[2]))


def main():
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = fk.build(root, stock)
    for nm in names:
        owner, a = rm[nm]
        L = a.length
        print(f"=== {nm} @{owner} on {root}  length {L:.4f}s   (model space, metres; "
              f"pitch = bone's -Z toward +Y, degrees)")
        hdr = ["t", "phase"]
        for b in BONES:
            hdr += [f"{b}.x", f"{b}.y", f"{b}.z", f"{b}.pitch"]
        print("\t".join(hdr))
        for i in range(BINS):
            t = i * L / BINS
            p = mdlparse.pose(skel, a, t)
            row = [f"{t:.3f}", f"{i/BINS:.2f}"]
            for b in BONES:
                pos, q = p[b]
                row += [f"{pos[0]:+.4f}", f"{pos[1]:+.4f}", f"{pos[2]:.4f}", f"{pitch(q):+.1f}"]
            print("\t".join(row))
        print()


if __name__ == "__main__":
    main()
