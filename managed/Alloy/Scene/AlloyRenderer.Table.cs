using System;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: the OWNERSHIP TABLE and the staging tables built from it. Every one is a fixed-capacity
/// parallel array indexed by Alloy slot (or by VDO slot, or by batch group), allocated once at type init and
/// never after -- which is what makes the whole per-frame path allocation-free. A struct-of-arrays rather
/// than an array-of-structs on purpose: the hot walks touch two or three columns of a wide row, and the
/// sorts permute an index array rather than the rows themselves.</summary>
public sealed unsafe partial class AlloyRenderer
{
    // the ownership table (parallel arrays, index = Alloy slot; _order walks them grouped by VAO)
    private static readonly int[] _oEntry = new int[MaxOwned];      // PartTable slot
    private static readonly IntPtr[] _oTri = new IntPtr[MaxOwned];  // Zero = revoked, harvested at enrollment
    private static readonly IntPtr[] _oNode = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oMat = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oVdo = new IntPtr[MaxOwned];
    private static readonly int[] _oItem = new int[MaxOwned];
    private static readonly int[] _oPrim = new int[MaxOwned];
    private static readonly int[] _oVao = new int[MaxOwned];        // slot in the per-VDO VAO cache
    private static readonly int[] _oRotBits = new int[MaxOwned];    // part+0xb0 at enrollment (revalidation)
    private static readonly float[] _oCos = new float[MaxOwned];
    private static readonly float[] _oSin = new float[MaxOwned];
    private static readonly bool[] _oMx = new bool[MaxOwned];       // matrix-class: needs uMVP = model*view*proj
    private static readonly bool[] _oLit = new bool[MaxOwned];      // #83: shade it, don't paint it fullbright
    private static readonly int[] _oTexKey = new int[MaxOwned];     // #84: interned diffuse-texture sort key
    /// <summary>#128: the Gob this part instances off, zero for static geometry. The key the all-owned-or-none
    /// sweep groups by -- never dereferenced here, only compared.</summary>
    private static readonly IntPtr[] _oGob = new IntPtr[MaxOwned];
    /// <summary>#137: this enrolled part's bone slot for THIS pass, or -1 for a rigid one. Rewritten every
    /// frame in <see cref="PoseSkinned"/>, never carried across one -- a pose is per-frame by definition.
    /// </summary>
    private static readonly int[] _oBone = new int[MaxOwned];
    /// <summary>#120: the two PBR vec4s of this part's draw record, staged verbatim -- (Specularity,
    /// Roughness, Metallicness, _) then (CustomSpecularColor.rgb, declared?). Read once at enrollment off the
    /// material's parsed .mtr list; a value of 0 means "not declared" and the shader synthesises it, which is
    /// what the stock shaders do with the same uniforms. The spare w is filled in per frame with the live
    /// slot mask, so index 3 of each pair is deliberately never written here.</summary>
    private static readonly float[] _oParam = new float[MaxOwned * 8];
    /// <summary>#120: which of slots 1..5 this material had a texture in at enrollment (bit s-1). The live
    /// tid is still re-fetched per frame like slot 0's -- this only says which slots are worth asking for.
    /// #123 adds slot 6, the flow map, at bit 5 -- above the five, below <see cref="EnvSlot"/>.</summary>
    private static readonly int[] _oSlots = new int[MaxOwned];
    /// <summary>#123: this part's <see cref="ShaderFeature"/> and the three .mtr parameters that drive it,
    /// staged verbatim into the record's <c>feat</c> vec4. Which three depends on the feature -- sway
    /// speed/strength/tileset-Z for wind, roof offset/clip/interior for the toggles, slide speed for the
    /// flow map. Read once at enrollment: a .mtr does not animate.</summary>
    private static readonly float[] _oFeat = new float[MaxOwned * 4];
    /// <summary>#126: this part's <see cref="AlloyClass"/> -- the pass it draws in. The most significant
    /// radix digit, so each class occupies a contiguous run of <see cref="_order"/> and each pass owns a
    /// contiguous run of batches.</summary>
    private static readonly byte[] _oCls = new byte[MaxOwned];
    /// <summary>The bucket this part was captured in, for the one-owner audit alone: an UNMATCHED part is
    /// one we drew that the engine's iterator never walked, and "which bucket did it come from" is the only
    /// question that separates a suppression gap from geometry the engine simply culled this frame.</summary>
    private static readonly byte[] _oBucket = new byte[MaxOwned];
    /// <summary>#126: the material's Aurora blend pair, translated to GL at bind time. Only the blending
    /// classes read it.</summary>
    private static readonly byte[] _oSrc = new byte[MaxOwned], _oDst = new byte[MaxOwned];
    /// <summary>#126: the part's LOCAL bbox centre (part+0x64/+0x70, the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> and the caster cull both read). Run through the live model matrix
    /// each pass it gives the world-space sort key for the back-to-front blended order; captured at
    /// enrollment because a bbox does not move under a part.</summary>
    private static readonly float[] _oCx = new float[MaxOwned], _oCy = new float[MaxOwned],
                                    _oCz = new float[MaxOwned];
    /// <summary>#152: the part's bbox RADIUS (half the diagonal), the colour LOD's size input. 0 = bbox
    /// degenerate or unread -- such a part is never LOD-culled, unknown bounds are not "small".</summary>
    private static readonly float[] _oR = new float[MaxOwned];
    private static readonly int[] _order = new int[MaxOwned];
    private static readonly int[] _orderTmp = new int[MaxOwned];    // #84: LSD radix intermediate
    /// <summary>#126: the third radix digit's buckets, and the [start, end) range each class occupies in
    /// <see cref="_order"/> afterwards. A pass draws its class's range and nothing else.</summary>
    private static readonly int[] _clsCount = new int[AlloyClasses.Count],
                                  _clsStart = new int[AlloyClasses.Count],
                                  _clsEnd = new int[AlloyClasses.Count];
    private static int _ownCount, _litCount;
    /// <summary>Entries in <see cref="_order"/>: <see cref="_ownCount"/> minus whatever the all-owned-or-none
    /// sweep dropped. The draw walks this, never the slot count.</summary>
    private static int _orderCount;
    /// <summary>#126: blended parts handed back this enrollment because the class could not be Alloy whole.
    /// Reported: a class that never lands is the one number that separates "no transparent content here"
    /// from "one part keeps refusing and takes the class down with it".</summary>
    private static int _blendedDropped;

