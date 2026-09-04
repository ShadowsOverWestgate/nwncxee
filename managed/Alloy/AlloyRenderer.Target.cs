using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: FRAMEBUFFER PLUMBING. Mirroring the engine's draw target into the owned 4.6 context (framebuffer objects do not share across contexts; their attachments do) and reporting what it was made of.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#257: where the engine wants this frame's result. Every field is a GL query answer, latched
    /// per generation rather than re-asked per frame.</summary>
    private struct Target
    {
        internal int Vp0, Vp1, Vp2, Vp3;
        internal int Fbo, ColorType, ColorName, DepthType, DepthName;
        internal bool Srgb;
        internal float Clear0, Clear1, Clear2, Clear3, ClearDepth;
    }

    private static Target _target;
    /// <summary>The generation the latch belongs to: the camera's own viewport (<c>+0x4dc</c>/<c>+0x4e0</c>),
    /// the GUI scale, and the enrolled Scene. The first two are what the GL viewport is BUILT from --
    /// <c>Camera::RenderScene</c> @0x4a8365 sets it from <c>Camera::UpscaleViewport</c>'s output, and that
    /// function's whole scale factor is <c>CAurora::GetGUIScale()</c> (@0x4a44b0's first read), which
    /// <c>Metrics.GameUiScale</c> already tracks per frame. The third covers an area transition, which is
    /// when the engine rebuilds its render targets. Deliberately conservative -- a spurious re-read costs
    /// seven GL calls once.</summary>
    private static float _tgtVpW = -1f, _tgtVpH = -1f, _tgtUiScale = -1f;
    private static IntPtr _tgtScene = IntPtr.Zero;
    private static bool _tgtValid;

    /// <summary>Drop the latch: the next frame re-reads it. Called wherever the GL objects behind it stop
    /// meaning anything (context loss / disarm), where a stale answer would be a draw into a dead target.</summary>
    internal static void ForgetTarget() => _tgtValid = false;

    /// <summary>This frame's target block. One int compare in the common case; a re-read on a generation
    /// change, and a re-read-and-compare once every <see cref="LogEvery"/> frames -- the self-check that
    /// keeps the latch honest, since every value in it is ultimately GL's to change.</summary>
    private static ref Target ThisFrameTarget()
    {
        bool gen = !_tgtValid || _tgtVpW != FrameInputs.CamViewW || _tgtVpH != FrameInputs.CamViewH
                   || _tgtUiScale != Sowx.Ui.Metrics.GameUiScale || _tgtScene != _enrollScene;
        if (gen)
        {
            _tgtVpW = FrameInputs.CamViewW; _tgtVpH = FrameInputs.CamViewH;
            _tgtUiScale = Sowx.Ui.Metrics.GameUiScale; _tgtScene = _enrollScene;
            ReadTarget(out _target);
            // Latch ONLY the cutover's answer. A non-zero fbo means the latch would be holding engine GL
            // NAMES, and a name is exactly what the owned-resource lifetimes doc says never to key a table
            // on: the engine recreates its render targets on a device reset (alt-tab, workspace switch) and
            // the numbers can come back identical while the objects behind them are gone. That path
            // (ownframe=0) keeps the per-frame queries it always had; under the cutover the
            // engine has no world FBO at all, so there is no name here to go stale.
            _tgtValid = _target.Fbo == 0;
            FboTopology(in _target);
        }
        else if (_frameSeq % LogEvery == 0)
        {
            ReadTarget(out Target now);
            if (!Same(in now, in _target))
            {
                _log?.Info($"AlloyRenderer: target self-check DRIFT -- latched fbo {_target.Fbo} vp " +
                           $"{_target.Vp2}x{_target.Vp3} srgb {_target.Srgb}, GL says fbo {now.Fbo} vp " +
                           $"{now.Vp2}x{now.Vp3} srgb {now.Srgb}; adopting the live answer. The latch " +
                           "generation key is missing an edge.");
                _target = now;
                FboTopology(in _target);
            }
        }
        // The clear values are NOT latched, and the reason is the one #257 kept verbatim: a GL share list
        // shares objects, not per-context state, so glClearColor cannot be queried from inside the 4.6
        // bracket -- this is the only place the value can be had, and it is the one the engine's own
        // Clear(7) just used at the anchor. Two calls, not nine.
        ReadClear(ref _target);
        return ref _target;
    }

    private static bool Same(in Target a, in Target b)
        => a.Vp0 == b.Vp0 && a.Vp1 == b.Vp1 && a.Vp2 == b.Vp2 && a.Vp3 == b.Vp3
        && a.Fbo == b.Fbo && a.ColorType == b.ColorType && a.ColorName == b.ColorName
        && a.DepthType == b.DepthType && a.DepthName == b.DepthName && a.Srgb == b.Srgb;

    /// <summary>Where the engine wants the result: viewport, framebuffer, its attachments and the sRGB
    /// encode state. Alloy draws in its own share-listed 4.6 context (GlContextService), so it cannot
    /// desync GLRender's state and has nothing to restore -- it still has to be TOLD this much.</summary>
    private static void ReadTarget(out Target t)
    {
        t = default;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        t.Vp0 = vp[0]; t.Vp1 = vp[1]; t.Vp2 = vp[2]; t.Vp3 = vp[3];
        int fbo; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fbo);
        t.Fbo = fbo;
        if (fbo != 0)
        {
            int v;
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &v); t.ColorType = v;
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &v); t.ColorName = v;
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &v); t.DepthType = v;
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &v); t.DepthName = v;
        }
        t.Srgb = Gl.IsEnabled(Gl.FRAMEBUFFER_SRGB) != 0;
        ReadClear(ref t);
    }

    /// <summary>#132/#214: the engine's clear colour and depth, the values its own Clear(7) just used at the
    /// anchor. The owned target starts from them, and the resolve blits ours back over every pixel of the
    /// depth. Read per frame, never latched -- they are live area state, not frame topology.</summary>
    private static void ReadClear(ref Target t)
    {
        t.Clear0 = 0f; t.Clear1 = 0f; t.Clear2 = 0f; t.Clear3 = 1f; t.ClearDepth = 1f;
        if (Gl.GetFloatv == null) return;
        float* c = stackalloc float[4];
        Gl.GetFloatv(Gl.COLOR_CLEAR_VALUE, c);
        t.Clear0 = c[0]; t.Clear1 = c[1]; t.Clear2 = c[2]; t.Clear3 = c[3];
        float d; Gl.GetFloatv(Gl.DEPTH_CLEAR_VALUE, &d); t.ClearDepth = d;
    }

    /// <summary>#151: settle the AA question with the actual FBO topology. The user sees "MSAA on" in the
    /// game options while the Alloy frame draws into PostChain's single-sample RGBA16F target -- whether the
    /// ENGINE's world FBO is itself multisampled here decides where anti-aliasing has to be put back. Runs
    /// only when the latch is (re-)taken, and capped, so a ping-ponging engine cannot spam the log.</summary>
    private static void FboTopology(in Target t)
    {
        int sig = t.Fbo ^ t.ColorName << 8 ^ t.DepthName << 16 ^ t.ColorType << 24 ^ t.DepthType << 28;
        if (t.Fbo == 0 || sig == _fboDiagSig || _fboDiagLeft <= 0) return;
        _fboDiagSig = sig;
        _fboDiagLeft--;
        int cs = AttachmentSamples(t.ColorType, t.ColorName), ds = AttachmentSamples(t.DepthType, t.DepthName);
        int ctxSamples = 0; Gl.GetIntegerv(Gl.SAMPLES, &ctxSamples);
        _log?.Info($"AlloyRenderer: FBO topology -- engine fbo {t.Fbo}, colour {TypeName(t.ColorType)} " +
                   $"{t.ColorName} ({cs}x), depth {TypeName(t.DepthType)} {t.DepthName} ({ds}x), " +
                   $"GL_SAMPLES here {ctxSamples}; the Alloy HDR target is 1x by construction, so owned " +
                   $"passes are anti-aliased only if the engine attachment they END UP in is multisampled");
    }

    /// <summary>Sample count of an existing FBO attachment, by its reported object type; 1 = single-sampled
    /// (or unqueryable, which for this diagnostic reads the same).</summary>
    private static int AttachmentSamples(int type, int name)
    {
        int n = 0;
        if (type == (int)Gl.RENDERBUFFER && Gl.GetRenderbufferParameteriv != null && name != 0)
        {
            int prev; Gl.GetIntegerv(Gl.RENDERBUFFER_BINDING, &prev);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, (uint)name);
            Gl.GetRenderbufferParameteriv(Gl.RENDERBUFFER, Gl.RENDERBUFFER_SAMPLES, &n);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, (uint)prev);
        }
        else if (type == (int)Gl.ATTACHMENT_TEXTURE && Gl.GetTexLevelParameteriv != null && name != 0)
        {
            // asking TEXTURE_2D_MULTISAMPLE about a plain TEXTURE_2D raises GL_INVALID_OPERATION and writes
            // nothing, which leaves n = 0 -> reported as 1x. The error is harmless and read by nobody.
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D_MULTISAMPLE, 0, Gl.TEXTURE_SAMPLES, &n);
        }
        return n <= 0 ? 1 : n;
    }

    internal static string TypeName(int type)
        => type == (int)Gl.RENDERBUFFER ? "renderbuffer" : type == (int)Gl.ATTACHMENT_TEXTURE ? "texture" : $"type 0x{type:x}";

    /// <summary>#375: the target block the colour pass was ENTERED with, copied at the top of the pass. A
    /// second <see cref="ThisFrameTarget"/> call mid-pass is not a read of the same answer: on a self-check
    /// frame it re-queries GL, and mid-pass GL says whatever Alloy last bound -- the post stack's own
    /// colour-only framebuffer, which the latch then adopted as the engine's target and every later frame
    /// resolved into. That was the "screen goes dark for good once gamma moves" report, live.</summary>
    private static Target _passTarget;

    /// <summary>Snapshot the pass's target block. Once, at the top of the colour pass, after the one
    /// <see cref="ThisFrameTarget"/> read.</summary>
    private static void HoldPassTarget(in Target t) => _passTarget = t;

    /// <summary>#375: put the pass's engine target back as the draw framebuffer, sRGB state included -- for
    /// the post stack's last stage, which writes where the resolve would have. From the snapshot, never a
    /// fresh read.</summary>
    internal static void RebindFrameTarget()
    {
        ref Target t = ref _passTarget;
        BindTarget(t.Fbo, t.ColorType, t.ColorName, t.DepthType, t.DepthName);
        if (t.Srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
    }

    /// <summary>Bind the 4.6-side draw target: default framebuffer when the engine draws to 0 (same
    /// drawable, shared), else a mirror FBO over the engine FBO's attachments (textures/renderbuffers
    /// share; FBO containers do not). Incomplete mirror = clean skip, and since #264 that means the 3D world does not draw at all.
    /// <para>Re-attached and re-checked EVERY pass rather than cached on attachment name/type. GL reuses
    /// freed object names, and an FBO attachment references the object, not the name -- so when the engine
    /// recreates its render targets (focus loss / alt-tab / workspace switch reruns its device reset) the
    /// names can come back identical while our attachments still point at destroyed objects. Cached, that
    /// reads as "unchanged, still complete" and we draw into a dead target forever while still stamping the
    /// parts as drawn, so the walk suppresses geometry nobody rendered. Three GL calls per pass is the
    /// cheaper half of that trade.</para></summary>
    private static bool BindTarget(int engFbo, int colorType, int colorName, int depthType, int depthName)
    {
        if (engFbo == 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); return true; }
        if (_fbo46 == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo46 = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo46);
        Attach(Gl.COLOR_ATTACHMENT0, colorType, (uint)colorName);
        Attach(Gl.DEPTH_ATTACHMENT, depthType, (uint)depthName);
        bool bad = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE;
        if (bad != _mirrorBad)
        {
            _mirrorBad = bad;
            _log?.Info(bad
                ? "AlloyRenderer: engine-FBO mirror incomplete -- Alloy draws skipped, so the 3D world is NOT DRAWN"
                : "AlloyRenderer: engine-FBO mirror complete again -- Alloy draws resumed");
        }
        return !bad;
    }

    private static void Attach(uint attachment, int type, uint name)
    {
        if (type == (int)Gl.RENDERBUFFER) Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, attachment, Gl.RENDERBUFFER, name);
        else Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, attachment, Gl.TEXTURE_2D, name, 0);
    }
}
