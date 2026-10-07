using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.PartTable;
using static CX.Alloy.TexProbe;
namespace CX.Alloy;

/// <summary>The material rules the frame still asks, and their constants: which draw buckets and late keywords a
/// material lands in, which vertex stage it names, and a texture's name for diagnostics. The ownership census
/// that gave the class its name went with the engine-scene path.</summary>
internal static unsafe partial class AlloyCensus
{
    /// <summary>The engine's per-item-alpha draw bucket (#126).</summary>
    internal const int AlphaBucket = 8;

    /// <summary><c>dst == 2</c> is <c>GL_ONE</c>: the engine's own additive test on
    /// <see cref="EngineLayout.MatDstBlend"/>, and the one pair it turns the depth mask off for.</summary>
    internal const int BlendOne = 2;

    /// <summary><c>alloy.gradedblend</c> in cx.cfg: the graded-alpha fraction at or above which a cutout diffuse
    /// draws in the blended class instead. 1 = never (the #357 alpha-test-only behaviour).
    /// <para>0.15, not the 0.3 it shipped at: the tcn01 splotch decals (dark grime with a soft alpha skirt,
    /// which vanilla blends) scan at 0.29-0.30 and drawn alpha-tested their skirt is solid black; ivy sits
    /// at 0.45+ and was already over, and a binary-alpha tree is under 0.05 either way.</para></summary>
    internal static readonly float GradedBlend = Renderer.GradedBlend.Boot;

    /// <summary>#134: the dynamic gob pair (#103's taxonomy). Everything instanced lands in one of these two
    /// -- placeables, doors, and the tileset's own static objects -- and until the <c>shaped</c> gate was
    /// corrected nothing in them ever reached enrollment, so excluding them here cost nothing visible. They
    /// are ownable as a PAIR: the engine walks 2 then 3, and owning one without the other splits a single
    /// instance's geometry across two renderers.</summary>
    internal const int GobOpaqueBucket = 2, GobBlendBucket = 3;

    /// <summary><c>dumptex</c> (cx.cfg): log only parts whose diffuse texture name contains this, one frame's worth at a
    /// time, for as long as it takes the camera to find them.</summary>
    internal static readonly string? _dumpTex = Renderer.DumpTex.Boot.Length == 0 ? null : Renderer.DumpTex.Boot.ToLowerInvariant();

    /// <summary><c>sample_framebuffer</c>'s two values: 1 routes to draw bucket 5 (the late class), 2 to
    /// bucket 6 (water). Different classes, not degrees of one.</summary>
    internal const int SampleFbWater = 2;

    /// <summary>CX-90: <c>sample_framebuffer 1</c> -- draw bucket 5. The engine's own changelog
    /// (<c>lang/en/docs/patchnotes/87.8193.35.md</c>) states the whole of what it buys: "renders near the end
    /// of the frame. Allows rendering refractions and similar." It is a draw-ORDER keyword; the refraction is
    /// the material's own fragment shader's business, and the copy it would read is bound by the engine on
    /// texture units 11/12 as <c>texFBColor</c>/<c>texFBDepth</c>
    /// (<c>Camera::BindRenderSubframeTextures</c> @0x4a6ab0, called from <c>CopyRenderSubframe</c> @0x4a6b90).
    /// <para>No stock shader samples those from world geometry (only <c>fswater.shd</c> and
    /// <c>fsparticle.shd</c>'s depth tap do) and no stock <c>.mtr</c> declares the keyword at all, so there is
    /// no vanilla shading behaviour attached to it -- see <c>docs/alloy-refusals.md</c>.</para></summary>
    internal const int SampleFbLate = 1;

    /// <summary>Draw buckets the Alloy pass takes parts from. 0/1 are the static tile pair (#82), and #126
    /// adds the three residual static classes named by <see cref="AlloyClass"/>: 4 additive, 7 the
    /// tilefade cap, 8 the fade buckets. #134 and #128 then added the dynamic gob pair 2/3 -- drawn from
    /// Scene::RenderDynamicGeometry rather than from RenderSinglePass's own walk, but through the same
    /// RenderDrawBucket this service brackets, which is why owning placeables and then creatures needed no new
    /// capture point. CX-90 adds 5, the late class: <c>sample_framebuffer 1</c> asks for draw ORDER and
    /// nothing else, so its parts are classified off their material like every other part and drawn in the
    /// pass that material implies -- what <see cref="TryOwnCandidate"/> still rejects there is
    /// <c>volumetric</c> (<see cref="RefusedByLateKeywords"/>).
    /// <para>Membership here and suppression in <c>AlloyRenderer.IterSight</c> are ONE list: suppressing a
    /// bucket we do not draw drops geometry, and drawing one we do not suppress double-draws it.</para></summary>
    /// <summary>The engine's late draw bucket: <c>sample_framebuffer 1</c> parts.</summary>
    internal const int LateBucket = 5;

