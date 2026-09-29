"""Momentum OFF: how much of what Mass and Follow read is real, and how much is the integer-ms clock.

Splits the measured acceleration into
  * the RAMP window   -- the engine's own speed-factor ramp (genuine acceleration), and
  * the CRUISE window -- the factor pinned at 1.0, so every m/s^2 read there is measurement noise.
and turns each into what the two modules actually draw (pelvis dip in mm, arm lag in degrees).
"""
import math
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from engine_model import Engine                                                    # noqa: E402
from pose_model import (Slot, motion, mass_advance, mass_load, follow_trail,       # noqa: E402
                        stats, MIN_LIFT)

FPS = 143.0


def run(rate_mm, length=25.0, momentum=False, jitter_ms=0.25, fps=FPS, seed=3):
    from pose_model import momentum_advance
    e = Engine(rate_mm=rate_mm, length=length, fps=fps, jitter_ms=jitter_ms, seed=seed)
    s = Slot()
    last = 0.0
    prev = 0.0
    arm = armv = 0.0
    rows = []
    for _ in range(30):
        e.step(moving=False)
    for _ in range(20000):
        moving = e.pos < length - 1e-9
        dt, pos, f, dtms = e.step(moving=moving)
        moved = pos - last
        last = pos
        drift = momentum_advance(s, pos, moved / dt if dt > 0 else 0.0, dt) if momentum else 0.0
        motion(s, moved + (drift - prev), dt)
        prev = drift
        dip = mass_advance(s, 0.0, dt)
        arm, armv = follow_trail(arm, armv, s.accel, 0.4, dt)
        rows.append((e.t, dt, f, s.speed, s.accel, dip, arm, dtms, pos))
        if not moving and len(rows) > 30:
            break
    return rows


def window(rows, lo, hi):
    t0 = rows[0][0]
    return [r for r in rows if lo <= r[0] - t0 <= hi]


def describe(rows, name, lo, hi):
    w = window(rows, lo, hi)
    if not w:
        print(f"  {name}: empty window")
        return
    a = [r[4] for r in w]
    am, asd, amx = stats(a)
    dips = [r[5] for r in w]
    _, _, dmx = stats(dips)
    loaded = sum(1 for d in dips if d < -MIN_LIFT) / len(dips)
    arms = [r[6] for r in w]
    _, _, armx = stats(arms)
    fs = [r[2] for r in w]
    print(f"  {name:28s} factor {min(fs):4.2f}-{max(fs):4.2f}   accel mean {am:6.2f} sd {asd:5.2f} "
          f"peak {amx:6.2f} m/s^2   dip peak {dmx*1000:5.1f} mm   arm lag peak {math.degrees(armx):4.2f} deg   "
          f"pelvis loaded {loaded*100:5.1f}% of frames")


if __name__ == "__main__":
    print("Momentum OFF -- the accel Mass and Follow are handed, split by phase\n")
    for rate, lab in ((2000.0, "walk 2.0 m/s"), (4000.0, "run 4.0 m/s")):
        rows = run(rate)
        t0 = rows[0][0]
        end = rows[-1][0] - t0
        print(f"=== {lab}, 25 m path, 143 fps ===")
        describe(rows, "RAMP (first 1.0 s)", 0.0, 1.0)
        describe(rows, "CRUISE (2 s .. stop-2 s)", 2.0, end - 3.0)
        describe(rows, "STOP (last 1.5 s of travel)", end - 2.5, end - 1.0)
        print()

    print("=== the cruise noise is the integer-millisecond clock: vary the frame rate ===")
    for fps in (30.0, 60.0, 90.0, 143.0, 240.0):
        rows = run(4000.0, fps=fps)
        end = rows[-1][0] - rows[0][0]
        w = window(rows, 2.0, end - 3.0)
        a = [r[4] for r in w]
        _, asd, amx = stats(a)
        dips = [r[5] for r in w]
        _, _, dmx = stats(dips)
        loaded = sum(1 for d in dips if d < -MIN_LIFT) / len(dips)
        ms = [r[7] for r in w]
        print(f"  {fps:5.0f} fps (frame {1000/fps:5.2f} ms, world step {min(ms)}-{max(ms)} ms): "
              f"cruise accel sd {asd:5.2f} peak {amx:5.2f} m/s^2 -> dip peak {dmx*1000:4.1f} mm, "
              f"loaded {loaded*100:5.1f}%")

    print("\n=== what a noise-free signal would give: accel from the ENGINE's own speed factor ===")
    for rate, lab in ((2000.0, "walk"), (4000.0, "run")):
        rows = run(rate)
        # d/dt of factor * rate/1000 -- the engine's own intended speed, no position differencing
        sp = [r[2] * rate / 1000.0 for r in rows]
        dts = [r[1] for r in rows]
        acc = [(sp[i] - sp[i - 1]) / dts[i] for i in range(1, len(sp))]
        end = rows[-1][0] - rows[0][0]
        idx = [i for i, r in enumerate(rows[1:]) if 2.0 <= r[0] - rows[0][0] <= end - 3.0]
        cruise = [acc[i] for i in idx]
        _, csd, cmx = stats(cruise)
        _, _, rmx = stats(acc)
        print(f"  {lab}: cruise accel sd {csd:.3f} peak {cmx:.3f} m/s^2 (measured-from-position: see above); "
              f"whole-run peak {rmx:.2f} m/s^2 = the real ramp")

    print("\n=== Mass.Load: the dip a given acceleration draws ===")
    for a in (0.5, 1.0, 2.0, 4.0, 8.6, 20.0):
        print(f"  |accel| {a:5.1f} m/s^2 -> {mass_load(a)*1000:5.1f} mm of pelvis dip "
              f"({'CAPPED at MaxDip' if mass_load(a) >= 0.06 - 1e-9 else ''})")
