using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#173: PROGRAM BUILD. Linking the Alloy stages -- fullbright, depth, lit and water -- from the embedded text or a content override (#92), and rebuilding from the embedded text whenever an override fails a ShaderContract predicate. Cold: once per program per context.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Build the water variant -- vertex and fragment only since #339 dropped the tessellated
    /// sea. A failure disarms Alloy water for the session, and since #264 deleted the engine's bucket walk
    /// that means water is simply not drawn (#285).</summary>
    private static bool EnsureWaterProgram()
    {
        if (_progWater != 0) return true;
        if (_waterFailed) return false;
        if (!FrameSnapshot.On) { _waterFailed = true; return false; }
        string waterVs = ShaderStages.Source(ShdWaterVs), waterFs = ShaderStages.Source(ShdWaterFs);
        uint prog = GlProgram.Graphics(ShdWaterVs, waterVs, ShdWaterFs, waterFs, _log,
                                       "AlloyRenderer water program", out string wvs, out string wfs);
        // An override that declares the wrong per-draw record reads every draw out of a neighbour's slot --
        // the #120 failure, and silent. #132 adds the colour space to the same guard, for the same reason.
        if (prog != 0 && !(ShaderContract.RecordSafe(wvs) && ShaderContract.RecordSafe(wfs)
                           // #242: ...and reaches it through the slot map. An override that predates the
                           // persistent records declares exactly the right record and indexes it by DRAW.
                           && ShaderContract.SlotIndexSafe(wvs)
                           && ShaderContract.LinearSafe(wfs)
                           // #208: ...and the samplers this program binds units to. A dropped uNoise leaves
                           // the wave field reading unit 0, which is the material diffuse.
                           && ShaderContract.SlotSafe(wfs, "uTex", "uFbColor", "uFbDepth", "uNoise", "uEnvMap")))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("AlloyRenderer: water override declares the wrong per-draw record or still " +
                       "encodes to display space -- rebuilding every water stage from the embedded default");
            prog = GlProgram.Graphics(FrameSnapshot.Vs, waterFs, _log, "AlloyRenderer water program (embedded)");
        }
        if (prog == 0)
        {
            _waterFailed = true;
            _log?.Info("AlloyRenderer: water program unavailable -- water is NOT DRAWN this session (#285: no engine walk behind it)");
            return false;
        }
        _progWater = prog;
        _watVp = GlProgram.Loc(prog, "uVP"); _watBase = GlProgram.Loc(prog, "uDrawBase");
        _watTex = GlProgram.Loc(prog, "uTex"); _watCut = GlProgram.Loc(prog, "uAlphaCut");
        _watAmbient = GlProgram.Loc(prog, "uAreaAmbient"); _watDiffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        _watDir = GlProgram.Loc(prog, "uAreaDir");
        _watGrid = GlProgram.Loc(prog, "uGrid"); _watTile = GlProgram.Loc(prog, "uTile");
        _watZ = GlProgram.Loc(prog, "uZ"); _watFalloff = GlProgram.Loc(prog, "uFalloff");
        _watWrap = GlProgram.Loc(prog, "uWrap");
        _watAtten = GlProgram.Loc(prog, "uAtten"); _watHot = GlProgram.Loc(prog, "uHot");
        _watWrapN = GlProgram.Loc(prog, "uWrapN"); _watSss = GlProgram.Loc(prog, "uSss"); _watReach = GlProgram.Loc(prog, "uReach");
        _watViz = GlProgram.Loc(prog, "uViz");
        _watSunTex = GlProgram.Loc(prog, "uSunShadow"); _watSunVp = GlProgram.Loc(prog, "uSunVp");
        _watSunTexel = GlProgram.Loc(prog, "uSunTexel"); _watSunBias = GlProgram.Loc(prog, "uSunBias");
        _watSunCount = GlProgram.Loc(prog, "uSunCascades");
        _watSunFocus = GlProgram.Loc(prog, "uSunFocus"); _watSunRadius = GlProgram.Loc(prog, "uSunRadius");
        _watSunDynTex = GlProgram.Loc(prog, "uSunDynShadow"); _watSunDyn = GlProgram.Loc(prog, "uSunDyn");
        _watCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _watCubeLight = GlProgram.Loc(prog, "uLocalLight");
        _watCubePlane = GlProgram.Loc(prog, "uLocalPlane"); _watCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _watCubeSoft = GlProgram.Loc(prog, "uLocalSoft"); _watCubeFloor = GlProgram.Loc(prog, "uLocalFloor"); _watCubePcss = GlProgram.Loc(prog, "uLocalPcss");
        _watCubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"); _watCubeDynPage = GlProgram.Loc(prog, "uLocalDynPage"); _watCubeFade = GlProgram.Loc(prog, "uLocalFade");
        _watCapture = GlProgram.Loc(prog, "uCapture");
        _watFbColor = GlProgram.Loc(prog, "uFbColor"); _watFbDepth = GlProgram.Loc(prog, "uFbDepth");
        _watNoise = GlProgram.Loc(prog, "uNoise");
        _watEnvMap = GlProgram.Loc(prog, "uEnvMap"); _watEnvOk = GlProgram.Loc(prog, "uEnvOk");
        _watNearFar = GlProgram.Loc(prog, "uNearFar");
        _watView = GlProgram.Loc(prog, "uView"); _watProj = GlProgram.Loc(prog, "uProj");
        _watFeat = FeatLocs(prog);
        // Sampler units, bound once: a sampler left at its default points at unit 0, and two sampler types
        // on one image unit is INVALID_OPERATION -- the draw is dropped, silently as far as the frame knows.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_watTex, 0);
        Gl.Uniform1i(_watSunTex, (int)SunUnit);
        Gl.Uniform1i(_watSunDynTex, (int)SunDynUnit);
        Gl.Uniform1i(_watCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_watCubeDynTex, (int)CubeDynUnit);
        Gl.Uniform1i(_watFbColor, (int)FrameSnapshot.ColorUnit);
        Gl.Uniform1i(_watFbDepth, (int)FrameSnapshot.DepthUnit);
        Gl.Uniform1i(_watNoise, (int)FrameSnapshot.NoiseUnit);
        Gl.Uniform1i(_watEnvMap, (int)FrameSnapshot.EnvUnit);
        _log?.Info("AlloyRenderer: water program ready, vanilla fswater (/water enabled 0 disables) -- " + FrameSnapshot.Describe());
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
                   "staged (#92 override vs #120's grown CxDraw), or it predates #126 and carries no " +
                   "uAlphaCut/per-draw alpha (which would render every cutout and blended surface solid). " +
                   "Ignoring it and building the embedded stages, which agree. Regenerate the override from " +
                   "the current embedded stage to get it back.");
        vsSrc = vs;
        return GlProgram.Graphics(vs, fs, _log, $"AlloyRenderer {what} program (embedded)");
    }

    /// <summary>#350: delete every program that splices content and clear its failure latch, so each rebuilds
    /// on its next use from the module now loaded. Inside the bracket, which is why the edge only sets the
    /// flag. The fullbright and depth programs splice nothing and stay.</summary>
    private static void DropSpliced()
    {
        _spliceStale = false;
        if (_progLit46 != 0) Gl.DeleteProgram(_progLit46);
        if (_progLitHost != 0) Gl.DeleteProgram(_progLitHost);
        if (_progLitList != 0) Gl.DeleteProgram(_progLitList);
        if (_progWater != 0) Gl.DeleteProgram(_progWater);
        _progLit46 = 0; _progLitHost = 0; _progLitList = 0; _progWater = 0; OitPass.ListsArmed = false;
        _litFailed = false; _waterFailed = false;
        GrassField.DropProgram();
        PostStack.DropSpliced();
        SceneWalk.Dirty = true;
        _log?.Info("AlloyRenderer: module changed -- the spliced programs (lit, hosted lit, water, grass, post) are dropped and rebuild from this module's resource chain");
    }

    private static bool EnsureProgram()
    {
        if (_spliceStale) DropSpliced();
        if (_prog46 == 0 && !_progFailed)
        {
            // ponytail: one cheap stage keeps the lists block (no early-Z for unlit parts); twin it as the lit one if it ever shows
            string fbFs = OitPass.WithLists(ShaderStages.Source(ShdFullbrightFs));
            uint prog = GlProgram.Graphics(ShdFullbrightVs, ShaderStages.Source(ShdFullbrightVs),
                                           ShdFullbrightFs, fbFs,
                                           _log, "AlloyRenderer fullbright program",
                                           out string vsSrc, out string fsSrc);
            if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                               && ShaderContract.SlotIndexSafe(vsSrc)   // #242
                               && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)))
            {
                prog = RebuildEmbedded(prog, OwnedVs, fbFs, "fullbright", ref vsSrc);
            }
            _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
            // #263: programs build on first use, i.e. inside the first frame the verdict already froze on.
            // A silent latch here is a permanently bare world, so it is a hard failure with a reason.
            if (prog == 0) { _progFailed = true; AlloyGate.Fail("an Alloy program did not build"); return false; }
            _prog46 = prog;
            _locVp = GlProgram.Loc(prog, "uVP"); _locBase = GlProgram.Loc(prog, "uDrawBase");
            _locTex = GlProgram.Loc(prog, "uTex"); _locViz = GlProgram.Loc(prog, "uViz"); _locOit = GlProgram.Loc(prog, "uOit");
            _locOitDim = GlProgram.Loc(prog, "uOitDim"); _locOitBias = GlProgram.Loc(prog, "uOitBias"); _locOitGob = GlProgram.Loc(prog, "uOitGob");
            Gl.UseProgram(prog); Gl.Uniform1i(GlProgram.Loc(prog, "uOitDepth"), (int)TextureSlots.OitDepth);
            _locCut = GlProgram.Loc(prog, "uAlphaCut");
            _locTint = GlProgram.Loc(prog, "uTint");
            _locSkyFog = GlProgram.Loc(prog, "uSkyFog");
            _locFeat = FeatLocs(prog);
            _log?.Info("AlloyRenderer: fullbright program ready (4.6 context)");
        }
        if (_prog46 == 0) return false;
        if (_lighting && _progLit46 == 0 && !_litFailed) EnsureLitProgram();
        return true;
    }

    /// <summary>The feature-aware depth stage (cutout + wind-displaced batches). Built from the vertex text
    /// that actually compiled for the lit (else fullbright) program, so a content override's displacement
    /// is the one the prepass writes. Not fatal: without it those batches stay out, as before.</summary>
    private static bool EnsureDepthFeatProgram()
    {
        if (_progDepthFeat != 0) return true;
        if (_depthFeatFailed || !_vsDepthSafe) return false;
        // the fullbright vs writes no vDraw, and DepthFeatFs indexes the record buffer by it: lit only
        string? vs = _litVsSrc;
        if (vs == null) return false;   // no lit program compiled yet: try again next frame
        // #153: lit batches merge across textures under bindless, so one bound uTex would alpha-test every
        // draw in the batch against the first draw's leaves; the stage samples the per-draw handle instead
        string fs = _bindless ? BindlessFs(DepthFeatFs) : DepthFeatFs;
        uint prog = GlProgram.Graphics(vs, fs, _log, "AlloyRenderer feature depth prepass program");
        if (prog == 0)
        {
            _depthFeatFailed = true;
            _log?.Info("AlloyRenderer: feature depth prepass program unavailable -- cutout and displaced batches stay out of the prepass");
            return false;
        }
        _progDepthFeat = prog;
        _depthFeatVp = GlProgram.Loc(prog, "uVP"); _depthFeatBase = GlProgram.Loc(prog, "uDrawBase");
        _depthFeatTex = GlProgram.Loc(prog, "uTex"); _depthFeatCut = GlProgram.Loc(prog, "uAlphaCut");
        _depthFeatFeat = FeatLocs(prog);
        _log?.Info("AlloyRenderer: feature depth prepass ready -- cutout and wind-displaced batches write depth (/alloy prepassfeat 0 disables)");
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
        _progSkyDecode = GlProgram.Graphics(PostChain.FullscreenVs, SkyDecodeFs, _log, "AlloyRenderer sky decode");
        _skyDecodeHdr = GlProgram.Loc(_progSkyDecode, "uHdr");
        if (_progSkyDecode == 0 || Gl.TextureBarrier == null)
            _log?.Info("AlloyRenderer: sky decode unavailable -- a hosted sky program draws display space into the linear frame and reads bright");
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
        _log?.Info("AlloyRenderer: depth prepass ready (/alloy prepass 0 disables)" +
                   (keyProg != 0 ? "" : " -- WITHOUT the keyhole stage, so dissolving batches stay out of it"));
        return true;
    }

    /// <summary>What the owned stages ask of the driver beyond the GL 4.6 floor: three vertex-stage SSBO
    /// blocks (draw, draw index, bones; floor 0) and the whole 16-unit fragment sampler budget
    /// (<see cref="TextureSlots"/> ends at 15; floor 16, so zero margin).</summary>
    private const int VsSsboNeed = 3, TexUnitsNeed = 16;

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
            // alloy.bindless=auto: on wherever the extension exists EXCEPT zink, whose bindless bookkeeping faults on
            // a worker thread once handles exist at all (five Zink crashes on 2026-09-05, with deletes, without
            // deletes, without non-resident calls); alloy.bindless=on forces it there, alloy.bindless=off vetoes it anywhere
            int want = Renderer.Bindless.Boot;
            bool zinkVeto = want < 0 && GlContextService.Zink;
            _bindless = want != 0 && !zinkVeto && HasBindless();
            if (zinkVeto) _log?.Info("AlloyRenderer: zink renderer -- bindless texturing stood down (alloy.bindless=on in cx.cfg forces it)");
            _log?.Info(_bindless
                ? "AlloyRenderer: ARB_bindless_texture armed -- lit batches merge across textures (alloy.bindless=off in cx.cfg reverts)"
                : "AlloyRenderer: bindless texturing unavailable or vetoed -- per-batch texture binds");
        }
        if (!_limitsProbed)
        {
            _limitsProbed = true;
            // The two limits every owned vertex stage exceeds the GL 4.6 floor on (0 vertex SSBO blocks,
            // 8 bindings): asked once, so a driver that cannot link says so by name rather than by log spam.
            int vsBlocks = 0, bindings = 0, units = 0;
            Gl.GetIntegerv(Gl.MAX_VERTEX_SHADER_STORAGE_BLOCKS, &vsBlocks);
            Gl.GetIntegerv(Gl.MAX_SHADER_STORAGE_BUFFER_BINDINGS, &bindings);
            Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
            int needBind = (int)CasterGather.BindView + 1;
            bool short_ = vsBlocks < VsSsboNeed || bindings < needBind || units < TexUnitsNeed;
            _log?.Info($"AlloyRenderer: driver limits -- vertex SSBO blocks {vsBlocks} (needs {VsSsboNeed}, GL 4.6 floor 0), " +
                       $"SSBO bindings {bindings} (needs {needBind}, floor 8), fragment texture units {units} (needs {TexUnitsNeed}, floor 16)" +
                       (short_ ? " -- BELOW ALLOY'S NEED: expect link failures on this driver" : ""));
        }
        string litFs = ShaderStages.Source(ShdLitFs);
        if (_bindless) litFs = BindlessFs(litFs);
        // #350: from the hosted regions' STUBS -- the program every lit batch draws with carries no content
        // body; the second build below carries them for the batches that need them.
        uint prog = GlProgram.Graphics(ShdLitVs, ShaderStages.Source(ShdLitVs), ShdLitFs, litFs,
                                       _log, "AlloyRenderer clustered lit program",
                                       out string vsSrc, out string fsSrc, host: false);
        string finalFs = fsSrc;
        if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                           && ShaderContract.SlotIndexSafe(vsSrc)   // #242
                           && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)
                           && ShaderContract.ChartSafe(vsSrc, fsSrc)   // #674
                           // #208: ...and the sampler declarations the unit table binds against, which only
                           // the compiled source can answer for. Bound-sampler check, so bindless skips it.
                           && (_bindless || ShaderContract.LitSlotSafe(fsSrc))
                           && (!_bindless || fsSrc.Contains("CxTexH"))))
        {
            prog = RebuildEmbedded(prog, OwnedLitVs, litFs, "clustered lit", ref vsSrc);
            finalFs = litFs;
        }
        // a content override without the handle block compiled fine but still samples bound units: the
        // C# side must keep binding for it, so the whole bindless path stands down for the session
        if (prog != 0 && _bindless && !fsSrc.Contains("CxTexH") && !litFs.Contains("CxTexH"))
        {
            _bindless = false;
            _log?.Info("AlloyRenderer: lit stage carries no handle block -- bindless stands down, binds resume");
        }
        _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
        if (prog != 0) _litVsSrc = vsSrc;
        if (prog == 0)
        {
            _litFailed = true;
            SceneWalk.Dirty = true;   // re-enroll at once so lit parts revert to the engine's own draw
            _log?.Info("AlloyRenderer: clustered lit program unavailable -- lit parts revert to engine drawing");
            return;
        }
        _progLit46 = prog;
        _lit = LitLocs(prog);
        // The OIT-lists twin: the same text that just compiled, plus the append block, bound only inside a mode-2
        // bracket. Verified on the text because a content override may carry no block at all.
        string listFs = OitPass.WithLists(finalFs);
        uint pl = listFs.Contains("atomicAdd(uOitCount") ? GlProgram.Graphics(vsSrc, listFs, _log, "AlloyRenderer lit program (OIT lists)", host: false) : 0;
        _progLitList = pl;
        if (pl != 0) _litL = LitLocs(pl);
        else _log?.Info("AlloyRenderer: lit OIT-lists twin unavailable -- /alloy oit 2 resolves the weighted way");
        OitPass.ListsArmed = pl != 0;
        // #350: the SECOND lit program, the one with the content bodies in. Same text, hosted regions spliced
        // instead of stubbed; same contracts, because it draws the same records. Optional twice over: alloy.hostfs=0,
        // no hosted feature, or a failed build all leave it 0 and hosted materials on the plain path.
        _progLitHost = 0;
        if (VanillaShader.HostArmed && System.Linq.Enumerable.Any(ShaderFeatures.FsNames, n => n.Length > 0))
        {
            uint ph = GlProgram.Graphics(ShdLitVs, ShaderStages.Source(ShdLitVs), ShdLitFs, litFs,
                                         _log, "AlloyRenderer hosted lit program", out _, out string hfs, host: true);
            if (ph != 0 && !(ShaderContract.RecordSafe(hfs) && ShaderContract.LinearSafe(hfs)
                             && (!_bindless || hfs.Contains("CxTexH"))))
            {
                Gl.DeleteProgram(ph); ph = 0;
                _log?.Info("AlloyRenderer: hosted lit program failed its contracts -- hosted materials draw the plain lit path");
            }
            if (ph == 0) _log?.Info("AlloyRenderer: hosted lit program unavailable -- hosted materials draw the plain lit path");
            else { _progLitHost = ph; _litH = LitLocs(ph); }
        }
        _log?.Info("AlloyRenderer: clustered forward+ lit program ready (4.6 context)");
    }

    /// <summary>Resolve one lit program's locations and bind its fixed sampler units. Called once per program
    /// at build; #350 calls it twice.</summary>
    internal static LitLoc LitLocs(uint prog)
    {
        var L = new LitLoc();
        L.Vp = GlProgram.Loc(prog, "uVP"); L.Base = GlProgram.Loc(prog, "uDrawBase");
        L.Tex = GlProgram.Loc(prog, "uTex");
        L.Cut = GlProgram.Loc(prog, "uAlphaCut");
        L.Ambient = GlProgram.Loc(prog, "uAreaAmbient"); L.Diffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        L.Dir = GlProgram.Loc(prog, "uAreaDir");
        L.Grid = GlProgram.Loc(prog, "uGrid"); L.Tile = GlProgram.Loc(prog, "uTile");
        L.Z = GlProgram.Loc(prog, "uZ"); L.Falloff = GlProgram.Loc(prog, "uFalloff");
        L.Wrap = GlProgram.Loc(prog, "uWrap");
        L.Atten = GlProgram.Loc(prog, "uAtten"); L.Hot = GlProgram.Loc(prog, "uHot");
        L.WrapN = GlProgram.Loc(prog, "uWrapN"); L.Sss = GlProgram.Loc(prog, "uSss");
        L.Reach = GlProgram.Loc(prog, "uReach");
        L.Viz = GlProgram.Loc(prog, "uViz"); L.Oit = GlProgram.Loc(prog, "uOit"); L.OitDim = GlProgram.Loc(prog, "uOitDim"); L.OitBias = GlProgram.Loc(prog, "uOitBias"); L.OitGob = GlProgram.Loc(prog, "uOitGob");
        L.SunTex = GlProgram.Loc(prog, "uSunShadow"); L.SunVp = GlProgram.Loc(prog, "uSunVp");
        L.SunTexel = GlProgram.Loc(prog, "uSunTexel"); L.SunBias = GlProgram.Loc(prog, "uSunBias");
        L.SunCount = GlProgram.Loc(prog, "uSunCascades");
        L.SunFocus = GlProgram.Loc(prog, "uSunFocus"); L.SunRadius = GlProgram.Loc(prog, "uSunRadius");
        L.SunDynTex = GlProgram.Loc(prog, "uSunDynShadow"); L.SunDyn = GlProgram.Loc(prog, "uSunDyn");
        L.CubeTex = GlProgram.Loc(prog, "uLocalShadow"); L.CubeLight = GlProgram.Loc(prog, "uLocalLight");
        L.CubePlane = GlProgram.Loc(prog, "uLocalPlane"); L.CubeCount = GlProgram.Loc(prog, "uLocalCubes");
        L.CubeSoft = GlProgram.Loc(prog, "uLocalSoft"); L.CubeFloor = GlProgram.Loc(prog, "uLocalFloor"); L.CubePcss = GlProgram.Loc(prog, "uLocalPcss");
        L.CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"); L.CubeDynPage = GlProgram.Loc(prog, "uLocalDynPage"); L.CubeFade = GlProgram.Loc(prog, "uLocalFade");
        L.Probe = GlProgram.Loc(prog, "uProbe"); L.ProbeMips = GlProgram.Loc(prog, "uProbeMips");
        L.ProbeGain = GlProgram.Loc(prog, "uProbeGain");
        L.SceneEnv = GlProgram.Loc(prog, "uSceneEnv");
        L.Capture = GlProgram.Loc(prog, "uCapture");
        L.LmPages = GlProgram.Loc(prog, "uLmPages"); L.LmMovers = GlProgram.Loc(prog, "uLmMovers"); L.CubeDynSphere = GlProgram.Loc(prog, "uLocalDynSphere");
        L.Ssao = GlProgram.Loc(prog, "uSsao"); L.SsaoOn = GlProgram.Loc(prog, "uSsaoOn");
        L.SsaoHalf = GlProgram.Loc(prog, "uSsaoHalf");
        L.AoDirect = GlProgram.Loc(prog, "uAoDirect"); L.AoCutout = GlProgram.Loc(prog, "uAoCutout");
        L.Weather = GlProgram.Loc(prog, "uWeather"); L.WeatherLook = GlProgram.Loc(prog, "uWeatherLook");
        L.CoverVp = GlProgram.Loc(prog, "uCoverVp"); L.CoverLayer = GlProgram.Loc(prog, "uCoverLayer");
        L.SnowLook = GlProgram.Loc(prog, "uSnowLook"); L.SnowLight = GlProgram.Loc(prog, "uSnowLight"); L.CoverEsm = GlProgram.Loc(prog, "uCoverEsmP"); L.SnowPom = GlProgram.Loc(prog, "uSnowPom");
        L.Feat = FeatLocs(prog);
        // Bind the two shadow samplers to their units ONCE, here, and not only on the frames a map is fresh.
        // A sampler uniform defaults to unit 0 -- the same unit uTex lives on -- and GL forbids two samplers
        // of different types pointing at one image unit: the draw raises INVALID_OPERATION and is dropped.
        // The fresh path alone is not enough, because a session that never renders a map (an interior start,
        // shadow.cubes=0) never reaches it, and then EVERY Alloy lit batch silently vanishes.
        Gl.UseProgram(prog);
        Gl.Uniform1i(L.SunTex, (int)SunUnit);
        Gl.Uniform1i(L.SunDynTex, (int)SunDynUnit);
        Gl.Uniform1i(L.CubeTex, (int)CubeUnit);
        Gl.Uniform1i(L.CubeDynTex, (int)CubeDynUnit);
        Gl.Uniform1i(L.Probe, (int)EnvProbe.ProbeUnit);
        Gl.Uniform1i(GlProgram.Loc(prog, "uOitDepth"), (int)TextureSlots.OitDepth);
        Gl.Uniform1i(GlProgram.Loc(prog, "uCoverEsm"), (int)TextureSlots.CoverEsm);
        Gl.Uniform1i(L.Ssao, (int)TextureSlots.Ssao);
        Gl.Uniform1i(GlProgram.Loc(prog, "uSceneDepth"), (int)TextureSlots.SsaoDepth);
        // #120's five map samplers, for the same reason: left at their default they would all point at unit
        // 0 and read the diffuse as a normal map on every part that declares one.
        for (int i = 0; i < MapSlots; i++)
            Gl.Uniform1i(GlProgram.Loc(prog, TextureSlots.MapSamplers[i]), (int)PbrUnit0 + i);
        return L;
    }

    /// <summary>#123: the five <see cref="FeatDecl"/> uniform locations of one program, in the order
    /// <see cref="UploadFeatureTerms"/> writes them.</summary>
    internal static FeatLoc FeatLocs(uint prog) => WithScriptables(new FeatLoc
    {
        Time = GlProgram.Loc(prog, "uTimeOfDay"),
        Wind = GlProgram.Loc(prog, "uAreaWind"),
        Flags = GlProgram.Loc(prog, "uAreaFlags"),
        Cam = GlProgram.Loc(prog, "uCamPos"),
        CamFocus = GlProgram.Loc(prog, "uCamFocus"),
        Player = GlProgram.Loc(prog, "uPlayerPos"),
        Fog = GlProgram.Loc(prog, "uFogParams"),
        FogColor = GlProgram.Loc(prog, "uFogColor"),
        KhDia = GlProgram.Loc(prog, "uKeyholeDia"),
        KhCam = GlProgram.Loc(prog, "uKeyholeCam"),
        Pixel = GlProgram.Loc(prog, "uPixel"),
        LodBias = GlProgram.Loc(prog, "uLodBias"),
        Eye = GlProgram.Loc(prog, "uEyePos"),
        FogSky = GlProgram.Loc(prog, "uFogSky"),
        FogSkyColor = GlProgram.Loc(prog, "uFogSkyColor"),
        FogSun = GlProgram.Loc(prog, "uFogSun"),
        FogSunColor = GlProgram.Loc(prog, "uFogSunColor"),
        FogOrigin = GlProgram.Loc(prog, "uFogOrigin"),
        // #346: the ENGINE's own uniform names, because the stage compiles the engine's own file. Renaming
        // any of these means editing spliced content, which is the drift the bridge exists to end.
        TgtShapeL = GlProgram.Loc(prog, "targetShape"),
        TgtSelfL = GlProgram.Loc(prog, "targetShapeOriginOnSelf"),
        TgtObjL = GlProgram.Loc(prog, "targetObjectType"),
        TgtSizeXL = GlProgram.Loc(prog, "targetShapeSizeX"),
        TgtSizeYL = GlProgram.Loc(prog, "targetShapeSizeY"),
        TgtRangeL = GlProgram.Loc(prog, "targetShapeSpellRange"),
        TgtColL = GlProgram.Loc(prog, "targetShapeColour"),
        TgtWorldL = GlProgram.Loc(prog, "targetWorldCoords"),
        // #347: the shared vanilla environment. Same rule as above -- the engine's names, because the code
        // that reads them is the engine's.
        ProjL = GlProgram.Loc(prog, "m_proj"),
    }, prog);

    /// <summary>#551: the scriptable block's locations, filled after the struct so the fixed buffers can be
    /// written. Cold -- once per program, at build.</summary>
    private static FeatLoc WithScriptables(FeatLoc loc, uint prog)
    {
        for (int i = 0; i < ScriptableSlots; i++)
        {
            loc.SInt[i] = GlProgram.Loc(prog, $"scriptableInt{i + 1}");
            loc.SFloat[i] = GlProgram.Loc(prog, $"scriptableFloat{i + 1}");
            loc.SVec[i] = GlProgram.Loc(prog, $"scriptableVec{i + 1}");
        }
        return loc;
    }
}
