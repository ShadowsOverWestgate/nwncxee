using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: FRAMEBUFFER PLUMBING. Mirroring the engine's draw target into the owned 4.6 context (framebuffer objects do not share across contexts; their attachments do) and reporting what it was made of.</summary>
public sealed unsafe partial class AlloyRenderer
{
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

    /// <summary>Bind the 4.6-side draw target: default framebuffer when the engine draws to 0 (same
    /// drawable, shared), else a mirror FBO over the engine FBO's attachments (textures/renderbuffers
    /// share; FBO containers do not). Incomplete mirror = clean skip, the engine draws everything.
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
                ? "AlloyRenderer: engine-FBO mirror incomplete -- Alloy draws skipped, engine draws everything"
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
