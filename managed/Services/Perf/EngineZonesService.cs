using System;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Perf;

/// <summary>#336 step 5: split the [zones] line's <c>other</c> column. Four timing hooks on the engine main
/// loop's non-render steps (see <see cref="FrameZones.Zone.Net"/>); each calls the original through its
/// trampoline between two timestamps and returns whatever it returned. The handlers forward the argument
/// register untouched -- one integer or float each, no by-value structs (the mangled names say so) -- and
/// return <c>long</c> so a void, int or pointer result rides through rax intact. Hot, allocation-free.</summary>
public sealed unsafe class EngineZonesService : IExtenderService
{
    private static delegate* unmanaged<IntPtr, int, long> _net;
    private static delegate* unmanaged<IntPtr, uint, long> _res;
    private static delegate* unmanaged<IntPtr, long> _ai;
    private static delegate* unmanaged<IntPtr, float, long> _gui;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!Settings.Bool("enginezones", true)) { log.Info("EngineZones: off (config enginezones=0)"); return; }
        _net = (delegate* unmanaged<IntPtr, int, long>)hooks.Install(
            SymbolMap.NetProcessReceivedFrames, (IntPtr)(delegate* unmanaged<IntPtr, int, long>)&OnNet);
        _res = (delegate* unmanaged<IntPtr, uint, long>)hooks.Install(
            SymbolMap.ResManUpdate, (IntPtr)(delegate* unmanaged<IntPtr, uint, long>)&OnRes);
        _ai = (delegate* unmanaged<IntPtr, long>)hooks.Install(
            SymbolMap.ClientAiUpdateState, (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnAi);
        _gui = (delegate* unmanaged<IntPtr, float, long>)hooks.Install(
            SymbolMap.GuiUpdateAndRender, (IntPtr)(delegate* unmanaged<IntPtr, float, long>)&OnGui);
        _demand = (delegate* unmanaged<IntPtr, IntPtr, long>)hooks.Install(
            SymbolMap.ResManDemand, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, long>)&OnDemand);
        _bsp = (delegate* unmanaged<IntPtr, long>)hooks.Install(
            SymbolMap.ManageSceneBSP, (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnBsp);
        _buckets = (delegate* unmanaged<IntPtr, long>)hooks.Install(
            SymbolMap.AddPartsToDrawBuckets, (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnBuckets);
        // 2 by default (2026-09-06, live: Buckets 0,7 -> 0,2-0,3ms, gob parts intact); 0 is the A/B, 1 the
        // experiment that empties the world of gob parts.
        int skip = Settings.Int("skipbuckets", 2, 0, 2);
        _skipBuckets = skip == 1;
        _bucketsCadence = skip == 2;
        if (_bucketsCadence)
        {
            _meshBuckets = (byte*)Native.Sym(SymbolMap.MeshBuckets); _fadeBuckets = (byte*)Native.Sym(SymbolMap.FadeBuckets);
            _capBuckets = (byte*)Native.Sym(SymbolMap.CapBuckets); _staticShadowBucket = (byte*)Native.Sym(SymbolMap.StaticShadowBucket);
            _meshShadowBucket = (byte*)Native.Sym(SymbolMap.MeshShadowBucket);
            string missing = (_meshBuckets == null ? " meshbuckets" : "") + (_fadeBuckets == null ? " fadebuckets" : "")
                           + (_capBuckets == null ? " capbuckets" : "") + (_staticShadowBucket == null ? " staticshadowbucket" : "")
                           + (_meshShadowBucket == null ? " meshshadowbucket" : "");
            if (missing.Length > 0)
            { _bucketsCadence = false; log.Info($"EngineZones: skipbuckets=2 refused -- unresolved:{missing}"); }
        }
        log.Info("EngineZones: timing the engine loop's net / resman / AI / GUI steps and resource demands into [zones] (config enginezones=0 disables)" +
                 (_skipBuckets ? " -- EXPERIMENT skipbuckets=1: the world scene's AddPartsToDrawBuckets is SKIPPED while Alloy draws (expect no gob parts)" : "") +
                 (_bucketsCadence ? $" -- skipbuckets=2: the tile-part loops of AddPartsToDrawBuckets run every {BucketsEvery}th frame while Alloy draws (skipbuckets=0 restores them)" : ""));
    }

    private static delegate* unmanaged<IntPtr, IntPtr, long> _demand;
    private static delegate* unmanaged<IntPtr, long> _bsp, _buckets;

    /// <summary>The world scene's render preparation, timed and not subtracted. A GUI 3D panel's Scene has
    /// no tile BSP root and is left out, so the two zones read the world walk alone.</summary>
    [UnmanagedCallersOnly]
    private static long OnBsp(IntPtr scene)
    {
        bool world = *(IntPtr*)(scene + Sowx.Alloy.SceneLayout.SceneBspRoot) != IntPtr.Zero;
        long t = world ? FrameZones.Now : 0;
        long r = _bsp != null ? _bsp(scene) : 0;
        if (world) FrameZones.Add(FrameZones.Zone.Bsp, t);
        return r;
    }

    /// <summary>EXPERIMENT (docs/research/engine-scene-walk-bypass.md §5 step 1): <c>skipbuckets=1</c> in
    /// sowx.cfg skips the world scene's draw-bucket build while Alloy draws the frame. Expected to freeze
    /// every gob part's transform (the build is the sole writer of the model matrix Alloy draws with), which
    /// is the measurement: Ai drops by the Buckets figure, and what breaks names the load-bearing half.
    /// Never on a GUI panel's scene and never while the engine renders (vanilla=1).</summary>
    private static bool _skipBuckets;
    private static long _bucketsSkipped;
    /// <summary><c>skipbuckets=2</c>: the six flat loops over the BSP walk's tile-part lists (matrix rewrite
    /// of STATIC parts plus the filing of buckets nothing reads) run every <see cref="BucketsEvery"/>th frame;
    /// the gob loop (ProcessGob, the attached-hierarchy sync, the part-tree matrix walk creatures and
    /// placeables live by) and the beam loop run every frame. Done by zeroing the lists' counts ahead of
    /// the call on the other frames; the visit callback refills them next frame. A freshly spawned tile's
    /// parts carry no matrix until the next full frame. ponytail: a fixed cadence, no knob.</summary>
    private static bool _bucketsCadence;
    private const int BucketsEvery = 8;
    private static int _bucketsFrame;
    private static byte* _meshBuckets, _fadeBuckets, _capBuckets, _staticShadowBucket, _meshShadowBucket;

    /// <summary>meshbuckets and fadebuckets are <c>List&lt;Bucket&gt;</c> objects in .bss -- CExoArrayList
    /// {items at +0, count at +8, alloc at +0xc} -- whose 16-byte {items, count} pairs live on the HEAP,
    /// indexed by texture id (ClearBuckets @0x47f4e0, CheckBucketArraySize @0x47f420, alloc &gt;= 5000). The
    /// first cut of this treated the .bss symbol as the inline pair array and zeroed past the object into
    /// AurGetCompressedImage's function pointer (two crashes, 2026-09-06). Every count on the heap pairs is
    /// cleared, as the engine's own clear does; the three single lists carry their count at +8 in .bss.</summary>
    private static void ThinTileLists()
    {
        if (_meshBuckets == null) return;
        ClearPairCounts(_meshBuckets);
        ClearPairCounts(_fadeBuckets);
        *(int*)(_capBuckets + 8) = 0;
        *(int*)(_staticShadowBucket + 8) = 0;
        *(int*)(_meshShadowBucket + 8) = 0;
    }

    private static void ClearPairCounts(byte* list)
    {
        byte* pairs = *(byte**)list;
        int alloc = *(int*)(list + 0xc);
        if (pairs == null) return;
        for (int k = 0; k < alloc; k++) *(int*)(pairs + (long)k * 16 + 8) = 0;
    }

    [UnmanagedCallersOnly]
    private static long OnBuckets(IntPtr scene)
    {
        bool world = *(IntPtr*)(scene + Sowx.Alloy.SceneLayout.SceneBspRoot) != IntPtr.Zero;
        if (world && _skipBuckets && Sowx.Alloy.AlloyGate.Alloy) { _bucketsSkipped++; return 0; }
        if (world && _bucketsCadence && Sowx.Alloy.AlloyGate.Alloy && (++_bucketsFrame % BucketsEvery) != 0) ThinTileLists();
        long t = world ? FrameZones.Now : 0;
        long r = _buckets != null ? _buckets(scene) : 0;
        if (world) FrameZones.Add(FrameZones.Zone.Buckets, t);
        return r;
    }
    private static int _demandDepth;

    /// <summary>The on-demand resource load, wherever the engine asks for it (a model on first sight, a
    /// texture, an animation). Demands nest -- a model demands its textures -- so only the outermost call
    /// is bracketed and counted. Nested zone: reported for shape inside Ai, not subtracted.</summary>
    [UnmanagedCallersOnly]
    private static long OnDemand(IntPtr self, IntPtr res)
    {
        long t = _demandDepth++ == 0 ? FrameZones.Now : 0;
        long r = _demand != null ? _demand(self, res) : 0;
        if (--_demandDepth == 0) { FrameZones.Add(FrameZones.Zone.Demand, t); FrameZones.Count(FrameZones.Ev.Demands); }
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnNet(IntPtr self, int a)
    {
        long t = FrameZones.Now;
        long r = _net != null ? _net(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Net, t);
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnRes(IntPtr self, uint a)
    {
        long t = FrameZones.Now;
        long r = _res != null ? _res(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Res, t);
        return r;
    }

    /// <summary>The first live read had <c>Ai</c> at 10ms with <c>other</c> at -7: the AI update is where
    /// the engine renders the scene from, so every renderer bracket sits inside it. Their ticks are
    /// subtracted, and what is left is the update itself.</summary>
    [UnmanagedCallersOnly]
    private static long OnAi(IntPtr self)
    {
        long t = FrameZones.Now;
        long inner0 = Inner();
        long r = _ai != null ? _ai(self) : 0;
        FrameZones.Add(FrameZones.Zone.Ai, t, Inner() - inner0);
        return r;
    }

    private static long Inner()
        => FrameZones.Ticks(FrameZones.Zone.Enroll) + FrameZones.Ticks(FrameZones.Zone.Shadow)
           + FrameZones.Ticks(FrameZones.Zone.Shim) + FrameZones.Ticks(FrameZones.Zone.Alloy)
           + FrameZones.Ticks(FrameZones.Zone.Ui) + FrameZones.Ticks(FrameZones.Zone.Present)
           + FrameZones.Ticks(FrameZones.Zone.Bcn) + FrameZones.Ticks(FrameZones.Zone.Prebuild);   // subtracted from Enroll already; they must not land in Ai instead

    [UnmanagedCallersOnly]
    private static long OnGui(IntPtr self, float dt)
    {
        long t = FrameZones.Now;
        long r = _gui != null ? _gui(self, dt) : 0;
        FrameZones.Add(FrameZones.Zone.Gui, t);
        return r;
    }
}
