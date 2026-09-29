"""Why Momentum looks snappy: rank the mechanisms by the screen motion each one adds.

The camera is welded to the ENGINE position (CameraKnobs.LagPos defaults to 0 -- "0 is welded, as in
the stock game"), so the drift IS the body's on-screen offset. Everything is reported both in metres
and in screen pixels at the user's measured setup: 2544x1378, 45 deg vertical FOV, camera ~10 m out
=> 689 / (10 * tan(22.5 deg)) = 166 px per metre.
"""
import math
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from engine_model import track                       # noqa: E402
from pose_model import (Slot, motion, momentum_advance, mass_advance, stats,  # noqa: E402
                        stride_factor, MIN_LIFT)

PX_PER_M = 689.0 / (10.0 * math.tan(math.radians(22.5)))
FPS = 143.0


def run(rate_mm, length, *, fixed_time=False, clamp_resets_v=True, smoothed_vel=False,
        feedback=True, jitter_ms=0.25, correction_hz=0.0, correction_m=0.0, seed=1, momentum=True):
    tr = track(int(length / (rate_mm / 1000.0) / (1 / FPS)) + 200, rate_mm=rate_mm, length=length,
               jitter_ms=jitter_ms, seed=seed, correction_hz=correction_hz, correction_m=correction_m)
    s = Slot()
    last = tr[0][1]
    drifts, drawn_pos, real_pos, dts = [], [], [], []
    accels, dips, factors = [], [], []
    prev_drift = 0.0
    for dt, pos, f, dt_ms in tr:
        moved = pos - last
        last = pos
        vel = (s.vel if smoothed_vel else moved / dt) if dt > 0 else 0.0
        drift = momentum_advance(s, pos, vel, dt, fixed_time=fixed_time,
                                 clamp_resets_v=clamp_resets_v) if momentum else 0.0
        fed = moved + (drift - prev_drift) if (momentum and feedback) else moved
        prev_drift = drift
        motion(s, fed, dt)
        mass_advance(s, 0.0, dt)
        drifts.append(drift)
        drawn_pos.append(pos + drift)
        real_pos.append(pos)
        dts.append(dt)
        accels.append(s.accel)
        dips.append(s.dip)
        factors.append(s.factor)
    return dict(drift=drifts, drawn=drawn_pos, real=real_pos, dt=dts, accel=accels,
                dip=dips, stride=factors)


def jerk_px(r):
    """Per-frame change of the body's on-screen offset, in pixels (the visible shimmer)."""
    d = r["drift"]
    return [abs(d[i] - d[i - 1]) * PX_PER_M for i in range(1, len(d))]


def drawn_accel(r):
    """Second difference of the drawn position: the drawn body's own acceleration, m/s^2."""
    p, dt = r["drawn"], r["dt"]
    v = [(p[i] - p[i - 1]) / dt[i] for i in range(1, len(p))]
    return [(v[i] - v[i - 1]) / dt[i + 1] for i in range(1, len(v))]


def real_accel(r):
    p, dt = r["real"], r["dt"]
    v = [(p[i] - p[i - 1]) / dt[i] for i in range(1, len(p))]
    return [(v[i] - v[i - 1]) / dt[i + 1] for i in range(1, len(v))]


def line(name, r):
    j = jerk_px(r)
    m, sd, mx = stats(j)
    da = drawn_accel(r)
    _, dsd, dmx = stats(da)
    _, rsd, rmx = stats(real_accel(r))
    loaded = sum(1 for d in r["dip"] if d < -MIN_LIFT) / len(r["dip"])
    print(f"{name:44s} shimmer mean {m:6.2f} px/f  p-max {mx:7.2f} px/f   "
          f"drawn accel sd {dsd:8.1f}  (real {rsd:7.1f})  loaded {loaded*100:5.1f}%")


if __name__ == "__main__":
    print(f"px per metre = {PX_PER_M:.0f}   frame = {1000/FPS:.2f} ms\n")

    for rate, label in ((2000.0, "walk 2.0 m/s"), (4000.0, "run 4.0 m/s"), (6000.0, "hasted 6.0 m/s")):
        print(f"=== {label}, 12 m path, vsync 143 fps, +-0.25 ms frame jitter ===")
        base = run(rate, 12.0)
        line("A  Momentum as shipped", base)
        line("B  + fixed LagTime (no speed-driven time)", run(rate, 12.0, fixed_time=True))
        line("C  + cap clamp keeps DrawnV", run(rate, 12.0, clamp_resets_v=False))
        line("D  + chase fed the SMOOTHED velocity", run(rate, 12.0, smoothed_vel=True))
        line("E  + no drift fed back into Motion", run(rate, 12.0, feedback=False))
        line("F  B+C+D together", run(rate, 12.0, fixed_time=True, clamp_resets_v=False,
                                      smoothed_vel=True))
        line("Z  Momentum OFF", run(rate, 12.0, momentum=False))
        print()

    print("=== the frame clock: how much of it is the integer-ms world step? ===")
    for j in (0.0, 0.25, 1.0):
        r = run(4000.0, 12.0, jitter_ms=j)
        m, sd, mx = stats(jerk_px(r))
        print(f"  frame jitter +-{j:4.2f} ms -> shimmer mean {m:5.2f} px/f, max {mx:6.2f} px/f")

    print("\n=== server corrections (MoveCreatureToPosition) on top ===")
    for c in (0.0, 0.02, 0.05):
        r = run(4000.0, 12.0, correction_hz=5.0, correction_m=c)
        m, sd, mx = stats(jerk_px(r))
        print(f"  5 Hz correction sd {c*100:4.1f} cm -> shimmer mean {m:5.2f} px/f, max {mx:6.2f} px/f")

    print("\n=== the reset pop: drift thrown away when a gob goes >100 ms without an Apply ===")
    for rate in (2000.0, 4000.0, 6000.0):
        r = run(rate, 12.0)
        steady = sorted(abs(d) for d in r["drift"])[int(0.9 * len(r["drift"])):]
        print(f"  {rate/1000:.0f} m/s: steady drift {sum(steady)/len(steady)*100:5.1f} cm "
              f"= {sum(steady)/len(steady)*PX_PER_M:5.1f} px thrown away in ONE frame on a reset")

    print("\n=== the chase time constant Momentum actually uses, per speed ===")
    for v in (0.5, 1.0, 2.0, 2.7, 4.0, 6.0):
        t = min(0.1, 0.9 * 0.3 / max(v, 1e-3))
        print(f"  |v| {v:4.1f} m/s -> LagTime {t*1000:5.1f} ms   "
              f"(a +-14% speed wobble moves it by {abs(min(0.1,0.9*0.3/max(v*1.14,1e-3))-t)*1000:4.1f} ms)")
