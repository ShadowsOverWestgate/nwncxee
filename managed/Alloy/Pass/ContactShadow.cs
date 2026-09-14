using System.Numerics;
using CX.Graphics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Screen-space contact shadows for the sun: the lit stage marches a few steps from the fragment
/// toward <c>uAreaDir</c> through this frame's depth copy and darkens the sun term where a nearer surface
/// stands in the way. Fills the gap under the cascades' resolution floor -- the light leak where a foot meets
/// the floor, a barrel meets a wall -- that no shadow map size closes. Depth-only, so the same term works in
/// an interior with no cascades at all.
/// <para>Receivers are TILESET parts only (the record's <c>emi.w</c>: a part with no gob is static tileset geometry, plus the tile-gob half of a split tile): a
/// low-poly creature, and above all a single-sided cloak, reads its own facets as blockers, and there is no
/// thickness rule that tells a cloak fold from a wall. The ground under a creature is what the term is for.</para>
/// <para>The copy is <see cref="SceneDepth"/>'s, taken once per frame after the prepass (the bound attachment
/// is not readable while the colour pass writes it); a frame with no prepass depth marches nothing.</para>
/// <para>ponytail: one march length is also the hit thickness. Per-fragment thickness from the surface
/// slope is the upgrade if thin casters (railings) show a detached shadow.</para></summary>
internal static unsafe class ContactShadow
{
    /// <summary>World units (metres) the march covers; 0 = off. Boot 0.35: a step's height.</summary>
    private static float _len = 0.35f;
    /// <summary>Depth fetches per fragment along the march.</summary>
    private static float _steps = 12f;
    /// <summary>View distance (world units) the term has faded out by. Near the camera is where a contact
    /// reads; far away the march is cost and speckle for nothing.</summary>
    private static float _far = 20f;
    private const float LenBoot = 0.35f, StepsBoot = 12f, FarBoot = 20f;

    private static bool _armed;
    private static string _why = "never ran";
    private static float _projZ, _projW;

    internal static bool On => _len > 0f;

    /// <summary>Frame site, after the prepass and with the frame target bound: snapshot its depth and keep the
    /// projection's two linearisation terms (<c>uProj[2].z</c>, <c>uProj[3].z</c> -- SsaoPass's own form).</summary>
    internal static void Take(int* viewport, Matrix4x4* proj, bool depthFilled)
    {
        _why = !On ? "off" : !depthFilled ? "no prepass depth" : !SceneDepth.Take(viewport) ? "depth copy refused" : "armed";
        _armed = _why == "armed";
        if (!_armed) return;
        _projZ = proj->M33; _projW = proj->M43;   // column-major GLSL [2].z / [3].z of the raw engine bytes
    }

    /// <summary>Per lit program bind: the copy on <see cref="TextureSlots.SceneDepth"/> (the unit
    /// <c>uSceneDepth</c> reads) and the march terms. Off under the probe capture -- no prepass ran there.</summary>
    internal static void BindForDraw(int locContact, int locFar, bool capturing)
    {
        if (locContact < 0) return;
        bool on = _armed && !capturing;
        if (on) SceneDepth.Bind();
        Gl.Uniform4f(locContact, on ? _len : 0f, _steps, _projZ, _projW);
        Gl.Uniform1f(locFar, _far);
    }

    internal static string Describe() => $"contact {_len:F2} contactsteps {_steps:F0} contactfar {_far:F0} ({_why}; /draw viz 39)";

    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "contact": old = _len; _len = MathF.Max(0f, v); return old;
            case "contactsteps": old = _steps; _steps = MathF.Max(1f, MathF.Min(32f, MathF.Round(v))); return old;
            case "contactfar": old = _far; _far = MathF.Max(1f, v); return old;
            default: return float.NaN;
        }
    }

    internal static void ResetTuning() { _len = LenBoot; _steps = StepsBoot; _far = FarBoot; }

    /// <summary>The lit stage carries the march, applies it to the sun term only, and linearises with the same
    /// form SsaoPass does.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ContactShadow.SelfCheck FAIL: {w}"); } }
        string head = AlloyRenderer.OwnedLitFsHead, main = AlloyRenderer.OwnedLitFsMain;
        E(head.Contains("smoothstep(uContactFar * 0.75, uContactFar, vDepth)"), "the term fades out over the last quarter of contactfar");
        E(head.Contains("uniform vec4 uContact;") && head.Contains("uniform mat4 uVP;"), "the lit fragment stage declares the march terms and the projection");
        E(head.Contains("uContact.w / (depth * 2.0 - 1.0 + uContact.z)"), "the linearisation is SsaoPass's (uProj[3].z / (ndc + uProj[2].z))");
        E(main.Contains("if (d.emi.w > 0.5) sunVis *= CxContactShadow(vWorld);"), "the term scales the SUN visibility, on tileset receivers only (emi.w)");
        E(TextureSlots.SceneDepth == TextureSlots.SsaoDepth, "the copy binds on the unit uSceneDepth reads");
        E(head.Contains("float CxContactShadow(vec3 worldPos)"), "the term reads no normal: a normal-dependent term cuts along every facet edge of a low-poly mesh");
        E(!head.Contains("gl_FragCoord.xy, vec2(0.06711056"), "no per-pixel jitter: without TAA it reads as stipple");
        E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
        float l = _len; Tune("contact", 0f); E(!On, "contact 0 is off"); _len = l;
        log.Info(ok ? "ContactShadow.SelfCheck: passed" : "ContactShadow.SelfCheck: FAILED");
    }
}
