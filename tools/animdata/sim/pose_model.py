"""Ports of the pose layer's own maths, transcribed from managed/Services/Anim/.

PoseMath.Smooth      -- Game Programming Gems 4 SmoothCD
PoseService.Motion   -- MotionTime 0.15, AccelTime 0.3
Momentum.Advance     -- LagTime 0.1, MaxLag 0.3, Settle 0.9
Mass.Advance/Load    -- DipPerAccel 0.007, MaxDip 0.06, DipHz 2, DipZeta 0.6, ClimbTime 0.1
Stride.Factor        -- MinFactor 0.6, MaxFactor 1.6, Deadband 0.04, FactorTime 0.3
Follow.Trail         -- Hz 1.8, Zeta 0.45, LagGain 2, MaxLag 25 deg
"""
import math

MIN_LIFT = 1e-4          # FootPlacement.MinLift


def smooth(x, v, target, dt, time):
    omega = 2.0 / time
    k = omega * dt
    e = 1.0 / (1.0 + k + 0.48 * k * k + 0.235 * k * k * k)
    change = x - target
    temp = (v + omega * change) * dt
    v = (v - omega * temp) * e
    x = target + (change + temp) * e
    return x, v


class Slot:
    def __init__(self):
        self.speed = self.accel = self.yaw = self.yaw_rate = self.slope = 0.0
        self.vel = 0.0
        self.drawn = 0.0
        self.drawn_v = 0.0
        self.drift = 0.0
        self.dip = self.dip_v = self.climb = self.climb_a = 0.0
        self.factor = self.vfactor = 0.0
        self.stance = 0.0


MOTION_TIME = 0.15
ACCEL_TIME = 0.30


def motion(s, moved, dt, snap=False):
    """PoseService.Motion, 1-D along the path (yaw/slope dropped: straight-line walk)."""
    if snap or dt <= 0.0:
        s.speed = s.accel = 0.0
        s.vel = 0.0
        return
    a = min(1.0, dt / MOTION_TIME)
    s.vel += (moved / dt - s.vel) * a
    run = abs(moved)
    speed = s.speed + (run / dt - s.speed) * a
    s.accel += ((speed - s.speed) / dt - s.accel) * min(1.0, dt / ACCEL_TIME)
    s.speed = speed


LAG_TIME = 0.1
MAX_LAG = 0.3
SETTLE = 0.9


def momentum_advance(s, at, vel, dt, scale=1.0, fixed_time=False, clamp_resets_v=True):
    """Momentum.Advance, 1-D."""
    cap = MAX_LAG * scale
    if fixed_time:
        t = LAG_TIME
    else:
        t = min(LAG_TIME, SETTLE * cap / max(abs(s.drawn_v), abs(vel), 1e-3))
    s.drawn, s.drawn_v = smooth(s.drawn, s.drawn_v, at, dt, t)
    drift = s.drawn - at
    ln = abs(drift)
    if drift * s.drift < 0.0 and abs(vel) < 1e-3:
        drift = 0.0
        s.drawn_v = vel
    elif ln > cap:
        drift *= cap / ln
        if clamp_resets_v:
            s.drawn_v = vel
    s.drawn = at + drift
    s.drift = drift
    return drift


DIP_PER_ACCEL = 0.007
MAX_DIP = 0.06
DIP_HZ, DIP_ZETA = 2.0, 0.6
CLIMB_TIME = 0.1
WOBBLE_STEP = 1.0 / 120.0
WOBBLE_MAX = 12


def wobble(x, v, omega, zeta, dt):
    if dt <= 0:
        return x, v
    n = math.ceil(dt / WOBBLE_STEP)
    if n > WOBBLE_MAX:
        return 0.0, 0.0
    h = dt / n
    for _ in range(n):
        v += (-omega * omega * x - 2.0 * zeta * omega * v) * h
        x += v * h
    return x, v


def settle(x, v, target, hz, zeta, dt):
    y = x - target
    y, v = wobble(y, v, 2.0 * math.pi * hz, zeta, dt)
    return y + target, v


def mass_load(accel, lift=0.0):
    return min(DIP_PER_ACCEL * (abs(accel) + max(0.0, lift - 2.0)), MAX_DIP)


def mass_advance(s, rise, dt, scale=1.0):
    a = min(1.0, dt / CLIMB_TIME)
    climb = s.climb + (rise / dt - s.climb) * a
    s.climb_a += ((climb - s.climb) / dt - s.climb_a) * a
    s.climb = climb
    s.dip, s.dip_v = settle(s.dip, s.dip_v, -scale * mass_load(s.accel, s.climb_a), DIP_HZ, DIP_ZETA, dt)
    return s.dip


MIN_STANCE = 0.2
MIN_FACTOR, MAX_FACTOR = 0.6, 1.6
DEADBAND = 0.04
FACTOR_TIME = 0.3


def stride_factor(ground, stance):
    if not stance > MIN_STANCE or not ground > 0.0:
        return 1.0
    f = ground / stance
    return 1.0 if f < MIN_FACTOR or f > MAX_FACTOR or abs(f - 1.0) < DEADBAND else f


FOLLOW_HZ, FOLLOW_ZETA, FOLLOW_GAIN = 1.8, 0.45, 2.0
FOLLOW_OMEGA = 2.0 * math.pi * FOLLOW_HZ
FOLLOW_MAXLAG = 25.0 * math.pi / 180.0
SUBSTEP = 1.0 / 120.0


def follow_trail(x, v, accel, hang_len, dt):
    """Follow.Trail reduced to the accel-driven axis of one arm (dw = 0: straight walk)."""
    push = -FOLLOW_GAIN / hang_len * accel
    n = math.ceil(dt / SUBSTEP)
    if n > 12:
        return 0.0, 0.0
    h = dt / n
    for _ in range(n):
        v += (push - FOLLOW_OMEGA * FOLLOW_OMEGA * x - 2.0 * FOLLOW_ZETA * FOLLOW_OMEGA * v) * h
        x += v * h
    if abs(x) > FOLLOW_MAXLAG:
        x = math.copysign(FOLLOW_MAXLAG, x)
    return x, v


def stats(xs):
    n = len(xs)
    if n == 0:
        return 0.0, 0.0, 0.0
    m = sum(xs) / n
    sd = math.sqrt(sum((x - m) ** 2 for x in xs) / n)
    return m, sd, max(abs(x) for x in xs)
