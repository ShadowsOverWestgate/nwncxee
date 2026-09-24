using System;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#193: the engine's C++ classes as C# types. Every pointer the renderer walks is an
/// <c>IntPtr</c>; three different classes wear the same one, and nothing stopped a <c>SharedMaterial*</c>
/// reaching a material-only read and returning garbage. The hazard was never unfamiliar syntax -- it was
/// syntax that is UNIFORM across things that are not the same.
/// <para><b>Why <c>readonly ref struct</c> and not a class.</b> The whole liveness protocol (Part::~Part,
/// EntryLive, the identity gates) exists because these pointers are freed and recycled: #145 walked a freed
/// part into GobAlphaMul, #150 chased a gob pointer into a freelist word. A ref struct cannot be a field,
/// cannot go in an array, cannot be boxed and cannot cross an await -- it physically cannot be stashed, so
/// the scar is a compile error rather than a comment.</para>
/// <para><b>The tables keep storing raw <c>IntPtr</c>.</b> That is the design working, not a gap: storage
/// stays visibly raw and unsafe, access becomes typed and scoped.</para>
/// <para><b>Convention.</b> A PROPERTY is a single load. Anything that walks a vtable, calls a native
/// function or can fail is a METHOD with a verb -- <see cref="TextureRef.ResolveName"/> is the canonical
/// one. So much of this codebase is about WHEN a deref is safe that hiding a vtable call behind an
/// innocent-looking property would be worse than the raw pointers were.</para>
/// <para><b>CX-39.</b> The offsets are gone from this file: every read below goes through the generated
/// view in <see cref="CX.Engine"/> (<c>tools/engine/views.py</c> from <see cref="EngineLayout"/>'s rows),
/// which is the same single load. What stays here is what a generated view cannot say -- the null-guarded
/// chases, the concepts the engine has no class for (<see cref="PartRef.IsInstance"/>,
/// <see cref="GobRef.OwnerFades"/>), and the verbs that call native code.</para></summary>
internal readonly unsafe ref struct PartRef
{
    private readonly IntPtr _p;
    internal PartRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Part</c> base this ref reads through.</summary>
    internal Part View => new(_p);
    /// <summary>The <c>PartTriMesh</c> block: the LAYOUT view, for a part already known to be one. The
    /// engine's own subclass cast is <see cref="EngineLayout.PartAsTriMeshSlot"/>.</summary>
    internal PartTriMesh Tri => new(_p);

    internal MaterialRef Material => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Material.Ptr);
    internal GobRef Gob => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Gob.Ptr);
    /// <summary>The part's <c>VertexPrimitive</c>, which despite the name is what carries the model matrix
    /// and the bucket flags -- see <see cref="EngineLayout.PartNode"/> for why the name stays.</summary>
    internal NodeRef Node => new(_p == IntPtr.Zero ? IntPtr.Zero : Tri.VertexPrimitive.Ptr);
    /// <summary>The MATERIAL node at part+0x8, which is a different object from <see cref="Node"/> and
    /// carries the ambient/diffuse terms MatStatic sets.</summary>
    internal MdlRef Mdl => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Node.Ptr);
    internal IntPtr Tile => _p == IntPtr.Zero ? IntPtr.Zero : Tri.Tile.Ptr;

    internal float Alpha => Tri.Alpha;
    /// <summary>Raw BITS of the texture-rotation angle: it is compared, never arithmetic, and a float
    /// compare on a group key would make NaN a group of its own.</summary>
    internal int TexRotateBits => Tri.TexRotate;
    internal float* SelfIllum => (float*)Unsafe.AsPointer(ref Tri.SelfIllum);
    internal float* BboxMin => (float*)Unsafe.AsPointer(ref Tri.BboxMin);
    internal float* BboxMax => (float*)Unsafe.AsPointer(ref Tri.BboxMax);
    internal IntPtr Vtbl => *(IntPtr*)_p;

    /// <summary>Is this part an INSTANCE -- does it hang off a Gob? See <see cref="EngineLayout.PartGob"/>;
    /// this is NOT a skin test, which is the misreading #134 corrected.</summary>
    internal bool IsInstance => !Gob.IsNull;
}

