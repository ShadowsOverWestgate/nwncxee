using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: PROGRAM BUILD. Linking the Alloy stages -- fullbright, depth, lit and water -- from the embedded text or a content override (#92), and rebuilding from the embedded text whenever an override fails a ShaderContract predicate. Cold: once per program per context.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Build the water variant. A failure disarms Alloy water for the session -- the engine's own
    /// bucket-6 walk (with its own framebuffer copy) then draws every water surface, which is vanilla.</summary>
    private static bool EnsureWaterProgram()
    {
        if (_progWater != 0) return true;
        if (_waterFailed) return false;
        if (!FrameSnapshot.On) { _waterFailed = true; return false; }
        // #147: four stages, all four overridable (ADR 0012) -- water is the one class that TESSELLATES,
        // so its evaluation stage, not its vertex stage, is what projects.
        string waterFs = ShaderStages.Source(ShdWaterFs);
        uint prog = GlProgram.Graphics(ShdWaterVs, ShaderStages.Source(ShdWaterVs),
                                       ShdWaterTcs, ShaderStages.Source(ShdWaterTcs),
                                       ShdWaterTes, ShaderStages.Source(ShdWaterTes), ShdWaterFs, waterFs,
                                       _log, "AlloyRenderer water program",
                                       out string wvs, out string wtc, out string wte, out string wfs);
        // An override that declares the wrong per-draw record reads every draw out of a neighbour's slot --
        // the #120 failure, and silent. #132 adds the colour space to the same guard, for the same reason.
        // Same pair the fullbright and lit programs take. #161: all four stages declare the record, so all
        // four are counted -- the tessellation pair reads it to build the wave field.
        if (prog != 0 && !(ShaderContract.RecordSafe(wvs) && ShaderContract.RecordSafe(wtc) && ShaderContract.RecordSafe(wte) && ShaderContract.RecordSafe(wfs)
                           && ShaderContract.LinearSafe(wfs)
                           // #208: ...and the four samplers this program binds units to. A dropped uProbe
                           // leaves a samplerCube on unit 0 beside the diffuse: INVALID_OPERATION, every
                           // water draw dropped, nothing in the log.
                           && ShaderContract.SlotSafe(wfs, "uTex", "uFbColor", "uFbDepth", "uProbe")))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("AlloyRenderer: water override declares the wrong per-draw record or still " +
                       "encodes to display space -- rebuilding every water stage from the embedded default");
            prog = GlProgram.Tessellated(FrameSnapshot.Vs, FrameSnapshot.Tcs, FrameSnapshot.Tes, waterFs,
                                         _log, "AlloyRenderer water program (embedded)");
        }
        if (prog == 0)
        {
            _waterFailed = true;
            _log?.Info("AlloyRenderer: water program unavailable -- the engine draws water itself");
            return false;
        }
        _progWater = prog;
        FrameSnapshot.ClampTess();
        // One patch is one triangle: the Alloy pass hands the tessellator the engine's own index buffer.
        if (Gl.PatchParameteri != null) Gl.PatchParameteri(Gl.PATCH_VERTICES, 3);
        _watVp = GlProgram.Loc(prog, "uVP"); _watBase = GlProgram.Loc(prog, "uDrawBase");
        _watTex = GlProgram.Loc(prog, "uTex"); _watCut = GlProgram.Loc(prog, "uAlphaCut");
        _watAmbient = GlProgram.Loc(prog, "uAreaAmbient"); _watDiffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        _watDir = GlProgram.Loc(prog, "uAreaDir");
        _watGrid = GlProgram.Loc(prog, "uGrid"); _watTile = GlProgram.Loc(prog, "uTile");
        _watZ = GlProgram.Loc(prog, "uZ"); _watFalloff = GlProgram.Loc(prog, "uFalloff");
        _watWrap = GlProgram.Loc(prog, "uWrap");
        _watAtten = GlProgram.Loc(prog, "uAtten"); _watHot = GlProgram.Loc(prog, "uHot");
        _watWrapN = GlProgram.Loc(prog, "uWrapN");
        _watViz = GlProgram.Loc(prog, "uViz"); _watEye = GlProgram.Loc(prog, "uEyePos");
        _watSunTex = GlProgram.Loc(prog, "uSunShadow"); _watSunVp = GlProgram.Loc(prog, "uSunVp");
        _watSunTexel = GlProgram.Loc(prog, "uSunTexel"); _watSunBias = GlProgram.Loc(prog, "uSunBias");
        _watSunCount = GlProgram.Loc(prog, "uSunCascades");
        _watCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _watCubeLight = GlProgram.Loc(prog, "uLocalLight");
        _watCubePlane = GlProgram.Loc(prog, "uLocalPlane"); _watCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _watCubeSoft = GlProgram.Loc(prog, "uLocalSoft");
        _watProbe = GlProgram.Loc(prog, "uProbe"); _watProbeMips = GlProgram.Loc(prog, "uProbeMips");
        _watProbeGain = GlProgram.Loc(prog, "uProbeGain"); _watSceneEnv = GlProgram.Loc(prog, "uSceneEnv");
        _watCapture = GlProgram.Loc(prog, "uCapture");
        _watFbColor = GlProgram.Loc(prog, "uFbColor"); _watFbDepth = GlProgram.Loc(prog, "uFbDepth");
        _watNearFar = GlProgram.Loc(prog, "uNearFar");
        _watView = GlProgram.Loc(prog, "uView"); _watProj = GlProgram.Loc(prog, "uProj");
        _watDials = GlProgram.Loc(prog, "uWaterDials");
        _watWaveTime = GlProgram.Loc(prog, "uWaveTime"); _watTess = GlProgram.Loc(prog, "uTessDials");
        _watWind = GlProgram.Loc(prog, "uAreaWind");
        _watFeat = FeatLocs(prog);
        // Sampler units, bound once: a sampler left at its default points at unit 0, and two sampler types
        // on one image unit is INVALID_OPERATION -- the draw is dropped, silently as far as the frame knows.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_watTex, 0);
        Gl.Uniform1i(_watSunTex, (int)SunUnit);
        Gl.Uniform1i(_watCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_watProbe, (int)EnvProbe.ProbeUnit);
        Gl.Uniform1i(_watFbColor, (int)FrameSnapshot.ColorUnit);
        Gl.Uniform1i(_watFbDepth, (int)FrameSnapshot.DepthUnit);
        _log?.Info("AlloyRenderer: water program ready (/water on 0 disables) -- " + FrameSnapshot.Describe());
        return true;
    }

    /// <summary>Throw away a program built from a stale content override and rebuild it from the embedded
    /// stages. Preferred over standing the pass down: the embedded source is the one that agrees with the
    /// record the CPU stages, so the frame is CORRECT here and merely un-restyled -- and the override's
    /// author gets a line naming the file to update rather than a black world to bisect. Cold: once per
    /// program per session.</summary>
    private static uint RebuildEmbedded(uint stale, string vs, string fs, string what, ref string vsSrc)
    {
        Gl.DeleteProgram(stale);
        _log?.Info($"AlloyRenderer: a content .shd for the {what} program disagrees with the pass -- either " +
                   $"it declares a per-draw record that is not the {DrawFloats * sizeof(float)}-byte one being " +
                   "staged (#92 override vs #120's grown SowxDraw), or it predates #126 and carries no " +
                   "uAlphaCut/per-draw alpha (which would render every cutout and blended surface solid). " +
                   "Ignoring it and building the embedded stages, which agree. Regenerate the override from " +
                   "the current embedded stage to get it back.");
        vsSrc = vs;
        return GlProgram.Graphics(vs, fs, _log, $"AlloyRenderer {what} program (embedded)");
    }

    /// <summary>#179: consume a pending relink request. Called at the top of the program ensure, i.e. inside
    /// the owned 4.6 bracket -- the only place a GL name may be deleted. Dropping the handles is the whole
    /// mechanism: every Ensure below rebuilds from source when its handle is zero.</summary>
    private static void TakeProgramRebuild()
    {
        if (!_rebuildPrograms) return;
        _rebuildPrograms = false;
        // Only the stages that bake $BAND$ in. The fullbright and depth stages carry no lighting at all, so
        // relinking them would be a hitch that changes nothing.
        bool any = false;
        if (_progLit46 != 0) { Gl.DeleteProgram(_progLit46); _progLit46 = 0; any = true; }
        if (_progWater != 0) { Gl.DeleteProgram(_progWater); _progWater = 0; any = true; }
        // Nothing built yet means nothing to relink -- AlloyLights.SelfCheck drives the shadeband dial at
        // boot, so the flag is pending on the first frame of every session. The line must report a player
        // action, not that one.
        if (any) _log?.Info("AlloyRenderer: relinking the lit and water stages -- a compile-time dial moved");
    }

    private static bool EnsureProgram()
    {
        TakeProgramRebuild();
        if (_prog46 == 0 && !_progFailed)
        {
            uint prog = GlProgram.Graphics(ShdFullbrightVs, ShaderStages.Source(ShdFullbrightVs),
                                           ShdFullbrightFs, ShaderStages.Source(ShdFullbrightFs),
                                           _log, "AlloyRenderer fullbright program",
                                           out string vsSrc, out string fsSrc);
            if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                               && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)))
            {
                prog = RebuildEmbedded(prog, OwnedVs, OwnedFs, "fullbright", ref vsSrc);
            }
            _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
            if (prog == 0) { _progFailed = true; return false; }
            _prog46 = prog;
            _locVp = GlProgram.Loc(prog, "uVP"); _locBase = GlProgram.Loc(prog, "uDrawBase");
            _locTex = GlProgram.Loc(prog, "uTex"); _locViz = GlProgram.Loc(prog, "uViz");
            _locCut = GlProgram.Loc(prog, "uAlphaCut");
            _locFeat = FeatLocs(prog);
            _log?.Info("AlloyRenderer: fullbright program ready (4.6 context)");
        }
        if (_prog46 == 0) return false;
        if (_lighting && _progLit46 == 0 && !_litFailed) EnsureLitProgram();
        return true;
    }

    /// <summary>Build the depth-only program. A failure disarms the prepass for the session and the colour
    /// pass keeps its pre-#119 behaviour -- shading every fragment -- which is slower, never wrong.</summary>
    private static bool EnsureDepthProgram()
    {
        if (_progDepth != 0) return true;
        if (_depthFailed) return false;
        // A content vertex override (#92) replaces the stage wholesale, so what SelfCheck asserts about the
        // embedded constants is no longer what runs -- and a rewritten association loses colour-pass fragments
        // to LEQUAL as speckled holes with nothing in the log. Checked on the source that ACTUALLY compiled,
        // not on whether an override exists at all: every real deployment ships these shaders, and refusing
        // the pass on their mere presence would mean it never runs anywhere it matters.
        if (!_vsDepthSafe)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: a content vertex .shd does not carry `" + ClipExpr +
                       "` under `invariant gl_Position;` -- no depth prepass (update the override to match " +
                       "the embedded stage, or the colour pass would lose fragments to LEQUAL)");
            return false;
        }
        if (Gl.ColorMask == null)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: glColorMask unresolved -- no depth prepass");
            return false;
        }
        uint prog = GlProgram.Graphics(DepthVs, DepthFs, _log, "AlloyRenderer depth prepass program");
        if (prog == 0)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: depth prepass program unavailable -- the colour pass shades every fragment");
            return false;
        }
        _progDepth = prog;
        _depthVp = GlProgram.Loc(prog, "uVP"); _depthBase = GlProgram.Loc(prog, "uDrawBase");
        // #124's second stage. A failure here is NOT fatal to the prepass -- dissolving batches simply go
        // back to being left out of it, which is what they did before the fix.
        uint keyProg = GlProgram.Graphics(DepthKeyVs, DepthKeyFs, _log,
                                          "AlloyRenderer keyhole depth prepass program");
        if (keyProg != 0)
        {
            _progDepthKey = keyProg;
            _depthKeyVp = GlProgram.Loc(keyProg, "uVP");
            _depthKeyBase = GlProgram.Loc(keyProg, "uDrawBase");
            _depthKeyFeat = FeatLocs(keyProg);
        }
        _log?.Info("AlloyRenderer: depth prepass ready (/draw prepass 0 disables)" +
                   (keyProg != 0 ? "" : " -- WITHOUT the keyhole stage, so dissolving batches stay out of it"));
        return true;
    }

    /// <summary>Build the lit variant. A failure disarms lit ownership for the session (and the next
    /// enrollment hands every lit part back to the engine) -- never a silent fallback to fullbright, which
    /// would look worse than vanilla.</summary>
    private static void EnsureLitProgram()
    {
        // #153: decide bindless BEFORE assembling text -- the transform must apply to the same source the
        // program is built from, and to nothing when the context cannot honour it.
        if (!_bindlessProbed)
        {
            _bindlessProbed = true;
            // Config bucket (ADR 0013): a capability gate that decides the lit stage's BUFFER LAYOUT, so it
            // is settled before the program is assembled and cannot be a command.
            _bindless = Settings.Bool("bindless", true) && HasBindless();
            _log?.Info(_bindless
                ? "AlloyRenderer: ARB_bindless_texture armed -- lit batches merge across textures (bindless=0 in sowx.cfg reverts)"
                : "AlloyRenderer: bindless texturing unavailable or vetoed -- per-batch texture binds");
        }
        string litFs = ShaderStages.Source(ShdLitFs);
        if (_bindless) litFs = BindlessFs(litFs);
        uint prog = GlProgram.Graphics(ShdLitVs, ShaderStages.Source(ShdLitVs), ShdLitFs, litFs,
                                       _log, "AlloyRenderer clustered lit program",
                                       out string vsSrc, out string fsSrc);
        if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                           && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)
                           // #208: ...and the sampler declarations the unit table binds against, which only
                           // the compiled source can answer for. Bound-sampler check, so bindless skips it.
                           && (_bindless || ShaderContract.LitSlotSafe(fsSrc))
                           && (!_bindless || fsSrc.Contains("SowxTexH"))))
        {
            prog = RebuildEmbedded(prog, OwnedLitVs, litFs, "clustered lit", ref vsSrc);
        }
        // a content override without the handle block compiled fine but still samples bound units: the
        // C# side must keep binding for it, so the whole bindless path stands down for the session
        if (prog != 0 && _bindless && !fsSrc.Contains("SowxTexH") && !litFs.Contains("SowxTexH"))
        {
            _bindless = false;
            _log?.Info("AlloyRenderer: lit stage carries no handle block -- bindless stands down, binds resume");
        }
        _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
        if (prog == 0)
        {
            _litFailed = true;
            _sinceEnroll = EnrollEvery;   // re-enroll at once so lit parts revert to the engine's own draw
            _log?.Info("AlloyRenderer: clustered lit program unavailable -- lit parts revert to engine drawing");
            return;
        }
        _progLit46 = prog;
        _litVp = GlProgram.Loc(prog, "uVP"); _litBase = GlProgram.Loc(prog, "uDrawBase");
        _litTex = GlProgram.Loc(prog, "uTex");
        _litCut = GlProgram.Loc(prog, "uAlphaCut");
        _litAmbient = GlProgram.Loc(prog, "uAreaAmbient"); _litDiffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        _litDir = GlProgram.Loc(prog, "uAreaDir");
        _litGrid = GlProgram.Loc(prog, "uGrid"); _litTile = GlProgram.Loc(prog, "uTile");
        _litZ = GlProgram.Loc(prog, "uZ"); _litFalloff = GlProgram.Loc(prog, "uFalloff");
        _litWrap = GlProgram.Loc(prog, "uWrap");
        _litAtten = GlProgram.Loc(prog, "uAtten"); _litHot = GlProgram.Loc(prog, "uHot");
        _litWrapN = GlProgram.Loc(prog, "uWrapN");
        _litViz = GlProgram.Loc(prog, "uViz");
        _litEye = GlProgram.Loc(prog, "uEyePos");
        _litSunTex = GlProgram.Loc(prog, "uSunShadow"); _litSunVp = GlProgram.Loc(prog, "uSunVp");
        _litSunTexel = GlProgram.Loc(prog, "uSunTexel"); _litSunBias = GlProgram.Loc(prog, "uSunBias");
        _litSunCount = GlProgram.Loc(prog, "uSunCascades");
        _litCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _litCubeLight = GlProgram.Loc(prog, "uLocalLight");
        _litCubePlane = GlProgram.Loc(prog, "uLocalPlane"); _litCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _litCubeSoft = GlProgram.Loc(prog, "uLocalSoft");
        _litProbe = GlProgram.Loc(prog, "uProbe"); _litProbeMips = GlProgram.Loc(prog, "uProbeMips");
        _litProbeGain = GlProgram.Loc(prog, "uProbeGain");
        _litSceneEnv = GlProgram.Loc(prog, "uSceneEnv");
        _litCapture = GlProgram.Loc(prog, "uCapture");
        _litFeat = FeatLocs(prog);
        // Bind the two shadow samplers to their units ONCE, here, and not only on the frames a map is fresh.
        // A sampler uniform defaults to unit 0 -- the same unit uTex lives on -- and GL forbids two samplers
        // of different types pointing at one image unit: the draw raises INVALID_OPERATION and is dropped.
        // The fresh path alone is not enough, because a session that never renders a map (an interior start,
        // cube_count=0) never reaches it, and then EVERY Alloy lit batch silently vanishes.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_litSunTex, (int)SunUnit);
        Gl.Uniform1i(_litCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_litProbe, (int)EnvProbe.ProbeUnit);
        // #120's five map samplers, for the same reason: left at their default they would all point at unit
        // 0 and read the diffuse as a normal map on every part that declares one.
        for (int i = 0; i < MapSlots; i++)
            Gl.Uniform1i(GlProgram.Loc(prog, TextureSlots.MapSamplers[i]), (int)PbrUnit0 + i);
        _log?.Info("AlloyRenderer: clustered forward+ lit program ready (4.6 context)");
    }

    /// <summary>#123: the five <see cref="FeatDecl"/> uniform locations of one program, in the order
    /// <see cref="UploadFeatureTerms"/> writes them.</summary>
    internal static FeatLoc FeatLocs(uint prog) => new()
    {
        Time = GlProgram.Loc(prog, "uTimeOfDay"),
        Wind = GlProgram.Loc(prog, "uAreaWind"),
        Flags = GlProgram.Loc(prog, "uAreaFlags"),
        Cam = GlProgram.Loc(prog, "uCamPos"),
        Player = GlProgram.Loc(prog, "uPlayerPos"),
        Fog = GlProgram.Loc(prog, "uFogParams"),
        FogColor = GlProgram.Loc(prog, "uFogColor"),
        KhDia = GlProgram.Loc(prog, "uKeyholeDia"),
        KhCam = GlProgram.Loc(prog, "uKeyholeCam"),
        Pixel = GlProgram.Loc(prog, "uPixel"),
    };
}
