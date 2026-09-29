"""Does Momentum add anything the engine has not already done?

ComputeSpeedFactor (decoded off nwmain-linux, see engine_model.py) ramps the factor by dtMs/1000 per
AI update -- +1.0 per SECOND -- and eases the last `rate/2000` metres down as sqrt(remaining/stopDist).
WalkUpdateLocation multiplies the same factor into the ANIMATION rate (objdump 0x725fc4), so the walk
cycle itself plays slow through the windup and the winddown.
"""
import math
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from engine_model import Engine                                        # noqa: E402
from pose_model import Slot, motion, momentum_advance                  # noqa: E402

FPS = 143.0


def profile(rate_mm, length, momentum):
    e = Engine(rate_mm=rate_mm, length=length, fps=FPS, jitter_ms=0.0)
    s = Slot()
    last = 0.0
    prev = 0.0
    rows = []
    for _ in range(30):
        e.step(moving=False)
    t0 = e.t
    while e.pos < length - 1e-9 or e.t - t0 < 0.1:
        dt, pos, f, dtms = e.step(moving=e.pos < length - 1e-9)
        moved = pos - last
        last = pos
        drift = momentum_advance(s, pos, moved / dt if dt > 0 else 0.0, dt) if momentum else 0.0
        drawn = pos + drift
        rows.append((e.t - t0, dt, pos, drawn, f))
        prev = drift
        if e.t - t0 > 30:
            break
    # drawn speed
    out = []
    for i in range(1, len(rows)):
        out.append((rows[i][0], (rows[i][3] - rows[i - 1][3]) / rows[i][1], rows[i][4],
                    (rows[i][2] - rows[i - 1][2]) / rows[i][1]))
    return out


def reach(out, frac, v):
    for t, ds, f, rs in out:
        if ds >= frac * v:
            return t
    return float("nan")


def tail(out, v, frac):
    last = out[-1][0]
    for t, ds, f, rs in reversed(out):
        if ds >= frac * v:
            return last - t
    return float("nan")


if __name__ == "__main__":
    for rate, lab in ((2000.0, "walk 2.0 m/s"), (4000.0, "run 4.0 m/s")):
        v = rate / 1000.0
        print(f"=== {lab}, 25 m path ===")
        for mom, name in ((False, "engine alone"), (True, "engine + Momentum")):
            o = profile(rate, 25.0, mom)
            print(f"  {name:20s} 50% speed at {reach(o, 0.5, v)*1000:6.0f} ms, "
                  f"90% at {reach(o, 0.9, v)*1000:6.0f} ms, "
                  f"last frame above 50% is {tail(o, v, 0.5)*1000:6.0f} ms before the stop, "
                  f"above 10% {tail(o, v, 0.1)*1000:6.0f} ms before")
        o = profile(rate, 25.0, False)
        fmin = min(f for _, _, f, _ in o[:int(1.2 * FPS)])
        print(f"  engine speed factor goes {fmin:.2f} -> 1.00 over "
              f"{[t for t, ds, f, rs in o if f >= 0.999][0]*1000:.0f} ms, and the ANIMATION rate is "
              f"multiplied by it (objdump 0x725fc4), so the cycle plays that slowly too")
        dec = [(t, f) for t, ds, f, rs in o if f < 0.999 and t > 2.0]
        if dec:
            print(f"  the stop: factor falls below 1.0 {o[-1][0]-dec[0][0]:.2f} s before the end, "
                  f"down to {min(f for _, f in dec):.2f} (sqrt(remaining/{rate/2000:.1f} m))")
        print()