internal readonly unsafe ref struct MaterialRef
{
    private readonly IntPtr _p;
    internal MaterialRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Material</c> this ref reads through.</summary>
    internal Material View => new(_p);

    internal SharedRef Shared => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Shared.Ptr);
    /// <summary>The material's own env map. Compared, never dereferenced -- a <c>CAurTexture*</c> is
    /// borrowed. See <see cref="EngineLayout.MatEnvMap"/>.</summary>
    internal IntPtr EnvMap => _p == IntPtr.Zero ? IntPtr.Zero : View.EnvMap.Ptr;
    internal int SrcBlend => View.SrcBlend;
    internal int DstBlend => View.DstBlend;
    /// <summary>A BYTE, not the int at the same offset: the only reader hashes it into a group key as a
    /// byte, and a widened load would fold three neighbouring bytes into that key.</summary>
    internal byte BlendFlag => View.BlendFlag;
    internal IntPtr Params => View.Params;
    internal int ParamCount => View.ParamCount;

    /// <summary>A numbered texture slot, live. A METHOD, not a property: it calls
    /// <c>Material::GetTexture</c>, and the result is a BORROWED <c>CAurTexture*</c> that dies at the next
    /// area transition -- never cached, re-fetched per frame (#33, #138).
    /// <para>Null-safe on the RECEIVER, like every other accessor here: a null material has no textures, and
    /// the alternative is the caller's own ternary at each of the dozen call sites. It is not null-safe on
    /// the RESULT -- that is what <see cref="TextureRef.IsNull"/> is for.</para></summary>
    /// <summary>#330: <c>Material::GetTexture(int)</c> @0x4c5810 as the two loads it is -- the
    /// SharedMaterial's slot table wins when it holds one (<c>shared + 8 + (slot+13)*0x10</c>), else the
    /// material's own slot at <c>mat + slot*0x10</c>. It was a <c>calli</c> paying a GC transition once per
    /// owned part per frame, and the Chase bracket is where that showed.</summary>
    internal TextureRef Texture(int slot)
    {
        if (_p == IntPtr.Zero) return new(IntPtr.Zero);
        IntPtr shared = View.Shared.Ptr;
        if (shared != IntPtr.Zero)
        {
            IntPtr over = *(IntPtr*)(shared + SharedSlot0 + slot * MatSlotStride);
            if (over != IntPtr.Zero) return new(over);
        }
        // the engine's fallback is unbounded: past the Material's own slots it hands back whatever field
        // sits at mat+slot*0x10 (crashes 1789305736, 1789311470 dereferenced it as a texture)
        if (slot >= OwnSlots) return new(IntPtr.Zero);
        return new(*(IntPtr*)(_p + slot * MatSlotStride));
    }
    /// <summary>Slots a plain <c>Material</c> owns itself (<c>BindAllStandardTextures</c> @0x4c6e30 stops at
    /// i &lt; 3); anything higher is real only in a SharedMaterial's table.</summary>
    private const int OwnSlots = 3;
    /// <summary>#275: map slot <paramref name="s"/> (0-based, <see cref="AlloyRenderer.MapSlots"/> wide) by its
    /// SOURCE: the AO slot lives in the extender-owned <c>texture11</c>, every other one is engine slot s+1.
    /// The only accessor the map block reads through, so no caller has to know which is which.</summary>
    internal TextureRef MapTexture(int s) => TextureAt(s == AlloyRenderer.AoSlot ? AlloyRenderer.AoTexture : s + 1);
    /// <summary>#275: a <c>.mtr</c> slot by its index, engine (<c>texture0..10</c>) or extender-owned (11+).</summary>
    internal TextureRef TextureAt(int n)
        => n >= MaterialExtraSlots.First ? MaterialExtraSlots.Texture(Shared.Ptr, n) : Texture(n);
    /// <summary>A slot TABLE's geometry, not a field: the stride between two slots and where the shared
    /// table starts. No view shape describes an index into an inline array of pointers.</summary>
    private static readonly int MatSlotStride = EngineLayout.MatSlotStride, SharedSlot0 = EngineLayout.SharedSlot0;
}

