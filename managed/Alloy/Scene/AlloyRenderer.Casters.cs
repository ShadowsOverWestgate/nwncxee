using System;
using System.Numerics;

namespace Sowx.Alloy;

/// <summary>#302: the SHADOW pass's view of the ownership table. Alloy already keeps every fact a depth bake
/// needs -- the model matrix in the persistent record, the bone base in <c>feat.y</c>, the index range in the
/// VDO item tables under <c>_oVdo/_oItem</c>, the VAO in the shared cache, the world sphere beside the record
/// -- so <see cref="CasterPass"/> reads them from here rather than maintaining the parallel copy it kept from
/// before Alloy existed (ADR 0019).
/// <para>Read-only and NON-CONSUMING by construction. Nothing below clears a flag, advances a cursor or takes
/// an edge: ADR 0017's hazard was a mover signal spent by whichever pass asked first, and the shadow bracket
/// runs before the colour build, so a consuming read here would be that same bug by a new route.</para>
/// <para>One frame late, deliberately. The records describe the world as of the last colour build, which is
/// the frame before this one -- the same lag the skin poses already carry by design (#113). It is COHERENT:
/// the mover stamp and the record content describe the same world state, so the re-bake predicate cannot
/// miss a bake, and the shadow trails a fast rigid mover by one frame rather than disagreeing with it.</para>
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The frame this slot's record last CHANGED, stamped beside <c>_recFrame</c> (which is stamped
    /// every build, changed or not). The two being equal is the mover signal: the slot's most recent build
    /// was also a build that moved something.</summary>
    private static readonly int[] _recMovedAt = new int[MaxOwned];
    /// <summary>#302: the caster's world sphere, written on the same edge as <c>_dSph</c> and from the same
    /// terms -- but GEOMETRIC even where the cull is exempted. A skinmesh, a dangly mesh and a displacing
    /// feature all get <see cref="NeverCull"/> in <c>_dSph</c>, which is right for a culler and useless for a
    /// re-bake predicate: "somewhere" cannot name the views it dirties, so every such part would blanket-
    /// invalidate and #241's whole win with it. This carries the bind-pose bbox instead, which is exactly
    /// what the shadow pass located them by before.</summary>
    private static readonly float[] _dCastSph = new float[MaxRecords * 4];
    /// <summary>A <c>PartDanglyMesh</c>: its vertices move while its node matrix does not, so no record
    /// compare can infer motion for it. Captured at enrollment, as the caster pass captured it before.</summary>
    private static readonly bool[] _oDangly = new bool[MaxOwned];
    /// <summary>#302: the slot TENANCY generation -- which enrollment owns this slot, and which enrollment
    /// the record in it was built for. NOT the record epoch, and the distinction is a live-diagnosed flicker:
    /// the epoch is bumped by every wholesale invalidation -- enrollment, but ALSO an animated texture
    /// retiring a copy (<c>Drain -&gt; DropHandle</c>, roughly once a second in a torch-lit area) and a
    /// <c>/draw</c> dial -- and those non-enrollment bumps land AFTER the frame's record build, so the next
    /// shadow bracket would see the whole table epoch-stale and refuse it: every shadow gone for one frame,
    /// once a second. What the bake actually must not trust is a record built for a DIFFERENT PART in the
    /// same slot, and only an enrollment can change that; so tenancy gets its own generation, bumped in
    /// Enroll alone and restamped by the same colour pass that rebuilds the records.
    /// <para><c>_enrollGen</c> starts at 1 so a record never built (stamp 0) disagrees with every real
    /// enrollment.</para></summary>
    private static readonly int[] _oEnrollGen = new int[MaxOwned];
    private static readonly int[] _recEnroll = new int[MaxOwned];
    private static int _enrollGen = 1;
    /// <summary>#302 (review): XOR of the enrolled part pointers -- the pre-#302 design's set signature,
    /// restored. A changed set is the one edge the per-slot machinery cannot cover (a departing caster's
    /// slot is re-tenanted before the vanish sweep looks), and it costs one blanket shadow re-bake.</summary>
    private static ulong _shadowSig;
    /// <summary>Casters admitted with an unknown (degenerate) sphere this frame -- each is a PERMANENT MOVER
    /// with infinite reach, so a nonzero steady value here means the re-bake predicate is being defeated and
    /// says by how much. Read and cleared by the caster period line.</summary>
    internal static int CasterDegenerate;

    /// <summary>Casters refused this frame for want of a record built for the slot's current tenant. Read
    /// and cleared by the caster period line; a number that stays high after an area settles means the colour
    /// build is skipping slots the bake wants.</summary>
    internal static int CasterNoRecord;

    /// <summary>Entries in the draw order -- the caster walk's own bound.</summary>
    internal static int OrderCount => _orderCount;
    /// <summary>The ownership slot at draw-order position <paramref name="k"/>. Grouped by class then VAO,
    /// which is the order a bake wants to batch in anyway.</summary>
    internal static int OrderSlot(int k) => _order[k];

    /// <summary>Everything one shadow caster is, resolved out of the ownership table and the records. Values,
    /// not references: the bake stages an index range and a slot number, and reads the rest through
    /// <c>uDraw[slot]</c> on the GPU.</summary>
    internal struct CasterFacts
    {
        public IntPtr Vdo, Mat;
        public int Item, Prim, Vao;
        /// <summary>World sphere: centre and radius, both from <see cref="_dCastSph"/>.</summary>
        public float Cx, Cy, Cz, R;
        /// <summary>The material's diffuse alpha is REFLECTIVITY rather than coverage (env-mapped), so its
        /// cutout runs at the threshold that only drops empty texels.</summary>
        public bool EnvAlpha;
        /// <summary>The record changed on its last build -- the matrix, the fade, a material colour.</summary>
        public bool RecMoved;
        /// <summary>A <c>PartDanglyMesh</c>: a permanent mover, because its vertices move under a matrix that
        /// does not and no record compare can see it.</summary>
        public bool Dangly;
        /// <summary>Posed by bones, so it is exempt from the sub-texel LOD cull for the same reason the
        /// colour cull exempts it: the player's own shadow must not flicker out in a far cascade.</summary>
        public bool Skinned;
        /// <summary>The record is stale or was never built. Such a caster CASTS anyway -- unknown state means
        /// cast, the same direction <see cref="NeverCull"/> takes for unknown bounds -- and its sphere is not
        /// trusted, so it is neither culled nor LOD-dropped.</summary>
        public bool Unknown;
    }

    /// <summary>Resolve one ownership slot as a shadow caster. False = this slot casts nothing: it is not an
    /// opaque or cutout part, or its ownership was revoked. Never false for a slot whose RECORD is merely
    /// stale -- that is <see cref="CasterFacts.Unknown"/>, which casts.</summary>
    internal static bool TryCaster(int slot, out CasterFacts f)
    {
        f = default;
        // Only the two classes that can honestly write a binary depth: a blended part is by definition partly
        // see-through and an additive one emits light. Same rule the caster census applied.
        if (_oTri[slot] == IntPtr.Zero || _oCls[slot] > (byte)AlloyClass.Cutout) return false;
        // A FADING tile must not cast (review: the bucket gate that stood here was dead code -- the walk
        // never produces buckets 7/8, a mid-fade roof enrolls as bucket 2/Opaque). The colour path reads the
        // fade live and routes such a part into the blended run for the frame; the caster path reads the
        // same live factor and refuses, because a roof being faded out for the camera's benefit must not
        // keep the room under a full roof shadow. One field chase per caster per frame, factor 1 for the
        // overwhelming majority.
        if (PartTable.TileFadeFactor(_oTri[slot]) < 1f) return false;
        f.Vdo = _oVdo[slot]; f.Mat = _oMat[slot];
        f.Item = _oItem[slot]; f.Prim = _oPrim[slot]; f.Vao = _oVao[slot];
        f.EnvAlpha = (_oSlots[slot] & EnvSlot) != 0;
        f.Skinned = _oBone[slot] >= 0;
        // Was the record built for THIS tenant of the slot? Deliberately NOT the record epoch -- see
        // _oEnrollGen for the once-a-second flicker that asking the wider question produced. An epoch-stale
        // record with agreeing tenancy still carries the right geometry (the matrix does not go stale with a
        // texture handle), and geometry is all the depth stages read.
        f.Unknown = _recEnroll[slot] != _oEnrollGen[slot];
        // Read as a LEVEL, never as an edge: _recFrame is stamped on every build and _recMovedAt only on a
        // build that changed something, so the two agreeing is "the last build moved it" and asking twice
        // gives the same answer. ADR 0017's hazard was a signal the first pass to ask consumed.
        f.Dangly = _oDangly[slot];
        f.RecMoved = _recMovedAt[slot] == _recFrame[slot];
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)slot * 4;
            f.Cx = s[0]; f.Cy = s[1]; f.Cz = s[2]; f.R = s[3];
        }
        // #302: a slot with no trustworthy record is NOT a caster this frame. "Unknown means cast" is right
        // about unknown BOUNDS -- a part whose sphere is degenerate still bakes, uncullable, below -- but it
        // cannot mean "bake this geometry through a matrix that may belong to another part". Not staging it
        // is not a silent freeze either: it was in the previous frame's list, so the vanish sweep ghosts it
        // at its last known sphere and the views it stood in re-bake. Nor is it an enrollment-frame dropout:
        // the epoch bump and the record rebuild share one colour pass, so the next shadow bracket already
        // sees current records. What stays refused is only what the colour build itself skips -- and a part
        // nothing draws should cast nothing.
        if (f.Unknown) { CasterNoRecord++; return false; }
        // Unknown BOUNDS, on the other hand, mean cast: an infinite radius passes every reach test and fails
        // every sub-texel one, so such a caster is neither culled nor LOD-dropped. Matches CullPass -- and is
        // COUNTED, because an infinite mover defeats the re-bake predicate in every view it is admitted to,
        // which is exactly the silent cost ADR 0019 S3 exists to keep visible.
        if (!(f.R > 0f)) { f.Cx = 0f; f.Cy = 0f; f.Cz = 0f; f.R = NeverCull; CasterDegenerate++; }
        return true;
    }

    /// <summary>Is this ownership slot posed by bones? The cheap gate the caster walk uses to put the
    /// skinned casters at the head of its list without resolving every slot twice.</summary>
    internal static bool SlotSkinned(int slot) => _oBone[slot] >= 0;

    /// <summary>The engine wrote vertices into this VDO's position pool this frame, so every caster on it
    /// moved without its matrix moving. Resolved per VDO slot, not per caster.</summary>
    internal static bool VdoDirty(int v)
        => (uint)v < MaxVdos && _vVdo[v] != IntPtr.Zero && OwnedBuffers.DirtySince(_vPos[v].Buf);

    /// <summary>The last sphere written for a slot, live or revoked. A caster that VANISHED leaves no live
    /// entry to name the views it was standing in, so its last known sphere is what invalidates them.</summary>
    internal static void CasterSphere(int slot, out float cx, out float cy, out float cz, out float r)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)slot * 4;
            cx = s[0]; cy = s[1]; cz = s[2]; r = s[3];
        }
    }

    /// <summary>Does this VDO slot carry a diffuse UV stream? A masked caster needs one to sample its cutout
    /// with; without it there is nothing to test and the caster draws solid.</summary>
    internal static bool VdoHasUv(int v) => (uint)v < MaxVdos && _vUv[v].Present;

    /// <summary>The 4.6 VAO for a VDO slot, built on demand. Shadow and colour draw in the SAME context, so
    /// there is one VAO for both -- the whole reason the caster pass no longer keeps its own cache. 0 = the
    /// harvest could not build one and this caster does not cast this frame.</summary>
    internal static uint CasterVao(int v)
        => (uint)v < MaxVdos && EnsureVao(v) ? _vVao46[v] : 0u;

    /// <summary>#302 triage: what the bake will actually transform this slot by, beside what the engine says
    /// it is right now. The record is the shadow pass's ONE input for placement, so a disagreement here is
    /// the whole bug and a match acquits the data plane -- there is nothing else it could be reading.
    /// <para><paramref name="lag"/> is the record's translation, <paramref name="live"/> the node's this
    /// instant. They differ by one frame's motion on a mover and by nothing at all on a still part; anything
    /// else (a zero record, a wildly different place) is a slot whose record was never built.</para></summary>
    internal static void CasterProbe(int slot, out Vector3 lag, out Vector3 live,
                                     out bool mx, out bool known, out int feat)
    {
        fixed (float* d = _dData)
        {
            var m = *(Matrix4x4*)(d + (long)slot * DrawFloats);
            lag = new Vector3(m.M41, m.M42, m.M43);
            feat = (int)d[(long)slot * DrawFloats + 40];
        }
        mx = _oMx[slot];
        known = _recGen[slot] == _recEpoch;
        live = mx && _oNode[slot] != IntPtr.Zero ? new NodeRef(_oNode[slot]).Model.Translation : Vector3.Zero;
    }

    /// <summary>The shadow pass's own copy of the buffer names it binds: the persistent per-draw records and
    /// the bone block, both of which the bake reads through the SAME bindings the colour path uses.</summary>
    internal static uint DrawRecordBuf => _drawBuf;
    internal static uint BoneBlockBuf => _boneBuf;
}
