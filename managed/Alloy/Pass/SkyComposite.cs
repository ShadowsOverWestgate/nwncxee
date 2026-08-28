using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#222: the skybox's display-space scratch target, and the one pass that folds it back into the
/// linear frame.
/// <para><b>Why it exists.</b> #132 made the Alloy frame LINEAR and moved the frame's one sRGB encode to
/// <see cref="PostChain"/>'s resolve. The engine composites the sky in DISPLAY space
/// (<c>Scene::RenderSkyBoxes</c> @0x466910, decompiled: <c>SetBlendFunc(0,3)</c> = SRC_ALPHA/ZERO for the
/// current sky, <c>(0,1)</c> = SRC_ALPHA/ONE_MINUS_SRC_ALPHA for the incoming one and the fade gob). Those
/// two disagree wherever a fragment's alpha is not 1: the engine writes <c>a*S</c>, we wrote
/// <c>enc(a*dec(S))</c> = <c>a^(1/2.2)*S</c>, brighter by <c>a^-0.545</c> -- 1.5x at alpha 0.5, 4x at 0.1.
/// The skyfade gob's horizon ramp is exactly that fragment, which is why the Alloy sky carried a bright
/// horizon glow vanilla does not have, and why hairline seams in the tileset read as glowing white lines
/// (#221: <c>/sky</c> made them disappear).</para>
/// <para><b>Why not a shader fix.</b> A per-fragment premultiply (<c>rgb *= pow(a, 1.2)</c>) is exact only
/// against a ZERO destination. The ramp rides the OVER-blend role, where the destination term gets no such
/// correction and the result undershoots as badly as it used to overshoot -- measured on the real values:
/// vanilla 0.300, before 0.448, premultiplied 0.219. <c>dec(a*enc(S) + (1-a)*enc(D))</c> needs the
/// destination, and fixed-function blending against a linear target cannot express it. So the sky gets a
/// destination it CAN blend against: an 8-bit display-space scratch.</para>
/// <para><b>Why the scratch needs no coverage channel.</b> It is cleared to the engine's clear colour in
/// display space -- the same value the HDR target holds (linearised) everywhere the sky does not cover. So
/// compositing the whole background is a no-op outside the dome: those texels decode back to exactly what
/// is already there. The composite still depth-tests, so nothing lands in front of the world.</para>
/// <para>Refusal is survivable here, unlike <see cref="PostChain"/>'s: a sky that cannot get its scratch
/// draws the old way (linear, slightly bright) rather than standing the frame down. It is a look bug, and a
/// look bug is not worth a black sky.</para></summary>
internal static unsafe class SkyComposite
{
    /// <summary>The unit the composite samples the scratch on. Shares <see cref="TextureSlots.PostHdr"/> for
    /// the same reason grass does: different programs, never in one bracket.</summary>
    private const uint ScratchUnit = TextureSlots.PostHdr;

    private static ExtenderLog? _log;
    private static uint _fbo, _tex, _prog;
    private static int _w, _h, _uTex;
    private static bool _failed;

    /// <summary>Is the scratch bound and being drawn into right now? <c>SkyOwnedPass</c> reads it to decide
    /// whether its fragment stage writes display or linear.</summary>
    internal static bool Active { get; private set; }

    internal static void Configure(ExtenderLog log) => _log = log;

    /// <summary>Point the sky's draws at the display-space scratch. False = draw the old way.</summary>
    internal static bool Begin(int* viewport, float* clearRgba)
    {
        Active = false;
        if (_failed || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null
            || Gl.ClearColor == null || Gl.Clear == null) return false;
        if (!EnsureProgram() || !EnsureTarget(viewport[2], viewport[3])) return false;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        // The clear colour RAW, not linearised: this target holds display-space values, which is the whole
        // point of it. PostChain linearises the same numbers for the HDR target, and the composite below
        // undoes the difference.
        Gl.ClearColor(clearRgba[0], clearRgba[1], clearRgba[2], clearRgba[3]);
        Gl.Clear(Gl.COLOR_BUFFER_BIT);
        // No depth attachment: the dome is drawn unconditionally here and the DEPTH test that keeps the sky
        // behind the world happens once, at the composite. Testing it twice would need the engine's depth
        // attached to a second framebuffer, and it is already attached to PostChain's.
        Gl.Disable(Gl.DEPTH_TEST);
        Active = true;
        return true;
    }

