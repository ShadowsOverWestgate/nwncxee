using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Framework.EngineLayout;

namespace Sowx.Services.Perf;

/// <summary>
/// ADR 0006 phase 0: per-frame draw-path histogram. Opt-in via <c>drawhooks=stats</c> in <c>sowx.cfg</c> (off = no
/// hooks, zero cost). Config and not a chat command (ADR 0013): it and <c>DrawFunnelService</c> hook the same
/// symbols, so which of them arms is decided once, at install, and cannot be flipped mid-session.
/// <para>Call-through hooks on <c>GLRender::RenderVertexData</c> (every GL draw the engine submits)
/// and <c>Scene::RenderDrawBucket</c> (to attribute draws to bucket ids). Aggregates draws by
/// (VDO, GL program) -- the upper bound on batch groupability; texture/param splits are phase-1's to
/// measure -- and logs a summary + top groups every ~900 presented frames.</para>
/// <para>Hot while enabled (~2k fires/frame): fixed-size open-address tables, no allocation; the log line
/// is the only cold part.</para>
/// </summary>
public sealed unsafe class DrawStatsService : IExtenderService
{
    private const int Groups = 512, Pairs = 8192, LogEvery = 900;

    /// <summary>True when <c>drawhooks=stats</c> armed the hooks (read by the frame tick).</summary>
    public static bool Enabled;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, int, void> _origBucket;
    private static int _bucket = -1;

    // period accumulators (reset each log)
    private static int _frames, _draws, _indexed, _bucketDraws;
    private static long _idxSum;
    // group table: key = vdo ^ (program rotated); value rows in parallel arrays
    private static readonly ulong[] _gKey = new ulong[Groups];
    private static readonly int[] _gDraws = new int[Groups];
    private static readonly int[] _gItems = new int[Groups];
    private static readonly long[] _gIdx = new long[Groups];
    private static readonly IntPtr[] _gVdo = new IntPtr[Groups];
    private static readonly int[] _gProg = new int[Groups];
    private static int _gOver;
    // (vdo,item,program) first-seen set for unique-member counts
    private static readonly ulong[] _pKey = new ulong[Pairs];
    private static int _pOver;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        // #170: ahead of the stand-down -- the host called EndFrame every frame regardless, and EndFrame
        // guards itself on Enabled. Registering after the return would silently change that.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 0, EndFrame, "DrawStatsService.endFrame", log);
        if (Settings.Text(Alloy.DrawFunnelService.DrawModeKey, Alloy.DrawFunnelService.DrawModeBatch)
            != Alloy.DrawFunnelService.DrawModeStats) return;
        _log = log;
        // Chained, not Install()ed: RenderVertexData has several subscribers (#88). Lowest priority --
        // we only count what actually reached the GPU.
        _drawSlot = hooks.Chain(SymbolMap.GlRenderVertexData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnVdoDraw, DrawPriority);
        _origBucket = (delegate* unmanaged<IntPtr, int, void>)hooks.Install(
            SymbolMap.RenderDrawBucket, (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnBucket);
        Enabled = _drawSlot != null;
        log.Info(Enabled
            ? "DrawStatsService: draw-path histogram armed ([drawstats] every 900 frames)"
            : "DrawStatsService: RenderVertexData unresolved -- stats unavailable");
    }

    [UnmanagedCallersOnly]
    private static void OnBucket(IntPtr scene, int id)
    {
        int prev = _bucket;
        _bucket = id;
        if (_origBucket != null) _origBucket(scene, id);
        _bucket = prev;
    }

    /// <summary>Chain priority on RenderVertexData: below the shadow cull and the draw funnels (#88).</summary>
    internal const int DrawPriority = 10;
    private static IntPtr* _drawSlot;

    /// <summary>Post-draw observer: calls the rest of the chain first, so CURRENT_PROGRAM is valid
    /// (and a swallowed draw is never counted as drawn).</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_drawSlot;
        if (next != null) next(vdo, item, prim, size, offset);
        Observe(vdo, item, (int)prim, size, offset);
    }

    private static void Observe(IntPtr vdo, uint item, int prim, uint size, uint offset)
    {
        if (!Enabled) return;
        if (vdo == IntPtr.Zero) return;

        _draws++;
        if (_bucket >= 0) _bucketDraws++;
        int idx = 0;
        if (*(int*)(vdo + VdoMode) == 1)
        {
            _indexed++;
            idx = size != uint.MaxValue ? (int)size
                : *(int*)(*(IntPtr*)(vdo + VdoElemItems) + item * 8 + 4);
            _idxSum += idx;
        }
        int prog = 0;
        if (Gl.GetIntegerv != null) Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);

        ulong key = (ulong)vdo ^ ((ulong)(uint)prog << 40) ^ 1;   // ^1 so key 0 stays "empty"
        int slot = (int)(key % Groups);
        for (int probe = 0; probe < 16; probe++, slot = (slot + 1) % Groups)
        {
            if (_gKey[slot] == key) { _gDraws[slot]++; _gIdx[slot] += idx; goto pairs; }
            if (_gKey[slot] == 0)
            {
                _gKey[slot] = key; _gDraws[slot] = 1; _gItems[slot] = 0; _gIdx[slot] = idx;
                _gVdo[slot] = vdo; _gProg[slot] = prog;
                goto pairs;
            }
        }
        _gOver++;
        return;
    pairs:
        ulong pk = key ^ ((ulong)item << 48) ^ ((ulong)item * 0x9e3779b97f4a7c15UL);
        int ps = (int)(pk % Pairs);
        for (int probe = 0; probe < 16; probe++, ps = (ps + 1) % Pairs)
        {
            if (_pKey[ps] == pk) return;
            if (_pKey[ps] == 0) { _pKey[ps] = pk; _gItems[slot]++; return; }
        }
        _pOver++;
    }

    /// <summary>Frame tick, called by UiHostService after present. Logs + resets every ~900 frames.</summary>
    public static void EndFrame()
    {
        if (!Enabled) return;
        if (Gl.GetIntegerv == null && _log != null) Gl.Init(_log);   // GL context exists here, not at service init
        if (++_frames < LogEvery || _draws == 0) return;
        int f = _frames;
        // cold path: allocation + sort are fine here
        var order = new int[Groups];
        int n = 0;
        for (int i = 0; i < Groups; i++) if (_gKey[i] != 0) order[n++] = i;
        Array.Sort(order, 0, n, Comparer<int>.Create((a, b) => _gDraws[b] - _gDraws[a]));
        _log?.Info($"DrawStatsService: [drawstats] {(float)_draws / f:F0} draws/frame " +
                   $"({100f * _indexed / _draws:F0}% indexed, {100f * _bucketDraws / _draws:F0}% in-bucket, " +
                   $"avg {(float)_idxSum / (_indexed > 0 ? _indexed : 1):F0} idx), " +
                   $"{n} (vdo,prog) groups{(_gOver + _pOver > 0 ? $", hash-over {_gOver}/{_pOver}" : "")}");
        for (int i = 0; i < n && i < 12; i++)
        {
            int s = order[i];
            _log?.Info($"DrawStatsService: [drawstats]   vdo=0x{(ulong)_gVdo[s]:x} prog={_gProg[s]}: " +
                       $"{(float)_gDraws[s] / f:F1} draws/frame, {_gItems[s]} items, avg {(float)_gIdx[s] / _gDraws[s]:F0} idx");
        }
        _frames = 0; _draws = 0; _indexed = 0; _bucketDraws = 0; _idxSum = 0;
        _gOver = 0; _pOver = 0;
        Array.Clear(_gKey); Array.Clear(_pKey);
    }
}
