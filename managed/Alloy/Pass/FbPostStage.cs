using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// #352: the content's own full-screen post chain -- the engine's <c>fsfbpostpr</c> program, framebuffer
/// effect 2 of <c>Camera::DoFrameBufferEffects</c> @0x4a8eb0, which the M4 cutover dropped with
/// RenderSinglePass -- hosted as one stage of the display-space stack. SoW's status overlay
/// (<c>inc_postpr_stat</c>), grade, noir, sky rays and lens flare all live in the <c>inc_postpr</c> that
/// chain includes, so this is what puts them back.
/// <para>Same mechanism as the lit program's <c>inc_target</c>: a <c>// $VANILLA inc_postpr</c> region is
/// replaced by the resource chain's copy at build, so a hak or NWSync override of the chain is what runs;
/// the stub between the markers is a pass-through. The environment ahead of the region stands in for
/// <c>inc_common</c>/<c>inc_uniforms</c> the way <see cref="AlloyRenderer.VanillaEnvFs"/> does for the lit
/// stage, under the ENGINE's names, so stock text drops in unedited; <see cref="Prepare"/> writes those
/// names from Alloy's own values. Runs where the engine ran it: on the resolved, display-space frame.</para>
/// <para>The client's option mask reaches the text as <c>POSTPROCESSING_TYPES_ENABLED</c> exactly as the
/// engine's preamble writes it, minus the bits Alloy serves with stages of its own (gamma, toon) and the two
/// whose stock text has a vertex half this fullscreen triangle does not run (depth of field, dynamic
/// contrast). Sharpen and vibrance are hosted. A mask change rebuilds the program, as the engine reloads
/// its shaders on int param 2.</para>
/// </summary>
internal static unsafe class FbPostStage
{
    /// <summary>The stage's resref (#92 override point), same rules as every stack stage.</summary>
    internal const string ShdFs = "cx_fbpost_fs";
    /// <summary><c>Camera::GetIsFBEffectEnabled(2)</c> @0x5a6530: bit 2 of <c>m_nFramebufferEffectsEnabled</c>.
    /// <c>ConfigureFBEffectPostProcessing</c> @0x66ad40 sets it from a LONG read at CClientOptions+0x118, i.e.
    /// option mask != 0 OR <c>run-shader-even-with-no-effects</c> (+0x11c) -- so it is the engine's own
    /// answer to "does the post program run this session".</summary>
    internal const uint EffectPost = 1u << 2;
    /// <summary>The option bits stripped from the hosted text: DOF 0x02 and dynamic contrast 0x04 (vertex
    /// halves), gamma 0x10 and toon 0x20 (Alloy's own stages).</summary>
    internal const int MaskStripped = 0x02 | 0x04 | 0x10 | 0x20;   // not-an-offset: inc_postpr.shd option bits
    /// <summary><c>GetGlobalShaderParami(7)</c>: the client's shader quality option, the preamble's
    /// <c>SHADER_QUALITY_MODE</c> (the same read <see cref="AlloyLights"/> makes for the subsurface term);
    /// the content's <c>inc_config</c> branches on it.</summary>
    internal const uint QualityParam = 7;   // not-an-offset: engine shader-param index

    private static ExtenderLog? _log;
    private static Matrix4x4 _view = Matrix4x4.Identity;
    private static float _near = 0.1f, _far = 100f;
    private static uint _prog, _sampler;
    private static AlloyRenderer.FeatLoc _feat;
    private static int _locView, _locPixel, _locClip, _locFrame, _locHour, _locMonth, _locDay, _locTransition, _locPlayer,
                       _locFbColor, _locFbDepth;

    /// <summary>What the text is built against: expanded into the source as <c>$POSTMASK$</c> and <c>$SQM$</c>
    /// by <see cref="ShaderResource.Tokens"/>. -1 = not built yet.</summary>
    internal static int BuildMask { get; private set; } = -1;
    internal static int BuildQuality { get; private set; } = -1;

    internal static void Attach(ExtenderLog log) => _log = log;
    /// <summary>Context loss: every GL name is gone.</summary>
    internal static void Forget() { DropProgram(); _sampler = 0; }
    /// <summary>The program is dropped (module change, option change); the sampler object outlives it.</summary>
    internal static void DropProgram() { _prog = 0; BuildMask = -1; BuildQuality = -1; }