    /// <summary>Fold the scratch back into the linear frame: one full-screen triangle, decoding as it goes,
    /// depth-tested exactly the way the sky's own draws used to be so it lands only where the world wrote no
    /// depth. <paramref name="hdrFbo"/> is the framebuffer the Alloy pass was drawing into.</summary>
    internal static void End(uint hdrFbo)
    {
        if (!Active) return;
        Active = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, hdrFbo);
        Gl.UseProgram(_prog);
        Gl.ActiveTexture(Gl.TEXTURE0 + ScratchUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.Uniform1i(_uTex, (int)ScratchUnit);
        // Same state the sky drew under before this class existed: depth mask off, the far plane, LEQUAL, so
        // the quad survives only where nothing has written depth. No blend -- the scratch already carries the
        // background it composited over.
        new PassState(depth: true, depthWrite: false).Apply();
        Gl.DepthRange(1.0, 1.0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.DepthRange(0.0, 1.0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>Drop the GL names. The context that owns them is going away, so this only ever runs where a
    /// delete is legal -- see docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _fbo = 0; _tex = 0; _prog = 0; _w = 0; _h = 0; _failed = false; Active = false;
    }

    private static bool EnsureTarget(int w, int h)
    {
        if (_tex != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_tex != 0 && Gl.DeleteTextures != null) { uint old = _tex; Gl.DeleteTextures(1, &old); _tex = 0; }
        _w = w; _h = h;
        uint tex;
        Gl.GenTextures(1, &tex);
        // RGBA8, i.e. display space and 8 bits -- deliberately the engine's own precision. The sky needs no
        // headroom: it is a background, and every value in it is one the 8-bit frame could already show.
        RenderTarget.AllocColor(tex, false, w, h, Gl.NEAREST);
        _tex = tex;
        if (_fbo == 0) { uint fbo; Gl.GenFramebuffers(1, &fbo); _fbo = fbo; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"SkyComposite: scratch framebuffer incomplete (0x{status:x}) -- the sky keeps the "
                       + "linear path, which is #222's bright horizon");
            return false;
        }
        return true;
    }

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        // The OVERRIDABLE link, and the vertex half is the post chain's shared full-screen stage -- the
        // same pairing PostChain's own three use. Registering the resref in ShaderStages.All and then
        // building past the override would be a contract that only looks kept.
        _prog = GlProgram.Graphics(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs),
                                   ShdSkyCompositeFs, ShaderStages.Source(ShdSkyCompositeFs),
                                   _log, "SkyComposite", out _, out string fsSrc);
        // #161's shape: an override that drops the decode puts #222's bright horizon straight back, and it
        // would link cleanly and say nothing. Refuse it and rebuild from the embedded text.
        if (_prog != 0 && !fsSrc.Contains("pow("))
        {
            Gl.DeleteProgram(_prog);
            _log?.Info("SkyComposite: the cx_skycomp_fs override is refused -- it drops the decode this "
                       + "stage exists to perform. Rebuilding from the embedded default.");
            _prog = GlProgram.Graphics(PostChain.FullscreenVs, SkyCompositeFs, _log, "SkyComposite (embedded)");
        }
        if (_prog == 0) { _failed = true; return false; }
        _uTex = GlProgram.Loc(_prog, "uSky");
        return true;
    }

    /// <summary>The override resref for the composite. Overridable like every other stage, and the one thing
    /// an override must keep is the decode: this stage is the boundary between the sky's display-space
    /// compositing and the linear frame, so a copy that drops the <c>pow</c> puts the glow straight back.</summary>
    internal const string ShdSkyCompositeFs = "cx_skycomp_fs";

    internal const string SkyCompositeFs = """
        #version 460 core
        in vec2 vUV;
        uniform sampler2D uSky;
        out vec4 frag;
        void main()
        {
            // The scratch holds what the ENGINE would have put on screen. The frame is linear and the
            // resolve encodes it again, so decode once here and the round trip is exact.
            frag = vec4(pow(max(texture(uSky, vUV).rgb, vec3(0.0)), vec3($SRGB$)), 1.0);
        }
        """;

    /// <summary>Off-device: the arithmetic this whole class exists for. The engine's display-space blend, run
    /// on the same numbers three ways -- what vanilla produces, what the linear frame produced before this
    /// ticket, and what the premultiply that looked like a one-line fix produces. The first and the third are
    /// what the scratch path has to reproduce and avoid; a change that makes them agree has undone the fix.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SkyComposite.SelfCheck FAIL: {w}"); } }
        const float g = 2.2f;
        float Dec(float v) => (float)System.Math.Pow(v, g);
        float Enc(float v) => (float)System.Math.Pow(v, 1.0 / g);

        // the measured case: the fade gob (display 0.9) over the sky (display 0.15) at fragment alpha 0.2
        const float s = 0.9f, d = 0.15f, a = 0.2f;
        float vanilla = a * s + (1f - a) * d;
        float linearFrame = Enc(a * Dec(s) + (1f - a) * Dec(d));
        float premultiplied = Enc((float)System.Math.Pow(a, g) * Dec(s) + (1f - a) * Dec(d));
        E(vanilla > 0.29f && vanilla < 0.31f, $"the engine's display blend is 0.300, not {vanilla:F3}");
        E(linearFrame > vanilla * 1.4f,
          $"the pre-#222 linear frame really was much brighter ({linearFrame:F3} vs {vanilla:F3})");
        E(premultiplied < vanilla * 0.8f,
          $"...and the premultiply really does undershoot ({premultiplied:F3} vs {vanilla:F3}), which is why "
          + "this class composites in display space instead of correcting in the shader");

        // #222 second pass: Mat()'s fog branch is an ALPHA override as well as a colour one. A tinted part
        // draws at alpha 1.0, so a change that keeps the tint and re-applies the role alpha fades the tint
        // straight back out -- the two halves are one branch in the engine and must stay one here.
        E(SkyCompositeFs.Contains("pow("), "the composite still decodes");

        // and the round trip the composite performs: a display value that goes through the scratch, the
        // decode here and the resolve's encode must come back unchanged. This is the ONE property the whole
        // arrangement rests on, and it is pure arithmetic.
        foreach (float v in new[] { 0f, 0.15f, 0.5f, 0.9f, 1f })
        {
            float round = Enc(Dec(v));
            E(System.Math.Abs(round - v) < 1e-4f, $"decode-then-encode returns {v:F3}, not {round:F3}");
        }
        log.Info(ok ? "SkyComposite.SelfCheck: passed" : "SkyComposite.SelfCheck: FAILED");
        return ok;
    }
}
