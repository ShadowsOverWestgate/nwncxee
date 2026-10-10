using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The sky's own fragment shader, compiled as an ALLOY program: a dynamic sky IS its fragment stage, spliced
/// off the resource chain into <c>cx_sky_fs</c> (whose environment stands in for the engine's own includes,
/// so stock text drops in unedited) and linked against Alloy's sky vertex stage -- so the dome takes
/// <c>uVP</c> like any Alloy draw and renders into the probe's cube faces. Its textures stay the ENGINE's.
/// </summary>
internal static unsafe class SkyStage
{
    /// <summary>The stage's resref (an override point): the environment plus the <c>// $VANILLA-MAIN</c>
    /// region the content sky stage is spliced into. An author overriding this replaces the environment, not
    /// the content's sky.</summary>
    internal const string ShdFs = "cx_sky_fs";
    /// <summary>The token the region's resref is written into before the splice. Not a
    /// <see cref="ShaderResource.Tokens"/> entry: the splice runs BEFORE token expansion, so a marker naming
    /// a token would ask the resource chain for a file called <c>$SKYFS$</c>.</summary>
    private const string FsToken = "$SKYFS$";

    /// <summary>A sky has three gobs and they share one material in every module measured; four distinct
    /// custom fragment stages is a ceiling, not a sizing decision. Past it the sky is refused for the frame
    /// (loudly, once), and nothing else draws it, so the frame has no sky.</summary>
    private const int MaxProgs = 4;

    private struct Entry
    {
        public string Ref;
        public uint Prog;
        public bool Failed;
        public int Vp, DrawBase, Tex, Tex1, Tint, SkyFog, ViewInv, Global, Oit, OitDepth;
        public int Year, Month, Day, Hour, Dawn, Dusk, Transition, Weather;
        public AlloyRenderer.FeatLoc Feat;
    }

    private static readonly Entry[] _progs = new Entry[MaxProgs];
    private static int _count;
    private static ExtenderLog? _log;
    /// <summary>The program whose per-pass uniforms are already uploaded this pass, so a role's several parts
    /// cost one upload. Cleared by <see cref="BeginPass"/>.</summary>
    private static uint _primed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: every GL name is gone, and a failure latch taken against the old context is not
    /// evidence about the new one.</summary>
    internal static void Forget()
    {
        for (int i = 0; i < _count; i++) _progs[i] = default;
        _count = 0;
        _primed = 0;
    }

    /// <summary>Module change: the spliced text came off THAT module's resource chain, so it has a module
    /// lifetime (the server-switch trap -- a program built from one server's chain running on another's). Rebuilds
    /// lazily on the next sky frame.</summary>
    internal static void DropPrograms()
    {
        for (int i = 0; i < _count; i++) if (_progs[i].Prog != 0) Gl.DeleteProgram(_progs[i].Prog);
        Forget();
    }

    /// <summary>The program for a material's custom fragment stage <paramref name="fs"/>, built on first sight
    /// and cached for the module. 0 when it cannot be built (logged there), which refuses the sky rather than
    /// drawing a dome whose whole look that stage was.</summary>
    internal static uint Program(string fs)
    {
        if (fs.Length == 0) return 0;
        for (int i = 0; i < _count; i++)
            if (string.Equals(fs, _progs[i].Ref, StringComparison.OrdinalIgnoreCase)) return _progs[i].Prog;
        if (_count >= MaxProgs)
        {
            _log?.Always($"SkyStage: more than {MaxProgs} distinct sky fragment stages in one session -- the sky "
                       + "is NOT DRAWN for this area");
            return 0;
        }
        string resref = fs.ToLowerInvariant();
        ref Entry e = ref _progs[_count++];
        e.Ref = resref;
        e.Prog = Build(resref);
        if (e.Prog == 0) { e.Failed = true; return 0; }
        Locate(ref e);
        _log?.Info($"SkyStage: {resref}.shd hosted as the sky's fragment stage");
        return e.Prog;
    }

