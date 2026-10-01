using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>
/// CX-106: the leaf of the owned animation runtime -- one <c>NewController</c> track sampled at an animation
/// time, and the blend of that sample into the part field the controller names. Ports
/// <c>NewController::GetKeyIndexAndAlpha</c> @0x4980e0, <c>GetFloatValue</c> @0x498480,
/// <c>GetVectorValue</c> @0x498620 (its outlined body @0x498180), <c>GetQuaternionValue</c> @0x498640,
/// <c>slerp</c> @0x5b6500 and <c>NewController::Control</c> @0x498f20.
/// </summary>
/// <remarks>
/// <para><b>The one intentional divergence is the key SEARCH.</b> The engine scans key times from index 1
/// upward on every sample, so a 40-key track costs 40 compares per part per controller per run per frame.
/// This binary-searches instead. The result is identical by construction -- both answer "the smallest index
/// whose key time is at or after <c>t</c>" -- and <c>AnimRuntime.SelfCheck</c> proves it against a transcription
/// of the engine's own loop over random tracks. A cursor would have been cheaper still and is wrong:
/// <c>NewController</c> records live on the <c>MdlNode</c>, which every instance of a model shares, so there
/// is no per-instance place to keep one.</para>
/// <para><b>Bezier tracks are handed to the engine's own getter.</b> The scalar Bezier path pairs its control
/// points differently from the vector one (<c>d[k]+d[k+3]</c>, value plus value) and its past-the-end branch
/// passes the raw time where every other branch passes the segment alpha and reads one key past the array --
/// both look like upstream bugs (docs/research/anim-runtime.md section 8). Transcribing them into a second
/// implementation buys nothing and can only drift, so a Bezier track costs one call into the engine and is
/// counted on the period line.</para>
/// <para>Allocation-free: every sample is stack locals over the engine's own float pool.</para>
/// </remarks>
internal static unsafe class AnimKeys
{
    /// <summary>The <see cref="EngineLayout.NewControllerFormat"/> low nibble: the track's value width.</summary>
    private const byte Scalar = 1, Vector3Keys = 3, Rotation = 4;
    /// <summary>...and its high nibble, set on a Bezier-authored track.</summary>
    private const byte Bezier = 0x10;   // not-an-offset: the format byte's high nibble

    private static CtrlGetFloatFn _engineFloat;
    private static CtrlGetVectorFn _engineVector;
    /// <summary>The engine's <c>position_scale_factor</c>, or a local 1.0 before the symbol resolves -- so a
    /// SelfCheck can sample a position track off-device without a special case in the sampler.</summary>
    private static float* _posScale = Local(1f);
    private static int _bezier;

    private static float* Local(float v)
    {
        float* p = (float*)System.Runtime.InteropServices.NativeMemory.Alloc(sizeof(float));
        *p = v;
        return p;
    }

    /// <summary>What a position track is being multiplied by right now: the running <c>AnimRun</c>'s
    /// <see cref="EngineLayout.AnimRunPosScale"/> once the runtime is live.</summary>
    internal static float Scale => *_posScale;

    /// <summary>Resolve the two engine leaves the owned path still needs (the Bezier getters and the
    /// position-scale global). False = the runtime must not arm.</summary>
    internal static bool Attach()
    {
        _engineFloat = EngineFn.CtrlGetFloat;
        _engineVector = EngineFn.CtrlGetVector;
        float* scale = (float*)Native.Sym(SymbolMap.PositionScaleFactor);
        if (scale != null) _posScale = scale;
        return _engineFloat != null && _engineVector != null && scale != null;
    }

    /// <summary>Bezier tracks handed to the engine since the last period, for the period line.</summary>
    internal static int TakeBezier() { int n = _bezier; _bezier = 0; return n; }

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

    /// <summary>A scalar track's value at <paramref name="t"/>. False = not a scalar track (nothing written).</summary>
    internal static bool Float(IntPtr ctrl, IntPtr node, float t, out float v)
    {
        v = 0f;
        var c = new NewController(ctrl);
        byte fmt = c.Format;
        if ((fmt & 0xf) != Scalar) return false;
        float* d = (float*)new MdlNode(node).ControllerData;
        if (d == null) return false;
        if ((fmt & 0xf0) == Bezier)
        {
            _bezier++;
            fixed (float* p = &v) return _engineFloat(ctrl, t, node, p) != 0;
        }
        int n = (ushort)c.KeyCount, vb = c.ValueKey;
        Key(d + c.TimeKey, n, t, out int idx, out float a);
        v = idx == 0 ? d[vb]
          : idx == n ? d[vb + idx - 1]
          : (1f - a) * d[vb + idx - 1] + a * d[vb + idx];
        return true;
    }