internal readonly unsafe ref struct SharedRef
{
    private readonly IntPtr _p;
    internal SharedRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>SharedMaterial</c> this ref reads through.</summary>
    internal SharedMaterial View => new(_p);

    internal bool Transparency => View.Transparency != 0;
    internal int SampleFb => View.SampleFb;
    internal bool Volumetric => View.Volumetric != 0;
    /// <summary>#292: the <c>twosided</c> keyword -- draw this material's faces with culling OFF. See
    /// <see cref="EngineLayout.SharedTwoSided"/> for the engine's own three-sub-list spelling of it.</summary>
    internal bool TwoSided => View.TwoSided != 0;
    /// <summary>Is a <c>customShaderVS</c>/<c>FS</c> resref named at all? The first byte of the inline
    /// 17-byte buffer -- an empty VS name is the "pure texture redirect" test.</summary>
    internal bool HasVs => View.VsName != 0;
    internal bool HasFs => View.FsName != 0;
    internal byte* VsName => (byte*)Unsafe.AsPointer(ref View.VsName);
    internal byte* FsName => (byte*)Unsafe.AsPointer(ref View.FsName);
    internal IntPtr Params => View.Params;
    internal int ParamCount => View.ParamCount;
}

internal readonly unsafe ref struct GobRef
{
    private readonly IntPtr _p;
    internal GobRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Gob</c> this ref reads through.</summary>
    internal Gob View => new(_p);

    internal IntPtr Node => View.Node.Ptr;
    internal IntPtr PartTree => View.PartTree.Ptr;
    internal float* Position => (float*)Unsafe.AsPointer(ref View.Position);

    internal bool Highlight => View.Highlight != 0;
    internal bool SceneTint => View.SceneTint != 0;
    /// <summary>The short that ARMS the colour/alpha modulation below; the values are meaningless without
    /// it (PartTriMesh::Mat @0x450960 reads it first).</summary>
    internal bool ColourMulArmed => View.ColourMul != 0;
    internal bool SelfIllumArmed => View.SelfIllum != 0;
    internal float Alpha => View.Alpha;
    internal float FadeAlpha => View.FadeAlpha;
    internal float* Colour => (float*)Unsafe.AsPointer(ref View.Colour);
    internal float* Emissive => (float*)Unsafe.AsPointer(ref View.Emissive);
    internal GobRef FadeOwner => new(View.Node.Ptr);
    internal IntPtr EnvOverride => View.EnvOverride;
    /// <summary>Bit 3 of the OWNER's flag byte, which is what arms the second alpha multiply. Named for what
    /// it decides rather than where it sits, because the owner is a gob and the byte is not on the gob whose
    /// alpha it gates -- it is a <c>Model</c>, which is why this one read goes through that view.</summary>
    internal bool OwnerFades => (new Model(_p).OwnerFlags & AlloyCensus.GobOwnerFadeBit) != 0;
}

internal readonly unsafe ref struct NodeRef
{
    private readonly IntPtr _p;
    internal NodeRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>VertexPrimitive</c> this ref reads through.</summary>
    internal VertexPrimitive View => new(_p);

    internal float* Matrix => (float*)Unsafe.AsPointer(ref View.Matrix);
    /// <summary>The same 16 floats as a value. The raw bytes already read as the transpose GLSL wants, so
    /// this is a copy and not a conversion -- every caller that wanted one was casting the pointer back.</summary>
    internal System.Numerics.Matrix4x4 Model => View.Matrix;
    internal byte Flags => View.Flags;
    /// <summary>The <c>SetModelMatrix</c> half of RenderDrawBucket's matrix-class condition -- the flag bits
    /// alone; the other half is <see cref="PartRef.IsInstance"/>.</summary>
    internal bool MatrixClass => (Flags & MatrixClassBits) != 0;
    /// <summary>The bits <c>Scene::RenderDrawBucket</c> @0x46d060 tests on <see cref="EngineLayout.NodeFlags"/>
    /// before it pushes a matrix. A mask, not-an-offset: it names bits, not a place in memory.</summary>
    private const int MatrixClassBits = 0x1e;   // not-an-offset: the matrix-class mask
}

internal readonly unsafe ref struct MdlRef
{
    private readonly IntPtr _p;
    internal MdlRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>MdlNodeTriMesh</c> block this ref reads through -- the part's material node, already
    /// known to be a tri-mesh by the caller that fetched it.</summary>
    internal MdlNodeTriMesh View => new(_p);

    internal float* Ambient => (float*)Unsafe.AsPointer(ref View.Ambient);
    internal float* Diffuse => (float*)Unsafe.AsPointer(ref View.Diffuse);
}

