using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: the one remaining reader of the engine's per-frame buckets, and it is an INSTRUMENT.
/// Behind <c>buckets.validate</c> (off by default) it keeps sampling <c>emitterbucket</c>,
/// <c>triggerbucket</c>, <c>binbucket</c> and the two <c>TileData</c> fade factors beside Alloy's own sets and
/// counts where the two disagree, so "did we reproduce the engine's population?" is a number on a period line
/// rather than an argument. Nothing here ever feeds a draw.
/// <para>The comparison is deliberately asymmetric in what it MEANS. The engine's bucket is the frame's
/// in-view slice, so an entry it holds that Alloy did not select is a MISS -- something the engine would have
/// drawn and Alloy will not. An entry Alloy selected that the engine did not is EXTRA: Alloy's frustum is its
/// own and a little slack there costs a draw, not a pixel. Both are counted; only the first is a bug.</para>
/// <para>Allocation-free, and it costs exactly the bucket reads this ticket removed -- which is why it is
/// off.</para></summary>
internal static unsafe class BucketValidator
{
    internal const int KindEmitter = 0, KindTrigger = 1, KindBeam = 2, KindBin = 3;
    private const int Kinds = 4;
    private static readonly string[] _names = ["emitters", "triggers", "beams", "grass bins"];

    /// <summary>Stamped membership set, sized past the largest bucket a sane frame can hold.</summary>
    private const int SetSize = 1 << 13;
    private const int SetMask = SetSize - 1;
    /// <summary>A bucket longer than this did not read sanely and the sample is dropped, exactly as the chase's
    /// own load cap does.</summary>
    private const int SaneMax = 1 << 13;

    private static readonly IntPtr[] _keyA = new IntPtr[SetSize];
    private static readonly int[] _stampA = new int[SetSize];
    private static readonly IntPtr[] _keyB = new IntPtr[SetSize];
    private static readonly int[] _stampB = new int[SetSize];
    /// <summary>Where a draw bucket's phase sub-buckets are gathered into one list.</summary>
    private static readonly IntPtr[] _scratch = new IntPtr[SaneMax];
    private static int _seq;

    private static readonly long[] _miss = new long[Kinds];
    private static readonly long[] _extra = new long[Kinds];
    private static readonly long[] _samples = new long[Kinds];
    private static readonly int[] _lastMiss = new int[Kinds];
    private static readonly int[] _lastExtra = new int[Kinds];
    private static long _unreadable;

    private static long _tileFrames, _tileFadeOff, _tileExploredOff;
    private static float _worstFade, _worstExplored;
    private static int _period;
    private static ExtenderLog? _log;

    internal static void Resolve(ExtenderLog log) => _log = log;

    /// <summary>Compare one class's post-cull selection against the engine's bucket for the same frame.
    /// <paramref name="selected"/> may hold zeros (a part that died since the last walk); they are skipped.
    /// </summary>
    internal static void Compare(int kind, ReadOnlySpan<IntPtr> selected, EngineList engine)
    {
        if (engine.IsNull) return;
        Core(kind, selected, (IntPtr*)engine.Items, engine.Count);
    }

    /// <summary>...and against a DRAW bucket, whose one class is spread over the phase sub-buckets: they are
    /// gathered into one scratch list first, because "missed" and "extra" only mean anything against the whole
    /// class.</summary>
    internal static void CompareDrawBucket(int kind, ReadOnlySpan<IntPtr> selected, DrawBucket buckets,
                                           int bucket, int phases)
    {
        int m = 0;
        for (int phase = 0; phase < phases; phase++)
        {
            DrawBucket b = buckets[bucket * phases + phase];
            IntPtr* items = b.Items;
            int n = b.Count;
            if (items == null || n <= 0 || n > SaneMax) continue;
            for (int i = 0; i < n && m < SaneMax; i++) if (items[i] != IntPtr.Zero) _scratch[m++] = items[i];
        }
        fixed (IntPtr* s = _scratch) Core(kind, selected, s, m);
    }

