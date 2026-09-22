using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// #493, visibility-buffer slice 1: the vis-ID target. Alongside the depth prepass the opaque range writes a
/// second attachment, one <c>R32UI</c> per render pixel packing the RECORD slot (the ownership slot, stable
/// across frames -- not the per-pass draw index, which the cull reorders) and <c>gl_PrimitiveID</c>. Zero is
/// "no owned opaque draw". Shading reads nothing from it yet; this slice proves it correct and cheap.
/// <para>NOT the SSAO "visibility" term of <c>Issue.cs</c> -- that name was taken, so this one
/// is the ID target / vis-ID everywhere in code and logs.</para>
/// <para>It rides the prepass draws themselves (same FBO, same view-projection, same viewport), so render scale
/// and the TAA jitter follow by construction: whatever the prepass depth is written at, so is this.</para>
/// <para>Coverage is the prepass's: plain and keyhole sweeps plus the feature sweep (cutouts and wind-displaced
/// batches discard through <c>DepthFeatFs</c>, so their IDs are cut to the same holes). The roof pair and
/// flowmap stay out of the prepass and so out of the ID target -- forward-only, decided rather than pending.</para>
/// <para>ponytail: 18 bits of slot (<see cref="AlloyRenderer.MaxRecords"/> + 1 &lt; 2^18) and 14 of primitive, so
/// a single draw past 16383 triangles wraps its primitive field. RG32UI is the upgrade if a mesh is ever that big.</para>
/// </summary>
internal static unsafe class IdTarget
{
    /// <summary>Bits the primitive id keeps below the slot.</summary>
    internal const int PrimBits = 14;
    /// <summary><c>/alloy viz</c> mode that paints the target.</summary>
    internal const int VizMode = 40;
    /// <summary><c>/alloy idtarget</c>: is the second attachment written at all (default on).</summary>
    internal static bool On => Renderer.IdTarget.V;

    private static ExtenderLog? _log;
    private static uint _tex, _prog;
    private static int _w, _h, _locTex;
    private static bool _failed, _built, _armed, _checked;
    private static int _writes;

    /// <summary>The vis-ID texture, 0 until the first prepass has written it at this size.</summary>
    internal static uint Tex => _tex;
    /// <summary>Attached and cleared this pass: the retest call and the viz may rely on it.</summary>
    internal static bool Armed => _armed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names are gone.</summary>
    internal static void Forget() { _tex = 0; _prog = 0; _w = 0; _h = 0; _built = false; _failed = false; _armed = false; _checked = false; }

    /// <summary>The GLSL both halves share: the fragment output and the pack. Concatenated into every depth
    /// fragment stage so the three write the identical word.</summary>
    internal static string OutDecl => ShaderSource.Get("IdTarget.OutDecl.glsl");

    /// <summary>C# twin of <c>CxVisId</c>, for the self-check and a later CPU readback.</summary>
    internal static uint Pack(int slot, int prim) => ((uint)(slot + 1) << PrimBits) | ((uint)prim & ((1u << PrimBits) - 1));
    internal static int Slot(uint id) => (int)(id >> PrimBits) - 1;
    internal static int Prim(uint id) => (int)(id & ((1u << PrimBits) - 1));

    /// <summary>Attach the target at COLOR_ATTACHMENT1 of the bound frame FBO and route the fragment output
    /// to it (draw buffer 0 = NONE, so the depth sweeps still write no colour). <paramref name="clear"/> on
    /// phase one only; the retest lands on the same image. The caller enables the colour mask.</summary>
    internal static bool Begin(int w, int h, bool clear)
    {
        _armed = false;
        if (!On || _failed || Gl.DrawBuffers == null || Gl.ClearBufferuiv == null) return false;
        if (!EnsureTarget(w, h)) return false;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _tex, 0);
        uint* bufs = stackalloc uint[2] { Gl.NONE, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        // once per allocation, not per frame: driver-side validation is a CPU cost inside the Prepass term
        if (!_checked)
        {
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            if (status != Gl.FRAMEBUFFER_COMPLETE)
            {
                End();
                _failed = true;
                _log?.Info($"IdTarget: the frame FBO is incomplete with the R32UI attachment (0x{status:x}) -- the ID target stands down on this context");
                return false;
            }
            _checked = true;
        }
        // draw-buffer INDEX 1, not the attachment enum; after DrawBuffers or the clear lands nowhere
        if (clear) { uint zero = 0; Gl.ClearBufferuiv(Gl.COLOR, 1, &zero); _writes++; }
        _armed = true;
        return true;
    }