    /// <summary>The bit in <see cref="EngineLayout.NodeOwnerFlags"/> that arms a second alpha multiply by
    /// <see cref="EngineLayout.GobFadeAlpha"/> -- Ghidra <c>PartTriMesh::Mat</c>:
    /// <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.</summary>
    internal const int GobOwnerFadeBit = 8;

    /// <summary>Distinct unrecognised custom-shader names the diag names. A handful is a report; the whole
    /// set would be a wall, and past a handful the answer is "port more features", not "list them all".</summary>
    internal const int MaxUnknown = 6;

    internal static readonly string[] _unknown = new string[MaxUnknown];

    /// <summary>CX-90: does a material's pair of late-draw keywords refuse the part outright? Pure, so the
    /// precedence is testable off-device: <c>volumetric</c> always does (the engine draws it TWICE per pass,
    /// back faces to depth and then front faces against that depth -- a two-pass trick no one-owner scheme
    /// survives, and <c>AddPartsTreeToDrawBuckets</c> files it in buckets 3 AND 5 accordingly), and so does
    /// any <c>sample_framebuffer</c> value that is neither water nor <see cref="SampleFbLate"/>.
    /// <para><paramref name="water"/> is <see cref="IsWater"/>'s verdict, not <c>sampleFb == 2</c>: the
    /// bumpy-shiny route reaches bucket 6 with the keyword absent.</para></summary>
    internal static bool RefusedByLateKeywords(int sampleFb, bool volumetric, bool water)
        => volumetric || (sampleFb != 0 && !water && sampleFb != SampleFbLate);

    internal static bool OwnableBucket(int bucket)
        => bucket == 0 || bucket == 1 || bucket == GobOpaqueBucket || bucket == GobBlendBucket
           || bucket == 4 || bucket == 7 || bucket == AlphaBucket || bucket == LateBucket
           || (bucket == AlloyRenderer.WaterBucket && FrameSnapshot.Usable);

    /// <summary>#125: does this material's SharedMaterial name a custom shader the Alloy pass does not
    /// implement? For world geometry an unknown custom shader is owned anyway and drawn with the plain
    /// stage, which is usually a small difference on one part. The sky is the case where it is not: a
    /// dynamic skybox IS its fragment shader (sow-topdata's <c>fst_sky</c> is ~1900 lines of procedural
    /// gradient, clouds, stars, sun and moon over a near-black night texture), so drawing it flat sends the
    /// diffuse alone and the sky comes out black.</summary>
    internal static bool HasUnportedShader(IntPtr mat)
    {
        if (mat == IntPtr.Zero) return false;
        SharedRef s = new MaterialRef(mat).Shared;
        if (s.IsNull) return false;
        // The VS name decides when there is one -- that is #123's model, and a recognised feature covers
        // its fragment half by construction. What the VS test alone MISSES is a .mtr that names only
        // customShaderFS (+0x199, the next resref slot after +0x188 per SharedMaterial::ParseField
        // @0x4c6760): none of the seven features is authored that way, so an FS with no VS is a custom
        // program by definition -- and the black sky is a fragment shader.
        byte* vs = s.VsName;
        if (*vs != 0) return Classify(vs) == 0;
        return s.HasFs;
    }

    /// <summary>#123: match a SharedMaterial's <c>customShaderVS</c> resref against the seven the owned
    /// program reproduces as feature switches. Case-insensitive over the whole NUL-terminated name, so
    /// <c>shd_roofkn_vs</c> cannot swallow <c>shd_roofknm_vs</c>. Cold: one call per shared-material part per
    /// enrollment scan.</summary>
    internal static int Classify(byte* vs)
    {
        var names = ShaderFeatures.VsNames;
        for (int f = 1; f < names.Length; f++)
        {
            string n = names[f];
            int i = 0;
            for (; i < n.Length; i++)
            {
                byte a = vs[i], b = (byte)n[i];
                if (a >= 'A' && a <= 'Z') a += 32;
                if (a != b) break;
            }
            if (i == n.Length && vs[i] == 0) return f;
        }
        return 0;
    }

    internal static string TexName(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "-";
        IntPtr real = new CAurTexture(tex).GetReferencedTexture().Ptr;
        if (real == IntPtr.Zero) real = tex;
        CAurTextureBasic basic = new CAurTexture(real).AsCAurTextureBasic();
        return basic.IsNull ? "?" : Marshal.PtrToStringAnsi((IntPtr)Unsafe.AsPointer(ref basic.Name)) ?? "?";
    }
}