    // per-VDO VAO cache: attribute formats harvested in the ENGINE context at enrollment, the 4.6-side VAO
    // built lazily inside the bracket (VAOs don't share across contexts; the buffers they reference do)
    private static readonly IntPtr[] _vVdo = new IntPtr[MaxVdos];
    private static readonly uint[] _vVao46 = new uint[MaxVdos];
    private static readonly uint[] _vElemBuf = new uint[MaxVdos];
    private static readonly VertexStream[] _vPos = new VertexStream[MaxVdos];
    private static readonly VertexStream[] _vUv = new VertexStream[MaxVdos];
    private static readonly VertexStream[] _vNrm = new VertexStream[MaxVdos];
    private static readonly bool[] _vLit = new bool[MaxVdos];       // #83: has a usable 3-float normal stream
    /// <summary>The VDO carries the BONE streams, i.e. it belongs to a true skinmesh (a cloak, a robe, a
    /// tail). #128 refused such a part outright -- no skinning branch existed, so drawing one meant a BIND
    /// pose at the node matrix, a misrender rather than a fallback. #137 gave the Alloy vertex stages that
    /// branch, so the flag is now what SELECTS it: bone attributes bound in the Alloy VAO, a per-frame pose
    /// forced out of the engine, a bone slot in the record. Both streams or neither -- a half-skinned VAO
    /// would skin some vertices and leave the rest at the origin.</summary>
    private static readonly bool[] _vSkin = new bool[MaxVdos];
    /// <summary>#137: the two bone streams' formats, harvested from the same engine VAO as position and UV.
    /// Normalisation is carried (the weights ship as normalised shorts on some content) because
    /// <c>VertexAttribPointer</c> silently means something else without it.</summary>
    private static readonly VertexStream[] _vBi = new VertexStream[MaxVdos], _vBw = new VertexStream[MaxVdos];
    private static readonly bool[] _vStale = new bool[MaxVdos];     // slot recycled: rebuild the 4.6 VAO
    private static int _vdoCount;
    /// <summary>Counting-sort buckets. The draw order groups by (lit, VDO) so both the VAO and the PROGRAM
    /// bind once per group -- lit slots are the upper half.</summary>
    private static readonly int[] _sortCount = new int[MaxVdos * 2];
    /// <summary>#84 second radix digit: counting-sort buckets over the interned texture key. Sorting by it
    /// FIRST and then stably by (lit, VDO) leaves the order grouped by (lit, VDO) with equal textures
    /// contiguous inside each group -- i.e. exactly the batch key, in one pass over the table.</summary>
    private static readonly int[] _texSortCount = new int[MaxMats];
    private static readonly IntPtr[] _mKey = new IntPtr[MatHash];
    private static readonly int[] _mVal = new int[MatHash];
    private static int _matCount;

