using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// CX-95/CX-96: the GL side of Alloy's GUI path. <see cref="GuiCapture"/> turns every GUI scene into layers of
/// captured draws; this issues them from the 4.6 context in ONE bracket per flush, one program, the records in one
/// SSBO read through <c>gl_BaseInstance</c>. Two flushes a frame, at the two points the engine's draw order needs:
/// just before nuklear's (<c>CGuiMan::UpdateAndRender</c> renders every panel view, then NUI, then the two overlay
/// views at <c>CGuiMan+0x40/+0x48</c>), and at the present for those overlays.
/// <para>The gate is its own, not <see cref="AlloyGate"/>'s world verdict: the GUI has no world frame to wait for, so
/// it is Alloy's from the first present the 4.6 context and this program exist, main menu included. Windows draws
/// into <see cref="PresentTarget"/>'s texture between a take-back and a hand-back blit, for the reason that type
/// exists at all. ponytail: that is two full-screen blits per flush and three flushes a frame (panels, NUI, overlays);
/// moving the CX windows into the 4.6 context would let the whole GUI composite once and blit once.</para>
/// </summary>
internal static unsafe class GuiPass
{
    private const uint BindGui = 31;
    /// <summary>Ahead of <c>UiHostService</c>'s window roll-call (20) on the <c>nk_sdl_render</c> chain: the panels sit
    /// under the CX windows and NUI, as they do in the engine's order.</summary>
    private const int NkPriority = 40;
    private const int Vec4PerRec = GuiCapture.RecFloats / 4;

    private static ExtenderLog? _log;
    private static bool _armed, _progTried, _failed, _live, _dropSaid;
    private static uint _prog, _buf, _vtxBuf, _idxBuf, _fbo, _depth;
    private static long _bufCap, _vtxCap, _idxCap;
    private static int _locBoneStart, _locGamma, _depthW, _depthH;
    private static int* _screenW, _screenH;
    private static NkSdlRenderFn* _nkSlot;
    private static readonly Dictionary<uint, uint> _streamVao = new(16);
    private static long _flushes, _drawn, _noVao;