    /// <summary>A vector track's value at <paramref name="t"/>, times <c>position_scale_factor</c> when the
    /// track is the part's POSITION (the engine's one use of that global). False = not a vector track.</summary>
    internal static bool Vector(IntPtr ctrl, IntPtr node, float t, out Vector3 v)
    {
        v = default;
        var c = new NewController(ctrl);
        byte fmt = c.Format;
        if ((fmt & 0xf) != Vector3Keys) return false;
        float* d = (float*)new MdlNode(node).ControllerData;
        if (d == null) return false;
        if ((fmt & 0xf0) == Bezier)
        {
            _bezier++;
            fixed (Vector3* p = &v) return _engineVector(ctrl, t, node, p) != 0;
        }
        if (KeyCurveService.OwnedVector(ctrl, node, t, out v)) return true;
        int n = (ushort)c.KeyCount;
        ushort vb = c.ValueKey;
        float s = c.Id == EngineLayout.PartPos ? *_posScale : 1f;
        Key(d + c.TimeKey, n, t, out int idx, out float a);
        if (idx == 0)
        {
            // 16-bit index arithmetic, exactly as the engine's: a pool over 65535 floats wraps on both.
            v = new Vector3(d[vb], d[(ushort)(vb + 1)], d[(ushort)(vb + 2)]) * s;
            return true;
        }
        ushort k = (ushort)(vb + 3 * idx - 3);
        if (idx == n)
        {
            v = new Vector3(d[k], d[(ushort)(k + 1)], d[(ushort)(k + 2)]) * s;
            return true;
        }
        float u = 1f - a;
        v = new Vector3(d[(ushort)(k + 3)] * a + d[k] * u,
                        d[(ushort)(k + 4)] * a + d[(ushort)(k + 1)] * u,
                        d[(ushort)(k + 5)] * a + d[(ushort)(k + 2)] * u) * s;
        return true;
    }

    /// <summary>A rotation track's value at <paramref name="t"/>. The keys are stored <c>(x, y, z, w)</c>
    /// while the part's quaternion is <c>(w, x, y, z)</c>; there is no Bezier form for rotations.</summary>
    internal static bool Turn(IntPtr ctrl, IntPtr node, float t, out Quat q)
    {
        q = new Quat { W = 1f };
        var c = new NewController(ctrl);
        if ((c.Format & 0xf) != Rotation) return false;
        float* d = (float*)new MdlNode(node).ControllerData;
        if (d == null) return false;
        if (KeyCurveService.OwnedRotation(ctrl, node, t, out q)) return true;
        int n = (ushort)c.KeyCount;
        ushort vb = c.ValueKey;
        Key(d + c.TimeKey, n, t, out int idx, out float a);
        if (idx == 0) { q = At(d, vb); return true; }
        int k = vb - 4 + idx * 4;
        if (idx == n) { q = At(d, k); return true; }
        q = Slerp(At(d, k), At(d, k + 4), a);
        return true;
    }

    /// <summary>One rotation key: four floats stored x, y, z, w.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Quat At(float* d, int i) => new() { X = d[i], Y = d[i + 1], Z = d[i + 2], W = d[i + 3] };

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

    /// <summary>
    /// <c>NewController::Control</c>: sample the track and blend it into the part field the controller's id
    /// names, by <paramref name="w"/>. This blend IS the crossfade and the overlay: a weight of 1 writes the
    /// key outright, a weight below 1 blends it over whatever the previous run of this frame left there, and
    /// nothing else in the engine holds a pose.
    /// </summary>
    internal static void Control(IntPtr ctrl, IntPtr part, IntPtr node, float t, float w)
    {
        var c = new NewController(ctrl);
        int off = c.Id;
        switch (c.Format & 0xf)
        {
            case Scalar:
            {
                if (!Float(ctrl, node, t, out float v)) return;
                float* f = (float*)(part + off);
                float next = w * v + (1f - w) * *f;
                // BW3: with anim.owned this store is the ONLY writer of part alpha, so it is where a cached
                // alpha goes stale (PartTable.NoteAlphaWrite). Marked on a CHANGE only, so a held pose costs
                // one compare and no probe.
                if (off == EngineLayout.PartAlpha && next != *f) PartTable.NoteAlphaWrite(part);
                *f = next;
                return;
            }
            case Vector3Keys:
            {
                // The out value is pre-zeroed, so a getter that writes nothing blends toward the origin --
                // the engine's behaviour, and the reason `Vector` returning false still falls through here.
                Vector(ctrl, node, t, out Vector3 v);
                float* f = (float*)(part + off);
                float u = 1f - w;
                f[2] = v.Z * w + f[2] * u;
                f[1] = v.Y * w + f[1] * u;
                f[0] = w * v.X + u * f[0];
                return;
            }
            case Rotation:
            {
                Turn(ctrl, node, t, out Quat q);
                Quat* f = (Quat*)(part + off);
                *f = Slerp(*f, q, w);
                return;
            }
        }
    }
}
