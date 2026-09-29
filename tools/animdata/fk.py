#!/usr/bin/env python3
"""Forward-kinematics measurement of a locomotion cycle: contacts, stride, bob, arm swing.

Composes the rest skeleton (appearance model + supermodel chain) with an animation's absolute
local keys and samples the whole hierarchy. Everything below is MEASURED off the composed
ankle/hip world positions; nothing is read out of a 2DA or the engine.

    ./fk.py --stock pmh0 walk run pause1
    ./fk.py pmh0 walk --dump            # per-sample CSV of the cycle
"""
import math, sys
import chain, mdlparse

N = 720          # samples per cycle
FEET = ("lfoot_g", "rfoot_g")


def build(root, stock):
    ch = chain.walk(root, stock)
    skel = mdlparse.merge_skeleton([m for _, _, m in ch if m is not None])
    return ch, skel, chain.resolve_map(ch)


def samples(skel, a, n=N):
    """[(t, {node: (pos, quat)})] over [0, length), one cycle."""
    L = a.length
    return [(i * L / n, mdlparse.pose(skel, a, i * L / n)) for i in range(n)]


def series(sm, node, comp):
    idx = {"x": 0, "y": 1, "z": 2}[comp]
    return [p[node][0][idx] for _, p in sm]


def deriv(vals, dt, wrap=True):
    n = len(vals)
    out = []
    for i in range(n):
        a = vals[(i - 1) % n] if wrap else vals[max(0, i - 1)]
        b = vals[(i + 1) % n] if wrap else vals[min(n - 1, i + 1)]
        out.append((b - a) / (2 * dt))
    return out


def contact_windows(z, thresh):
    """Contiguous (possibly wrapping) index runs where z <= thresh. Returns [(start, end_exclusive)]."""
    n = len(z)
    below = [v <= thresh for v in z]
    if all(below):
        return [(0, n)]
    if not any(below):
        return []
    start = next(i for i in range(n) if below[i] and not below[i - 1])
    runs, i = [], start
    for _ in range(n):
        if below[i] and not below[i - 1]:
            j = i
            ln = 0
            while below[j % n] and ln < n:
                j += 1
                ln += 1
            runs.append((i, j))
        i = (i + 1) % n
        if i == start:
            break
    seen, out = set(), []
    for s, e in runs:
        if s not in seen:
            seen.add(s)
            out.append((s, e))
    return out