    /// <summary>Is the GUI Alloy's right now? Asked by the capture hook per scene; everything it names exists before
    /// the first scene is captured, so a scene is never captured into a flush that cannot draw it.</summary>
    internal static bool Capturing => _armed && !_failed && _prog != 0 && Renderer.Gui.V && GlContextService.Available;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        if (_screenW == null || _screenH == null)
        { log.Always("GuiPass: g_nScreenWidth/Height unresolved -- the GUI stays the ENGINE's draw"); return; }
        if (!GuiCapture.Install(log, hooks)) return;
        ScreenOwner.Install(log, hooks);
        _nkSlot = hooks.ChainNkSdlRender(&OnNk, NkPriority);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 5, OnPresent, "GuiPass.flush", log);
        _armed = true;
        log.Info("GuiPass: armed -- GUI scenes are captured and drawn by Alloy once the 4.6 context is up "
               + (_nkSlot != null ? "(flushed before NUI and at the present)" : "(nk_sdl_render unhooked: flushed at the present, above NUI)")
               + "; /alloy gui 0 hands them back");
    }

    /// <summary>The panels, just before nuklear flushes (and before the CX windows chained under it).</summary>
    [UnmanagedCallersOnly]
    private static void OnNk(float aa)
    {
        Flush();
        (*_nkSlot)(aa);
    }

    /// <summary>The overlay views drawn after NUI, and the one-time program build.</summary>
    private static void OnPresent()
    {
        if (GuiCapture.SlotMissed && !GameGate.InModule) { GuiCapture.SlotMissed = false; AlloyRenderer.DripRearm(); }
        if (_armed && !_progTried && GlContextService.Available && GlContextService.Enter())
        {
            _progTried = true;
            BuildProgram();
            GlContextService.Leave();
        }
        Flush();
        GuiCapture.NewFrame();
        _frameColour = false;
        _seqAtFrame = PostChain.ResolveSeq;
    }

    /// <summary>CX-101: has this frame's colour been cleared, and the world-resolve count at the last present -- a
    /// frame whose count moved had a world frame paint the whole screen, so it needs no clear.</summary>
    private static bool _frameColour;
    private static int _seqAtFrame;

    /// <summary>CX-101: the engine's swallowed frame clear, done here -- once, at the first flush of a frame that no world
    /// frame covered (the menus), in the engine's own clear colour. Unscissored, like <c>Update</c>'s <c>Clear(3)</c>.</summary>
    private static void FrameClear()
    {
        if (_frameColour) return;
        _frameColour = true;
        if (!ScreenOwner.Owned || PostChain.ResolveSeq != _seqAtFrame) return;
        Gl.Disable(Gl.SCISSOR_TEST);
        fixed (float* c = ScreenOwner.ClearRgba) Gl.ClearBufferfv(Gl.COLOR, 0, c);
        Gl.DepthMask(1);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
    }

    private static void BuildProgram()
    {
        if (Gl.DrawElementsInstancedBaseVertexBaseInstance == null || Gl.DrawArraysInstancedBaseInstance == null
            || Gl.CreateBuffers == null || Gl.VertexArrayAttribFormat == null)
        { Fail("glDrawElementsInstancedBaseVertexBaseInstance or DSA is missing"); return; }
        _prog = GlProgram.Graphics(ShaderSource.Get("cx_gui_v.glsl"), ShaderSource.Get("cx_gui_f.glsl"), _log, "Alloy GUI");
        if (_prog == 0) { Fail("the GUI program did not build"); return; }
        _locBoneStart = GlProgram.Loc(_prog, "uBoneStart");
        _locGamma = GlProgram.Loc(_prog, "uGamma");
        uint b;
        Gl.CreateBuffers(1, &b); _buf = b;
        Gl.CreateBuffers(1, &b); _vtxBuf = b;
        Gl.CreateBuffers(1, &b); _idxBuf = b;
        _log?.Info("GuiPass: program built -- the GUI is Alloy's from this frame");
    }

    /// <summary>Our own mechanism failed: said once, the GUI goes back to the engine for the session.</summary>
    private static void Fail(string why)
    {
        _failed = true;
        _log?.Always($"GuiPass: {why} -- the GUI is the ENGINE's draw for the rest of the session");
        Notice.Show(_log, $"Alloy could not draw the interface ({why}); the game's renderer draws it instead.");
    }

    /// <summary>CX-101: flush now -- a caller whose draws must land before the next foreign draw (nuklear's own site).</summary>
    internal static void FlushNow() => Flush();

    /// <summary>Issue every layer captured since the last flush, then forget them. A flush that cannot run drops what
    /// it holds -- the engine did not draw those scenes either -- and says so once.</summary>
    private static void Flush()
    {
        if (GuiCapture.LayerN == 0) return;
        if (_prog == 0 || _failed || !Draw())
        {
            if (!_dropSaid) { _dropSaid = true; _log?.Always("GuiPass: a GUI flush could not enter the 4.6 context -- that frame's GUI is NOT DRAWN"); }
        }
        GuiCapture.Reset();
    }

    /// <summary>ponytail: one GL draw per captured record, state changes elided by comparison. A menu is a few hundred
    /// draws; if the GUI ever shows in the frame zones, sort each layer by (VAO, texture, blend) and multi-draw the runs.</summary>
    private static bool Draw()
    {
        int w = *_screenW, h = *_screenH;
        bool win = PresentTarget.Needed;
        if (win) ProbeDepth();
        if (win && PresentTarget.Width == w && PresentTarget.Height == h) { PresentTarget.TakeBack(); Depth(fromScreen: true); }
        else if (win) Resize(w, h);
        if (!GlContextService.Enter()) return false;
        Upload();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, win ? Target() : 0u);
        Gl.Disable(Gl.FRAMEBUFFER_SRGB);
        Gl.Disable(Gl.MULTISAMPLE);
        Gl.Disable(Gl.STENCIL_TEST);
        FrameClear();
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.FrontFace(Gl.CCW);
        Gl.CullFace(Gl.BACK);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locBoneStart, GuiCapture.DrawN * Vec4PerRec);
        Gl.Uniform1i(_locGamma, PostChain.GammaOff() ? 0 : 1);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindGui, _buf);
        Gl.ActiveTexture(Gl.TEXTURE0);
        int blend = -1, depth = -1, write = -1, cull = -1;
        uint src = 0, dst = 0, vao = uint.MaxValue;
        int tex = -1;
        for (int li = 0; li < GuiCapture.LayerN; li++)
        {
            ref GuiCapture.Layer l = ref GuiCapture.Layers[li];
            // The run's depth clear rides on its FIRST view, drawn or not: skipped with an empty layer, it left last
            // frame's text quads (0.001 nearer) in the buffer, and everything behind them failed there (live, holes).
            if (l.Clear)
            {
                Gl.Disable(Gl.SCISSOR_TEST);
                Gl.DepthMask(1); write = 1;
                Gl.Clear(Gl.DEPTH_BUFFER_BIT | Gl.STENCIL_BUFFER_BIT);
                Gl.Enable(Gl.SCISSOR_TEST);
            }
            if (l.Count == 0 || l.W <= 0 || l.H <= 0) continue;
            Gl.Viewport(l.X, l.Y, l.W, l.H);
            Gl.Scissor(l.SX, l.SY, l.SW, l.SH);
            Gl.DepthRange(l.Near, l.Far);
            for (int di = l.First; di < l.First + l.Count; di++)
            {
                ref GuiCapture.Draw d = ref GuiCapture.Draws[di];
                uint v = d.Stream ? StreamVao(d.Format, d.Stride) : AlloyRenderer.GuiVao(d.Slot);
                if (v == 0) { _noVao++; continue; }
                if (v != vao) { Gl.BindVertexArray(v); vao = v; }
                Toggle(Gl.BLEND, d.Blend, ref blend);
                if (d.Blend && (d.Src != src || d.Dst != dst)) { Gl.BlendFunc(d.Src, d.Dst); src = d.Src; dst = d.Dst; }
                Toggle(Gl.DEPTH_TEST, d.DepthTest, ref depth);
                if ((d.DepthWrite ? 1 : 0) != write) { write = d.DepthWrite ? 1 : 0; Gl.DepthMask((byte)write); }
                Toggle(Gl.CULL_FACE, d.Cull, ref cull);
                if (d.Tex != tex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)d.Tex); tex = d.Tex; }
                if (d.Indexed)
                    Gl.DrawElementsInstancedBaseVertexBaseInstance(d.Prim, d.Count, Gl.UNSIGNED_SHORT, (void*)d.Offset, 1,
                                                                   d.BaseVertex, (uint)di);
                else
                    Gl.DrawArraysInstancedBaseInstance(d.Prim, (int)d.Offset, d.Count, 1, (uint)di);
                _drawn++;
            }
        }
        Gl.BindVertexArray(0);
        Gl.DepthRange(0.0, 1.0);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        GlContextService.Leave();
        if (win) { PresentTarget.HandBack(); Depth(fromScreen: false); }
        _flushes++;
        if (!_live) { _live = true; _log?.Info($"GuiPass: LIVE -- {GuiCapture.LayerN} GUI scene(s), {GuiCapture.DrawN} draw(s) this flush, none reached the engine's GL"); }
        return true;
    }

    private static void Toggle(uint cap, bool on, ref int cur)
    {
        int want = on ? 1 : 0;
        if (want == cur) return;
        if (on) Gl.Enable(cap); else Gl.Disable(cap);
        cur = want;
    }

    /// <summary>Records then palettes into one SSBO, stream vertices and indices into their own buffers -- each
    /// orphaned per flush. ponytail: two orphans a frame at a few hundred KiB; a persistent ring if the GUI upload
    /// ever shows in the zones.</summary>
    private static void Upload()
    {
        long rec = (long)GuiCapture.DrawN * GuiCapture.RecFloats * sizeof(float);
        long bones = (long)GuiCapture.SkinN * GuiCapture.BoneFloats * sizeof(float);
        Orphan(_buf, ref _bufCap, rec + bones);
        fixed (float* r = GuiCapture.Records) Gl.NamedBufferSubData(_buf, IntPtr.Zero, (IntPtr)rec, r);
        if (bones > 0) fixed (float* b = GuiCapture.Bones) Gl.NamedBufferSubData(_buf, (IntPtr)rec, (IntPtr)bones, b);
        if (GuiCapture.VtxN > 0)
        {
            Orphan(_vtxBuf, ref _vtxCap, GuiCapture.VtxN);
            fixed (byte* v = GuiCapture.Vtx) Gl.NamedBufferSubData(_vtxBuf, IntPtr.Zero, (IntPtr)GuiCapture.VtxN, v);
        }
        if (GuiCapture.IdxN > 0)
        {
            Orphan(_idxBuf, ref _idxCap, GuiCapture.IdxN * 2L);
            fixed (ushort* i = GuiCapture.Idx) Gl.NamedBufferSubData(_idxBuf, IntPtr.Zero, (IntPtr)(GuiCapture.IdxN * 2L), i);
        }
    }

    private static void Orphan(uint buf, ref long cap, long bytes)
    {
        if (bytes > cap) cap = Math.Max(bytes, Math.Max(cap * 2, 64 * 1024));
        Gl.NamedBufferData(buf, (IntPtr)cap, null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>One VAO per stream format, over the stream buffer at the engine's interleave. A draw addresses its run
    /// by base vertex / first vertex, which is why the capture aligns every run to its stride.</summary>
    private static uint StreamVao(uint fmt, int stride)
    {
        if (_streamVao.TryGetValue(fmt, out uint v)) return v;
        if (!GuiStream.Layout(fmt, stride, out GuiStream.Offsets o)) return 0;
        Gl.CreateVertexArrays(1, &v);
        Gl.VertexArrayVertexBuffer(v, 0, _vtxBuf, IntPtr.Zero, stride);
        Gl.VertexArrayElementBuffer(v, _idxBuf);
        Attr(v, AlloyRenderer.LocPos, o.PosN, Gl.FLOAT, 0, o.Pos);
        if (o.Uv >= 0) Attr(v, AlloyRenderer.LocUv, 2, Gl.FLOAT, 0, o.Uv);
        if (o.Normal >= 0) Attr(v, AlloyRenderer.LocNrm, 3, Gl.FLOAT, 0, o.Normal);
        if (o.Color >= 0) Attr(v, LocColor, 4, Gl.UNSIGNED_BYTE, 1, o.Color);
        _streamVao[fmt] = v;
        return v;
    }

    /// <summary>The GUI program's vertex-colour location (<c>cx_gui_v.glsl</c>); nothing else in Alloy binds it.</summary>
    private const uint LocColor = 3;

    private static void Attr(uint vao, uint loc, int size, uint type, byte norm, int offset)
    {
        Gl.EnableVertexArrayAttrib(vao, loc);
        Gl.VertexArrayAttribFormat(vao, loc, size, type, norm, (uint)offset);
        Gl.VertexArrayAttribBinding(vao, loc, 0);
    }

    /// <summary>Windows: the GUI framebuffer -- the present texture for colour and a depth of its own, rebuilt when
    /// the texture is.</summary>
    private static uint Target()
    {
        uint tex = PresentTarget.Tex;
        if (tex == 0) return 0;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        if (_depth == 0 || _depthW != PresentTarget.Width || _depthH != PresentTarget.Height)
        {
            RenderTarget.AllocDepth(ref _depth, _depthFmt != 0 ? _depthFmt : Gl.DEPTH24_STENCIL8, PresentTarget.Width, PresentTarget.Height);
            _depthW = PresentTarget.Width; _depthH = PresentTarget.Height;
        }
        // Re-attached every flush, as PresentTarget does: GL reuses freed names across a resize.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, DepthAttachment, Gl.TEXTURE_2D, _depth, 0);
        return _fbo;
    }

    /// <summary>Windows, the present texture missing or the wrong size (a frame before any world frame, or a resize):
    /// build it in the 4.6 context, then take the screen back into it.</summary>
    private static void ProbeDepth()
    {
        if (_depthProbed) return;
        _depthProbed = true;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _depthFmt = RenderTarget.ProbeDepthFormat(0);
        if (_depthFmt == 0) _log?.Always("GuiPass: the default framebuffer reports no depth -- on Windows a content-shader GUI draw "
                                       + "will not depth-test against Alloy's GUI draws");
    }

    private static void Resize(int w, int h)
    {
        if (!GlContextService.Enter()) return;
        PresentTarget.Ensure(w, h);
        GlContextService.Leave();
        PresentTarget.TakeBack();
    }

    /// <summary>Windows: the default framebuffer's depth format, probed once in the engine context; the GUI depth is
    /// allocated in it so the two can be blitted into each other.</summary>
    private static uint _depthFmt, _fboEngDepth;
    private static bool _depthProbed;
    private static bool HasStencil => _depthFmt == Gl.DEPTH24_STENCIL8 || _depthFmt == Gl.DEPTH32F_STENCIL8;
    private static uint DepthAttachment => HasStencil ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT;

    /// <summary>Windows, in the ENGINE context: carry the GUI's depth between the screen and Alloy's GUI target, beside the
    /// colour <see cref="PresentTarget"/> carries. On Linux both contexts share the drawable's one depth buffer; here a
    /// content-shader draw the engine makes (<see cref="GuiCapture"/>'s hand-off) lands in the screen's, so without this
    /// its depth and Alloy's GUI depth would never meet.</summary>
    private static void Depth(bool fromScreen)
    {
        if (_depth == 0 || _depthFmt == 0) return;
        if (_fboEngDepth == 0) { uint f; Gl.GenFramebuffers(1, &f); _fboEngDepth = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fboEngDepth);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, DepthAttachment, Gl.TEXTURE_2D, _depth, 0);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, fromScreen ? 0u : _fboEngDepth);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, fromScreen ? _fboEngDepth : 0u);
        uint mask = Gl.DEPTH_BUFFER_BIT | (HasStencil ? Gl.STENCIL_BUFFER_BIT : 0u);
        Gl.BlitFramebuffer(0, 0, _depthW, _depthH, 0, 0, _depthW, _depthH, mask, (uint)Gl.NEAREST);
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>GL names die with the context.</summary>
    internal static void ContextLost()
    {
        _prog = 0; _buf = _vtxBuf = _idxBuf = _fbo = _depth = _fboEngDepth = 0; _depthProbed = false; _bufCap = _vtxCap = _idxCap = 0;
        _streamVao.Clear(); _progTried = false;
    }

    internal static string Stats() => !_armed ? "gui: engine (not armed)" : _failed ? "gui: engine (FAILED)"
        : !Renderer.Gui.V ? "gui: engine (/alloy gui 0)" : _prog == 0 ? "gui: waiting for the 4.6 context"
        : $"gui: Alloy -- {_flushes} flushes, {_drawn} draws" + (_noVao == 0 ? "" : $", {_noVao} with no VAO (NOT DRAWN)")
          + "; " + GuiCapture.Stats() + "; " + ScreenOwner.Stats();
}