    /// <summary>Link the sky pair: Alloy's own fullbright VERTEX stage (the same text the unlit program
    /// compiles, so the dome's clip position, texture rotation and world position come out identically) and
    /// <see cref="ShdFs"/> with the content stage's resref written into its region.</summary>
    private static uint Build(string resref)
    {
        if (resref.Length > ShaderResource.ResRefBytes) return 0;
        string what = $"AlloyRenderer sky program ({resref})";
        // Refused rather than stubbed: the region's fallback stub is a BLACK DOME, which is a picture, so a
        // program built from it would be a silent fallback in the exact sense CX-45 forbids. No stage in the
        // chain (or the bridge disarmed) means no sky for this area, said out loud.
        if (!VanillaShader.InChain(resref))
        {
            _log?.Always($"SkyStage: {resref}.shd is not in the resource chain (or alloy.vanillabridge=0) -- the "
                       + "sky cannot be compiled here and is NOT DRAWN (nothing else draws the sky since CX-87)");
            return 0;
        }
        // The override lookup is done HERE, not by GlProgram's overridable overload, because the region's
        // resref has to be written in before the splice reads the marker -- and an override that keeps the
        // token is entitled to the same substitution the embedded text gets.
        string evs = ShaderStages.Source(AlloyRenderer.ShdFullbrightVs), efs = ShaderStages.Source(ShdFs);
        string vs = ShaderResource.Override(AlloyRenderer.ShdFullbrightVs, _log) ?? evs;
        string fs = ShaderResource.Override(ShdFs, _log) ?? efs;
        // The vertex half reads the per-draw record, so it owes the same two contracts the unlit program's
        // build checks -- an override that dropped them would put every dome at its neighbour's matrix.
        if (!ReferenceEquals(vs, evs) && !(ShaderContract.RecordSafe(vs) && ShaderContract.SlotIndexSafe(vs)))
        {
            _log?.Info($"{what}: the {AlloyRenderer.ShdFullbrightVs} override does not declare the per-draw "
                     + "record -- the sky takes the embedded vertex stage");
            vs = evs;
        }
        uint prog = GlProgram.Graphics(vs, fs.Replace(FsToken, resref, StringComparison.Ordinal), _log, what);
        if (prog == 0 && (!ReferenceEquals(vs, evs) || !ReferenceEquals(fs, efs)))
        {
            _log?.Info($"{what}: an override .shd failed to build -- falling back to the embedded stages");
            prog = GlProgram.Graphics(evs, efs.Replace(FsToken, resref, StringComparison.Ordinal), _log, what);
        }
        if (prog != 0 && GlProgram.LastStubbed)
        {
            Gl.DeleteProgram(prog);
            prog = 0;
        }
        if (prog == 0)
            _log?.Always($"SkyStage: the sky program for {resref}.shd did not build (a stubbed stage is a black "
                       + "dome, so it is refused too) -- the sky is NOT DRAWN; see the compile log above");
        return prog;
    }

    private static void Locate(ref Entry e)
    {
        uint p = e.Prog;
        e.Vp = GlProgram.Loc(p, "uVP"); e.DrawBase = GlProgram.Loc(p, "uDrawBase");
        e.Tex = GlProgram.Loc(p, "uTex"); e.Tex1 = GlProgram.Loc(p, "texUnit1");
        e.Tint = GlProgram.Loc(p, "uTint"); e.SkyFog = GlProgram.Loc(p, "uSkyFog");
        e.ViewInv = GlProgram.Loc(p, "uViewInv");
        // The engine's own per-draw constant colour: PartTriMesh::Mat's SetColor4f, which every stock stage
        // seeds FragmentColor from. Declared by the CONTENT stage, so this may be -1.
        e.Global = GlProgram.Loc(p, "globalColor");
        e.Oit = GlProgram.Loc(p, "uOit"); e.OitDepth = GlProgram.Loc(p, "uOitDepth");
        e.Year = GlProgram.Loc(p, "uModYear"); e.Month = GlProgram.Loc(p, "uModMonth");
        e.Day = GlProgram.Loc(p, "uModDay"); e.Hour = GlProgram.Loc(p, "uModHour");
        e.Dawn = GlProgram.Loc(p, "uModDawn"); e.Dusk = GlProgram.Loc(p, "uModDusk");
        e.Transition = GlProgram.Loc(p, "uModTransition"); e.Weather = GlProgram.Loc(p, "uWeatherDensity");
        e.Feat = Programs.FeatLocs(p);
    }

    /// <summary>A pass is starting: nothing is primed. Cheap, and it is what stops one pass's upload being
    /// credited to the next (the probe's six faces each have their own view-projection).</summary>
    internal static void BeginPass() => _primed = 0;