    private static void Core(int kind, ReadOnlySpan<IntPtr> selected, IntPtr* items, int n)
    {
        if ((uint)kind >= Kinds) return;
        if (n < 0 || n > SaneMax || (n > 0 && items == null)) { _unreadable++; return; }
        _seq++;
        for (int i = 0; i < selected.Length; i++) if (selected[i] != IntPtr.Zero) Put(_keyA, _stampA, selected[i]);
        for (int i = 0; i < n; i++) if (items[i] != IntPtr.Zero) Put(_keyB, _stampB, items[i]);
        int miss = 0, extra = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = items[i];
            if (p != IntPtr.Zero && !Has(_keyA, _stampA, p)) miss++;
        }
        for (int i = 0; i < selected.Length; i++)
        {
            IntPtr p = selected[i];
            if (p != IntPtr.Zero && !Has(_keyB, _stampB, p)) extra++;
        }
        _miss[kind] += miss; _extra[kind] += extra; _samples[kind]++;
        _lastMiss[kind] = miss; _lastExtra[kind] = extra;
    }

    /// <summary>Compare Alloy's own tile factors against the two the engine's BSP walk left on each tile. Also
    /// the period tick: this runs once per world frame while validation is on.</summary>
    internal static void CheckTiles(IntPtr[] tiles, bool[] freed, float[] fade, float[] explored, int count)
    {
        _tileFrames++;
        for (int t = 0; t < count; t++)
        {
            if (freed[t] || tiles[t] == IntPtr.Zero) continue;
            var td = new TileData(tiles[t]);
            float df = MathF.Abs(td.Fade - fade[t]), de = MathF.Abs(td.GeomFade - explored[t]);
            if (df > Tolerance) { _tileFadeOff++; if (df > _worstFade) _worstFade = df; }
            if (de > Tolerance) { _tileExploredOff++; if (de > _worstExplored) _worstExplored = de; }
        }
        if (++_period < AlloyRenderer.LogEvery) return;
        _period = 0;
        _log?.Info("[buckets] " + Note());
    }

    /// <summary>How far the two factors may differ before it counts: the engine commits its factor one frame
    /// late (the copy at the top of <c>ManageSceneBSP</c>), so a tile mid-ramp is legitimately one 4.0/s step
    /// apart -- about 0.07 at 60 fps. Anything past 0.2 is a different DECISION, not a phase difference.
    /// </summary>
    private const float Tolerance = 0.2f;

    /// <summary>The clause the <c>buckets</c> knob group and the period line print.</summary>
    internal static string Note()
    {
        string s = $"validator: tiles {_tileFadeOff} fade / {_tileExploredOff} reveal disagreement(s) over "
                 + $"{_tileFrames} frame(s)";
        if (_worstFade > 0f || _worstExplored > 0f)
            s += FormattableString.Invariant($" (worst {_worstFade:0.00} / {_worstExplored:0.00})");
        for (int k = 0; k < Kinds; k++)
        {
            if (_samples[k] == 0) continue;
            s += $" | {_names[k]} {_lastMiss[k]} missed / {_lastExtra[k]} extra now, {_miss[k]}/{_extra[k]}"
               + $" over {_samples[k]} sample(s)";
        }
        if (_unreadable > 0) s += $" | {_unreadable} unreadable bucket read(s)";
        return s;
    }

    private static int Bucket(IntPtr p) => (int)((ulong)p >> 4) & SetMask;

    private static void Put(IntPtr[] key, int[] stamp, IntPtr p)
    {
        int h = Bucket(p);
        for (int probe = 0; probe <= SetMask; probe++, h = (h + 1) & SetMask)
        {
            if (stamp[h] == _seq && key[h] == p) return;
            if (stamp[h] == _seq) continue;
            stamp[h] = _seq; key[h] = p;
            return;
        }
    }

    private static bool Has(IntPtr[] key, int[] stamp, IntPtr p)
    {
        int h = Bucket(p);
        for (int probe = 0; probe <= SetMask; probe++, h = (h + 1) & SetMask)
        {
            if (stamp[h] != _seq) return false;
            if (key[h] == p) return true;
        }
        return false;
    }

    /// <summary>Off-device: the set maths, which is the only part that can be wrong on its own. Synthetic
    /// pointers, no engine list -- <see cref="Compare"/>'s bucket arm needs one, so the check drives the set
    /// through the two helpers it is built from.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "BucketValidator.SelfCheck");
        _seq++;
        IntPtr a = (IntPtr)0x1000, b = (IntPtr)0x2000, c = (IntPtr)0x3000;
        Put(_keyA, _stampA, a);
        Put(_keyA, _stampA, b);
        check.Expect(Has(_keyA, _stampA, a) && Has(_keyA, _stampA, b), "a stamped member is found");
        check.Expect(!Has(_keyA, _stampA, c), "a non-member is not");
        Put(_keyA, _stampA, a);
        check.Expect(Has(_keyA, _stampA, a), "a repeated insert is idempotent");
        _seq++;
        check.Expect(!Has(_keyA, _stampA, a), "bumping the stamp empties the set without clearing it");
        log.Info(check.Ok ? "BucketValidator.SelfCheck: passed" : "BucketValidator.SelfCheck: FAILED");
        return check.Ok;
    }
}