internal readonly unsafe ref struct VdoRef
{
    private readonly IntPtr _p;
    internal VdoRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>VertexDataObject</c> this ref reads through.</summary>
    internal VertexDataObject View => new(_p);

    internal uint Vao => View.Vao;
    /// <summary>1 = indexed static, which is the only mode this renderer can replay.</summary>
    internal int Mode => View.Mode;
    internal long VertItems => (long)View.VertItems;
    internal int VertCount => View.VertCount;
    internal long ElemItems => (long)View.ElemItems;
    internal int ElemCount => View.ElemCount;

    /// <summary>#284: <c>Build</c> ran and gave this VDO an element stream. See
    /// <see cref="EngineLayout.VdoIndexed"/> for why the VAO drip refuses without it.</summary>
    internal bool Indexed => View.Indexed != 0;
    internal uint ElemToken => View.ElemToken;
    internal uint VertToken => View.VertToken;
    internal uint DynToken => View.DynToken;
    internal uint VertMask => View.VertMask;
    internal uint DynMask => View.DynMask;
}

/// <summary>Everything a VAO-cache slot's harvest depends on, read off the live VDO: the engine VAO it was
/// harvested from, the mode, the three pool tokens and the two attribute masks (which fix every stride and
/// offset). Equal fingerprints mean the harvested layout is still the truth. A VDO freed and re-minted at
/// the same address -- operator new recycles the 0x68-byte block, and no destructor notice exists for
/// VDOs -- changes it, which pointer identity alone cannot see: the slot would keep serving the dead VDO's
/// strides and buffers to the new one's parts, and they draw as screen-spanning nonsense until a full
/// release wipes the cache.</summary>
internal readonly record struct VdoSig(uint Vao, int Mode, uint Elem, uint Vert, uint Dyn, uint VertMask, uint DynMask)
{
    internal static VdoSig Of(IntPtr vdo)
    {
        var v = new VdoRef(vdo);
        return new VdoSig(v.Vao, v.Mode, v.ElemToken, v.VertToken, v.DynToken, v.VertMask, v.DynMask);
    }
}

/// <summary>A borrowed <c>CAurTexture*</c>. Every accessor is a METHOD: the name walks two vtable slots, and
/// the pointer itself is freed on area transitions and module unload -- there is no field read here that
/// could honestly be a property.</summary>
internal readonly unsafe ref struct TextureRef
{
    private readonly IntPtr _p;
    internal TextureRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>CAurTexture</c> this ref reads through -- the vtable slots the verbs below call.</summary>
    internal CAurTexture View => new(_p);

    /// <summary>The texture's name, through <c>GetReferenced</c> then <c>AsBasic</c>. Two vtable calls.</summary>
    internal string ResolveName() => AlloyCensus.TexName(_p);
    /// <summary>This texture's GL id, vtable-checked -- see AurTex (#138): Material::GetTexture is a plain
    /// field read, so the pointer it hands back can outlive the object.</summary>
    internal int ResolveTid() => AurTex.Tid(_p);
    /// <summary>#211: the name an owned DRAW samples -- Alloy's own copy of this texture, or the engine's id
    /// when <c>alloy.ownedtex=0</c> or the copy refused. Diagnostics stay on <see cref="ResolveTid"/>: they are
    /// asking about the engine's texture, not about what we draw. Must be called inside the 4.6 bracket.</summary>
    internal int OwnedTid() => OwnedTextures.Draw(AurTex.Tid(_p));
}

/// <summary>#193: the engine's own pure queries about a part or a material, taking the typed refs. The
/// <c>_orig*</c> trampolines deliberately do NOT move here -- those belong beside the hooks they chain.</summary>
internal static unsafe class Engine
{
    internal static bool IsOpaque(PartRef part) => PartTable._isOpaque(part.Ptr) != 0;
    internal static bool HasEnvMap(MaterialRef mat) => PartTable._hasEnvMap(mat.Ptr) != 0;
    internal static bool HasBumpyShiny(MaterialRef mat) => PartTable._hasBumpyShiny(mat.Ptr) != 0;
}
