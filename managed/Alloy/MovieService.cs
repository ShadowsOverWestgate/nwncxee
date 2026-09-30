using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Ui;

namespace CX.Alloy;

/// <summary>The NDC quad and texture window one movie frame is drawn as: the letterbox fit, as a value.
/// Pure arithmetic over the decoded frame size, the texture size and the viewport, so it is checkable
/// off-device (<see cref="MovieService.SelfCheck"/>).</summary>
internal readonly unsafe struct MoviePlate
{
    /// <summary>Half-extent of the quad in normalised device coordinates. The larger of the two is always
    /// exactly 1 -- the movie touches the viewport on its long axis and gets bars on the other.</summary>
    internal readonly float Sx, Sy;
    /// <summary>The texture window: the decoded frame occupies only the top-left <c>frame/tex</c> corner of a
    /// padded texture, and the bounds are inset by half a texel so a LINEAR fetch never reaches the pad.</summary>
    internal readonly float U0, U1, V0, V1;

    private MoviePlate(float sx, float sy, float u0, float u1, float v0, float v1)
    { Sx = sx; Sy = sy; U0 = u0; U1 = u1; V0 = v0; V1 = v1; }

    /// <summary>Fit a <paramref name="frameW"/>x<paramref name="frameH"/> frame into the viewport, preserving
    /// its aspect. The port of <c>CExoMoviePlayerInternal::ShowFrame</c>'s ortho: the engine builds
    /// <c>SetOrthoTransform(-right, right, -top, top)</c> around a fixed [-1,1] quad, which is the same
    /// picture as a [-1/right, 1/right] quad in clip space -- and the GUI scale the engine multiplies into
    /// both terms cancels, so it is not a term here.</summary>
    internal static MoviePlate Fit(int frameW, int frameH, int texW, int texH, int vpW, int vpH)
    {
        float a = (float)frameW / vpW, b = (float)frameH / vpH;
        float m = a > b ? a : b;
        return new MoviePlate(a / m, b / m,
                              0.5f / texW, (frameW - 0.5f) / texW,
                              0.5f / texH, (frameH - 0.5f) / texH);
    }

    /// <summary>The four triangle-strip vertices (x, y, u, v), screen-top at V0: the decoder uploads the
    /// frame's first row first, and the engine's quad maps it to the top of the screen.</summary>
    internal void Write(float* v)
    {
        v[0] = -Sx; v[1] = Sy; v[2] = U0; v[3] = V0;
        v[4] = -Sx; v[5] = -Sy; v[6] = U0; v[7] = V1;
        v[8] = Sx; v[9] = Sy; v[10] = U1; v[11] = V0;
        v[12] = Sx; v[13] = -Sy; v[14] = U1; v[15] = V1;
    }
}