    /// <summary>Everything a sky program needs that does not vary per draw, uploaded once per program per
    /// pass: the view-projection, the sampler units, the camera's world matrix (stock reads its translation
    /// column as the eye) and the engine's calendar and weather scalars from
    /// <c>m_shaderExposedRuntimeData</c> -- the same values the engine feeds its own copy. Hot.</summary>
    internal static void Prepare(uint prog, Matrix4x4* vp, int* viewport)
    {
        int at = -1;
        for (int i = 0; i < _count; i++) if (_progs[i].Prog == prog) { at = i; break; }
        if (at < 0) return;
        ref Entry e = ref _progs[at];
        Gl.UseProgram(prog);
        if (_primed == prog)
        {
            Gl.UniformMatrix4fv(e.Vp, 1, 0, (float*)vp);
            return;
        }
        _primed = prog;
        Gl.UniformMatrix4fv(e.Vp, 1, 0, (float*)vp);
        Gl.Uniform1i(e.Tex, 0);
        Gl.Uniform1i(e.Tex1, 1);
        // uOit is program state and the sky is never in the OIT lists; left at the accumulation arm the dome
        // would land as a near-zero weighted write (gl_FragCoord.z = 1), which is a black sky.
        Gl.Uniform1i(e.Oit, 0);
        Gl.Uniform1i(e.OitDepth, (int)TextureSlots.OitDepth);
        // ponytail: stock reads m_view_inv's TRANSLATION COLUMN only (`vPosWorld.xyz - m_view_inv[3].xyz`,
        // the view ray: inc_sunm, inc_moonm, inc_stars, inc_clouds), so a translation carries every read the
        // sky chain makes, at every pass's own eye. A content stage that wants the camera BASIS needs the real
        // inverse view threaded through from AlloyRenderer.Frame.
        Matrix4x4 viewInv = Matrix4x4.CreateTranslation(AlloyRenderer.EyePos);
        Gl.UniformMatrix4fv(e.ViewInv, 1, 0, (float*)&viewInv);
        AlloyRenderer.UploadFeatureTerms(e.Feat, viewport);
        if (!AlloyRenderer.HasRuntime) return;
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        Gl.Uniform1i(e.Year, rt.ModuleYear); Gl.Uniform1i(e.Month, rt.ModuleMonth);
        Gl.Uniform1i(e.Day, rt.ModuleDay); Gl.Uniform1i(e.Hour, rt.ModuleHour);
        Gl.Uniform1i(e.Dawn, rt.ModuleDawnHour); Gl.Uniform1i(e.Dusk, rt.ModuleDuskHour);
        Gl.Uniform1f(e.Transition, rt.ModuleTimeIntoTransition);
        Gl.Uniform1f(e.Weather, rt.AreaWeatherDensity);
    }

    /// <summary>The per-draw terms: the record index the vertex stage reads its matrix at, the fog band, and
    /// the engine's own constant colour -- <c>(tint, alpha)</c>, which is <c>PartTriMesh::Mat</c>'s two
    /// branches exactly (white at the role's alpha, or the scene's fog colour at 1.0 for the skyfade
    /// gob).</summary>
    internal static void Draw(uint prog, int slot, float* tint, float alpha, float skyFog)
    {
        int at = -1;
        for (int i = 0; i < _count; i++) if (_progs[i].Prog == prog) { at = i; break; }
        if (at < 0) return;
        ref Entry e = ref _progs[at];
        Gl.Uniform1i(e.DrawBase, slot);
        Gl.Uniform3fv(e.Tint, 1, tint);
        Gl.Uniform4f(e.Global, tint[0], tint[1], tint[2], alpha);
        Gl.Uniform1f(e.SkyFog, skyFog);
    }

    /// <summary>Diagnostic: the stages built this session and the ones that refused, by name -- "no stage
    /// named" and "a stage that would not build" are different facts.</summary>
    internal static string Stats()
    {
        if (_count == 0) return "none";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _count; i++)
            sb.Append(i > 0 ? ", " : "").Append(_progs[i].Ref).Append(_progs[i].Failed ? " REFUSED" : " hosted");
        return sb.ToString();
    }

    /// <summary>Off-device: the token, the place a typo would show as "there is no sky and nothing says
    /// why".</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string src = ShaderStages.Source(ShdFs);
        check.Expect(src.Contains("// $VANILLA-MAIN " + FsToken + "\n"), "the stage carries the whole-stage region, token and all");
        check.Expect(src.Contains("void " + VanillaShader.MainEntry + "()"), "...and a stub under the entry name the splice renames to");
        check.Expect(src.Contains("CxWholeTexel = texture("), "...and the wrapper samples the texel inc_standard's setup call multiplies in");
        check.Expect(!src.Contains(FsToken + "$"), "the token is not itself a sizing token (the splice runs first)");
        log.Info(check.Ok ? "SkyStage.SelfCheck: passed" : "SkyStage.SelfCheck: FAILED");
    }
}
