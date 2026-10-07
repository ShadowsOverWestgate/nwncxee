using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The animation arithmetic the scene database samples with -- the key search, the engine's slerp and
/// the animmesh morph frame -- checked off-device against transcriptions of the engine's own code.</summary>
internal static unsafe class AnimMathCheck
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AnimMathCheck");
        check.Expect(KeySearch(), "key search: the binary search answers exactly what the engine's linear scan does, on 4000 probes over random tracks");
        check.Expect(SlerpMath(), "slerp: the engine's shortest-arc negation, its two exact escapes, and the halfway rotation of a 90 degree arc");
        check.Expect(MorphFrames(), "animmesh morph: the frame index is shared -- the position pool's clamp is what the UV pool then reads");
        return check.Ok;
    }

    /// <summary><c>NewController::GetKeyIndexAndAlpha</c> transcribed as the engine writes it -- a linear scan
    /// from key 1 -- so the shipping binary search can be compared against it rather than against a belief.</summary>
    private static void EngineKey(float* times, int n, float t, out int idx, out float alpha)
    {
        idx = 0;
        alpha = 1f;
        if (n == 0) return;
        if (!(times[0] <= t && t != times[0])) return;
        uint i = 1;
        while (true)
        {
            idx = (int)(ushort)i;
            if (idx >= n) break;
            if (t <= times[(int)i]) break;
            i = (i + 1) & 0xffff;
        }
        if (idx == n) return;
        float t0 = times[(int)i - 1];
        alpha = (t - t0) / (times[(int)i] - t0);
    }

    private static bool KeySearch()
    {
        float* times = stackalloc float[32];
        var rng = new Random(106);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = trial % 32;
            float at = 0f;
            for (int i = 0; i < n; i++)
            {
                // Every third track holds a pair of keys at one time, which is where the engine's own
                // division by zero lives: the two implementations must agree even on that.
                at += trial % 3 == 0 && i % 5 == 2 ? 0f : (float)rng.NextDouble() * 0.5f + 0.01f;
                times[i] = at;
            }
            for (int p = 0; p < 20; p++)
            {
                float t = p switch
                {
                    0 => -1f,
                    1 => n == 0 ? 0f : times[0],
                    2 => n == 0 ? 0f : times[n - 1],
                    3 => n == 0 ? 0f : times[n - 1] + 1f,
                    _ => n == 0 ? (float)rng.NextDouble() : times[p % Math.Max(n, 1)] * (float)rng.NextDouble() * 1.2f,
                };
                EngineKey(times, n, t, out int wantIdx, out float wantA);
                AnimKeys.Key(times, n, t, out int gotIdx, out float gotA);
                if (gotIdx != wantIdx) return false;
                bool same = wantA.Equals(gotA) || MathF.Abs(wantA - gotA) < 1e-6f
                            || (float.IsNaN(wantA) && float.IsNaN(gotA))
                            || (float.IsInfinity(wantA) && float.IsInfinity(gotA));
                if (!same) return false;
            }
        }
        return true;
    }

    private static bool SlerpMath()
    {
        Quat id = new() { W = 1f };
        // |dot| >= 1 returns the first argument UNCHANGED -- not a lerp, not normalised.
        Quat big = new() { W = 3f };
        if (AnimKeys.Slerp(big, id, 0.5f) is not { W: 3f, X: 0f, Y: 0f, Z: 0f }) return false;
        // A 90 degree turn about Z, halved, is 45 degrees about Z.
        float c = MathF.Cos(MathF.PI / 4f), s = MathF.Sin(MathF.PI / 4f);
        Quat q90 = new() { W = c, Z = s };
        Quat half = AnimKeys.Slerp(id, q90, 0.5f);
        float c45 = MathF.Cos(MathF.PI / 8f), s45 = MathF.Sin(MathF.PI / 8f);
        if (MathF.Abs(half.W - c45) > 1e-5f || MathF.Abs(half.Z - s45) > 1e-5f) return false;
        // Shortest arc: the NEGATED 90 degree turn must give the same halfway rotation up to sign, because
        // the negation is applied to the first argument and only when the dot product is negative.
        Quat neg = new() { W = -c, Z = -s };
        Quat other = AnimKeys.Slerp(neg, id, 0.5f);
        if (MathF.Abs(MathF.Abs(other.W) - c45) > 1e-5f || MathF.Abs(MathF.Abs(other.Z) - s45) > 1e-5f) return false;
        // Both endpoints are reached exactly.
        Quat at0 = AnimKeys.Slerp(id, q90, 0f), at1 = AnimKeys.Slerp(id, q90, 1f);
        return MathF.Abs(at0.W - 1f) < 1e-5f && MathF.Abs(at1.W - c) < 1e-5f && MathF.Abs(at1.Z - s) < 1e-5f;
    }

    /// <summary>
    /// <see cref="AnimHierarchy.Frames"/> against the engine's own control flow. The rows that matter are the
    /// last two: with the two pools at different frame counts, the UV pool sees the frame the POSITION pool
    /// clamped, not one it clamped itself. Every expected value is read off the disassembly
    /// (0x4993c2, 0x4993de, 0x499748, 0x499940, 0x499555, 0x49986f), not off this implementation.
    /// </summary>
    private static bool MorphFrames()
    {
        // (frame, vertFrames, uvFrames) -> (posFrame, lerpPos, uvFrame, lerpUv)
        (int F, int Vf, int Tf, int P, bool Lp, int U, bool Lu)[] rows =
        [
            // interior of both: neither pool clamps
            (2, 8, 8, 2, true, 2, true),
            // last segment of both: the position pool holds its last key, and so does the UV pool
            (7, 8, 8, 7, false, 7, false),
            // past the end of both: the position pool clamps to vf-1 and the UV pool reads THAT
            (99, 8, 8, 7, false, 7, false),
            // before the start: the position pool forces the shared index to 0 and holds key 0, and the UV
            // pool -- reading that 0 as an in-range frame -- LERPS 0->1 with a negative alpha, i.e. it
            // extrapolates backward. Asymmetric, and exactly what 0x499940 -> 0x499555 does.
            (-3, 8, 8, 0, false, 0, true),
            // no position frames at all: the frame stays negative, the UV pool forces 0 and holds it
            (-3, 0, 8, -3, false, 0, false),
            // no position frames, frame in range: the UV pool decides on the RAW frame
            (2, 0, 8, 2, false, 2, true),
            // THE FIX: 4 morph positions, 16 UVs, frame past the positions. The position pool clamps the
            // shared index to 3, so the UV pool holds key 3 -- it does NOT lerp 12->13 off the raw frame.
            (12, 4, 16, 3, false, 3, true),
            // ...and past both, so the UV pool holds its own last key because the clamped index reaches it
            (99, 4, 2, 3, false, 1, false),
        ];
        foreach (var r in rows)
        {
            int f = r.F;
            AnimHierarchy.Frames(ref f, r.Vf, r.Tf, out bool lp, out bool lu, out int uf);
            if (f != r.P || lp != r.Lp || uf != r.U || lu != r.Lu) return false;
        }
        return true;
    }
}