/// <summary>
/// CX-99 (map CX-94, <c>docs/research/total-render-ownership.md</c> §3): the movie's pixels are CX's.
/// </summary>
/// <remarks>
/// <para><b>What moved, and what deliberately did not.</b> A movie bypasses the whole frame:
/// <c>Update</c> @0x430030 skips its clear, its string layer and its present while one plays, and
/// <c>CExoMoviePlayerInternal::RenderFrame</c> @0x11694b0 runs its own loop -- decode a packet, sleep off
/// <c>GetFrameTimeOffset</c>, repeat until a video frame lands -- then <c>EndScene(1)</c> and
/// <c>SwapBuffers</c>. That loop IS the A/V clock (the audio is queued to OpenAL from the same
/// <c>UpdateMovie</c> call), so none of it is touched. The one thing swallowed is
/// <c>ShowFrame</c> @0x1165e30, which is the clear plus the one textured quad and nothing else.</para>
/// <para><b>Where it draws.</b> In the ENGINE's context with CX's own core-profile program, the way the UI
/// layer draws today -- not inside the 4.6 bracket. The movie texture is the engine's, the movie is not a
/// world frame, and <see cref="PresentTarget"/> exists precisely because the 4.6 context must not write
/// framebuffer 0 on Windows. CX-101 unifies every CX 2D surface into Alloy's present; this is one of its
/// inputs, not a second present path.</para>
/// <para><b>The gate is the session's renderer choice</b> (<c>alloy.enabled</c>, read as <c>Boot</c>), not
/// <see cref="AlloyGate"/>: movies play at the main menu, where the frame verdict is <c>Idle</c> by
/// construction, and a gate that asked for a world frame would never fire on the intro. Nothing is hooked
/// at all in vanilla mode, so the engine's movie is bit-identical there.</para>
/// <para>Cold by frame standards -- once per decoded video frame, ~24-30 a second -- but allocation-free
/// anyway, because it runs on the render thread.</para>
/// </remarks>
public sealed unsafe partial class MovieService : IExtenderService
{
    private static ExtenderLog? _log;
    private static MovieShowFrameFn _orig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);
        // #263's shape: read off the knob, not asked of AlloyGate, so this does not depend on service
        // discovery order -- and so vanilla installs nothing.
        if (!Renderer.Enabled.Boot)
        { log.Info("Movies: standing down (alloy.enabled=0) -- the engine draws its own movie quad"); return; }
        _orig = hooks.InstallMovieShowFrame(&OnShowFrame);
        if (_orig == null)
        { log.Info("Movies: CExoMoviePlayerInternal::ShowFrame did not install -- the engine keeps the movie draw"); return; }
        FrameRegistry.Global.Register(FramePhase.PostPresent, 5, AgePresent, "Movie.onscreen", log);
        log.Info("Movies: Alloy draws the movie frame (the engine keeps the decode, the pacing and the present)");
    }

    // ---- "a movie owns the screen" ----

    /// <summary>Presents since the last movie frame was drawn, saturating. Two, not one: the roll-call runs
    /// before the present it belongs to, so "this present" is not yet a count.</summary>
    private static int _idle = Stale;
    private const int Stale = 2;

    /// <summary>Is a movie covering the screen? While one is, <see cref="UiHostService"/> holds the window
    /// roll-call back -- chat over a cinematic is not a feature.
    /// <para>The ticket's open question, answered: <c>CGuiMan::UpdateAndRender</c> @0x580450 gates its whole
    /// render block -- the view loop, <c>Nui__Render</c> AND <c>nk_sdl_render</c> -- on
    /// <c>GetIsPlayingMovie() == 0</c> (@0x580568, <c>je</c> past it), so on Linux the roll-call's normal site
    /// simply does not run during a movie. <c>UiHostService.OnSwap</c>'s FALLBACK roll-call is a
    /// different story: it draws at the present, after <c>ShowFrame</c>, and on Windows it is not a fallback
    /// at all -- <c>nk_sdl_render</c> is not exported there, so the chain never installs and every CX window
    /// lands on top of the movie. This flag is what stops that.</para></summary>
    internal static bool OnScreen => _idle < Stale;

    private static void AgePresent() { if (_idle < Stale) _idle++; }

    [UnmanagedCallersOnly]
    private static void OnShowFrame(IntPtr self, int frameW, int frameH, int texW, int texH)
    {
        _idle = 0;
        if (Renderer.Movie.V && Draw(self, frameW, frameH, texW, texH)) return;
        // The engine draws this frame, and its quad opens with Clear(1): ScreenOwner must let that one through.
        ScreenOwner.EngineFrame = true;
        if (_orig != null) _orig(self, frameW, frameH, texW, texH);
        ScreenOwner.EngineFrame = false;
    }

    // ---- the draw ----

    private static uint _prog, _vao, _vbo, _sampler;
    private static bool _samplerSaid;
    private static int _uTex;
    private static bool _progTried, _vaoUp;
    private static bool _stood, _announced;

    /// <summary>ADR 0025 / CX-45: our own mechanism failing is loud and hands the frame back -- never a quiet
    /// fallback. Said once per session; the engine's quad draws every frame after it.</summary>
    private static bool Stand(string why)
    {
        if (_stood) return false;
        _stood = true;
        _log?.Always($"Movies: Alloy cannot draw this movie frame -- {why}. The engine's own quad is drawing it "
                   + "(set alloy.movie 0 to make that the deliberate choice).");
        return false;
    }

    /// <summary>Clear the viewport and put the frame on it. False = the engine's quad should run instead.</summary>
    private static bool Draw(IntPtr self, int frameW, int frameH, int texW, int texH)
    {
        if (_log == null) return false;
        if (frameW <= 0 || frameH <= 0 || texW < frameW || texH < frameH)
            return Stand($"the decoder handed us a {frameW}x{frameH} frame in a {texW}x{texH} texture");
        if (!Gl.Init(_log)) return Stand("the GL entry points did not resolve");
        if (Gl.Clear == null || Gl.ClearColor == null || Gl.GetFloatv == null || Gl.ColorMask == null)
            return Stand("a core GL entry point did not resolve");
        uint tex = new CExoMoviePlayerInternal(self).Texture;
        if (tex == 0) return Stand("the movie player holds no GL texture");
        if (!EnsureProgram()) return Stand("the movie program did not build");

        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        if (vp[2] <= 0 || vp[3] <= 0) return Stand($"the GL viewport is {vp[2]}x{vp[3]}");

        if (!_vaoUp) SetupVao();
        if (_vao == 0 || _vbo == 0) return Stand("the movie VAO did not build");

        // The engine's Clear(1) is scissor-limited like ours, and its clear colour is whatever the last
        // pass left; black is what a letterbox is, so it is set explicitly and put back.
        float* clear = stackalloc float[4];
        Gl.GetFloatv(Gl.COLOR_CLEAR_VALUE, clear);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Disable(Gl.BLEND);
        Gl.ColorMask(1, 1, 1, 1);
        Gl.ClearColor(0f, 0f, 0f, 1f);
        Gl.Clear(Gl.COLOR_BUFFER_BIT);
        Gl.ClearColor(clear[0], clear[1], clear[2], clear[3]);

        float* verts = stackalloc float[16];
        MoviePlate.Fit(frameW, frameH, texW, texH, vp[2], vp[3]).Write(verts);

        Gl.UseProgram(_prog);
        Gl.Uniform1i(_uTex, 0);
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(16 * sizeof(float)), verts, Gl.DYNAMIC_DRAW);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        // A sampler object, never glTexParameteri: the texture is the engine's and it caches what it set on
        // it (#237). The engine's own filter here is NEAREST (RenderFrame opens the loop with
        // SetTextureParameteri(0, 2, 4) and (0, 3, 4), and 4 is GL_NEAREST); LINEAR is a deliberate difference.
        if (_sampler == 0 && (_sampler = Gl.LinearClampSampler()) == 0 && !_samplerSaid)
        {
            _samplerSaid = true;
            _log?.Always("MovieService: no sampler object -- movie frames sample with the engine's NEAREST filter");
        }
        if (_sampler != 0 && Gl.BindSampler != null) Gl.BindSampler(0, _sampler);
        Gl.DrawArrays(Gl.TRIANGLE_STRIP, 0, 4);
        if (_sampler != 0 && Gl.BindSampler != null) Gl.BindSampler(0, 0);

        // #210 (G1): raw GL in the engine's context went past GLRender's shadow cache, and the decode loop
        // binds this very texture again on its next packet -- a stale cache entry there would upload the
        // next frame into whatever WE left bound.
        UiHostService.HandBack();
        Gl.DrainErrors(4);

        if (!_announced)
        {
            _announced = true;
            _log.Info($"Movies: first frame drawn by Alloy -- {frameW}x{frameH} in a {texW}x{texH} texture, "
                    + $"viewport {vp[2]}x{vp[3]}; the engine's ShowFrame quad is swallowed from here on");
        }
        return true;
    }

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\nout vec2 vUV;\n" +
        "void main(){ gl_Position = vec4(aPos, 0.0, 1.0); vUV = aUV; }\n";
    /// <summary>Straight out, no colour-space step: the stage the engine draws this quad through is one of
    /// its UNLIT ones (<c>fsglu</c>), which neither linearises the texel nor re-encodes the result, and
    /// <c>FRAMEBUFFER_SRGB</c> is off client-wide. Alpha is forced to 1 -- blending is off, so the engine's
    /// texel alpha reaches nothing but the unused default-framebuffer alpha channel.</summary>
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = vec4(texture(uTex, vUV).rgb, 1.0); }\n";

    private static bool EnsureProgram()
    {
        if (_progTried) return _prog != 0;
        _progTried = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrc);
        if (vs == 0 || fs == 0) return false;
        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs); Gl.LinkProgram(_prog);
        int ok; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _prog = 0; return false; }
        _uTex = GlProgram.Loc(_prog, "uTex");
        return true;
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* sp = bytes)
        {
            byte* pp = sp; int len = bytes.Length;
            Gl.ShaderSource(sh, 1, &pp, &len);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) return sh;
        byte* buf = stackalloc byte[512]; int n = 0;
        Gl.GetShaderInfoLog(sh, 512, &n, buf);
        _log?.Always($"Movies: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
        return 0;
    }

    private static void SetupVao()
    {
        _vaoUp = true;
        uint a; Gl.GenVertexArrays(1, &a); _vao = a;
        uint b; Gl.GenBuffers(1, &b); _vbo = b;
        if (_vao == 0 || _vbo == 0) return;
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        int stride = 4 * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.BindVertexArray(0);
    }
}
