"""The transients: what the drift does at a start, a stop, a corner, and under a server-paced path.

Everything below is 1-D or 2-D over the SAME engine model (engine_model.py) whose speed-factor ramp
was decoded off the binary. The pose layer's numbers are transcribed in pose_model.py.
"""
import math
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from engine_model import Engine                                  # noqa: E402
from pose_model import Slot, motion, momentum_advance, stats     # noqa: E402

PX_PER_M = 689.0 / (10.0 * math.tan(math.radians(22.5)))
FPS = 143.0
MAX_SLIP = 0.2          # Stride.MaxSlip, world units * scale
STANCE = 0.45           # seconds a foot is planted in a walk cycle (walk_fem length 1.0 s, 2 plants)


def onedim(rate_mm, length, frames_after=300, **kw):
    e = Engine(rate_mm=rate_mm, length=length, fps=FPS, **kw)
    s = Slot()
    last = 0.0
    out = []
    for i in range(30):
        e.step(moving=False)
    for i in range(20000):
        moving = e.pos < length - 1e-9
        dt, pos, f, dtms = e.step(moving=moving)
        moved = pos - last
        last = pos
        drift = momentum_advance(s, pos, moved / dt if dt > 0 else 0.0, dt)
        motion(s, moved + (drift - (out[-1][3] if out else 0.0)), dt)
        out.append((e.t, dt, pos, drift, f, s.speed, s.accel))
        if not moving:
            frames_after -= 1
            if frames_after <= 0:
                break
    return out


