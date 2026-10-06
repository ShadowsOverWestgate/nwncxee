using System;
using System.Globalization;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>alloy</c> group (spec §4.3 Renderer), and the storage every Alloy boot gate and world-pass
/// dial reads. <see cref="Enabled"/> replaces the inverted <c>vanilla</c> key; the Restart rows are read through
/// <c>Boot</c> by the gate or pass that latches them (<see cref="AlloyGate"/>, <see cref="OwnedTextures"/>,
/// <see cref="GlContextService"/>, ...); the Live rows are the former <c>/draw</c> dials, read per frame by the
/// pass. Touched from <see cref="RenderCommands.Register"/> so the group declares at Initialize, on the main
/// thread, ahead of every stand-down -- bare <c>/alloy</c> must answer in a session that armed nothing.
/// <para>The <c>viz</c> ceiling is a REAL gate: a value past it used to leave the mode at 0 doing nothing with
/// no line to say so (#129's water views read as "LIGHTVIZ is broken" for exactly that reason); the slider
/// range clamps it, so the echo reports what is actually running.</para></summary>
internal static class Renderer
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Scale, Weld, Lod, GradedBlend;
    internal static readonly Knob<string> Highlight, DrawHooks, DumpTex, SkipTex;
    internal static readonly Knob<bool> TileDissolve, Bcn, Hdr, Gl46, OwnedTex, PltCpu, TexSingle, DdsRead, HandleDrop, OwnedGeom,
                                        Spirv, VanillaBridge, HostFs, OwnSkin, EnrollDelta, DumpShaders, GlDebug, EngineZones,
                                        NwSyncRead, Gui, Movie;
    internal static readonly Knob<int> Border, Oit, OitLayers, TexCache, Bindless, BcnDrip, TexPrefetch, TexDelete, MaxElements, RasterRead, TexStream, TexLoad, ModelLoad, MdlCache,
                                       EnrollBudget, ClusterCap, SwapProbe, GlSync, SkipBuckets;
    internal static readonly Knob<bool> Light, Prepass, AoTest, Retest, HlDiag, Poll, PrepassKey, PrepassFeat, IdTarget,
                                        BlendDepth, GpuMeter, VisList, WashTest, DepthEqual;
    // ---- CX-103/104/105: the scene pipeline's dials ----
    internal static readonly Knob<bool> OwnXform, XformCheck, OwnLod, BucketCheck, CullWatch, WalkDelta;
    internal static readonly Knob<int> XformRefresh, WalkAudit;
    internal static readonly Knob<float> LodHyst;
    internal static readonly Knob<int> Viz, Threads, Cull, HlChan, OitViz;
    internal static readonly KnobButton Consts, FrameDumpBtn, ShaderDump, Snap, Holes, Doors;

    /// <summary>The highlight mode the pass reads per frame: the stock glow, or the owned outline + fill.</summary>
    internal static bool OutlineHighlight => Highlight.V == "outline";

    /// <summary>The Renderer status line: which renderer this session is running, and why when it is not
    /// Alloy. The renderer is chosen once at boot (ADR 0018's amendment to ADR 0010), so the line names the
    /// failure clause too -- without it "I set alloy.enabled=0 and nothing changed" reads the same as "this
    /// machine cannot run the Alloy renderer".</summary>
    internal static string Status()
    {
        string s = AlloyGate.Alloy ? "Running: Alloy"
                 : AlloyGate.Stand == AlloyStand.Idle ? $"World: idle — {AlloyGate.Reason}"
                 : $"Running: Vanilla — {AlloyGate.Reason}";
        if (AlloyGate.Failed) s += $" — the Alloy renderer FAILED: {AlloyGate.CapabilityReason}";
        return s + " — " + GuiPass.Stats() + "; " + NuiFlush.Stats();
    }

    private static string AutoInt(int v) => v < 0 ? "auto" : v.ToString(CultureInfo.InvariantCulture);
    private static (bool, int) ParseAutoInt(string s)
        => s == "auto" ? (true, -1)
         : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? (true, i) : (false, 0);
    private static string AutoOnOff(int v) => v < 0 ? "auto" : v != 0 ? "on" : "off";
    private static (bool, int) ParseAutoOnOff(string s) => s switch
    {
        "auto" or "-1" => (true, -1),
        "on" or "1" => (true, 1),
        "off" or "0" => (true, 0),
        _ => (false, 0),
    };

    static Renderer()
    {
        G = KnobGroup.Declare("alloy", "Graphics", "Renderer", master: "Enable the Alloy renderer",
            masterHelp: "Draw the 3D world with CX's own OpenGL 4.6 renderer: real-time shadows, HDR, grass, post-processing. "
                      + "Off, the game's stock renderer draws everything and every Graphics submenu below is inactive. "
                      + "Takes effect at the next launch.",
            masterEffect: Effect.Restart);
        Enabled = G.Master!;
        G.Status = Status;

        Scale = G.Slider("scale", "Render scale", 0.25f, 2f, 0.05f, 1f,
                         help: "Size of the 3D frame relative to your screen. Below 1 renders fewer pixels and upscales (faster); "
                             + "above 1 supersamples (sharper, slower). The UI is never scaled.");
        Gui = G.Toggle("gui", "Draw the interface with Alloy", true, Tier.Advanced,
                       help: "Menus, panels, text, the minimap and the cursor are drawn by CX's renderer from what the game "
                           + "lays out. Off, the game's own renderer draws them again.");
        G.Section("Geometry");
        Highlight = G.Dropdown("highlight", "Selection highlight", new[] { "glow", "outline" }, "outline",
                               help: "How a selected or hovered object is marked: an outline with a light fill, or a soft glow around the object.");
        Weld = G.Slider("weld", "Smooth-shading crease angle", 0f, 180f, 5f, 60f, "°", Tier.Advanced,
                        help: "Faces meeting at a sharper angle than this keep a hard edge. 0 keeps every model's own normals. "
                            + "Applies to models loaded after the change.");
        Lod = G.Slider("lod", "Cull models smaller than", 0f, 8f, 0.5f, 1f, "px", Tier.Advanced,
                       help: "Distant models whose projected size is under this many pixels are skipped.",
                       changed: AlloyRenderer.InvalidateRecords);
        TileDissolve = G.Toggle("tiledissolve", "Dissolve tile props hiding the camera", true, Tier.Advanced,
                                help: "Off = only walls dissolve, as in the stock game.", changed: AlloyRenderer.InvalidateRecords);
        Border = G.Slider("border", "Neighbouring-area ring", 5, 40, 5, 15, "tiles", Tier.Advanced, Effect.Restart,
                          help: "Tiles drawn past the area edge so the horizon is not empty; the stock game draws 5.");
        G.Section("Transparency");
        Oit = G.Dropdown("oit", "Transparency", new[] { 0, 1, 2 }, 2, new[] { "sorted", "weighted", "linked lists" }, tier: Tier.Advanced,
                         help: "How overlapping transparent surfaces are combined.", changed: AlloyRenderer.InvalidateRecords);
        OitLayers = G.Dropdown("oitlayers", "Transparency layers per pixel", new[] { 1, 2, 3, 4, 6, 8 }, 3, tier: Tier.Advanced,
                               effect: Effect.Restart, help: "Memory reserved for linked-list transparency; ~33 MB per layer at 1080p.");
        G.Section("Memory");
        TexCache = G.Dropdown("texcache", "Texture memory budget", new[] { -1, 512, 1024, 2048, 4096, 8192 }, -1, tier: Tier.Advanced,
                              effect: Effect.Restart, format: AutoInt, parse: ParseAutoInt);
        Bindless = G.Dropdown("bindless", "Bindless textures", new[] { -1, 1, 0 }, -1, tier: Tier.Advanced, effect: Effect.Restart,
                              help: "Auto turns them off on Zink.", format: AutoOnOff, parse: ParseAutoOnOff);
        Bcn = G.Toggle("bcn", "Compress textures on load", true, Tier.Advanced, Effect.Restart,
                       help: "Re-encodes uncompressed textures as BC1/BC3: 3–4× less VRAM, a little load-time CPU.");

        G.Section("");
        Hdr = G.Toggle("hdr", "hdr", true, Tier.ChatOnly, Effect.Restart);
        Gl46 = G.Toggle("gl46", "gl46", true, Tier.ChatOnly, Effect.Restart);
        DrawHooks = G.Dropdown("drawhooks", "drawhooks", new[] { DrawFunnelService.DrawModeBatch, DrawFunnelService.DrawModeStats },
                               DrawFunnelService.DrawModeBatch, tier: Tier.ChatOnly, effect: Effect.Restart);
        OwnedTex = G.Toggle("ownedtex", "ownedtex", true, Tier.ChatOnly, Effect.Restart);
        PltCpu = G.Toggle("pltcpu", "pltcpu", true, Tier.ChatOnly, Effect.Restart);
        BcnDrip = G.Slider("bcndrip", "bcndrip", 1, 64, 1, 1, tier: Tier.ChatOnly, effect: Effect.Restart);
        TexPrefetch = G.Slider("texprefetch", "texprefetch", 0, 64, 1, 2, tier: Tier.ChatOnly, effect: Effect.Restart);
        TexSingle = G.Toggle("texsingle", "texsingle", true, Tier.ChatOnly, Effect.Restart);
        // CX-52 step 5: ms of engine texture builds per frame once the player is placed; 0 = the engine drains its whole
        // queue every frame. Off until a live session reads the [load] line both ways.
        TexStream = G.Slider("texstream", "texstream", 0, 50, 1, 0, tier: Tier.ChatOnly, effect: Effect.Live);
        // CX-52 step 1: off until a live session reads zero MISMATCHED on the dds: note (the ticket's acceptance).
        DdsRead = G.Toggle("ddsread", "ddsread", true, Tier.ChatOnly, Effect.Restart);
        // CX-54 step 1: 0 = the engine reads every texture file on the main thread, 1 = Alloy prefetches each
        // world DDS on the cx-load worker during LoadArea's texture suspend, 2 = the same with every hand-back
        // checked against the engine's own read. Off until a live session reads zero MISMATCHED at 2.
        TexLoad = G.Slider("texload", "texload", 0, 2, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-55 step 1: 0 = the engine reads every model file on the main thread, 1 = Alloy reads a tileset's
        // models on the cx-mdl worker during LoadArea and hands them back at CAuroraInterface::GetResource,
        // 2 = the same with every hand-back checked byte for byte against the engine's own read. Off until a
        // live session reads zero MISMATCHED at 2.
        ModelLoad = G.Slider("modelload", "modelload", 0, 2, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-55 step 3: 0 = every ASCII .mdl is parsed line by line on the main thread, 1 = the engine's own
        // compiler writes each one to <userdir>/cx-models once and every later load of the same bytes is served
        // the compiled binary. Independent of modelload. Off until a live session has eyeballed a compiled area
        // against an uncompiled one: the cache's fidelity is the engine's reader/writer pair, which this cannot
        // prove offline (CX-55 §3).
        MdlCache = G.Slider("mdlcache", "mdlcache", 0, 1, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-52 step 2: 0 = the engine decodes TGA/PLT, 1 = the engine's decode stands and Alloy's is checked
        // beside it, 2 = Alloy's decode is what the engine gets (live-clean 2026-09-26: 684 TGA + 830 PLT, 0 errors).
        RasterRead = G.Slider("rasterread", "rasterread", 0, 2, 1, 2, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-54 step 3: Alloy reads the NWSync store itself (its own read-only SQLite connections + zstd), so a
        // worker can fetch a manifest resource without the main-thread resource manager. Off until a live session
        // reads zero errors on the nwsyncread: note.
        NwSyncRead = G.Toggle("nwsyncread", "nwsyncread", false, Tier.ChatOnly, Effect.Restart);
        TexDelete = G.Slider("texdelete", "texdelete", -1, 1, 1, -1, tier: Tier.ChatOnly, effect: Effect.Restart);
        HandleDrop = G.Toggle("handledrop", "handledrop", true, Tier.ChatOnly, Effect.Restart);
        OwnedGeom = G.Toggle("ownedgeom", "ownedgeom", true, Tier.ChatOnly, Effect.Restart);
        MaxElements = G.Slider("maxelements", "maxelements", 0, 1 << 24, 1, OwnedBuffers.MaxElementsDefault, tier: Tier.ChatOnly, effect: Effect.Restart);
        Spirv = G.Toggle("spirv", "spirv", false, Tier.ChatOnly, Effect.Restart);
        VanillaBridge = G.Toggle("vanillabridge", "vanillabridge", true, Tier.ChatOnly, Effect.Restart);
        HostFs = G.Toggle("hostfs", "hostfs", true, Tier.ChatOnly, Effect.Restart);
        OwnSkin = G.Toggle("ownskin", "ownskin", true, Tier.ChatOnly, Effect.Restart);
        EnrollBudget = G.Slider("enrollbudget", "enrollbudget", 0, 100000, 1, AlloyRenderer.EnrollBudgetDefault, tier: Tier.ChatOnly, effect: Effect.Restart);
        EnrollDelta = G.Toggle("enrolldelta", "enrolldelta", true, Tier.ChatOnly, Effect.Restart);
        ClusterCap = G.Slider("clustercap", "clustercap", 8, 128, 1, 128, tier: Tier.ChatOnly, effect: Effect.Restart);
        GradedBlend = G.Slider("gradedblend", "gradedblend", 0f, 1f, 0.01f, 0.15f, tier: Tier.ChatOnly, effect: Effect.Restart);
        DumpTex = G.Text("dumptex", "dumptex", "", Tier.ChatOnly, Effect.Restart);
        SkipTex = G.Text("skiptex", "skiptex", "", Tier.ChatOnly, Effect.Restart);
        DumpShaders = G.Toggle("dumpshaders", "dumpshaders", false, Tier.ChatOnly, Effect.Restart);
        GlDebug = G.Toggle("gldebug", "gldebug", false, Tier.ChatOnly, Effect.Restart);
        SwapProbe = G.Slider("swapprobe", "swapprobe", 0, 100000, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        GlSync = G.Slider("glsync", "glsync", 0, 1, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        SkipBuckets = G.Slider("skipbuckets", "skipbuckets", 0, 3, 1, 3, tier: Tier.ChatOnly, effect: Effect.Restart);
        EngineZones = G.Toggle("enginezones", "enginezones", true, Tier.ChatOnly, Effect.Restart);
        // CX-99: 0 hands every movie frame back to the engine's own quad. Live, because a movie is the one
        // surface a player can be stuck staring at with no way to restart into a different config.
        Movie = G.Toggle("movie", "movie", true, Tier.ChatOnly);

        // The world pass's live dials (the former /draw): a dial can change what a persistent record MEANS
        // (#242), and one dirty-all on a chat command is free, so every one of them invalidates.
        Light = G.Toggle("light", "light", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Prepass = G.Toggle("prepass", "prepass", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Viz = G.Slider("viz", "viz", 0, AlloyRenderer.LightVizMax, 1, 0, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Threads = G.Slider("threads", "threads", 0, StagePool.MaxChunks - 1, 1, AlloyRenderer.DefThreads, tier: Tier.ChatOnly,
                           changed: () => { StagePool.Rearm(); AlloyRenderer.InvalidateRecords(); });
        AoTest = G.Toggle("aotest", "aotest", false, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Cull = G.Slider("cull", "cull", CullPass.ModeOff, CullPass.ModeHiZ, 1, CullPass.ModeHiZ, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Retest = G.Toggle("retest", "retest", CullPass.DefRetest, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        HlDiag = G.Toggle("hldiag", "hldiag", false, Tier.ChatOnly);
        HlChan = G.Slider("hlchan", "hlchan", 0, 31, 1, CX.Alloy.Highlight.DefHlChannels, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Poll = G.Toggle("poll", "poll", false, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        PrepassKey = G.Toggle("prepasskey", "prepasskey", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        PrepassFeat = G.Toggle("prepassfeat", "prepassfeat", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        DepthEqual = G.Toggle("depthequal", "depthequal", true, Tier.ChatOnly);
        IdTarget = G.Toggle("idtarget", "idtarget", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        BlendDepth = G.Toggle("blenddepth", "blenddepth", true, Tier.ChatOnly);
        GpuMeter = G.Toggle("gpumeter", "gpumeter", true, Tier.ChatOnly, changed: () => GpuQuery.Enabled = GpuMeter!.V);
        GpuQuery.Enabled = GpuMeter.V;
        VisList = G.Toggle("vislist", "vislist", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        WashTest = G.Toggle("washtest", "washtest", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        OitViz = G.Slider("oitviz", "oitviz", 0, 5, 1, 0, tier: Tier.ChatOnly);

        // ---- CX-103/104/105 ----
        // CX-103: Alloy composes every world matrix. 0 reads Scene::AddPartsTreeToDrawBuckets' vp+0x18 again,
        // which is the A/B -- and shows the fault the ticket removed, because the Fault 1 placement latch went
        // with the chase: a gob the engine never placed draws at the area's (0,0) corner.
        OwnXform = G.Toggle("ownxform", "ownxform", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        // The one remaining read of vp+0x18: compose AND read, and count the disagreements on [alloy].
        XformCheck = G.Toggle("xformcheck", "xformcheck", false, Tier.ChatOnly);
        // The flicker hunt's GPU half: every colour dispatch's kept total, read back fenced and never waited
        // on. Off by default -- a per-frame read of a GPU-written buffer is a real wait on Mesa.
        CullWatch = G.Toggle("cullwatch", "cullwatch", false, Tier.ChatOnly);
        // The net under the cheap change detection while the ENGINE still animates (CX-106/CX-107 will call
        // GobMotion.Mark instead): every gob is recomposed at least once every N frames.
        XformRefresh = G.Slider("xformrefresh", "xformrefresh", 1, 64, 1, 8, tier: Tier.ChatOnly);
        // CX-105: Alloy chooses each gob's LOD, with hysteresis vanilla has none of. 0 hands the choice back
        // AND puts SceneWalk back to enumerating all three roots per gob.
        OwnLod = G.Toggle("ownlod", "ownlod", true, Tier.ChatOnly, changed: GobLod.Reset);
        LodHyst = G.Slider("lodhyst", "lodhyst", 0f, 1f, 0.01f, 0.1f, tier: Tier.ChatOnly);
        // CX-104: the engine draw buckets as a VALIDATOR (ADR 0018 demotes them to exactly this). Off by
        // default now that nothing in the frame reads them -- on, it walks Scene+0x208 once per period and
        // reports the parts the engine classified that Alloy has no record for.
        BucketCheck = G.Toggle("bucketcheck", "bucketcheck", false, Tier.ChatOnly);
        // A membership change costs ONE gob's tree, not the area's: Gob::SetScene is the sole writer of the
        // engine's global epoch, so every bump can be answered per gob. 0 is the A/B -- every bump re-walks
        // the whole scene, which is what the 9,7ms `[enum] walk=` and its hitch blame were measured on.
        WalkDelta = G.Toggle("walkdelta", "walkdelta", true, Tier.ChatOnly);
        // ...and the validator for it: every N frames a full walk runs anyway and `[enum] audit misses=` counts
        // what it found that the deltas had not. It must read 0, and it is the ONLY instrument that would show
        // an edge nobody has found yet, so the default is short enough to be a net rather than a formality (one
        // ~5ms walk per 5s). If a long session reads 0, raise it; if it reads nonzero, the edge the split
        // (added / swept gob / swept tile) names gets its own notice -- the cadence is not the fix. 0 = off.
        WalkAudit = G.Slider("walkaudit", "walkaudit", 0, 7200, 60, 300, unit: "frames", tier: Tier.ChatOnly);

        Consts = G.Button("consts", "consts", AlloyRenderer.LogScriptables, Tier.ChatOnly);
        FrameDumpBtn = G.Button("framedump", "framedump", FrameDump.Arm, Tier.ChatOnly);
        G.Button("bench", "bench", AlloyRenderer.BenchArm, Tier.ChatOnly,
                 help: "Hold the camera still: steps the GPU-cost settings, measures each, restores them, logs a [bench] table.");
        ShaderDump = G.Button("shaderdump", "shaderdump", ShaderStages.DumpToLog, Tier.ChatOnly);
        Snap = G.Button("snap", "snap", FrameTap.Arm, Tier.ChatOnly);
        // CX-92: the per-instance hole dump -- which part of which creature Alloy is not drawing, and why.
        Holes = G.Button("holes", "holes", AlloyRenderer.LogHoles, Tier.ChatOnly);
        // CX-89: every visible door's fade alpha against mindooralpha -- the instrument for "doors still
        // take a click", which no screenshot shows.
        Doors = G.Button("doors", "doors", DoorFade.Arm, Tier.ChatOnly);
    }
}
