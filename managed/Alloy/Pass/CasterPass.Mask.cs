using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The cutout mask: which diffuse earns the alpha-tested depth stage (holes against graded alpha), the
/// ordered dither its discard steps on, and the bindless handle program that samples it.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Masked shadow depth (<c>/shadow mask</c>): 1 = alpha-tested, 0 = the solid program for masked
    /// batches (prices the fetch + discard), -1 = skip them (prices their raster), 2 = alpha-tested only where
    /// the texture's hole fraction reaches <see cref="_maskHoles"/> (a gate, a railing), solid elsewhere (a
    /// leaf card). Default 2.</summary>
    private static int _maskMode => Shadows.Mask.V;
    /// <summary><c>/shadow maskholes X</c>: the hole fraction (texels under the cutout) from which mode 2 masks.</summary>
    private static float _maskHoles => Shadows.MaskHoles.V;
    /// <summary><c>/shadow maskgraded X</c>: the graded-alpha fraction from which a diffuse counts as
    /// glass and takes the masked stage regardless of its holes. Default = the census's blend threshold, so a
    /// soft-edged leaf card stays on the hole gate.</summary>
    private static float _maskGraded => Shadows.MaskGraded.V;
    /// <summary><c>/shadow maskdither N</c>: the shadow-texel block the masked stage's ordered discard
    /// steps on (1 = per texel, the receiver's PCF averages it; 2 = 2x2 clumps read softer but blotchier);
    /// 0 = off. Never per-frame: the maps are cached, and a jittering pattern is a permanent mover.</summary>
    private static int _maskDither => Shadows.MaskDither.V;
    /// <summary>Past this the blocks read as a pattern, not a shadow.</summary>
    internal const int MaskDitherMax = 16;

    private static int _maskNoTex, _maskNoProg;

    /// <summary>The masked (alpha-tested) depth program and its uniforms. Zero = it could not build, and
    /// masked casters then stay out of every bake.</summary>
    private static uint _progMask;
    private static int _locMaskVp = -1, _locMaskTex = -1, _locMaskCut = -1, _locMaskDither = -1, _locMaskEnv = -1;
    /// <summary><c>uAlphaMip</c> on the four masked caster programs (<c>alloy.cutmip</c>).</summary>
    private static int _locMaskMip = -1, _locMaskHMip = -1, _locLayerMaskMip = -1, _locLayerMaskHMip = -1;
    /// <summary>The masked depth program that samples the mask through the record's bindless handle
    /// (<c>uTexH</c> slot 0) instead of a bound unit, so masked batches no longer split per texture.
    /// Built only when the renderer's bindless path is armed; <c>/shadow maskh 0</c> is the A/B.</summary>
    private static uint _progMaskH, _progLayerMaskH;
    private static int _locMaskHVp = -1, _locMaskHCut = -1, _locLayerMaskHCut = -1, _locMaskHDither = -1, _locLayerMaskHDither = -1, _locMaskHEnv = -1, _locLayerMaskHEnv = -1;
    private static bool _maskH => Shadows.MaskH.V;
    private static bool HandleMaskReady => _maskH && _progMaskH != 0 && (_progLayer == 0 || _progLayerMaskH != 0);
    private static bool _maskHFailed;

    /// <summary>Build the handle-sampling pair once the renderer's bindless path is armed. Lazy because the
    /// bindless probe runs in the first COLOUR frame and this pass's programs are built in the shadow
    /// bracket before it; a pass built on that frame would otherwise never get the pair. Re-stages once
    /// when the pair becomes usable, so the standing key picks it up.</summary>
    private static void EnsureHandleMask()
    {
        if (_maskHFailed || _progMask == 0 || !AlloyRenderer.Bindless || CastMaskHFs == CastMaskHFsSrc) return;
        if (_progMaskH == 0)
        {
            _progMaskH = GlProgram.Graphics(CastMaskHVs, CastMaskHFs, _log, "CasterPass handle-masked depth program");
            if (_progMaskH == 0) { _maskHFailed = true; return; }
            _locMaskHVp = GlProgram.Loc(_progMaskH, "uVP"); _locMaskHCut = GlProgram.Loc(_progMaskH, "uAlphaCut"); _locMaskHDither = GlProgram.Loc(_progMaskH, "uDither"); _locMaskHEnv = GlProgram.Loc(_progMaskH, "uEnvAlpha"); _locMaskHMip = GlProgram.Loc(_progMaskH, "uAlphaMip");
            _listVer++;
            _log?.Info("CasterPass: handle-sampled masks armed -- masked batches merge per VAO (/shadow maskh 0 is the A/B)");
        }
        if (_progLayer != 0 && _progLayerMaskH == 0)
        {
            _progLayerMaskH = GlProgram.Graphics(CastLayerMaskHVs, CastMaskHFs, _log, "CasterPass layered handle-masked depth program");
            if (_progLayerMaskH == 0) { _maskHFailed = true; return; }
            _locLayerMaskHCut = GlProgram.Loc(_progLayerMaskH, "uAlphaCut"); _locLayerMaskHDither = GlProgram.Loc(_progLayerMaskH, "uDither"); _locLayerMaskHEnv = GlProgram.Loc(_progLayerMaskH, "uEnvAlpha"); _locLayerMaskHMip = GlProgram.Loc(_progLayerMaskH, "uAlphaMip");
            _listVer++;
        }
    }

    /// <summary>The two discard thresholds. Alpha means COVERAGE on everything the engine ships, so the
    /// default is the engine's own 0.2 -- the value its colour pass uses, which is what makes a leaf cast a
    /// leaf. The exception is an env-mapped material, where the diffuse alpha is REFLECTIVITY
    /// (<c>fEnvMapLevel = 1 - alpha</c>): a polished floor at alpha 0.3 is solid and must keep its shadow.</summary>
    private const float CutoutDiscard = 0.2f, EnvAlphaDiscard = 0.02f;

    /// <summary>Does this texture carry coverage? Answered once by a level-0 readback, because
    /// <c>Material::GetIsOpaque</c> @0x4c6190 short-circuits on an env map, the gob alpha and its own memo,
    /// and the internal format lies (a shipped ivy card is a DXT1 with ALPHA_SIZE 0 and 48% punchthrough
    /// blocks). The same question is put to the decoded image at the upload funnel, texel by texel.</summary>
    internal static bool TexHasAlpha(int tid) => TexHoles(tid) > 0f;
    /// <summary>Fraction of level-0 texels that are neither cutout nor opaque -- a glass-imitating
    /// diffuse at alpha ~0.5. At or above <see cref="_maskGraded"/> the caster takes the masked stage whatever
    /// its hole fraction, and the stage's stochastic discard turns that alpha into a partial shadow.</summary>
    internal static float TexGraded(int tid) { TexStat(tid, out float g); return g; }

    /// <summary>Fraction of level-0 texels the cutout drops; 0 = a slab. 1 for a texture the funnel never
    /// scanned: test rather than cast a possible slab. No readback: the answer was computed from the engine's
    /// decoded image at upload (<see cref="TexScan"/>), which is what keeps a radeonsi engine context's
    /// descriptors of the same texture honest.</summary>
    internal static float TexHoles(int tid) => TexStat(tid, out _);
    private static float TexStat(int tid, out float graded)
    {
        graded = 0f;
        if (tid <= 0) return 0f;
        int h = (int)((uint)tid * 2654435761u % TexCacheSize);
        for (int probe = 0; probe < 8; probe++)
        {
            int k = (h + probe) % TexCacheSize;
            if (_texKey[k] == tid) { graded = _texGraded[k]; return _texHoles[k]; }
            if (_texKey[k] != 0) continue;
            float holes = OwnedTextures.TryHoles(tid, out float f, out graded) ? f : 1f;
            _texKey[k] = tid; _texHoles[k] = holes; _texGraded[k] = graded;
            _texQueried++;
            if (holes > 0f) _texWithAlpha++;
            return holes;
        }
        return 1f;
    }

    private const int TexCacheSize = 2048;
    private static readonly int[] _texKey = new int[TexCacheSize];
    private static readonly float[] _texHoles = new float[TexCacheSize], _texGraded = new float[TexCacheSize];
    private static int _texQueried, _texWithAlpha;

    /// <summary>The caster's alpha-mask texture id, 0 if it does not resolve; cold relative to the draw (once
    /// per masked caster per collect) and never cached. A DB caster has no engine material at all
    /// (<c>_oMat</c> is <c>IntPtr.Zero</c>), so its id comes off the draw recipe instead -- calling the
    /// engine's GetTexture on a null material took the client down.</summary>
    private static int MaskTid(in AlloyRenderer.CasterFacts f)
    {
        if (AlloyRenderer.IsArenaVao(f.Vao)) return AlloyRenderer.DbCasterTid(f.Slot);
        if (_getTexture == null || f.Mat == IntPtr.Zero) return 0;
        return AurTex.Tid((IntPtr)_getTexture(f.Mat, 0));
    }
}