    /// <summary>Detach and restore the FBO's single draw buffer. The caller runs it on every prepass exit
    /// that reached Begin: the draw-buffer state is the FBO's and persists, so a skipped End leaves the
    /// colour pass writing to NONE.</summary>
    internal static void End()
    {
        if (Gl.DrawBuffers == null) return;
        uint buf = Gl.COLOR_ATTACHMENT0;
        Gl.DrawBuffers(1, &buf);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, 0, 0);
    }

    /// <summary>The viz: hash every pixel's ID to a colour over the bound frame target, depth off. The target
    /// is detached by then, so sampling it is not the attached-texture feedback loop.</summary>
    internal static void Paint(int* viewport)
    {
        bool armed = _armed;
        _armed = false;   // per pass: a frame whose prepass stood down must not paint last frame's image
        if (!armed || _tex == 0) return;
        if (!_built) EnsureProgram();
        if (_prog == 0) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locTex, 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
    }

    internal static string Stats(int frames)
    {
        int n = _writes; _writes = 0;
        return _failed ? "idtarget STOOD DOWN" : !On ? "idtarget off" : n == 0 ? "idtarget idle"
             : $"idtarget {(float)n / System.MathF.Max(frames, 1):F2} writes/frame at {_w}x{_h} R32UI";
    }

    private static bool EnsureTarget(int w, int h)
    {
        if (_tex != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_tex == 0) { uint t; Gl.GenTextures(1, &t); _tex = t; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R32UI, w, h, 0, Gl.RED_INTEGER, Gl.UNSIGNED_INT, null);
        // an integer texture under LINEAR is incomplete and samples zero
        RenderTarget.Clamp(Gl.NEAREST);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        _w = w; _h = h; _checked = false;
        _log?.Info($"IdTarget: vis-ID target {w}x{h} R32UI");
        return true;
    }

    private static void EnsureProgram()
    {
        _built = true;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, VizFs, _log, "IdTarget viz");
        if (_prog != 0) _locTex = GlProgram.Loc(_prog, "uId");
    }

    /// <summary>Integer hash (lowbias32) of the whole word to rgb: neighbouring triangles land on unrelated
    /// colours, the same triangle on the same colour every frame. Black = no owned opaque draw.</summary>
    internal static string VizFs => ShaderSource.Get("IdTarget.VizFs.glsl");

    /// <summary>Off-device: the pack round-trips at the record ceiling, the three depth stages carry the one
    /// output text, and the viz mode is inside <see cref="AlloyRenderer.LightVizMax"/>.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int top = AlloyRenderer.MaxRecords - 1;
        check.Expect(Slot(Pack(top, 16383)) == top && Prim(Pack(top, 16383)) == 16383, "slot and primitive round-trip at the ceilings");
        check.Expect(Pack(0, 0) != 0 && Slot(Pack(0, 0)) == 0, "slot 0 packs non-zero (zero is the empty pixel)");
        check.Expect((long)AlloyRecordsPlusOne() << PrimBits <= uint.MaxValue, "MaxRecords + 1 fits above the primitive bits");
        check.Expect(OutDecl.Contains("<< 14") && PrimBits == 14, "the GLSL pack and PrimBits agree");
        check.Expect(AlloyRenderer.DepthFs.Contains(OutDecl) && AlloyRenderer.DepthKeyFs.Contains(OutDecl) && AlloyRenderer.DepthFeatFs.Contains(OutDecl),
          "every depth fragment stage declares the vis-ID output");
        check.Expect(AlloyRenderer.DepthFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);") && AlloyRenderer.DepthKeyFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);")
          && AlloyRenderer.DepthFeatFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);"), "...and writes the record slot, not the draw index");
        check.Expect(AlloyRenderer.DepthVs.Contains("flat out int vDraw;") && AlloyRenderer.DepthVs.Contains("vDraw = id;"), "DepthVs hands the slot down");
        check.Expect(VizMode <= AlloyRenderer.LightVizMax, "the viz mode is inside LightVizMax (or /alloy viz clamps it away)");
        check.Expect(VizFs.Contains("usampler2D") && VizFs.Contains("texelFetch"), "the viz reads the integer target unfiltered");
        log.Info(check.Ok ? "IdTarget.SelfCheck: passed" : "IdTarget.SelfCheck: FAILED");
        return check.Ok;
    }

    private static long AlloyRecordsPlusOne() => AlloyRenderer.MaxRecords + 1;
}
