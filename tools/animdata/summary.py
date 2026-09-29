#!/usr/bin/env python3
"""One TSV row per animation with the numbers a procedural layer cares about.

    ./summary.py --stock pmh0 walk run ... > table.tsv
    ./summary.py pmh0 walk_fem run_fem          # live chain, SoW animation-set cycles
"""
import sys
import fk

COLS = ["anim", "owner", "len_s", "trans_s", "events", "L_td", "R_td", "L_stance_s", "R_stance_s",
        "duty_L", "duty_R", "dbl_sup", "flight", "speed_mps", "speed2d", "dir_deg", "stride_m", "speed_min", "speed_max",
        "root_z_lo", "root_z_hi", "bob_m", "bob_min_t", "bob_max_t",
        "stance_width_m", "foot_dy_m", "swing_z", "hand_amp_L", "hand_amp_R",
        "pelvis_twist", "torso_tilt", "t0_low_foot"]


def windows_start(on, L):
    n = len(on)
    return [i * L / n for i in range(n) if on[i] and not on[i - 1]]


def row(skel, nm, owner, a):
    r = fk.analyse(skel, a, nm)
    L = a.length
    ev = sorted(set((round(t, 3), n.lower()) for t, n in a.events))
    tdL = windows_start(r.get("onL", []), L) if "onL" in r else []
    tdR = windows_start(r.get("onR", []), L) if "onR" in r else []
    pl, pr = r["plants"]["lfoot_g"], r["plants"]["rfoot_g"]
    f = lambda x, n=3: ("" if x is None else f"{x:.{n}f}")
    return [nm, owner, f(L, 4), f(a.transtime, 3),
            ",".join(f"{t:.3f}" for t, _ in ev),
            ";".join(f"{t:.3f}" for t in tdL), ";".join(f"{t:.3f}" for t in tdR),
            f(pl["frac"] * L) if pl else "", f(pr["frac"] * L) if pr else "",
            f(r["duty"][0], 2) if "duty" in r else "", f(r["duty"][1], 2) if "duty" in r else "",
            f(r.get("double_support"), 2), f(r.get("flight"), 2),
            f(r["speed_stance"]), f(r.get("speed2d")), f(r.get("travel_dir_deg"), 1), f(r.get("speed2d", 0) * L),
            f(r["speed_ripple"][0]) if "speed_ripple" in r else "",
            f(r["speed_ripple"][1]) if "speed_ripple" in r else "",
            f(r["root_z"][0], 4), f(r["root_z"][1], 4), f(r["root_bob"], 4),
            ";".join(f"{t:.3f}" for t, _ in r["bob_minima"]),
            ";".join(f"{t:.3f}" for t, _ in r["bob_maxima"]),
            f(r["stance_width"]), f(r["foot_y_offset"]),
            f(max(r["ank_swing_peak"].values())),
            f(r.get("hand_y_L", (0, 0, 0))[2]), f(r.get("hand_y_R", (0, 0, 0))[2]),
            f(r.get("pelvis_g_twist", (0, 0))[1] - r.get("pelvis_g_twist", (0, 0))[0], 1),
            f(r.get("torso_g_tilt", (0, 0))[1], 1),
            "L" if r["foot_down_t0"] == "lfoot_g" else "R"]


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = fk.build(root, stock)
    print("\t".join(COLS))
    for nm in names:
        if nm not in rm:
            sys.stderr.write(f"!! {nm} missing from {root}\n")
            continue
        owner, a = rm[nm]
        try:
            print("\t".join(row(skel, nm, owner, a)))
        except Exception as e:
            sys.stderr.write(f"!! {nm}: {type(e).__name__}: {e}\n")