def report_start_stop(rate_mm, label):
    o = onedim(rate_mm, 12.0)
    t0 = o[0][0]
    drifts = [r[3] for r in o]
    ts = [r[0] - t0 for r in o]
    dts = [r[1] for r in o]
    rates = [abs(drifts[i] - drifts[i - 1]) / dts[i] for i in range(1, len(drifts))]
    peak_rate = max(rates)
    # biggest change of drift inside any STANCE-second window: what drags a planted foot
    win = []
    for i in range(len(drifts)):
        j = i
        while j + 1 < len(drifts) and ts[j + 1] - ts[i] < STANCE:
            j += 1
        win.append(max(drifts[i:j + 1]) - min(drifts[i:j + 1]))
    worst = max(win)
    steady = sorted(abs(d) for d in drifts)[-len(drifts) // 5:]
    print(f"  {label:16s} steady drift {sum(steady)/len(steady)*100:5.1f} cm "
          f"({sum(steady)/len(steady)*PX_PER_M:5.1f} px)   peak |d drift/dt| {peak_rate:5.2f} m/s "
          f"({peak_rate/FPS*PX_PER_M:5.2f} px/frame)   worst drift swing inside one {STANCE:.2f}s stance "
          f"{worst*100:5.1f} cm  -> foot lock ({MAX_SLIP*100:.0f} cm) "
          f"{'BREAKS' if worst > MAX_SLIP else 'holds'}")


def server_paced(rate_mm, hop_m, hz, label):
    """The client is handed a SHORT path every 1/hz s (keyboard/drive movement): `remaining` never
    exceeds hop_m, so ComputeSpeedFactor runs on its sqrt(remaining/stopDist) branch the whole time."""
    e = Engine(rate_mm=rate_mm, length=1e9, fps=FPS)
    s = Slot()
    last = 0.0
    goal = hop_m
    t_next = 1.0 / hz
    drifts, speeds, ts = [], [], []
    prev = 0.0
    for i in range(int(6.0 * FPS)):
        dt = e.frame + e.rng.gauss(0.0, e.jitter)
        e.t += dt
        now_ms = int(e.t * 1000.0)
        dtms = now_ms - e.world_ms
        e.world_ms = now_ms
        if e.t >= t_next:
            t_next += 1.0 / hz
            goal = e.pos + hop_m
        remaining = max(0.0, goal - e.pos)
        pf = e.factor
        f = e.speed_factor(dtms, remaining) if remaining > 1e-6 else e.factor
        if remaining > 1e-6:
            e.factor = f
            e.pos = min(goal, e.pos + dtms * e.rate * (pf + f) * 0.5 / 1e6)
        moved = e.pos - last
        last = e.pos
        drift = momentum_advance(s, e.pos, moved / dt if dt > 0 else 0.0, dt)
        motion(s, moved + (drift - prev), dt)
        prev = drift
        if e.t > 1.5:
            drifts.append(drift)
            speeds.append(moved / dt)
            ts.append(e.t)
    m, sd, mx = stats(drifts)
    pp = max(drifts) - min(drifts)
    sm, ssd, _ = stats(speeds)
    print(f"  {label:34s} real speed {sm:4.2f} +- {ssd:4.2f} m/s   drift {m*100:5.1f} cm "
          f"peak-to-peak {pp*100:5.1f} cm = {pp*PX_PER_M:5.1f} px of on-screen wobble at {hz:.0f} Hz")


def corner(rate_mm, turn_deg):
    """A path waypoint: the travel direction changes in one frame, the drift vector has to swing round."""
    e = Engine(rate_mm=rate_mm, length=1e9, fps=FPS)
    s = Slot()
    sx = Slot()
    dxs, dys = 0.0, 0.0
    px = py = 0.0
    lx = ly = 0.0
    a = math.radians(turn_deg)
    drift = (0.0, 0.0)
    dv = (0.0, 0.0)
    lat = []
    for i in range(int(3.0 * FPS)):
        dt = e.frame
        e.t += dt
        now_ms = int(e.t * 1000.0)
        dtms = now_ms - e.world_ms
        e.world_ms = now_ms
        f = e.speed_factor(dtms, 1e9)
        d = dtms * e.rate * (e.factor + f) * 0.5 / 1e6
        e.factor = f
        ang = 0.0 if e.t < 1.5 else a
        px += d * math.cos(ang)
        py += d * math.sin(ang)
        vx, vy = (px - lx) / dt, (py - ly) / dt
        lx, ly = px, py
        # Momentum.Advance, both axes, with the shared speed-driven time constant
        speed = math.hypot(vx, vy)
        dspeed = math.hypot(dv[0], dv[1])
        t = min(0.1, 0.9 * 0.3 / max(dspeed, speed, 1e-3))
        from pose_model import smooth
        dx, dvx = smooth(s.drawn, s.drawn_v, px, dt, t)
        dy, dvy = smooth(sx.drawn, sx.drawn_v, py, dt, t)
        s.drawn, s.drawn_v, sx.drawn, sx.drawn_v = dx, dvx, dy, dvy
        drift = (dx - px, dy - py)
        ln = math.hypot(*drift)
        if ln > 0.3:
            drift = (drift[0] * 0.3 / ln, drift[1] * 0.3 / ln)
            s.drawn_v, sx.drawn_v = vx, vy
        s.drawn, sx.drawn = px + drift[0], py + drift[1]
        dv = (s.drawn_v, sx.drawn_v)
        if e.t >= 1.5:
            # lateral offset of the drawn body from the new travel line
            lat.append(abs(-math.sin(ang) * drift[0] + math.cos(ang) * drift[1]))
    return max(lat)


if __name__ == "__main__":
    print("=== start / stop transient, 12 m straight path ===")
    for r, lab in ((2000.0, "walk 2.0 m/s"), (4000.0, "run 4.0 m/s"), (6000.0, "hasted 6.0 m/s")):
        report_start_stop(r, lab)

    print("\n=== a server-paced path: ComputeSpeedFactor stays on its sqrt(remaining) branch ===")
    for hz, hop in ((10.0, 0.4), (6.7, 0.6), (5.0, 0.8), (4.0, 1.0)):
        server_paced(4000.0, hop, hz, f"run, {hop*100:.0f} cm hop every {1000/hz:.0f} ms")
    for hz, hop in ((10.0, 0.2), (5.0, 0.4)):
        server_paced(2000.0, hop, hz, f"walk, {hop*100:.0f} cm hop every {1000/hz:.0f} ms")

    print("\n=== a path corner: the drift vector swings across the new travel line ===")
    for deg in (45, 90, 135, 180):
        for r, lab in ((2000.0, "walk"), (4000.0, "run")):
            m = corner(r, deg)
            print(f"  {lab} {deg:3d} deg turn -> body swings {m*100:5.1f} cm "
                  f"({m*PX_PER_M:5.1f} px) sideways off the travel line")