def median(v):
    s = sorted(v)
    return s[len(s) // 2] if s else float("nan")


def analyse(skel, a, name, verbose=True, zband=None):
    """zband defaults to 10 % of each ankle's own vertical range, so a scaled race model
    (halfling, gnome, dwarf) gets the same window a human does."""
    L = a.length
    sm = samples(skel, a)
    dt = L / N
    res = {"name": name, "length": L, "transtime": a.transtime}

    ank = {f: {c: series(sm, f, c) for c in "xyz"} for f in FEET}
    vel = {f: {c: deriv(ank[f][c], dt) for c in "xyz"} for f in FEET}
    root = {c: series(sm, "rootdummy", c) for c in "xyz"}
    pelv = {c: series(sm, "pelvis_g", c) for c in "xyz"}

    out = []
    plants = {}
    for f in FEET:
        z = ank[f]["z"]
        zmin = min(z)
        band = zband if zband is not None else 0.10 * (max(z) - zmin)
        wins = contact_windows(z, zmin + band)
        # keep the longest run
        wins.sort(key=lambda w: (w[1] - w[0]), reverse=True)
        if not wins:
            plants[f] = None
            continue
        s, e = wins[0]
        idxs = [(s + k) % N for k in range(e - s)]
        vy = [vel[f]["y"][i] for i in idxs]
        vx = [vel[f]["x"][i] for i in idxs]
        # trim the first/last 15 % of the window: heel-strike and toe-off are eased
        k0, k1 = int(0.15 * len(idxs)), int(0.85 * len(idxs)) or len(idxs)
        core = idxs[k0:k1] or idxs
        mvy = median([vel[f]["y"][i] for i in core])
        mvx = median([vel[f]["x"][i] for i in core])
        # travel across the whole window (unwrapped)
        dxy = (ank[f]["x"][idxs[-1]] - ank[f]["x"][idxs[0]], ank[f]["y"][idxs[-1]] - ank[f]["y"][idxs[0]])
        plants[f] = dict(start=s * L / N, end=(e % N) * L / N, frac=(e - s) / N,
                         speed_y=-mvy, speed_x=-mvx, travel=math.hypot(*dxy),
                         zmin=zmin, zmax=max(z), idxs=idxs)
        out.append((f, plants[f]))
    res["plants"] = plants

    # cycle ground speed: median over the cycle of the faster-backward foot
    both = [max(-vel["lfoot_g"]["y"][i], -vel["rfoot_g"]["y"][i]) for i in range(N)]
    res["speed_envelope"] = median(both)
    res["speed_stance"] = median([p["speed_y"] for p in plants.values() if p])
    res["speed_profile"] = both
    # Lateral / diagonal cycles (sidesteps, strafes) need the full horizontal vector, not just -vy:
    # the planted foot's horizontal speed, and the direction the BODY travels (opposite the foot).
    # Direction is degrees from +Y (model forward), positive toward +X (the character's right).
    hsp = []
    for i in range(N):
        cand = [(math.hypot(vel[f]["x"][i], vel[f]["y"][i]), -vel[f]["x"][i], -vel[f]["y"][i])
                for f in FEET]
        hsp.append(max(cand))
    res["speed2d_env"] = median([h[0] for h in hsp])
    for f in FEET:
        p = plants[f]
        if p:
            core = p["idxs"][int(0.15 * len(p["idxs"])):int(0.85 * len(p["idxs"])) or None]
            sx = median([vel[f]["x"][i] for i in core]) if core else 0.0
            sy = median([vel[f]["y"][i] for i in core]) if core else 0.0
            p["speed2d"] = math.hypot(sx, sy)
            p["dir_deg"] = math.degrees(math.atan2(-sx, -sy))
    sp = [p["speed2d"] for p in plants.values() if p and "speed2d" in p]
    res["speed2d"] = sum(sp) / len(sp) if sp else float("nan")
    dd = [p["dir_deg"] for p in plants.values() if p and "dir_deg" in p]
    res["travel_dir_deg"] = (math.degrees(math.atan2(
        sum(math.sin(math.radians(d)) for d in dd), sum(math.cos(math.radians(d)) for d in dd)))
        if dd else float("nan"))

    # duty factor / double support, from a "moving with the ground" test rather than a z band:
    # a foot is bearing weight when it travels backwards at >= 70 % of the cycle speed.
    V = res["speed_stance"]
    if V and V == V and abs(V) > 1e-6:
        thr = float(res.get("thr", 0.5))
        onL = [-vel["lfoot_g"]["y"][i] >= thr * V for i in range(N)]
        onR = [-vel["rfoot_g"]["y"][i] >= thr * V for i in range(N)]
        res["onL"], res["onR"] = onL, onR
        res["duty"] = (sum(onL) / N, sum(onR) / N)
        res["double_support"] = sum(1 for i in range(N) if onL[i] and onR[i]) / N
        res["flight"] = sum(1 for i in range(N) if not onL[i] and not onR[i]) / N
        # speed ripple over the cycle: how constant the built-in ground speed is
        env = [both[i] for i in range(N) if onL[i] or onR[i]]
        res["speed_ripple"] = (min(env), max(env)) if env else (float("nan"),) * 2

    # which foot is lower at t=0
    res["foot_down_t0"] = min(FEET, key=lambda f: ank[f]["z"][0])
    res["z_t0"] = {f: ank[f]["z"][0] for f in FEET}

    res["root_z"] = (min(root["z"]), max(root["z"]))
    res["root_bob"] = max(root["z"]) - min(root["z"])
    res["root_bob_phase"] = [i * L / N for i in range(N) if root["z"][i] == min(root["z"])][:1] + \
                            [i * L / N for i in range(N) if root["z"][i] == max(root["z"])][:1]
    res["root_xy"] = (max(root["x"]) - min(root["x"]), max(root["y"]) - min(root["y"]))
    res["pelvis_z"] = (min(pelv["z"]), max(pelv["z"]))

    # bob peaks/troughs, in order
    res["bob_minima"] = local_extrema(root["z"], L, lo=True)
    res["bob_maxima"] = local_extrema(root["z"], L, lo=False)

    # arm swing: bicep world-space forward offset relative to the shoulder root
    for side, bic, hnd in (("L", "lbicep_g", "lhand_g"), ("R", "rbicep_g", "rhand_g")):
        if hnd in sm[0][1]:
            hy = [p[hnd][0][1] - p["torso_g"][0][1] for _, p in sm]
            res[f"hand_y_{side}"] = (min(hy), max(hy), max(hy) - min(hy))
            res[f"hand_y_{side}_maxphase"] = hy.index(max(hy)) * L / N
    # arm swing as a joint angle: bicep local +Z (down the bone) tilt toward +Y
    for side, bic in (("L", "lbicep_g"), ("R", "rbicep_g")):
        n = a.nodes.get(bic)
        if n and n.orikeys:
            ang = []
            for t, _ in sm:
                q = mdlparse.sample_ori(n.orikeys, t)
                d = mdlparse.qrot(q, (0, 0, -1))         # down the bone
                ang.append(math.degrees(math.atan2(d[1], -d[2])))
            res[f"bicep_swing_{side}"] = (min(ang), max(ang), max(ang) - min(ang))

    # torso / pelvis twist about +Z
    for bone in ("pelvis_g", "torso_g"):
        n = a.nodes.get(bone)
        if n and n.orikeys:
            tw, tl = [], []
            for t, _ in sm:
                q = mdlparse.sample_ori(n.orikeys, t)
                fx = mdlparse.qrot(q, (1, 0, 0))
                up = mdlparse.qrot(q, (0, 0, 1))
                tw.append(math.degrees(math.atan2(fx[1], fx[0])))
                tl.append(math.degrees(math.atan2(up[1], up[2])))
            res[f"{bone}_twist"] = (min(tw), max(tw))
            res[f"{bone}_tilt"] = (min(tl), max(tl))

    # stance width / foot placement (for idles)
    res["stance_width"] = median([abs(ank["lfoot_g"]["x"][i] - ank["rfoot_g"]["x"][i]) for i in range(N)])
    res["foot_y_offset"] = median([ank["lfoot_g"]["y"][i] - ank["rfoot_g"]["y"][i] for i in range(N)])
    res["ank_swing_peak"] = {f: max(ank[f]["z"]) for f in FEET}
    res["sm"] = sm
    res["ank"] = ank
    res["root"] = root
    return res


def local_extrema(v, L, lo=True):
    n = len(v)
    out = []
    for i in range(n):
        a, b, c = v[(i - 1) % n], v[i], v[(i + 1) % n]
        if (b < a and b <= c) if lo else (b > a and b >= c):
            out.append((i * L / n, b))
    # merge neighbours
    merged = []
    for t, y in out:
        if merged and abs(t - merged[-1][0]) < L * 0.05:
            continue
        merged.append((t, y))
    return merged


def report(res):
    L = res["length"]
    print(f"--- {res['name']}  length {L:.4f}s  transtime {res['transtime']:.3f}s")
    for f, p in res["plants"].items():
        if not p:
            print(f"    {f}: no contact window")
            continue
        print(f"    {f}: stance {p['start']:.3f}-{p['end']:.3f}s "
              f"({100*p['frac']:.0f}% of cycle, {100*p['start']/L:.0f}%-{100*p['end']/L:.0f}%) "
              f"back-speed y {p['speed_y']:+.3f} x {p['speed_x']:+.3f} m/s  travel {p['travel']:.3f} m "
              f"ankle z {p['zmin']:.3f}..{p['zmax']:.3f}")
    print(f"    cycle speed: stance-median {res['speed_stance']:+.3f} m/s, envelope-median "
          f"{res['speed_envelope']:+.3f} m/s   -> stride/cycle {res['speed_stance']*L:.3f} m")
    if "duty" in res:
        print(f"    weight-bearing (>=50% of cycle speed backwards): L {100*res['duty'][0]:.0f}% "
              f"R {100*res['duty'][1]:.0f}%, double support {100*res['double_support']:.0f}%, "
              f"neither (flight) {100*res['flight']:.0f}%; speed over the bearing part "
              f"{res['speed_ripple'][0]:.2f}..{res['speed_ripple'][1]:.2f} m/s")
    print(f"    foot lower at t=0: {res['foot_down_t0']}  (L z={res['z_t0']['lfoot_g']:.3f}, "
          f"R z={res['z_t0']['rfoot_g']:.3f})")
    print(f"    root z {res['root_z'][0]:.4f}..{res['root_z'][1]:.4f} (bob {res['root_bob']:.4f} m), "
          f"root xy range {res['root_xy'][0]:.4f}/{res['root_xy'][1]:.4f}")
    print(f"    bob minima {['%.3f' % t for t,_ in res['bob_minima']]}  maxima "
          f"{['%.3f' % t for t,_ in res['bob_maxima']]}")
    for k in ("hand_y_L", "hand_y_R"):
        if k in res:
            lo, hi, amp = res[k]
            print(f"    {k}: {lo:+.3f}..{hi:+.3f} (amp {amp:.3f} m), peak-forward at "
                  f"{res[k+'_maxphase']:.3f}s")
    for k in ("bicep_swing_L", "bicep_swing_R"):
        if k in res:
            lo, hi, amp = res[k]
            print(f"    {k}: {lo:+.1f}..{hi:+.1f} deg (amp {amp:.1f})")
    for k in ("pelvis_g_twist", "torso_g_twist", "torso_g_tilt"):
        if k in res:
            print(f"    {k}: {res[k][0]:+.1f}..{res[k][1]:+.1f} deg")
    print(f"    stance width {res['stance_width']:.3f} m, L-R y offset {res['foot_y_offset']:+.3f} m, "
          f"swing peak z L {res['ank_swing_peak']['lfoot_g']:.3f} R {res['ank_swing_peak']['rfoot_g']:.3f}")
    print(f"    events: " + ", ".join(f"{t:.3f} {n}" for t, n in sorted(set(
        (round(t, 4), n.lower()) for t, n in res.get("events", [])))))


if __name__ == "__main__":
    stock = "--stock" in sys.argv
    dump = "--dump" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    root, names = args[0], args[1:]
    ch, skel, rm = build(root, stock)
    print(f"# {root} ({'stock' if stock else 'live'}) chain: " + " -> ".join(r for r, _, _ in ch))
    for nm in names:
        if nm not in rm:
            print(f"!! {nm} not in chain")
            continue
        owner, a = rm[nm]
        r = analyse(skel, a, f"{nm} @{owner}")
        r["events"] = a.events
        report(r)
        if dump:
            with open(f"../cycle_{root}_{nm}{'_stock' if stock else ''}.csv", "w") as fh:
                fh.write("t,lx,ly,lz,rx,ry,rz,rootx,rooty,rootz\n")
                for i, (t, p) in enumerate(r["sm"]):
                    fh.write(",".join(f"{x:.5f}" for x in [
                        t, r["ank"]["lfoot_g"]["x"][i], r["ank"]["lfoot_g"]["y"][i], r["ank"]["lfoot_g"]["z"][i],
                        r["ank"]["rfoot_g"]["x"][i], r["ank"]["rfoot_g"]["y"][i], r["ank"]["rfoot_g"]["z"][i],
                        r["root"]["x"][i], r["root"]["y"][i], r["root"]["z"][i]]) + "\n")
