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
    /// frame in <c>PoseSkinned</c>, never carried across one -- a pose is per-frame by definition.</summary>
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
    /// <summary>#292: this part's material said <c>twosided</c>, so it draws with face culling OFF. The
    /// engine's default -- and what a part with no material gets -- is culling ON, which is why this is the
    /// exception flag rather than a cull flag.</summary>
    private static readonly bool[] _oNoCull = new bool[MaxOwned];
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
    /// <summary>#126: blended parts dropped from this enrollment because the class could not be Alloy whole.
    /// #285: dropped means NOT DRAWN -- there is no engine walk behind the class since #264.
    /// Reported: a class that never lands is the one number that separates "no transparent content here"
    /// from "one part keeps refusing and takes the class down with it".</summary>
    private static int _blendedDropped;

    // per-VDO VAO cache: attribute formats harvested in the ENGINE context at enrollment, the 4.6-side VAO
    // built lazily inside the bracket (VAOs don't share across contexts; the buffers they reference do)
    private static readonly IntPtr[] _vVdo = new IntPtr[MaxVdos];
    private static readonly uint[] _vVao46 = new uint[MaxVdos];
    /// <summary>#260: does Alloy own the pools this slot's VAO pulls from? There is only one VAO now -- the
    /// takeover uploads into the pool's OWN buffer, so "Alloy's copy" and "the engine's buffer" are the same
    /// name -- and this is what is left of the distinction: the acceptance signal
    /// (<see cref="AlloyRenderer.VaoName"/>), stamped once per slot build rather than probed per draw.</summary>
    private static readonly bool[] _vOwned = new bool[MaxVdos];
    private static readonly uint[] _vElemBuf = new uint[MaxVdos];
    private static readonly VertexStream[] _vPos = new VertexStream[MaxVdos];
    private static readonly VertexStream[] _vUv = new VertexStream[MaxVdos];
    /// <summary>#294: the engine's UV location was DISABLED on this VDO's VAO, as opposed to enabled over no
    /// buffer. Both leave <c>_vUv</c> absent and both are owned, but only the first proves the engine's
    /// vertex stage reads the same generic (0,0,0,1) constant Alloy's does -- so this is a report, not a
    /// gate.</summary>
    private static readonly bool[] _vUvDisabled = new bool[MaxVdos];
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

    /// <summary>#294: which live VDO slots carry NO UV stream, for the frame dump's header. A part on one of
    /// these samples every texture at the generic constant (0,0) -- one texel of the diffuse, one texel of
    /// each map -- so its whole material is a single sample stretched over the mesh. That is the engine's
    /// input too, but it is the first thing to suspect when an owned part draws in the wrong COLOUR rather
    /// than not at all, and no other column in the dump would say so. Slot indices, matching the <c>vao=</c>
    /// field on the G lines.</summary>
    internal static string UvlessSlots()
    {
        string s = "";
        for (int v = 0; v < _vdoCount; v++)
        {
            if (_vVdo[v] == IntPtr.Zero || _vUv[v].Present) continue;
            // disabled = the engine's own vertex stage reads the same constant, so Alloy matches it.
            // unbacked = the location is ENABLED over no buffer, and then it does not: the engine may be
            // pulling real UVs where Alloy feeds (0,0), which is a silent misrender rather than parity.
            s += (s.Length == 0 ? "" : ",") + v + (_vUvDisabled[v] ? "(disabled)" : "(unbacked)");
        }
        return s.Length == 0 ? "none" : s;
    }
    /// <summary>Counting-sort buckets. The draw order groups by (lit, no-cull, VDO) so the VAO, the PROGRAM
    /// and (#292) the face-culling state each settle once per group -- lit slots are the upper half, and the
    /// two-sided slots the upper half of each of those. <see cref="SortKeyOf"/> says why lit outranks cull.
    /// </summary>
    private static readonly int[] _sortCount = new int[MaxVdos * 4];
    /// <summary>#84 second radix digit: counting-sort buckets over the interned texture key. Sorting by it
    /// FIRST and then stably by (lit, VDO) leaves the order grouped by (lit, VDO) with equal textures
    /// contiguous inside each group -- i.e. exactly the batch key, in one pass over the table.</summary>
    private static readonly int[] _texSortCount = new int[MaxMats];
    private static readonly IntPtr[] _mKey = new IntPtr[MatHash];
    private static readonly int[] _mVal = new int[MatHash];
    private static int _matCount;

    /// <summary>#244 (AC1): the mesh-sharing census's scratch. One packed (VDO slot, matrix-class, item) key
    /// per live owned part, sorted in place at enrollment so equal meshes come out adjacent. Cold -- it runs
    /// on the enrollment cadence, never per frame.</summary>
    private static readonly long[] _msKey = new long[MaxOwned];
    /// <summary>#244: the census key's layout -- VDO slot, then the matrix-class bit, then the item's INDEX
    /// RANGE (first index, then element count) -- so a sort groups equal meshes and the bit can be read back
    /// off the run's key. Named because the pack and the read-back are two sites that must agree.
    /// <para>The range rather than the item INDEX, and that is the difference between two tiers of sharing:
    /// two placements handed distinct item entries that name the same (offset, count) are the same mesh, and
    /// a key on the index cannot see it. The entry is one dereference the enrollment can afford.</para>
    /// <para>63 bits, so the key stays positive and sorts as an ordinary long: 12 for the VDO slot (capacity
    /// <see cref="MaxVdos"/>), 1 for matrix-class, 32 for the first index and 18 for the count. ponytail: a
    /// count past <see cref="MsCountMax"/> saturates rather than carrying into the offset field -- an item
    /// of a quarter-million indices does not exist on NWN content, and a wrong SATURATION merges two meshes
    /// that differ only above that size, which over-reports sharing rather than hiding it.</para></summary>
    private const int MsVaoShift = 51, MsFirstShift = 18;
    private const long MsMxBit = 1L << 50;
    private const int MsCountMax = (1 << 18) - 1;
    /// <summary>#244: the SHAPE key's layout -- element count in the low bits under the interned diffuse
    /// texture, the primitive mode and the gob bit. Same count field as the range key, so the clamp is
    /// shared. <see cref="MaxMats"/> is 2^14, which is the width reserved for the texture.</summary>
    private const int MsTexShift = 18, MsPrimShift = 32;
    private const long MsGobBit = 1L << 35;
    /// <summary>#244: matrix-class, in the SHAPE key too. It is the collapsibility discriminator, not a
    /// detail: an object-space part's vertices are the model's own, so two placements of one model hash
    /// identically and could genuinely share them -- while a world-space part has its placement baked into
    /// those vertices, so two placements differ by construction and no de-duplication can collapse them
    /// without un-baking the transform first. The hash reporting them apart is the truth about what is
    /// collapsible, not a miss.</summary>
    private const long MsShapeMxBit = 1L << 34;
    /// <summary>#244: the shape census's scratch, parallel to <see cref="_msKey"/> and sorted the same way.
    /// A second array rather than a second walk: the guarded element-pool read that both keys need is the
    /// expensive half, and doing it once is what keeps this on the enrollment cadence.</summary>
    private static readonly long[] _msShape = new long[MaxOwned];
    /// <summary>#244: what the last census found -- distinct meshes, the largest instance count on any one
    /// mesh, how many parts sit on a mesh at least two parts share, and how many of THOSE are matrix-class.
    /// The last number is the one the ticket turns on: a world-space part has an identity model matrix, so
    /// "same mesh, different transform" is not even true of it and instancing it collapses nothing.</summary>
    /// <summary>#244: parts the last census actually keyed, and parts it had to leave out because their
    /// index range would not resolve. The second number is what stops the first being read as the
    /// population when the dereference went blind.</summary>
    private static int _msParts, _msUnread;
    private static int _msMeshes, _msMaxInstances, _msShared, _msSharedMx;
    /// <summary>#244: the shape tier -- distinct meshes by signature, the largest repeat, how many parts sit
    /// on a repeated shape and how many of those are gob (placeable/creature) parts. This is the tier the
    /// range key is blind to: repeated geometry the engine copied per placement.</summary>
    private static int _msShapes, _msShapeMax, _msShapeRepeat, _msShapeRepeatGob;
    /// <summary>#244: how many censused parts belong to a gob at all -- the denominator that says whether a
    /// placeable-heavy area was even in the population.</summary>
    private static int _msGobParts;
    /// <summary>#244: censused parts that are matrix-class, and the repeated ones among them. If the first is
    /// near zero the area's geometry is world-space and nothing repeated in it is collapsible whatever the
    /// shape tier says -- which is the reading that decides whether a repeat count means anything.</summary>
    private static int _msMxParts, _msShapeRepeatMx;

    // #84 multidraw submission: one entry per DRAW in _dCount/_dOff/_dBase + one staged SSBO record per draw,
    // sliced into batches by the _g* table. All static -- the hot path stages into them and allocates nothing.
    private static readonly int[] _dCount = new int[MaxOwned];
    private static readonly nint[] _dOff = new nint[MaxOwned];
    private static readonly int[] _dBase = new int[MaxOwned];
    private static readonly float[] _dData = new float[MaxRecords * DrawFloats];
    /// <summary>#243: the world bounding sphere of each RECORD, parallel to <see cref="_dData"/> -- centre
    /// in xyz, radius in w. Written on the same edges the record is (stable once at enrolment, again
    /// whenever the part moves), because the sphere is a function of the same model matrix. A radius of
    /// <see cref="NeverCull"/> means "do not cull me": the vertices move under a matrix that does not
    /// (skinned, dangly, a displacing feature), or the bbox is degenerate. Unknown bounds mean VISIBLE --
    /// the failure mode of a cull is invisible geometry.</summary>
    private static readonly float[] _dSph = new float[MaxRecords * 4];
    /// <summary>The radius that exempts a record from the cull. Large enough to fail every plane test and
    /// finite, so it survives a matrix multiply without becoming a NaN.</summary>
    internal const float NeverCull = 1e30f;
    /// <summary>#153: per-draw bindless handles, 8 per record (diffuse + material slots 1..7), parallel to
    /// <see cref="_dData"/>. 0 = slot empty; the record's slot mask already forbids sampling those.</summary>
    private static readonly ulong[] _dTexH = new ulong[MaxRecords * 8];
    /// <summary>#242: the per-draw index array -- for draw <c>n</c> of this pass, the RECORD slot it reads.
    /// The records themselves are indexed by ownership slot and persist across frames, while draw order is
    /// per pass (a fading tile joins the blended run, and that run is sorted from this pass's eye), so the
    /// two cannot be the same number. This is the only per-draw array the frame still uploads whole -- four
    /// bytes each rather than a hundred and seventy-six.</summary>
    private static readonly int[] _dSlot = new int[MaxOwned];
    /// <summary>#242: which record slots were rewritten this frame, i.e. the movers. Uploaded one
    /// <c>BufferSubData</c> each; past a quarter of the table the whole range goes up in one call instead.
    /// </summary>
    private static readonly int[] _recDirty = new int[MaxRecords];
    private static int _recDirtyN;
    /// <summary>#242: the epoch each slot's record was built under, and the frame it was last verified on.
    /// A slot whose epoch is stale rebuilds unconditionally; one already verified this frame is free for
    /// every pass after the first. Epoch 0 is "never built", so the arrays need no initialisation.</summary>
    private static readonly int[] _recGen = new int[MaxOwned];
    private static readonly int[] _recFrame = new int[MaxOwned];
    /// <summary>#242: the blanket invalidation counter. Bumped by anything that can make every persistent
    /// record wrong at once -- an enrollment (the slots reshuffle), a bindless handle wipe, a lost context,
    /// a debug toggle, an armed frame dump. See ADR 0017's "Step 2, as built".</summary>
    private static int _recEpoch = 1;
    /// <summary>#242: the borrowed <c>CAurTexture*</c> this slot's record resolved its diffuse (and each
    /// declared map slot) from, last rebuild. The pointer is the CHEAP half of the texture path -- a plain
    /// field read off the material -- and the tid and bindless handle behind it are the expensive half, so
    /// an unchanged pointer is what buys the whole re-fetch being skipped. An animated texture cycles engine
    /// names, so its pointer changes every frame and it is a permanent mover; that is correct.</summary>
    private static readonly IntPtr[] _recAur = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _recAurS = new IntPtr[MaxOwned * MapSlots];
    /// <summary>#242: the resolved tids and live slot mask behind <see cref="_recAur"/> -- still needed
    /// every frame by the batch key (a bind-path batch cuts on its textures) even when the record itself
    /// does not move.</summary>
    private static readonly int[] _recTid = new int[MaxOwned];
    private static readonly int[] _recTidS = new int[MaxOwned * MapSlots];
    private static readonly int[] _recSlotMask = new int[MaxOwned];
    /// <summary>#242: how many record slots the persistent GPU buffer holds for OWNED parts, the sky's
    /// reserved block sitting immediately above them. Grows to the high-water ownership count and never
    /// shrinks; a growth reallocates, so the whole range is re-sent on that frame.</summary>
    private static int _recOwnCap;
    /// <summary>#242: the dirty list overflowed (or the buffer was just reallocated) -- send the whole
    /// range instead of naming slots. Correct, and merely more expensive than the incremental path.</summary>
    private static bool _recDirtyAll = true;
    /// <summary>#242: the audit AC3 asks for -- record writes should scale with the MOVER count. If
    /// "written" reads as the owned count, the split has a permanent mover in it.</summary>
    private static long _recWritten, _recStable;
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
    /// <summary>#322: the draw-arg cache -- index count, byte offset and base vertex as read off the VDO
    /// item tables the last time this slot staged. Valid while stamped with the live epoch AND the stamp
    /// survives <c>UnstampRecord</c>, which fires on every re-tenant and drifted keep -- the only edges that
    /// re-point <c>_oVdo</c>/<c>_oItem</c>; Repack revokes before the tables move, and a freed VDO kills its
    /// parts through the identity check first. Measured 2026-08-31: cutting these
    /// (plus the duplicate identity compare) did NOT move the Restage bracket -- its bulk is elsewhere in
    /// the staging loop. The cache stays because it is correct and strictly cheaper, not because it won.</summary>
    private static readonly int[] _cIdx = new int[MaxOwned];
    private static readonly nint[] _cOff = new nint[MaxOwned];
    private static readonly int[] _cBase = new int[MaxOwned];
    private static readonly int[] _cGen = new int[MaxOwned];
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
    /// <summary>#292: this batch draws with face culling off. A batch key term rather than a per-draw one:
    /// culling is fixed-function GL state and one multidraw cannot vary it.</summary>
    private static readonly bool[] _gNoCull = new bool[MaxOwned];
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
    /// <summary>#242: the per-draw index SSBO (binding <see cref="BindDrawIdx"/>), and how many records the
    /// GPU side of <see cref="_drawBuf"/>/<see cref="_texHBuf"/> currently holds. A capacity growth
    /// reallocates both, which is why it forces a wholesale re-send.</summary>
    private static uint _slotBuf;
    private static int _recCapGpu;
    /// <summary>#137's bone block and the CPU stage behind it. One region, rewritten every colour pass.
    /// #262: the stage is native and GROWS (<c>EnsureBoneSlots</c>) rather than turning a crowd past the
    /// budget into dropped creatures; <c>_boneHigh</c> is the per-report high-water mark that keeps the
    /// ceiling counted rather than silent.</summary>
    private static uint _boneBuf;
    private static float* _boneStage;
    private static int _boneSlots, _boneCap, _boneHigh;
    /// <summary>#137 armed: <c>ownskin</c> not 0. False refuses the skinmesh class at enrollment -- which
    /// since #264 means every cloak, robe and tail is NOT DRAWN, not drawn by the engine (#285).</summary>
    private static bool _skinning;
    /// <summary>#262: did <see cref="SkinPose.SelfCheck"/> pass? Checked once at install, ahead of every
    /// engine-symbol stand-down, so the answer is about this BUILD and not about this client.</summary>
    private static bool _skinMathOk;
    private static long _skinDrawn, _skinNoPose;
}
