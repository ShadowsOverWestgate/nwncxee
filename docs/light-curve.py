#!/usr/bin/env python3
"""Falloff table for the owned point-light curve -- the shader's arithmetic, on the CPU.
Run it to see what a knob setting does before launching the game."""
import sys
def att(d, R, gain=4.0, r0=0.7, n=2.0):
    dd, r02 = d*d, r0*r0
    if dd > R*R: return 0.0
    w = 1.0 - (dd/(R*R))**2
    return gain * (r02/max(dd, r02))**(0.5*n) * w * w

if __name__ == "__main__":
    gain, r0, n = (float(x) for x in (sys.argv[1:4] or [4.0, 0.7, 2.0]))
    R = 13.3
    assert abs(att(0, R, gain, r0, n) - gain) < 1e-6, "peak must equal the gain"
    assert att(R, R, gain, r0, n) == 0.0, "must reach exactly zero at the range"
    prev = float("inf")
    for d in [x * 0.1 for x in range(1, int(R * 10) + 1)]:
        a = att(d, R, gain, r0, n)
        assert a <= prev + 1e-9, f"not monotonic at {d}"
        prev = a
    print(f"gain={gain} r0={r0} n={n}  R={R}")
    for d in (0, 0.5, 1, 2, 3, 5, 6.65, 9, 12, 13.3):
        print(f"  d={d:5.2f}  att={att(d, R, gain, r0, n):7.3f}")
