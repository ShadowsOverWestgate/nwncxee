using System;
using System.Runtime.CompilerServices;
using CX.Engine;

namespace CX.Alloy;

/// <summary>
/// CX-106: the two pieces of the engine's key sampler the scene database still runs on --
/// <c>NewController::GetKeyIndexAndAlpha</c> @0x4980e0 and <c>slerp</c> @0x5b6500.
/// <para>CX-188: the SAMPLERS are gone. <c>Float</c>/<c>Vector</c>/<c>Turn</c>/<c>Control</c> read a
/// <c>NewController</c> record off a live <c>MdlNode</c> and blended the result into a <c>Part</c> field, and
/// their only callers were <c>AnimHierarchy.Control</c> -- itself unreachable, the engine part-tree walk --
/// and <c>EmitterSim.Keyed</c>, a self-recursive orphan. <c>DbAnim.Pose</c> samples the database's own
/// <c>ControllerTrack</c> arrays and calls the two below.</para>
/// </summary>
/// <remarks>
/// <para><b>The key SEARCH is an intentional divergence.</b> The engine scans key times from index 1 upward
/// on every sample, so a 40-key track costs 40 compares per part per controller per run per frame. This
/// binary-searches instead. The result is identical by construction -- both answer "the smallest index whose
/// key time is at or after <c>t</c>" -- and <c>AnimMathCheck</c> proves it against a transcription of the
/// engine's own loop over random tracks. A cursor would have been cheaper still and is wrong:
/// <c>NewController</c> records live on the <c>MdlNode</c>, which every instance of a model shares, so there
/// is no per-instance place to keep one.</para>
/// <para>Allocation-free.</para>
/// </remarks>
internal static unsafe class AnimKeys
{
    /// <summary>
    /// <c>NewController::GetKeyIndexAndAlpha</c>: <paramref name="idx"/> is 0 at or before the first key,
    /// <paramref name="n"/> past the last, and otherwise the index whose key time first reaches
    /// <paramref name="t"/>, with <paramref name="alpha"/> the position inside
    /// <c>[times[idx-1], times[idx]]</c>. Binary search where the engine scans; same answer.
    /// </summary>
    /// <remarks>Premise: <paramref name="times"/> is non-decreasing. Every MDL key track is authored that
    /// way and the engine's own alpha is garbage otherwise, but the engine SCANS and so still answers "the
    /// first key at or past <paramref name="t"/>" on an unsorted track where this search need not. Checking
    /// per sample would cost exactly what the search saves; the place to check, if it is ever worth it, is
    /// once at model load.</remarks>
    internal static void Key(float* times, int n, float t, out int idx, out float alpha)
    {
        alpha = 1f;
        // The engine's two early exits, in its order: an empty track and a time at or before key 0 both
        // answer (0, 1). Note `t > times[0]` is strict, so a time exactly ON key 0 takes this exit.
        if (n <= 0 || !(t > times[0])) { idx = 0; return; }
        // lower_bound over [1, n-1] for the first key time >= t; none means past the end.
        int lo = 1, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (times[mid] < t) lo = mid + 1; else hi = mid - 1;
        }
        if (lo >= n) { idx = n; return; }
        idx = lo;
        float t0 = times[lo - 1];
        // No guard on a zero-width segment: the engine has none either, and a track with two keys at one
        // time is content, not a mechanism failure. It yields the engine's own infinity.
        alpha = (t - t0) / (times[lo] - t0);
    }

    /// <summary>
    /// <c>slerp(Quaternion a, Quaternion const&amp; b, float w)</c> @0x5b6500, transcribed. Shortest arc by
    /// negating <paramref name="a"/> (never <paramref name="b"/>), the two exact escapes the engine has --
    /// <c>|dot| &gt;= 1</c> and a zero angle, both returning the possibly-negated <paramref name="a"/> -- and
    /// NO renormalisation of the result.
    /// </summary>
    internal static Quat Slerp(Quat a, Quat b, float w)
    {
        float d = Dot(a, b);
        if (0f > d) { a.W = -a.W; a.X = -a.X; a.Y = -a.Y; a.Z = -a.Z; d = Dot(a, b); }
        float ad = MathF.Abs(d);
        if (!(ad < 1f)) return a;
        float ang = MathF.Acos(ad);
        // Only an EXACTLY zero angle escapes: the engine's `jp` at 0x5b65e4 jumps PAST this return, so a NaN
        // angle falls through into sinf and yields a NaN quaternion. Unreachable -- the |dot| < 1 test above
        // already excludes a NaN dot, so acosf only ever sees [0, 1).
        if (ang == 0f) return a;
        float s = MathF.Sin(ang), ka = MathF.Sin(ang - w * ang) / s, kb = MathF.Sin(w * ang) / s;
        return new Quat { W = a.W * ka + b.W * kb, X = a.X * ka + b.X * kb,
                          Y = a.Y * ka + b.Y * kb, Z = a.Z * ka + b.Z * kb };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(Quat a, Quat b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
