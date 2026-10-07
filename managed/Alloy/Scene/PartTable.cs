using System;
using CX.Engine;
using CX.Framework;
namespace CX.Alloy;

/// <summary>The engine reads the frame still makes: the camera keyhole dissolve (<c>Camera::GetCanKeyholeDissolve</c>
/// and its cached-box port), <c>g_nEnableBumpyWater</c>, and the material/part accessors resolved once at
/// <see cref="Resolve"/>. The part-record table that gave the class its name went with the engine-scene path.</summary>
internal static unsafe class PartTable
{
    /// <summary>#330: <c>SuppressGCTransition</c> -- the callee is ~60 instructions of pure field reads
    /// (decompiled @0x4a6f70: three camera fields, three part fields, <c>BoxAbovePlane</c>) and it is asked
    /// once per lit part per frame, so the GC transition around a plain <c>calli</c> was a real share of the
    /// Scan bracket. Legal because it never blocks, never calls back and never throws.
    /// <para>CX-39: hand-written on purpose -- the calling CONVENTION is the point of this pointer, and a
    /// declared signature carries none.</para></summary>
    internal static delegate* unmanaged[SuppressGCTransition]<IntPtr, IntPtr, byte> _keyhole;
    internal static IntPtr _camSlot;   // &CurrentCamera; the KEYHOLE gate only -- see CurrentCamera
    /// <summary>The camera the world view renders from, or zero when there is none.
    /// <para>CX-109: <see cref="AlloyCamera.World"/> IS the answer now -- Alloy's own latch, set at the world
    /// view's <c>Camera::RenderScene</c> and good for the whole frame, where the engine's <c>CurrentCamera</c>
    /// global was null outside a scene render. <see cref="_camSlot"/> stays as the RESOLUTION check behind
    /// <see cref="KeyholeArmed"/>, which is about whether this client's symbol table is complete, not about
    /// which camera is live.</para></summary>
    internal static IntPtr CurrentCamera => AlloyCamera.World;
    /// <inheritdoc cref="Camera.KeyholeWordCount"/>
    internal const int CamKeyholeWords = Camera.KeyholeWordCount;
    internal static PartGetIsOpaqueFn _isOpaque;
    internal static MatHasEnvMapFn _hasEnvMap;
    internal static MatHasBumpyShinyMapFn _hasBumpyShiny;
    internal static MatGetTextureFn _getTexture;
    internal static uint* _primTable;
    /// <summary><c>g_nEnableBumpyWater</c>: null (Windows, or a build without the symbol) leaves the
    /// bumpy-shiny route to water unrecognised, so only .mtr-declared water is owned. Never a misrender:
    /// an unrecognised water part stays the engine's.</summary>
    internal static int* _bumpyWater;
    /// <summary>Is the engine's bumpy-shiny route into the water class live this session? The gate
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 reads, so both scene paths classify water on one
    /// term; false where the symbol did not resolve, which leaves only <c>.mtr</c>-declared water.</summary>
    internal static bool BumpyWaterOn => _bumpyWater != null && *_bumpyWater != 0;
    /// <summary><c>_ZTV14PartDanglyMesh</c> + 0x10 -- the value a PartDanglyMesh's vtable slot holds (the
    /// Itanium ABI puts the function pointers two words in).</summary>
    internal static IntPtr _danglyVtbl;
    /// <summary>Resolve every engine read the three modules make off a part, a node or a material. One
    /// place, because all three call them and none may resolve a symbol of its own. False = a required one
    /// is missing (headless, or Windows), and nothing above may arm.</summary>
    internal static bool Resolve()
    {
        // Resolved ONCE into a static here rather than through EngineFn at the use site: these three are
        // asked per part per frame, and a lazy compare in that loop is the cost the declaration removes.
        _isOpaque = EngineFn.PartGetIsOpaque;
        _hasEnvMap = EngineFn.MatHasEnvMap;
        _hasBumpyShiny = EngineFn.MatHasBumpyShinyMap;
        IntPtr dv = Native.Sym(SymbolMap.PartDanglyMeshVtbl);
        _danglyVtbl = EngineAbi.Vtbl(dv);
        _getTexture = EngineFn.MatGetTexture;
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _bumpyWater = (int*)Native.Sym(SymbolMap.EnableBumpyWater);
        _keyhole = (delegate* unmanaged[SuppressGCTransition]<IntPtr, IntPtr, byte>)Native.Sym(SymbolMap.GetCanKeyholeDissolve);
        _camSlot = Native.Sym(SymbolMap.CurrentCamera);
        return _isOpaque != null && _hasEnvMap != null && _hasBumpyShiny != null && _getTexture != null && _primTable != null;
    }
    /// <summary>A <c>PartDanglyMesh</c>, which reaches this hook because <c>PartDanglyMesh::Draw</c> @0x451b60
    /// calls <c>PartTriMesh::Draw</c> outright. An identity for the census and the per-frame re-harvest; not a
    /// reason to refuse the part. The CPU re-solve is <c>PartDanglyMesh::Animate(float)</c> @0x49bbb0 -- a
    /// separate per-frame pass, NOT the draw, so suppressing the draw does not freeze the sway -- and it
    /// writes per-part into that part's own VDO item (<c>GetItemDynamicData(vp+0x58, vp+0x60)</c>, the DYNAMIC
    /// vertex pool at vdo+0x30, stride vdo+0x38, through the same item-offset table at vdo+0x48 the static
    /// pool uses), so a harvest off the engine's own VAO inherits whichever pool the engine bound. Tested by
    /// vtable, which is exact -- the class has no flag to read (#135).</summary>
    internal static bool IsDangly(IntPtr part)
    {
        if (_danglyVtbl == IntPtr.Zero || part == IntPtr.Zero) return false;
        return new PartRef(part).Vtbl == _danglyVtbl;
    }
    /// <summary>Does the camera dissolve this part right now (the keyhole wall-fade)? The Alloy pass asks
    /// per part per frame -- the same test the engine's own loop body runs. Since #124 it is what STAGES the
    /// dissolve into the per-draw record, not what hands the part back.</summary>
    /// <summary>#336: the engine's verdict reproduced from a cached box. <c>Camera::GetCanKeyholeDissolve</c>
    /// @0x4a6f70 decompiled: <c>cam+0xe50 &amp;&amp; part+0x50 == 0 &amp;&amp; part+0x78 &gt; cam+0xe64 &amp;&amp;
    /// BoxAbovePlane(min, max, plane@cam+0xe54) &lt; 0</c>; <c>BoxAbovePlane</c> @0x5b32d0 evaluates the
    /// plane at the box corner that minimises it (x = a &gt; 0 ? min : max, y = b &gt; 0 ? min : max, z =
    /// c &gt; 0 ? min : max) as <c>y*b + a*x + c*z + d</c>, in that order, in floats. The caller has
    /// already required a gobless part; <paramref name="box"/> is its six floats.</summary>
    internal static bool KeyholeDissolvesBox(float* box, byte keyholeOn, float* plane, float cut)
    {
        if (keyholeOn == 0 || !(box[5] > cut)) return false;
        float a = plane[0], b = plane[1], c = plane[2], d = plane[3];
        float x = a > 0f ? box[0] : box[3];
        float y = b > 0f ? box[1] : box[4];
        float z = c > 0f ? box[2] : box[5];
        return y * b + a * x + c * z + d < 0f;
    }
    internal static bool KeyholeArmed => _keyhole != null && _camSlot != IntPtr.Zero;
    internal static bool KeyholeDissolves(IntPtr tri)
    {
        if (_keyhole == null || _camSlot == IntPtr.Zero) return false;
        IntPtr cam = CurrentCamera;
        return cam != IntPtr.Zero && _keyhole(cam, tri) != 0;
    }
    /// <summary>#330: has the keyhole camera state moved since <paramref name="key"/> was last taken? Compares
    /// the live camera pointer plus its <see cref="Camera.KeyholeWords"/> against the four-word key and
    /// refreshes it. A null camera keys as all-zero, so the verdicts cached under a real camera do not
    /// survive its loss. Pure in its inputs, which is what the self-check exercises with a fake camera block.</summary>
    internal static bool KeyholeCameraMoved(IntPtr cam, ulong* key)
    {
        ulong* live = stackalloc ulong[CamKeyholeWords + 1];
        live[0] = (ulong)cam;
        Span<ulong> words = cam == IntPtr.Zero ? default : new Camera(cam).KeyholeWords;
        for (int w = 0; w < CamKeyholeWords; w++)
            live[1 + w] = words.IsEmpty ? 0 : words[w];
        bool moved = false;
        for (int w = 0; w <= CamKeyholeWords; w++)
            if (key[w] != live[w]) { key[w] = live[w]; moved = true; }
        return moved;
    }
}