    // #84 multidraw submission: one entry per DRAW in _dCount/_dOff/_dBase + one staged SSBO record per draw,
    // sliced into batches by the _g* table. All static -- the hot path stages into them and allocates nothing.
    private static readonly int[] _dCount = new int[MaxOwned];
    private static readonly nint[] _dOff = new nint[MaxOwned];
    private static readonly int[] _dBase = new int[MaxOwned];
    private static readonly float[] _dData = new float[MaxOwned * DrawFloats];
    /// <summary>#153: per-draw bindless handles, 8 per record (diffuse + material slots 1..7), parallel to
    /// <see cref="_dData"/>. 0 = slot empty; the record's slot mask already forbids sampling those.</summary>
    private static readonly ulong[] _dTexH = new ulong[MaxOwned * 8];
    /// <summary>#153/#220: GL texture NAME -&gt; resident bindless handle, open-addressed.
    /// <para>#220 was this cache keyed on an ENGINE name. The engine deletes its textures and GL recycles
    /// the names, so a handle could outlive the texture it was made for and a recycled name would hit the
    /// stale entry -- a wall sampling a portrait. The wipe-every-enrollment this used to do was the bound on
    /// that, and it was both too weak (the artifact was stable across wipes) and harmful: it forgot
    /// residency it never gave up, so the next mint re-resided an already-resident handle, which is
    /// GL_INVALID_OPERATION and the error burst the ticket recorded on transitions.</para>
    /// <para>#211 removed the premise -- these are Alloy-OWNED names, and nothing but
    /// <c>OwnedTextures</c> creates or destroys one. The cache is now invalidated EXACTLY when its texture
    /// is retired (<c>OwnedTextures.Drain</c> -&gt; <c>DropHandle</c>), and the enrollment wipe is gone while
    /// ownedtex is armed. Unarmed, engine names populate this again and the wipe is still the only heal.
    /// </para></summary>
    internal const int HandleHashBits = 15;
    private static readonly int[] _hTid = new int[1 << HandleHashBits];
    private static readonly ulong[] _hVal = new ulong[1 << HandleHashBits];
    /// <summary>#220: did WE make <see cref="_hVal"/>[i] resident? The driver cannot be asked, and guessing
    /// produces GL_INVALID_OPERATION in both directions -- residency is only ever ours to know.</summary>
    private static readonly bool[] _hRes = new bool[1 << HandleHashBits];
    /// <summary>#153: is the bindless path live? Extension present, entry points resolved, not vetoed by
    /// bindless=0, and the LIT program's actual sources carry the handle block. When false everything
    /// draws exactly as before -- per-batch texture binds.</summary>
    private static bool _bindless;
    private static bool _bindlessProbed;
    /// <summary>#126: this pass's draw order, rebuilt every pass from <see cref="_order"/> -- the opaque set
    /// first, then the blended members sorted back-to-front, then additive. It is a separate array from
    /// _order because the membership is per FRAME (a fading tile joins the blended run for the frames it
    /// fades) and the blended order is per PASS (it depends on the camera).</summary>
    private static readonly int[] _pass = new int[MaxOwned];
    /// <summary>...and each entry's class + alpha multiplier for this pass, since neither is
    /// <c>_oCls[i]</c> alone once the fade overlay applies.</summary>
    private static readonly byte[] _passCls = new byte[MaxOwned];
    private static readonly float[] _passAlpha = new float[MaxOwned];
    /// <summary>Blended candidates of this pass and their squared distance from the eye, before the sort.</summary>
    private static readonly int[] _blend = new int[MaxOwned];
    private static readonly float[] _blendKey = new float[MaxOwned];
    private static readonly float[] _blendAlpha = new float[MaxOwned];
    private static readonly int[] _gStart = new int[MaxOwned];
    private static readonly int[] _gLen = new int[MaxOwned];
    private static readonly int[] _gVao = new int[MaxOwned];
    private static readonly int[] _gTex = new int[MaxOwned];
    /// <summary>#120: the live tids of slots 1..5 for this batch (0 = nothing bound there). Part of the batch
    /// KEY, not just of the bind set -- a batch that grouped on the diffuse alone would render every member
    /// under the first member's normal and specular maps.</summary>
    private static readonly int[] _gTexN = new int[MaxOwned * MapSlots];
    private static readonly uint[] _gMode = new uint[MaxOwned];
    private static readonly bool[] _gLit = new bool[MaxOwned];
    /// <summary>#123: the batch's <see cref="ShaderFeature"/>. Read by the depth prepass, which skips a batch
    /// whose feature displaces vertices.</summary>
    private static readonly int[] _gFeat = new int[MaxOwned];
    /// <summary>#124: does this batch camera-dissolve? A key term for one reason only -- the keyhole
    /// fragment stage <c>discard</c>s, and the depth prepass has no per-draw granularity, so a dissolving
    /// batch has to be droppable whole. Prepass depth for a wall the colour pass punches through would cull
    /// the room behind it.</summary>
    private static readonly bool[] _gDis = new bool[MaxOwned];
    /// <summary>#126: the batch's class and blend pair. In the key because they ARE the GL state a multidraw
    /// cannot vary inside: depth mask, blend function and the alpha-discard threshold.</summary>
    private static readonly byte[] _gCls = new byte[MaxOwned];
    private static readonly byte[] _gSrc = new byte[MaxOwned], _gDst = new byte[MaxOwned];
    /// <summary>#126: where the opaque set ends and where the blended run ends, in batch indices. Each pass
    /// draws exactly its own range -- the depth prepass and the probe capture take the opaque prefix.</summary>
    private static int _gOpaqueEnd, _gBlendEnd, _gWaterEnd;
    private static int _drawN;
    // #125: the sky's own draw list, staged after the batch records and issued one draw at a time -- a
    // skybox is a handful of parts and each carries its own captured matrix, so there is nothing to group.
    internal const int MaxSkyDraws = 1024;
    private static readonly int[] _skVao = new int[MaxSkyDraws], _skTex = new int[MaxSkyDraws];
    private static readonly uint[] _skMode = new uint[MaxSkyDraws];
    private static readonly int[] _skSrc = new int[MaxSkyDraws], _skDst = new int[MaxSkyDraws];
    /// <summary>#222: the constant colour <c>PartTriMesh::Mat</c> would have set for this sky part -- white,
    /// or the scene's fog colour where the gob carries the +0x19 tint byte. Three floats per draw.</summary>
    private static readonly float[] _skTint = new float[MaxSkyDraws * 3];
    private static int _skyFirst, _skyN;
    private static uint _drawBuf;
    /// <summary>#153: the per-draw texture-handle SSBO (binding <see cref="BindTexH"/>).</summary>
    private static uint _texHBuf;
    /// <summary>#137's bone block and the CPU stage behind it. One region, rewritten every colour pass.</summary>
    private static uint _boneBuf;
    private static readonly float[] _boneStage = new float[MaxBoneSlots * BoneFloats];
    private static int _boneSlots;
    /// <summary><c>GLRender::m_bones</c>: where the engine leaves a solved pose. Read only immediately after
    /// <see cref="BatchService.ForceSkinPose"/> returns true -- it is ONE array shared by every skinned
    /// draw in the frame, so it holds the last part posed and nothing else.</summary>
    private static float* _bones;
    /// <summary>#137 armed: the bone array resolved and <c>ownskin</c> not 0. False leaves the skinmesh
    /// class exactly where #128 left it -- refused at enrollment, drawn by the engine.</summary>
    private static bool _skinning;
    private static long _skinDrawn, _skinNoPose, _skinSlotOverflow;
}
