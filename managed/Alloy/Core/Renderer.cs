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
    internal static readonly Knob<bool> TileDissolve, Bcn, Hdr, Gl46, OwnedTex, PltCpu, TexSingle, HandleDrop, OwnedGeom,
                                        Spirv, VanillaBridge, HostFs, OwnSkin, EnrollDelta, DumpShaders, GlDebug, EngineZones;
    internal static readonly Knob<int> Border, Oit, OitLayers, TexCache, Bindless, BcnDrip, TexPrefetch, TexDelete, MaxElements,
                                       EnrollBudget, ClusterCap, SwapProbe, GlSync, SkipBuckets;
    internal static readonly Knob<bool> Light, Prepass, AoTest, Retest, HlDiag, HlGlow, Poll, PrepassKey, PrepassFeat, IdTarget,
                                        BlendDepth, GpuMeter, VisList, WashTest, DepthEqual;
    internal static readonly Knob<int> Viz, Threads, Cull, HlChan, OitViz;
    internal static readonly KnobButton Consts, FrameDumpBtn, ShaderDump, Snap;

    /// <summary>The highlight mode the pass reads per frame: the stock glow, or the owned outline + fill.</summary>
    internal static bool OutlineHighlight => Highlight.V == "outline";

    /// <summary>The Renderer status line: which renderer this session is running, and why when it is not
    /// Alloy. The renderer is chosen once at boot (ADR 0018's amendment to ADR 0010), so the line names the
    /// failure clause too -- without it "I set alloy.enabled=0 and nothing changed" reads the same as "this
    /// machine cannot run the Alloy renderer".</summary>
    internal static string Status()
    {
        string s = AlloyGate.Alloy ? "Running: Alloy" : $"Running: Vanilla — {AlloyGate.Reason}";
        if (AlloyGate.Failed) s += $" — the Alloy renderer FAILED: {AlloyGate.CapabilityReason}";
        return s;
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
        G.Section("Geometry");
        Highlight = G.Dropdown("highlight", "Selection highlight", new[] { "glow", "outline" }, "glow",
                               help: "How a selected or hovered object is marked: the stock glow, or an outline with a light fill.");
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
        SkipBuckets = G.Slider("skipbuckets", "skipbuckets", 0, 2, 1, 2, tier: Tier.ChatOnly, effect: Effect.Restart);
        EngineZones = G.Toggle("enginezones", "enginezones", true, Tier.ChatOnly, Effect.Restart);

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
        HlGlow = G.Toggle("hlglow", "hlglow", true, Tier.ChatOnly);
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

        Consts = G.Button("consts", "consts", AlloyRenderer.LogScriptables, Tier.ChatOnly);
        FrameDumpBtn = G.Button("framedump", "framedump", FrameDump.Arm, Tier.ChatOnly);
        ShaderDump = G.Button("shaderdump", "shaderdump", ShaderStages.DumpToLog, Tier.ChatOnly);
        Snap = G.Button("snap", "snap", FrameTap.Arm, Tier.ChatOnly);
    }
}
