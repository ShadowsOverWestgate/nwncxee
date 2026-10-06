"""The engine's real position track, as measured off nwmain-linux (not guessed).

CNWCCreature::ComputeSpeedFactor @0x71df00, decoded from objdump (constants read out of .rodata:
0x12f04a8=1000.0, 0x131c3f8=2000000.0, 0x131c3f4=500.0, 0x12eee18=0.01, 0x12eeeac=0.1, 0x12ee9e4=1.0):

    float ComputeSpeedFactor(float rateMM)
    {
        float div = (driving == 1) ? 500.0f : 1000.0f;       // -0x58
        float remaining = |desiredPos - cursor[0]| + sum of the remaining waypoint segments;
        float stopDist  = rateMM * 1000.0f / 2000000.0f;      // = rateMM/2000, metres
        float stored    = this+0x2fc;
        if (stopDist < remaining) {                           // 0x71e080: far from the stop
            if (stored > 1.0f) return 1.0f;
            float up = stored + dtMs / div;                   // +1.0 per SECOND (driving: +2.0)
            if (up > 1.0f) return 1.0f;
            return max(0.1f, up);
        }
        float ratio = remaining / stopDist;                   // near the stop
        float cand  = ratio >= 0.01f ? sqrtf(ratio) : 0.1f;
        float down  = cand - dtMs / 1000.0f;
        if (1.0f >= stored) return max(0.1f, min(down, stored + dtMs / div));
        return max(0.1f, down);
    }

CNWCCreature::WalkUpdateLocation @0x725830 (objdump 0x725e93-0x725ee6 and 0x725fc4-0x725ffe):

    f        = ComputeSpeedFactor(rate);
    dist_m   = (float)dtMs * rate * (this[0x2fc] + f) * 0.5f / 1e6f;   // 0.5 @0x12eedac, 1e6 @0x12f1d0c
    this[0x2fc] = f;                                                    // stored AFTER the sum
    animSpeed = f * (rate/1000) / (strideMM / lenMs);                    // 0x725fc4: mulss the factor in

dtMs = CNWCObject+0x58, an INTEGER millisecond delta of world time; the pose clock is a float
second delta off a microsecond timer (CX-159 Q2).
"""
import math


class Engine:
    """One creature walking a straight path of `length` metres at `rate` mm/s."""

    def __init__(self, rate_mm=2000.0, length=12.0, driving=False, fps=143.0, jitter_ms=0.25,
                 seed=1, correction_hz=0.0, correction_m=0.0):
        self.rate = rate_mm
        self.length = length
        self.div = 500.0 if driving else 1000.0
        self.stop_dist = rate_mm * 1000.0 / 2e6
        self.factor = 0.0          # +0x2fc, persists between walks (0.1 after the last stop)
        self.pos = 0.0             # metres along the path
        self.frame = 1.0 / fps
        self.jitter = jitter_ms / 1000.0
        self.correction_hz = correction_hz
        self.correction_m = correction_m
        import random
        self.rng = random.Random(seed)
        self.t = 0.0               # real seconds
        self.world_ms = 0          # floor(t*1000) at the last AI update
        self._corr_t = 0.0

    def speed_factor(self, dt_ms, remaining):
        stored = self.factor
        if self.stop_dist < remaining:
            if stored > 1.0:
                return 1.0
            up = stored + dt_ms / self.div
            return 1.0 if up > 1.0 else max(0.1, up)
        ratio = remaining / self.stop_dist
        cand = math.sqrt(ratio) if ratio >= 0.01 else 0.1
        down = cand - dt_ms / 1000.0
        if 1.0 >= stored:
            return max(0.1, min(down, stored + dt_ms / self.div))
        return max(0.1, down)

    def step(self, moving=True):
        """One frame. Returns (dt_seconds, position_metres, speed_factor, dt_ms)."""
        dt = self.frame + self.rng.gauss(0.0, self.jitter)
        if dt <= 0.0005:
            dt = 0.0005
        self.t += dt
        now_ms = int(self.t * 1000.0)
        dt_ms = now_ms - self.world_ms
        self.world_ms = now_ms
        if not moving:
            return dt, self.pos, self.factor, dt_ms
        remaining = max(0.0, self.length - self.pos)
        if remaining <= 1e-6:
            return dt, self.pos, self.factor, dt_ms
        prev = self.factor
        f = self.speed_factor(dt_ms, remaining)
        dist = dt_ms * self.rate * (prev + f) * 0.5 / 1e6
        self.factor = f
        self.pos = min(self.length, self.pos + dist)
        # MoveCreatureToPosition: the server correction smoother, modelled as a bounded
        # lerp toward a target that jumps by correction_m every 1/correction_hz seconds.
        if self.correction_hz > 0.0:
            self._corr_t += dt
            if self._corr_t >= 1.0 / self.correction_hz:
                self._corr_t = 0.0
                self._err = self.rng.gauss(0.0, self.correction_m)
            err = getattr(self, "_err", 0.0)
            if err:
                take = err * (dt / (0.2 + dt))   # remaining/(remaining+dt) shape
                self.pos += take
                self._err = err - take
        return dt, self.pos, self.factor, dt_ms


def track(n, **kw):
    """n frames of (dt, pos, factor, dt_ms), preceded by 30 standing frames."""
    e = Engine(**kw)
    out = []
    for _ in range(30):
        out.append(e.step(moving=False))
    for _ in range(n):
        out.append(e.step(moving=True))
    for _ in range(int(1.5 / e.frame)):
        out.append(e.step(moving=False))
    return out