    /// <summary>The bits the hosted text carries, of the engine's mask.</summary>
    internal static int HostedMask(int mask) => mask & ~MaskStripped;
    /// <summary>Does the built program disagree with the options now? The stack drops and rebuilds it.</summary>
    internal static bool Stale(int mask, int quality) => BuildMask >= 0 && (HostedMask(mask) != BuildMask || quality != BuildQuality);
    /// <summary>Pin the tokens ahead of a build.</summary>
    internal static void Arm(int mask, int quality) { BuildMask = HostedMask(mask); BuildQuality = quality; }

    /// <summary>From the pass, beside the toon stage's projection: the camera the content's sun and lens
    /// maths project through (<c>m_view</c>, and the near/far the depth decode needs).</summary>
    internal static void SetCamera(Matrix4x4* view, Matrix4x4* proj)
    {
        _view = *view;
        AlloyLights.TryPerspective((float*)proj, out _, out _, out _near, out _far);
    }

    /// <summary>From <see cref="PostStack.Run"/>, ahead of the stage draw: every engine name the chain reads,
    /// from Alloy's values, onto this program: <c>m_proj</c> and the whole scriptable block through the
    /// renderer's own upload, the rest by name. <c>sceneCurrentFrame</c> is Alloy's presented-frame counter,
    /// not the engine's scene counter -- the chain only ever animates on it. Hot: allocates nothing. Also binds
    /// a LINEAR sampler on the source unit (the stack's textures are NEAREST; rays, chromatic aberration and
    /// the ink tap at fractional offsets), which Run unbinds after the draw.</summary>
    internal static void Prepare(uint src, uint prog, int* viewport)
    {
        if (prog != _prog)
        {
            _prog = prog;
            _feat = Programs.FeatLocs(prog);
            _locView = GlProgram.Loc(prog, "m_view"); _locPixel = GlProgram.Loc(prog, "pixelParams");
            _locClip = GlProgram.Loc(prog, "clipParams"); _locFrame = GlProgram.Loc(prog, "sceneCurrentFrame");
            _locHour = GlProgram.Loc(prog, "moduleHour"); _locMonth = GlProgram.Loc(prog, "moduleMonth");
            _locDay = GlProgram.Loc(prog, "moduleDay"); _locTransition = GlProgram.Loc(prog, "moduleTimeIntoTransition");
            _locPlayer = GlProgram.Loc(prog, "uPlayerPos");
            _locFbColor = GlProgram.Loc(prog, "texFBColor"); _locFbDepth = GlProgram.Loc(prog, "texFBDepth");
        }
        Gl.UseProgram(prog);
        Gl.Uniform1i(_locFbColor, (int)TextureSlots.PostHdr);
        Gl.Uniform1i(_locFbDepth, (int)TextureSlots.PostBloom);
        AlloyRenderer.UploadVanillaEnv(in _feat);
        fixed (Matrix4x4* v = &_view) Gl.UniformMatrix4fv(_locView, 1, 0, (float*)v);
        float w = viewport[2] > 0 ? viewport[2] : 1f, h = viewport[3] > 0 ? viewport[3] : 1f;
        Gl.Uniform3f(_locPixel, 1f / w, 1f / h, w / h);
        // clipParams = (near, far, 1 - near/far): inc_common.shd's own triple
        Gl.Uniform3f(_locClip, _near, _far, 1f - _near / MathF.Max(_far, 1e-6f));
        Gl.Uniform1i(_locFrame, AlloyRenderer.FrameSeq);
        Vector3 p = AlloyRenderer.PlayerPos;
        Gl.Uniform3f(_locPlayer, p.X, p.Y, p.Z);
        Gl.Uniform1i(_locHour, AlloyRenderer.ModuleHour);
        Gl.Uniform1i(_locMonth, AlloyRenderer.ModuleMonth);
        Gl.Uniform1i(_locDay, AlloyRenderer.ModuleDay);
        Gl.Uniform1f(_locTransition, AlloyRenderer.ModuleTimeIntoTransition);
        if (_sampler == 0 && Gl.GenSamplers != null)
        {
            uint s; Gl.GenSamplers(1, &s); _sampler = s;
            Gl.SamplerParameteri(s, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(s, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        }
        if (_sampler != 0) Gl.BindSampler(TextureSlots.PostHdr, _sampler);
    }

    /// <summary>The stage text: the environment the chain reads (inc_common/inc_uniforms as the fragment half
    /// of a post program sees them, under the engine's names), then the chain itself off the resource chain.
    /// <c>inc_postpr</c> defines <c>POSTPROCESSING</c>/<c>LIGHTING</c>/<c>FOG</c> for itself, so none of those
    /// are set here; <c>inc_lighting</c>/<c>inc_fog</c>, which it then includes, are the engine's forward
    /// pipeline and are skipped -- no post term reads them.</summary>
    internal const string FbPostFs = """
        // #352: the engine's post program (fsfbpostpr = inc_postpr + main), hosted. What is ahead of the
        // region is the ENVIRONMENT: the names inc_common/inc_uniforms give a fragment post stage, from
        // Alloy's values. Between the markers is the resource chain's inc_postpr; the stub is a pass-through.
        #define CX_ALLOY 1
        #define SHADER_TYPE 2
        #define GAMMA_CORRECTION 1
        #define SHADER_QUALITY_MODE $SQM$
        #define POSTPROCESSING_TYPES_ENABLED $POSTMASK$
        #define texture2D texture
        #define textureCube texture
        #define vVertexTexCoords vUV
        // texFBColor / texFBDepth are the chain's OWN declarations (inc_framebuffer); Prepare binds them to
        // the stack's units by name, so no macro stands between the two and no line has to be stripped.
        #ifndef PI
        #define PI 3.14159265359
        #endif
        uniform highp vec3 pixelParams;
        #define vPixelSize (pixelParams.xy)
        #define invScreenWidth (pixelParams.x)
        #define invScreenHeight (pixelParams.y)
        #define fAspectRatio (pixelParams.z)
        uniform highp vec3 clipParams;
        #define nearClip (clipParams.x)
        #define farClip (clipParams.y)
        #define fDepthToLinearModifier (clipParams.z)
        uniform highp mat4 m_view;
        uniform highp mat4 m_proj;
        uniform int sceneCurrentFrame;
        uniform int moduleHour;
        uniform int moduleMonth;
        uniform int moduleDay;
        uniform highp vec3 uPlayerPos;
        #define playerPosition uPlayerPos
        float sqr(float a) { return a * a; }
        float ColorToLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
        float ColorToLuma(vec4 c) { return ColorToLuma(c.rgb); }

        // $VANILLA-SKIP inc_lighting inc_fog inc_shadow
        // $VANILLA inc_postpr
        vec4 FS_PostProcess() { return texture(uSrc, vUV); }
        // $VANILLA-END

        vec4 CxStage(vec4 c)
        {
            vec4 o = FS_PostProcess();
            o.a = c.a;
            return o;
        }
        """;

    /// <summary>Off-device: the mask stripping is what the doc says, the gate is effect 2's bit, and the text
    /// neither claims the chain's own defines nor loses its stub.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(HostedMask(0x3f) == 0x09, "sharpen (0x01) and vibrance (0x08) are hosted; DOF, dynamic contrast, gamma, toon are stripped");
        check.Expect(EffectPost == 4u, "the gate is Camera::GetIsFBEffectEnabled(2)'s bit");
        check.Expect(!FbPostFs.Contains("#define LIGHTING") && !FbPostFs.Contains("#define FOG") && !FbPostFs.Contains("#define POSTPROCESSING 1"),
          "inc_postpr's own defines are left to it");
        check.Expect(FbPostFs.Contains("// $VANILLA inc_postpr\n") && FbPostFs.Contains("vec4 FS_PostProcess() {"), "the region and its stub");
        check.Expect(FbPostFs.Contains("$POSTMASK$") && FbPostFs.Contains("$SQM$"), "the option tokens are in the text");
        check.Expect(!ShaderResource.Tokens("$POSTMASK$ $SQM$").Contains('$'), "the tokens expand");
        check.Expect(!Stale(0, 0), "an unbuilt stage is never stale");
        Arm(0x3f, 2);
        check.Expect(BuildMask == 0x09 && !Stale(0x3f, 2) && !Stale(0x39, 2) && Stale(0x3e, 2) && Stale(0x3f, 1), "stale = a hosted bit or the quality moved");
        BuildMask = -1; BuildQuality = -1;
        return check.Ok;
    }
}
