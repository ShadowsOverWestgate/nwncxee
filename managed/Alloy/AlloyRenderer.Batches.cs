using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>#173: BATCH BUILDING. Revalidating every Alloy part against the live scene, routing it to this pass's class, sorting the blended remainder back-to-front, and staging the per-draw records and multidraw commands the issue phase submits. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#126: this pass's draw ORDER, ahead of the staging walk. Three things it settles that the
    /// enrollment order cannot: a tile that started fading since the last enrollment leaves the opaque set
    /// for the blended one (bucket 8 is a per-frame fact, not a material one), the blended run is sorted
    /// back-to-front from THIS pass's eye, and every entry carries the alpha multiplier its draw needs.
    /// <para>The sort is the only one in the frame, and it is paid by the blended remainder alone -- which is
    /// the whole point of the cutout/blended split: the cutout majority is order-independent and stays in the
    /// texture-grouped opaque order.</para>
    /// <para>#327: ...order-independent is also why each opaque class is PARTITIONED on the keyhole dissolve
    /// bit here. It is the one cut-key term the enrollment radix cannot sort (camera state, thousands of
    /// parts at once under a roof), and CullPass compacts a batch with an atomic, so staging order is not
    /// draw order: the dissolving members go after the rest of their class, so the dissolve term cuts once
    /// per class and the tail re-cuts only on the sorted static key -- at most twice the distinct static
    /// keys, rather than once per interleave.</para>
    /// <para>ponytail: insertion sort, i.e. O(n^2) in the blended count. That count is the small minority of
    /// a scene by construction, and the alternative (a radix over quantised depth) is a bucket table to size
    /// and clear per pass. If a scene ever lands enough blended parts to show on the [alloy] line's colour
    /// time, that is the upgrade.</para></summary>
    private static int RouteParts()
    {
        int n = 0, nb = 0;
        PrepareVisible();
        OccludeFrame();   // #537: the hit fan this walk tags occluders from
        fixed (float* sph = _dSph)
        {
        for (int c = (int)AlloyClass.Opaque; c <= (int)AlloyClass.Cutout; c++)
        {
            int nd = 0;   // _orderTmp is the enrollment radix's scratch, idle during a pass
            int[] list = RouteRange(c, out int from, out int to);
            for (int k = from; k < to; k++)
            {
                if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
                bool samp = (k & (ChaseEvery - 1)) == 0;
                long tFade = samp ? FrameZones.Now : 0;
                // The fade overlay, and the part's own animated alpha with it: while its tile fades, an opaque
                // or cutout part IS a bucket-8 part -- the engine folds the tile's factor into the part's own
                // alpha for the length of the draw (RenderDrawBucket @0x46d060, bucket 8 only) and draws it
                // blended. So does this pass. Reading the part alpha live here (rather than trusting the class
                // from the last enrollment) is also what keeps a part whose alpha animated below 1 from standing
                // solid for up to an EnrollEvery cadence.
                // #328: the tile term is kept apart -- it is the one alpha term the shadow side refuses on (a
                // part fading by its OWN alpha still casts), and this is the frame's one chase of it.
                float fade = TileFade(i, tri);
                // #546: an unexplored tile (fog-of-war ramp at 0) is not drawn at all, as vanilla skips a

                if (fade <= 0f)
                { if (_oGob[i] != IntPtr.Zero) Counters._skipFogGob++; else Counters._skipFogTile++; _oFading[i] = true; W(i, "skipped: tile fog ramp at 0"); continue; }
                _oFading[i] = fade < 1f;
                // #537: a faded occluder is a translucent part like any other, so it takes the blended run too.
                float alpha = PartAlphaCached(i, tri) * fade * (_oGob[i] != IntPtr.Zero ? AlloyCensus.GobAlphaMul(tri) : 1f)
                              * (1f - OccludeVerdict(i));
                bool dis = alpha >= 1f && Dissolves(i, tri, ref _stageCounters[0]);
                if (samp) FrameZones.AddSampled(FrameZones.Zone.Fade, tFade, ChaseEvery);
                if (alpha < 1f)
                { Counters._ownFade++; _blend[nb] = i; _blendAlpha[nb] = alpha; _blendKey[nb] = BlendKey(i); nb++; continue; }
                if (dis) { _orderTmp[nd++] = i; continue; }
                _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; _passDis[n] = false; n++;
            }
            for (int d = 0; d < nd; d++)
            { int i = _orderTmp[d]; _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; _passDis[n] = true; n++; }
        }
        {
        int[] list = RouteRange((int)AlloyClass.Blended, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _blend[nb] = i;
            _blendAlpha[nb] = alpha;
            _blendKey[nb] = BlendKey(i); nb++;
        }
        }
        SortBlended(_blend, _blendKey, _blendAlpha, nb);
        for (int a = 0; a < nb; a++)
        { _pass[n] = _blend[a]; _passCls[n] = (byte)AlloyClass.Blended; _passAlpha[n] = _blendAlpha[a]; n++; }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        {
        int[] list = RouteRange((int)AlloyClass.Water, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Water;
            _passAlpha[n] = alpha;
            n++;
        }
        }
        {
        int[] list = RouteRange((int)AlloyClass.Additive, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
            // Additive fades too: the engine routes an additive part on a fading tile into bucket 8 like any
            // other, and IterSight suppresses it there, so a glow left at full brightness through a fade
            // would be ours to explain.
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Additive;
            _passAlpha[n] = alpha;
            n++;
        }
        }
        }
        return n;
    }

    /// <summary>#322: the route-time validation contract, shared by all four class runs -- resolve the
    /// ordered slot, skip a vacated one, and REVOKE a failed identity (a recycled or freed pointer; every
    /// dereference the frame makes from here on sits behind this verdict, so the staging walk does not
    /// re-check). Sound across the walk because both walks run inside one bracket on one thread, exactly
    /// as the blended sort's route-time <see cref="EyeDist2"/> chase already assumes.</summary>
    /// <para>#330: ...and behind the FRUSTUM GATE, which runs before the first dereference of the part. A
    /// slot the engine did not visit and this pass cannot see keeps last frame's record untouched: nothing
    /// the engine does to an off-screen part reaches it (see <see cref="RouteWanted"/>).</para>
    private static bool RouteLive(int slot, int k, float* sph, out int i, out IntPtr tri)
    {
        // #336: the Route sample -- taken on the slots the gate rejects, which is the walk's O(world) half
        bool samp = (k & (ChaseEvery - 1)) == 0;
        long t0 = samp ? FrameZones.Now : 0;
        i = slot;
        tri = _oTri[i];
        if (tri == IntPtr.Zero || !RouteWanted(i, sph))
        {
            if (samp) FrameZones.AddSampled(FrameZones.Zone.Route, t0, ChaseEvery);
            W(i, tri == IntPtr.Zero ? "skipped: slot vacated (revoked or class dropped)"
                                    : "skipped: off this pass's frustum, record kept");
            return false;
        }
        if (!IdentityOk(i, tri))
        { Counters._skipIdentity++; W(i, "skipped: identity changed -- revoked"); Revoke(i); return false; }
        // Fault 1: the engine has never composed this gob's world matrix, so vp+0x18 is still the identity
        // the VertexPrimitive ctor left and the part would draw at the area's (0,0) corner. See PartPlaced.
        if (!PartPlaced(i))
        { Counters._skipUnplaced++; SetCastOk(i, false); W(i, "skipped: gob never placed -- identity matrix"); return false; }
        // #336: a part of a LOD tree the gob is not showing. The engine draws GobPartTree alone; the walk
        // enrols all three trees so a swap is a field read, and this is that read. Casts nothing either.
        if (_oLodRoot[i] != IntPtr.Zero && *(IntPtr*)(_oGob[i] + GobPartTree) != _oLodRoot[i])
        { Counters._skipLod++; SetCastOk(i, false); W(i, "skipped: inactive LOD alternate"); return false; }
        if (samp) FrameZones.AddSampled(FrameZones.Zone.Ident, t0, ChaseEvery);
        return true;
    }

    /// <summary>#287: the cull's source list, one <see cref="CullPass.SrcUints"/>-uint record per DRAW of
    /// this pass, staged beside <c>_dCount/_dOff/_dBase/_dSlot</c> by the walk that already writes them.
    /// <para>It used to be rebuilt by a second walk inside <see cref="CullPass.Begin"/>. The four draw-args
    /// columns cannot leave the CPU -- <c>/alloy cull 0</c> has to fall back to
    /// <c>glMultiDrawElementsBaseVertex</c>, which takes CPU pointers -- but writing them twice can, and the
    /// two columns the second walk existed for (the draw's batch and that batch's output base) are both known
    /// at this store, because the cut has just placed the draw.</para></summary>
    private static readonly uint[] _dSrc = new uint[MaxOwned * CullPass.SrcUints];

    /// <summary>Is this slot's part still the one that enrolled? The single-level compare
    /// <see cref="RouteParts"/> revokes on, once per pass at route (the staging walk trusts the verdict; #322)
    /// -- node, material and texture rotation read straight off the part. Reading a stale part is the
    /// accepted risk model here; chasing a POINTER out of one is not, so every dereference this pass makes
    /// sits behind this. Pointer identity ONLY: an instance's live shading state is a skip of its own in
    /// <see cref="BuildBatches"/>, since a false answer here would leave the part in the OPAQUE run at alpha 1
    /// and the staging walk never re-asks (#128).
    /// <para>The entry must be a LIVE recording of this part before any of that is read: a destroyed part can
    /// keep every field long enough to pass the compares, and then the gob chase in <see cref="LiveAlpha"/>
    /// walks a freelist word. <c>Part::~Part</c> is what makes this answer no (#145).</para>
    /// <para>The three field compares are an AUDIT on the colour cadence, not a per-frame gate. The Ghidra
    /// store scan (every write to <c>[reg+0x58|0x88|0xb0]</c> in the binary, on the ticket): part+0x58 has NO
    /// post-construction writer -- set in <c>PartTriMesh::PartTriMesh</c>, freed by <c>Part::~Part</c>;
    /// part+0x88 is written once by <c>PartInitMaterial</c> @0x43de50 and part+0xb0 once by
    /// <c>SetPartTextureRotate</c> @0x461670, both on a tree <c>CreateInstanceTree</c> made moments before,
    /// never on a part that has been drawn. So <c>EntryLive</c> (the death notice) is the whole lifetime
    /// guard, and the compares run every <see cref="ColourCadence"/> frames per slot (every frame under
    /// <c>/alloy poll 1</c>) so a writer the scan could miss (a LEA-then-store, a memcpy) shows on the [alloy]
    /// line as <c>identity N</c> within eight frames rather than never (#336).</para></summary>
    private static bool IdentityOk(int i, IntPtr tri)
    {
        if (!PartTable.EntryLive(_oEntry[i], tri)) return false;
        if (!_fullPoll && ((i + _frameSeq) & (ColourCadence - 1)) != 0) return true;
        var part = new PartRef(tri);
        return part.Node.Ptr == _oNode[i] && part.Material.Ptr == _oMat[i]
               && part.TexRotateBits == _oRotBits[i];
    }

    /// <summary>#126: order the blended run back to front -- the farthest key first, so nearer surfaces
    /// composite over what is already behind them. Stable, which is what keeps parts at equal distance in
    /// their texture-grouped enrollment order rather than shuffling them from pass to pass.</summary>
    /// <summary>Off-device: the sort really is back-to-front, really is stable, and really carries each
    /// entry's alpha with its index -- a permutation that lost the alpha pairing would draw every blended
    /// part at its neighbour's transparency, which reads as "the sort is wrong" and is not.</summary>
    private static bool SortSelfCheck(ExtenderLog log)
    {
        int[] idx = { 10, 11, 12, 13 };
        float[] key = { 1f, 9f, 5f, 9f }, alpha = { 0.1f, 0.2f, 0.3f, 0.4f };
        SortBlended(idx, key, alpha, 4);
        bool ok = idx[0] == 11 && idx[1] == 13 && idx[2] == 12 && idx[3] == 10   // stable within the 9f tie
                  && key[0] == 9f && key[1] == 9f && key[2] == 5f && key[3] == 1f
                  && alpha[0] == 0.2f && alpha[1] == 0.4f && alpha[2] == 0.3f && alpha[3] == 0.1f;
        if (!ok) log.Info("AlloyRenderer.SortSelfCheck: FAILED -- the blended order is not back-to-front");
        return ok;
    }

    /// <summary>#292: the middle radix digit's buckets and the rank of its two bits. Both halves matter and
    /// they fail differently: a key wider than <see cref="_sortCount"/> throws out of the enrollment sweep
    /// (loudly, but only on content that happens to reach the top bucket), and a wrong RANK costs nothing
    /// visible at all -- it just re-sends the lighting rig once more per class forever. So the width is
    /// asserted against the array itself rather than against the constant it was sized from.</summary>
    private static bool CullSortSelfCheck(ExtenderLog log)
    {
        int unlitCulled = SortKeyOf(false, false, 0), unlitFree = SortKeyOf(true, false, MaxVdos - 1);
        int litCulled = SortKeyOf(false, true, 0), litMax = SortKeyOf(true, true, MaxVdos - 1);
        bool ok = _sortCount.Length == MaxVdos * 4 && litMax == MaxVdos * 4 - 1
                  && unlitCulled == 0 && unlitFree < litCulled;
        // #327: the (feature, primitive) digit fills its buckets exactly, and an out-of-table prim clamps.
        ok &= (int)FeatCeiling == ShaderFeatures.VsNames.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling, PrimTypes - 1) == _featPrimCount.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling, PrimTypes + 5) == _featPrimCount.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling + 3, 0) == _featPrimCount.Length - PrimTypes
              && FeatPrimKeyOf(0, -1) == 0 && FeatPrimKeyOf(0, 0) == 0;
        if (!ok) log.Info("AlloyRenderer.CullSortSelfCheck: FAILED -- a radix digit does not fit its " +
                          "buckets, or the cull bit outranks the lit one");
        return ok;
    }

    /// <summary>The packing <see cref="SortKey"/> applies, spelled without the tables so the check above can
    /// exercise it. LIT is the senior bit: a program switch re-sends the whole lighting rig (cascades, local
    /// cubes, the probe -- see <see cref="Issue.IssueBatchesInner"/>), and face culling is one <c>glEnable</c>. So
    /// the order inside a class is (unlit culled, unlit two-sided, lit culled, lit two-sided): one program
    /// bind per class as before, and at most two cull toggles.</summary>
    private static int SortKeyOf(bool noCull, bool lit, int vao)
        => ((lit ? 2 : 0) + (noCull ? 1 : 0)) * MaxVdos + vao;

    /// <summary>#327: entries in the engine's <c>primTypeTranslationTable</c> -- 0x1c bytes of uint per
    /// <c>nm -S</c>, indexed by the mdl node's prim type. The key clamps rather than trusts the index: the
    /// draw path reads the table unguarded, but a sort bucket must not.</summary>
    private const int PrimTypes = 7;
    /// <summary><see cref="ShaderFeature"/>'s last member. Named here because the sort table below is sized
    /// from it: #348 (NoirWet/NoirGloss) and #556 (GroundWeather) appended above what was then the ceiling
    /// and this const kept naming <see cref="ShaderFeature.Skin"/>, so the first part carrying one of them
    /// walked <c>_featPrimCount</c> off its end and the enrollment threw IndexOutOfRange mid-frame -- and
    /// #556 alone is most of the outdoor tileset, so that was every rainy street.
    /// <see cref="CullSortSelfCheck"/> now ties it to <see cref="ShaderFeatures.VsNames"/>, which any
    /// appended feature has to extend anyway -- the old check compared Skin against a table sized from Skin
    /// and was therefore true by construction.</summary>
    private const ShaderFeature FeatCeiling = ShaderFeature.GroundWeather;
    /// <summary>Buckets for the (feature, primitive) digit.</summary>
    private const int FeatPrimKeys = ((int)FeatCeiling + 1) * PrimTypes;
    /// <summary>Both indices are CLAMPED, not trusted: this is a sort digit, so a feature past the ceiling
    /// merely shares a bucket (one extra batch cut, never a wrong draw) where an unclamped one takes the
    /// frame down. The self-check is what makes the drift loud; the clamp is what makes it survivable.</summary>
    private static int FeatPrimKeyOf(int feat, int prim)
        => Math.Clamp(feat, 0, (int)FeatCeiling) * PrimTypes + Math.Clamp(prim, 0, PrimTypes - 1);

    private static void SortBlended(int[] idx, float[] key, float[] alpha, int n)
    {
        for (int a = 1; a < n; a++)
        {
            int i = idx[a]; float k = key[a], al = alpha[a];
            int b = a - 1;
            for (; b >= 0 && key[b] < k; b--)
            { idx[b + 1] = idx[b]; key[b + 1] = key[b]; alpha[b + 1] = alpha[b]; }
            idx[b + 1] = i; key[b + 1] = k; alpha[b + 1] = al;
        }
    }

    /// <summary>The part's own alpha multiplier (part+0xa0), live: it is animated state, not enrollment
    /// state.</summary>
    private static float PartAlpha(IntPtr tri) => new PartRef(tri).Alpha;

    /// <summary>#336: the part's own alpha, read off the part every frame for a gob part or a matrix-class
    /// part (the animated ones) and every <see cref="ColourCadence"/> frames for a still gobless part --
    /// the tile geometry that is the routed majority, whose +0xa0 only an MDL alpha controller writes.
    /// Under <c>/alloy poll 1</c> every frame reads and audits the cache.</summary>
    private static float PartAlphaCached(int i, IntPtr tri)
    {
        bool poll = _oGob[i] != IntPtr.Zero || _oMx[i] || !_alphaKnown[i] || _fullPoll
                    || ((i + _frameSeq) & (ColourCadence - 1)) == 0;
        if (!poll) return _oAlpha[i];
        float a = PartAlpha(tri);
        if (_fullPoll && _alphaKnown[i] && a != _oAlpha[i]) _pollMissAlpha++;
        _oAlpha[i] = a; _alphaKnown[i] = true;
        return a;
    }
    private static readonly float[] _oAlpha = new float[MaxOwned];
    private static readonly bool[] _alphaKnown = new bool[MaxOwned];

    /// <summary>#128: this frame's whole alpha multiplier for a part -- its own animated alpha, the fade
    /// factor of the tile it stands on, and the instance's two multiplies. Every term is read live and none
    /// is cached at capture, which is what the creature case demands: a gob can fade a whole creature into
    /// the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(int i, IntPtr tri)
        => PartAlpha(tri) * TileFade(i, tri) * AlloyCensus.GobAlphaMul(tri);

    /// <summary>Squared distance from THIS pass's eye to the part's world centre -- the blended sort key.
    /// Derived exactly as the caster cull derives it and for the same reason (live-verified there): a
    /// matrix-class part's centre is its model matrix translation, and everything else is world-space
    /// geometry whose bbox centre already is the answer.</summary>
    /// <summary>The blended sort key: a gob part sorts by its RUN gob's distance (see <see cref="RunGob"/>),
    /// so every part of one creature carries the same key and the stable sort lands them contiguous -- one
    /// run the frame can depth-prepass and colour as a unit. A gobless part keeps its own centre.</summary>
    private static float BlendKey(int i)
    {
        IntPtr g = RunGob(i);
        if (g == IntPtr.Zero) return EyeDist2(i);
        float* p = new GobRef(g).Position;
        float dx = p[0] - _eyePos.X, dy = p[1] - _eyePos.Y, dz = p[2] - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The gob a blended part composites WITH: its fade owner when it has one (an attached weapon
    /// or shield rides its creature), else its own gob. Zero for tile geometry.</summary>
    /// <summary>The gob a blended part composites WITH: its fade owner, else the root of its attachment chain
    /// (#490: a creature's body parts and held items are gobs attached to the creature's, and one ghosted
    /// creature is one layer, not one per part). Bounded climb; a cycle is not a shape the engine builds.</summary>
    private static IntPtr RunGob(int i)
    {
        IntPtr g = _oGob[i];
        if (g == IntPtr.Zero) return g;
        // Climb FIRST: an attached gob's node pointer is the part it hangs on (a head keys on the neck, each
        // weapon piece on a hand), so the owner rule below applies to the root, not to the attachment.
        for (int hop = 0; hop < 8; hop++)
        {
            CAurBehavior behavior = new Gob(g).Attach;
            if (behavior.IsNull) break;
            IntPtr parent = behavior.Target.Ptr;
            if (parent == IntPtr.Zero) break;
            g = parent;
        }
        GobRef owner = new GobRef(g).FadeOwner;
        return owner.IsNull ? g : owner.Ptr;
    }

    /// <summary>#618: is this part a creature's? Its own gob, or any gob up its attachment chain, carries
    /// <see cref="EngineLayout.GobKindCreature"/> (a hat or a held weapon is its own gob hanging off the body's).
    /// NOT <see cref="RunGob"/>: that returns the fade-owner NODE (gob+0x68) as a composite key, and a node
    /// read as a gob has no type -- which is how every character first painted as an object.</summary>
    /// <summary>The caster pass's read of <see cref="CreaturePart"/>: a player character is RIGID parts under
    /// bone-driven nodes (pfh0_legl004...), not a skinmesh, so nothing else names it a creature.</summary>
    internal static bool SlotCreature(int i) => (uint)i < (uint)_oGob.Length && CreaturePart(i);
    private static bool CreaturePart(int i)
    {
        IntPtr g = _oGob[i];
        for (int hop = 0; hop < 8 && g != IntPtr.Zero; hop++)
        {
            if (SceneRay.IsCreature(g)) return true;
            CAurBehavior behavior = new Gob(g).Attach;
            if (behavior.IsNull) return false;
            g = behavior.Target.Ptr;
        }
        return false;
    }

    private static float EyeDist2(int i)
    {
        float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i];
        if (_oMx[i])
        {
            float* m = new NodeRef(_oNode[i]).Matrix;
            cx = m[12]; cy = m[13]; cz = m[14];
        }
        float dx = cx - _eyePos.X, dy = cy - _eyePos.Y, dz = cz - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>#242: store <paramref name="v"/> and say whether that changed anything. The whole
    /// persistent-record scheme is this compare: a field written every frame with the value it already held
    /// is not a mover, and the record it sits in does not need re-sending.</summary>
    private static bool St(float* p, float v)
    {
        if (*p == v) return false;
        *p = v;
        return true;
    }

    /// <summary>#242: the cached slot tids of a record that did not move. Still needed every frame -- a
    /// bind-path batch cuts on its textures, so the batch key reads them even when the record does not.
    /// </summary>
    private static void LoadSlotTids(int i, int* slotTid)
    {
        for (int s = 0; s < MapSlots; s++) slotTid[s] = _recTidS[i * MapSlots + s];
    }

    /// <summary>#242: the record fields ADR 0017's split table calls PER-FRAME, gathered off the live scene
    /// by the staging walk and handed to <see cref="WritePerFrame"/>. A plain value struct on the stack --
    /// no engine pointer reaches this far, which is what makes the split checkable off-device.
    /// <para>Everything NOT here is stable at enrolment and written once, by <see cref="WriteStable"/>.
    /// Moving a field from this struct into that one is the exact mistake the ticket describes: it does not
    /// read as a perf bug, it reads as a frozen animated texture or a tile stuck at full opacity months
    /// later. <see cref="SplitSelfCheck"/> is what makes that move go red.</para></summary>
    internal struct RecordFrameState
    {
        /// <summary>Matrix-class parts only -- creatures, doors, dangly, anything on a node. A part whose
        /// class stages identity has no live matrix to re-read, so it is stable by construction.</summary>
        internal bool HasModel;
        internal Matrix4x4 Model;
        internal float Dissolve, Alpha;
        internal bool Lit;
        internal float AmbR, AmbG, AmbB, DifR, DifG, DifB, EmiR, EmiG, EmiB, Slots;
        internal float FeatX, FeatY;
    }

    /// <summary>#243: this record's world bounding sphere, for the cull. The local bbox centre and radius
    /// are enrolment-stable (<c>_oCx..</c>, <c>_oR</c>); what moves is the model matrix, so the sphere is
    /// that centre through it and that radius scaled by the matrix's largest row.
    /// <para>Three kinds of part are EXEMPTED with <see cref="NeverCull"/> rather than bounded: a posed
    /// skinmesh (its vertices are placed by 64 bone matrices, not by this one), a dangly mesh (its vertices
    /// move under a matrix that does not -- #241's standing observation), and a displacing feature (wind
    /// sway leaves the rest bbox by construction). Each would otherwise be culled at a silhouette it is no
    /// longer standing in, and a cull's bug is invisible geometry.</para></summary>
    private static void WriteSphere(int i, in RecordFrameState f, IntPtr tri, int feat)
    {
        fixed (float* sp = _dSph)
        {
            float* s = sp + (long)i * 4;
            if (_oBone[i] >= 0 || ShaderFeatures.OutOfPrepass(feat) || PartTable.IsDangly(tri)
                || !(_oR[i] > 0f))
            { s[0] = 0f; s[1] = 0f; s[2] = 0f; s[3] = NeverCull; return; }
            // A matrix-class part's world centre is its model matrix TRANSLATION, and its radius is the
            // bbox extent unscaled. Not derived here -- copied from the three live-verified sites that
            // already answer this question the same way (EyeDist2 above, CasterPass's rigid matrix path and
            // its live-caster copy), whose note is explicit that the bbox centre is NOT object-space. The
            // repo rule is NEVER GUESS, and transforming the centre by the matrix would be a guess that adds
            // the node translation a second time.
            float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i];
            if (f.HasModel) { cx = f.Model.M41; cy = f.Model.M42; cz = f.Model.M43; }
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = _oR[i];
        }
    }

    /// <summary>Stand-in radius for a caster whose engine bbox is degenerate: one NWN tile's horizontal
    /// diagonal (tiles are 10u square; WaterBodies.TileHalf is the same convention's other half).</summary>
    private const float DegenerateCasterRadius = 7.1f;

    /// <summary>#302: the same sphere, GEOMETRIC even for the three exempted kinds. See
    /// <c>_dCastSph</c> for why the shadow pass cannot use a <see cref="NeverCull"/> one.</summary>
    private static void WriteCasterSphere(int i, in RecordFrameState f)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)i * 4;
            float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i];
            if (f.HasModel) { cx = f.Model.M41; cy = f.Model.M42; cz = f.Model.M43; }
            float r = _oR[i];
            // Review, restoring the deleted MinSkinRadius insurance: a skinmesh's bbox is its BIND pose,
            // and an animated pose (an extended arm, a swinging cloak) reaches past it. The slack keeps the
            // reach culls and the staleness volumes honest about where the pose can actually be; keyed on
            // the VDO's bone streams because that is enrollment-stable where the per-frame bone slot is not.
            if (_vSkin[_oVao[i]]) r = MathF.Max(r + 2f, 3f);
            // A part with an unpopulated engine bbox (min == max -- measured live on tile parts, the same
            // defect WaterBodies stands in for with TileHalf) must NOT store radius 0: a zero sphere makes
            // TryCaster admit it at infinite reach (defeating every view's re-bake predicate) and makes the
            // vanish sweep take the whole-frame blanket when it blinks out. One tile's diagonal is the
            // honest bound for the population this actually is.
            if (!(r > 0f)) r = DegenerateCasterRadius;
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = r;
        }
    }

    /// <summary>#242: the per-frame half of the record, compared into place. Returns whether anything
    /// actually moved -- which is the whole scheme: a field rewritten with the value it already held does
    /// not make its record a mover, and only movers are re-sent.</summary>
    /// <para>#392: <c>prev</c> (floats 44..59) is LAST FRAME's model. <paramref name="roll"/> is "this is the
    /// record's first build this frame": the old model rolls into prev when the model moved, and prev catches
    /// up to the model the frame after a mover stops (one extra re-send, so a closed door does not report
    /// motion forever). <paramref name="fresh"/> seeds prev = model, so a new tenant never carries the last
    /// tenant's matrix as its history.</para></summary>
    /// <summary>Did a model matrix move, to a tenth of a millimetre? The engine rebuilds a placed gob's matrix
    /// from its quaternion, and bit-exact inequality read 60-156 still barrels a frame as movers in a static
    /// area (run 13): each a record re-send, a moving-caster page under its light, and a hot light on every
    /// baked fragment in reach. ponytail: a drift slower than the step accumulates against the record and
    /// is written once it crosses it, so nothing is lost, only sent later.</summary>
    private const float MoveStep = 1e-4f;
    /// <summary>The caster's step: 1 cm of translation, or 0.01 of a rotation element (0.6 degrees). ponytail: the
    /// compare is against the record, which follows at the finer step, so a creep under 1 cm a frame never
    /// re-bakes its shadow; a last-baked matrix per caster is the upgrade if a pushed crate ever needs it.</summary>
    private const float CasterStep = 1e-2f;
    private static bool Moved(in Matrix4x4 a, in Matrix4x4 b, float step)
    {
        fixed (Matrix4x4* pa = &a, pb = &b)
        {
            float* x = (float*)pa, y = (float*)pb;
            for (int k = 0; k < 16; k++) if (MathF.Abs(x[k] - y[k]) > step) return true;
        }
        return false;
    }

    private static bool WritePerFrame(float* r, in RecordFrameState f, bool roll = true, bool fresh = false)
    {
        bool m = false;
        if (f.HasModel)
        {
            Matrix4x4* model = (Matrix4x4*)r, prev = (Matrix4x4*)(r + PrevFloat);
            if (Moved(in *model, in f.Model, MoveStep))
            {
                if (roll) *prev = *model;
                *model = f.Model; m = true;
            }
            else if (roll && *prev != *model) { *prev = *model; m = true; }
            if (fresh) *prev = f.Model;
        }
        m |= St(r + 18, f.Dissolve) | St(r + 19, f.Alpha);
        if (f.Lit)
        {
            m |= St(r + 20, f.AmbR) | St(r + 21, f.AmbG) | St(r + 22, f.AmbB)
                 | St(r + 28, f.EmiR) | St(r + 29, f.EmiG) | St(r + 30, f.EmiB)
                 | St(r + 35, f.Slots);
            m |= St(r + 24, f.DifR) | St(r + 25, f.DifG) | St(r + 26, f.DifB);
        }
        m |= St(r + 40, f.FeatX) | St(r + 41, f.FeatY);
        return m;
    }

    /// <summary>#242: the stable half -- written when the slot is built and never again while it holds the
    /// same part. The texture rotation cannot change without failing <c>IdentityOk</c> (which revokes), a
    /// .mtr does not animate, and the three alpha ones are literal constants. An identity model matrix is
    /// here too: a part whose class does not stage a node has no live matrix to re-read.</summary>
    /// <summary>Stable-record writes by emi.w class since arming (creature / object / tileset), for the weather line.</summary>
    internal static int RecCreature, RecObject, RecTile;

    private static void WriteStable(float* r, int i, bool lit)
    {
        if (!_oMx[i]) { *(Matrix4x4*)r = Matrix4x4.Identity; *(Matrix4x4*)(r + PrevFloat) = Matrix4x4.Identity; }
        r[16] = _oCos[i]; r[17] = _oSin[i];
        int fo = i * 4;
        r[42] = _oFeat[fo + 2];
        // #308 follow-up: -1 marks a DANGLY part for the receiver's creature gate (skinmesh is already
        // feat.x). Safe reuse: feat.w is only otherwise read inside the wind/roof feature branches, and no
        // dangly mesh names one of those forks.
        r[43] = PartTable.IsDangly(_oTri[i]) ? -1f : _oFeat[fo + 3];
        if (!lit) return;
        r[23] = 1f; r[27] = 1f;
        // emi.w: the contact-shadow RECEIVER flag -- 1 on tileset geometry, 0 on an object. A depth march
        // on a low-poly creature or cloak reads its own facets as blockers; the ground under it does not.
        // Static geometry (no gob at all) IS the tileset: only an object instances off a gob, and the gob'd
        // half a SpawnTile split leaves is caught by the tile-gob test.
        // #618: -1 marks a CREATURE's part (the root of its attachment chain is a creature gob), for the snow
        // gate -- snow does not settle on a moving body. Safe reuse: every other reader tests > 0.5.
        r[31] = _oGob[i] == IntPtr.Zero || _oTileGob[i] ? 1f
              : CreaturePart(i) ? -1f : 0f;
        if (r[31] < 0f) RecCreature++; else if (r[31] > 0f) RecTile++; else RecObject++;
        int o = i * ParamFloats;
        r[32] = _oParam[o]; r[33] = _oParam[o + 1]; r[34] = _oParam[o + 2];
        r[36] = _oParam[o + 4]; r[37] = _oParam[o + 5]; r[38] = _oParam[o + 6]; r[39] = _oParam[o + 7];
        for (int k = 0; k < ParamFloats - ParamExt; k++) r[ExtFloat + k] = _oParam[o + ParamExt + k];
    }

    /// <summary>#242 (AC5): off-device, the split itself. Every field of
    /// <see cref="RecordFrameState"/> is perturbed on its own and must make the record a mover; a record
    /// re-written with the values it already holds must not. Move any one of them out of
    /// <see cref="WritePerFrame"/> and into <see cref="WriteStable"/> -- the mistake the ticket is about --
    /// and its perturbation stops being seen, which is this check going red.
    /// <para>Verified red during development by moving <c>Alpha</c> into the stable half: a fading tile then
    /// stands at full opacity for as long as it is owned, and nothing else says so.</para></summary>
    private static bool SplitSelfCheck(ExtenderLog log)
    {
        float[] rec = new float[DrawFloats];
        var f = default(RecordFrameState);
        f.HasModel = true; f.Model = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        f.Dissolve = 1f; f.Alpha = 0.5f; f.Lit = true;
        f.AmbR = 0.1f; f.AmbG = 0.2f; f.AmbB = 0.3f;
        f.DifR = 0.4f; f.DifG = 0.5f; f.DifB = 0.6f;
        f.EmiR = 0.7f; f.EmiG = 0.8f; f.EmiB = 0.9f;
        f.Slots = 3f; f.FeatX = 5f; f.FeatY = 6f;
        bool ok;
        fixed (float* r = rec)
        {
            ok = WritePerFrame(r, in f, fresh: true) && !WritePerFrame(r, in f);
            if (!ok) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a record is a mover every frame, or "
                              + "never one; either way the persistent-record scheme is not running");
            // one field at a time, restored after each, so a miss names itself rather than being masked by
            // the next perturbation
            ok &= Perturb(r, ref f, ref f.Dissolve, log, "rot.z dissolve")
                  & Perturb(r, ref f, ref f.Alpha, log, "rot.w alpha")
                  & Perturb(r, ref f, ref f.AmbR, log, "amb.r")
                  & Perturb(r, ref f, ref f.AmbG, log, "amb.g")
                  & Perturb(r, ref f, ref f.AmbB, log, "amb.b")
                  & Perturb(r, ref f, ref f.DifR, log, "dif.r")
                  & Perturb(r, ref f, ref f.DifG, log, "dif.g")
                  & Perturb(r, ref f, ref f.DifB, log, "dif.b")
                  & Perturb(r, ref f, ref f.EmiR, log, "emi.r")
                  & Perturb(r, ref f, ref f.EmiG, log, "emi.g")
                  & Perturb(r, ref f, ref f.EmiB, log, "emi.b")
                  & Perturb(r, ref f, ref f.Slots, log, "pbr.w live slot mask")
                  & Perturb(r, ref f, ref f.FeatX, log, "feat.x")
                  & Perturb(r, ref f, ref f.FeatY, log, "feat.y bone base");
            // ...and the matrix, which is the one the ticket names first: a mover the split does not know
            // about is a frozen record, and it reads as correct until the thing moves.
            Matrix4x4 was = f.Model;
            f.Model = Matrix4x4.CreateTranslation(9f, 9f, 9f);
            bool mm = WritePerFrame(r, in f);
            // #392: the frame the matrix moved, prev holds where it WAS; the next build (nothing moved) rolls
            // prev up to the model and re-sends once; the one after that is quiet.
            bool prevLag = *(Matrix4x4*)(r + PrevFloat) == was && *(Matrix4x4*)r == f.Model;
            bool catchUp = WritePerFrame(r, in f) && *(Matrix4x4*)(r + PrevFloat) == f.Model && !WritePerFrame(r, in f);
            // ...and a second build in the SAME frame (roll: false) leaves prev where the first put it.
            f.Model = Matrix4x4.CreateTranslation(3f, 3f, 3f);
            WritePerFrame(r, in f);
            Matrix4x4 held = *(Matrix4x4*)(r + PrevFloat);
            bool sameFrame = !WritePerFrame(r, in f, roll: false) && *(Matrix4x4*)(r + PrevFloat) == held;
            f.Model = was;
            WritePerFrame(r, in f, fresh: true);
            if (!mm) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a moved model matrix does not "
                              + "re-send its record; every creature and door would freeze where it stood");
            if (!prevLag || !catchUp || !sameFrame)
                log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- prev is not last frame's model (#392): a stopped "
                         + "door would report motion forever, or a mover would lose its vectors");
            ok &= mm && prevLag && catchUp && sameFrame;
        }
        return ok;
    }

    private static bool Perturb(float* r, ref RecordFrameState f, ref float field, ExtenderLog log, string name)
    {
        float was = field;
        field = was + 1f;
        bool seen = WritePerFrame(r, in f);
        field = was;
        WritePerFrame(r, in f);
        if (!seen)
            log.Info($"AlloyRenderer.SplitSelfCheck: FAILED -- {name} is not a per-frame field any more; "
                     + "a record that stops carrying it renders stale state and says nothing");
        return seen;
    }

    /// <summary>#242: this slot's texture caches advanced but its record never did -- the walk skipped out
    /// between the two. Un-stamp it, so the part rebuilds from scratch when it comes back rather than being
    /// served as clean against caches its record does not match.
    /// <para>Without this the failure is the nastiest shape there is: the diffuse pointer moves, the resolve
    /// updates the cache, a transient range or VAO skip drops the part, and on its return the pointer
    /// compares EQUAL to the cache -- so the record keeps the previous texture's handles for as long as the
    /// part is owned. That is #220 again, with a longer fuse and nothing in the log.</para></summary>
    private static void UnstampRecord(int i)
    {
        _recGen[i] = 0;   // 0 = never built; the epoch starts at 1
        // #322 (review): the draw-arg cache dies on the same edge. Enrolment does NOT bump _recEpoch --
        // this per-slot unstamp, fired on every re-tenant and every drifted keep, is the only signal that
        // _oVdo/_oItem may have been re-pointed under the cache.
        _cGen[i] = 0;
        UnstampChase(i);
    }

    /// <summary>#242: this record moved and has to reach the GPU again. Overflow is not a silent drop -- it
    /// promotes the frame to a wholesale upload, which is correct and merely more expensive.</summary>
    private static void MarkRecordDirty(int slot) => MarkRecordDirty(slot, ref _stageCounters[0]);

    /// <summary>#331: advance every cycle texture behind an owned record. A step is picked up inline by the
    /// stage's texDirty test; a DROPPED controller (its ref died) un-registers its records here (cold) so
    /// they re-resolve and re-register with a live ref. Main thread, before the batch build.</summary>
    private static void TickCycleTextures()
    {
        CycleTextures.Tick();
        var dropped = CycleTextures.Dropped;
        if (dropped.Length == 0) return;
        for (int i = 0; i < _ownCount; i++)
            if (_recCtl[i] != IntPtr.Zero && dropped.IndexOf(_recCtl[i]) >= 0) { _recCtl[i] = IntPtr.Zero; _recTexStale[i] = true; }
    }
    private static void MarkRecordDirty(int slot, ref StageCounters c)
    {
        c.RecWritten++;
        // reserve-then-write: workers append concurrently
        int n = System.Threading.Interlocked.Increment(ref _recDirtyN) - 1;
        if (n < _recDirty.Length) _recDirty[n] = slot;
        else _recDirtyAll = true;
    }

    /// <summary>#242: every persistent record is suspect -- rebuild them all on the next frame that draws.
    /// The edges that need it are the ones no per-part compare can see: any bindless handle wipe or context
    /// loss (a record holding a dead handle is #220's wall-sampling-a-portrait), and the debug toggles that
    /// change what a record means. Enrolment is NOT one of them (#322 review corrected this doc): #308's
    /// stable slots mean a re-tenant is a per-slot event, and <see cref="UnstampRecord"/> at the enroll
    /// walk is what invalidates it. See ADR 0017's "Step 2, as built".</summary>
    internal static void InvalidateRecords() => System.Threading.Interlocked.Increment(ref _recEpoch);
                                              // Interlocked: /alloy runs on the input thread

    /// <summary>Phase A of the Alloy block: walk this pass's order (<see cref="RouteParts"/>), revalidate
    /// every part, and stage the survivors into the multidraw argument arrays plus one per-draw SSBO record
    /// each -- cutting a batch whenever the (class, program, VDO, texture, primitive, blend) key changes.
    /// Primitive mode is in the key because one multidraw carries a single mode; the sort does not group by
    /// it, so a mixed-mode VDO just yields more batches. Returns the batch count, leaving the draw count in
    /// <see cref="_drawN"/> and the per-class batch ranges in <see cref="_gOpaqueEnd"/>/<see cref="_gBlendEnd"/>.
    /// <para>OwnGen is stamped here, which is only honest because the issue phase cannot fail per part: the
    /// entry-point guard ran in <see cref="DrawOwned"/> and EnsureVao ran here.</para></summary>
    /// <summary>#322: how many frames apart each slot re-reads its slow-drift colour state (mdl colour
    /// triples, self-illum, gob colour folds, non-animated slot-map pointers). Power of two; the stagger by
    /// slot index spreads the reads evenly, so the per-frame cost is 1/N of the population and the worst
    /// staleness is N frames of a drift that moves over seconds. Fast-visible state (alpha, fade, matrix,
    /// the diffuse pointer, dissolve) never rides this.</summary>
    private const int ColourCadence = 8;

    /// <summary>#324: one routed part in this many carries the <c>Zone.Chase</c> timestamps. Power of two.</summary>
    internal const int ChaseEvery = 16;

    private static int BuildBatches(bool lightingOn)
    {
        int groups = 0, n = 0;
        int keyVao = -1, keyTex = -1, keyFeat = -1, keyCls = -1, keyBlend = -1;
        uint keyMode = 0;
        bool keyLit = false, keyDis = false, keyNoCull = false;
        IntPtr keyGob = IntPtr.Zero;
        // #322 step 1: attribute the Build monolith before drawing any phase boundary -- Scan is the
        // routing walk, Restage everything after it. No behaviour change; the [zones] line gains the split.
        long tScan = FrameZones.Now;
        if (_stageThreads > 0) StagePool.Prewake(Math.Min(_stageThreads, StagePool.MaxChunks - 1) + 1);
        int routed = RouteParts();
        FrameZones.Add(FrameZones.Zone.Scan, tScan);
        long tRestage = FrameZones.Now;
        bool dumping = FrameDump.Begin();
        // #134: routing walks the CLASS RANGES, not the ownership table -- a part whose class range was
        // never built simply never appears, and no skip site can report on it. Say so once.
        if (Diag._watchDrawLeft > 0 && AlloyCensus._watchEntry >= 0)
        {
            bool routedWatch = false;
            for (int k = 0; k < routed && !routedWatch; k++) routedWatch = AlloyCensus.WatchedEntry(_oEntry[_pass[k]]);
            if (!routedWatch)
            {
                bool owned = false;
                for (int i = 0; i < _ownCount && !owned; i++)
                    owned = _oTri[i] != IntPtr.Zero && AlloyCensus.WatchedEntry(_oEntry[i]);
                if (owned) { Diag._watchDrawLeft--; _log?.Info("[dumpframe] DRAW never routed -- owned, but RouteParts did not include it"); }
            }
        }
        _gCutoutStart = _gOpaqueEnd = _gBlendEnd = _gWaterEnd = 0;
        _mvN = 0; _mvSkinned = false; _mvDangly = false;   // #392: this pass's mover list, whether or not a velocity pass consumes it
        // #242: the persistent buffer grows to the high-water ownership count and never shrinks. A growth
        // reallocates it, so everything in it goes back up on this frame rather than only the movers.
        if (_ownCount > _recOwnCap) { _recOwnCap = _ownCount; _recDirtyAll = true; }
        // #120: this part's live slot-1..5 tids. Declared ONCE, outside the loop -- a stackalloc per
        // iteration grows the frame by the part count.
        int* slotTid = stackalloc int[MapSlots];
        // #287: the cull's bounds diagnostic, measured by the walk that already touches every sphere it
        // covers, rather than by a second walk of the staged list.
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue, maxR = 0f;
        long tStage = FrameZones.Now;
        BeginCasterTouched();
        int chunks = Math.Min(_stageThreads, StagePool.MaxChunks - 1) + 1;
        _stageRouted = routed; _stageLighting = lightingOn;
        fixed (float* data = _dData)
        {
            if (chunks > 1 && StagePool.Run(chunks, _log))
            {
                for (int k = 0; k < routed; k++)
                    if (_pkVerdict[k] == Deferred)
                    { _stageDeferred++; FrameZones.Count(FrameZones.Ev.Deferred); _pkVerdict[k] = StagePart(k, data, slotTid, lightingOn, ref _stageCounters[0], main: true); }
            }
            else
            {
                // A plain field write, not a Set: no Changed, no cfg write -- the stand-down is this session's.
                if (chunks > 1) { Renderer.Threads.V = 0; _log?.Info("AlloyRenderer: /alloy threads stood down by a StagePool fault; the frame re-staged serially (set /alloy threads again to re-arm)"); }
                for (int k = 0; k < routed; k++) _pkVerdict[k] = StagePart(k, data, slotTid, lightingOn, ref _stageCounters[0], main: true);
            }
        }
        _stageFrames++;
        for (int w = 0; w < _stageCounters.Length; w++) FoldCounters(ref _stageCounters[w]);
        if (chunks > 1) FrameZones.Add(FrameZones.Zone.Chase, tStage);   // threaded: the whole stage phase stands in for the sampled chase
        fixed (float* data = _dData)
        fixed (float* sphAll = _dSph)
        fixed (uint* srcAll = _dSrc)
        for (int k = 0; k < routed; k++)
        {
            if (_pkVerdict[k] != Staged) continue;
            int i = _pass[k];
            int cls = _passCls[k];
            IntPtr tri = _oTri[i];
            bool lit = _oLit[i];
            bool dis = _pkDis[k] != 0;
            int tid = _recTid[i], slots = _recSlotMask[i];
            LoadSlotTids(i, slotTid);
            int idxCount = _cIdx[i];
            int src = i;
            int v = _oVao[src];
            float* r = data + (long)i * DrawFloats;
            uint mode = _primTable[_oPrim[i]];
            // Automatic mesh LOD: the part's bounding sphere, which the cull already needs, projected to a
            // screen radius. A generated run lives in MeshLod's own element buffer, so the level rides the
            // slot index (LodVaoBit) -- that is what cuts the batch, because one multidraw cannot straddle
            // two element buffers.
            nint idxOff = _cOff[i];
            int baseVertex = _cBase[i];
            int vKey = v;
            int lvl = 0;
            if (MeshLod.On && _lodScale > 0f)
            {
                float* lsp = sphAll + (long)i * 4;
                if (lsp[3] < NeverCull)
                {
                    float dxe = lsp[0] - _eyePos.X, dye = lsp[1] - _eyePos.Y, dze = lsp[2] - _eyePos.Z;
                    lvl = MeshLod.Level(lsp[3], MathF.Sqrt(dxe * dxe + dye * dye + dze * dze), _lodScale);
                }
            }
            // #673: a charted mesh is the whole geometry (split vertices), picked at every level -- see MeshChart.Range.
            bool chartArmed = MeshChart.Armed;
            int drawn = 0;
            if (chartArmed && MeshChart.Range(ref _oChart[src], v, _oItem[src], lvl, out int cb, out nint co, out int cc, out drawn))
            {
                idxOff = co; idxCount = cc; baseVertex = cb; vKey = v | ChartVaoBit; MeshChart.Drew(cc);
                LightmapBake.Enrol(i, v, _oItem[src]);   // #674: once per slot (a -1 test after the first)
            }
            else if (lvl > 0 && MeshLod.Range(v, _oItem[src], lvl, out nint lo, out int lc, out int ll) && LodVao(v) != 0)
            { idxOff = lo; idxCount = lc; vKey = v | LodVaoBit; drawn = ll; }
            if (MeshLod.On) MeshLod.Drew(drawn, idxCount);
            _oLodBit[i] = vKey & (LodVaoBit | ChartVaoBit);
            // #123: the feature joins the batch key. Not for binding -- it rides in the per-draw record --
            // but so the depth prepass can drop a whole batch: a displacing feature must not write its
            // undisplaced depth, and the prepass has no per-draw granularity.
            int feat = (int)_oFeat[i * 4];
            // #137: a posed skinmesh overrides whatever content feature the material named -- see
            // ShaderFeature.Skin for why the two share the field, and why that trade is safe on shipped
            // content. Joining the cut key here is what lets a whole batch be skinned or not -- #262 turned
            // that from "the prepass drops it" into "the prepass runs it under its own bone arm".
            if (_oBone[i] >= 0) feat = (int)ShaderFeature.Skin;
            // #124: ...and the dissolve bit (dis, above) joins it for the same reason -- the keyhole stage discards.
            // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
            int blend = AlloyClasses.Blends(cls) ? (_oWash[i] << 16) | (_oSrc[i] << 8) | _oDst[i] : 0;
            // #292: ...and so is face culling, for exactly the same reason. Sorted under the lit bit
            // (SortKeyOf), so this cuts at most twice per class rather than once per interleaved part.
            bool noCull = _oNoCull[i];
            // A blended batch never straddles two creatures: the frame prepasses and colours each run of
            // one gob's batches as a unit, so the gob is a key term here and a tag on the batch.
            IntPtr gobKey = cls == (int)AlloyClass.Blended ? RunGob(i) : IntPtr.Zero;
            // #153: under bindless a LIT batch no longer splits on its textures -- the handles ride the
            // record, so one multidraw spans every texture in the class. Feat/dis stay in the key only for
            // the depth prepass's whole-batch drop; unlit parts keep the bind path and the full key.
            bool merge = _bindless && lit && cls != (int)AlloyClass.Water;
            int tidKey = merge ? -2 : tid;
            bool sameSlots = groups != 0 && merge;
            if (!merge)
            {
                sameSlots = groups != 0;
                for (int s = 0; sameSlots && s < MapSlots; s++)
                    sameSlots = _gTexN[(groups - 1) * MapSlots + s] == slotTid[s];
            }
            if (!sameSlots || lit != keyLit || vKey != keyVao || tidKey != keyTex || mode != keyMode
                || feat != keyFeat || dis != keyDis || cls != keyCls || blend != keyBlend
                || noCull != keyNoCull || gobKey != keyGob)
            {
                // #292 (AC6): what the cull term cost. Counted as the batches it OPENED on its own -- every
                // other key term agreed and only the culling state differed -- so the line reports the split
                // this ticket added rather than the whole batch count, which no baseline would separate.
                if (groups != 0 && noCull != keyNoCull && sameSlots && lit == keyLit && vKey == keyVao
                    && tidKey == keyTex && mode == keyMode && feat == keyFeat && dis == keyDis
                    && cls == keyCls && blend == keyBlend) Counters._cullCuts++;
                // #327: which term opened an opaque-range cut -- one counter per term, a cut that flipped
                // several charged to each. The instrument the sort above is judged by.
                if (groups != 0 && cls == keyCls && cls <= (int)AlloyClass.Cutout)
                {
                    if (vKey != keyVao) Counters._cutBy[0]++;
                    if (feat != keyFeat) Counters._cutBy[1]++;
                    if (dis != keyDis) Counters._cutBy[2]++;
                    if (blend != keyBlend) Counters._cutBy[3]++;
                    if (noCull != keyNoCull) Counters._cutBy[4]++;
                    if (mode != keyMode) Counters._cutBy[5]++;
                    if (lit != keyLit) Counters._cutBy[6]++;
                    if (tidKey != keyTex || !sameSlots) Counters._cutBy[7]++;
                }
                _gStart[groups] = n; _gLen[groups] = 0;
                _gLit[groups] = lit; _gVao[groups] = vKey; _gTex[groups] = tid; _gMode[groups] = mode;
                _gFeat[groups] = feat; _gDis[groups] = dis;
                _gCls[groups] = (byte)cls; _gSrc[groups] = _oSrc[i]; _gDst[groups] = _oDst[i]; _gWash[groups] = _oWash[i];
                _gNoCull[groups] = noCull; _gGob[groups] = gobKey;
                for (int s = 0; s < MapSlots; s++) _gTexN[groups * MapSlots + s] = slotTid[s];
                groups++;
                keyLit = lit; keyVao = vKey; keyTex = tidKey; keyMode = mode; keyFeat = feat; keyDis = dis;
                keyCls = cls; keyBlend = blend; keyNoCull = noCull; keyGob = gobKey;
            }
            W(i, "SUBMITTED");
            if (AlloyCensus.WatchTex[_oEntry[i]]) Counters._watchSubmitted++;
            _dCount[n] = idxCount;
            _dOff[n] = idxOff;
            _dBase[n] = baseVertex;
            // #242: the record this draw reads lives at the OWNERSHIP slot and outlives the frame; the draw
            // order does not, so the draw carries the slot rather than the record.
            _dSlot[n] = i;
            // #392: a matrix mover this frame -- its record's prev lags its model by one frame, and the velocity
            // stage redraws it with both. CX-26: a posed skinmesh is one too when its palette changed, and the
            // stage skins it with both palettes. Opaque only: a displaced mesh moves under a matrix that does
            // not, and a cutout mover would paint its own velocity over its holes.
            bool posed = _oBone[i] >= 0, dangly = !posed && DanglyPrevNow(i), recMoved = _recMovedAt[i] == _frameSeq;
            bool moved = posed ? recMoved || _poseMovedAt[i] == _frameSeq : dangly || recMoved && _oMx[i];
            if (moved && cls == (int)AlloyClass.Opaque && !ShaderFeatures.OutOfPrepass(feat))
                NoteMoverDraw(n, posed, dangly);
            // #287: and the same draw as the cull's source record -- the batch is the one the cut just
            // opened or extended, and its output base is where its compacted run starts.
            CullPass.Pack(srcAll + (long)n * CullPass.SrcUints, idxCount, _dOff[n], _dBase[n], i,
                          groups - 1, _gStart[groups - 1]);
            // #287: ...and after the record block, so the extent is measured on the sphere this frame wrote.
            if (cls <= (int)AlloyClass.Cutout)
            {
                float* sp = sphAll + (long)i * 4;
                if (sp[3] < NeverCull)
                {
                    lox = MathF.Min(lox, sp[0]); hix = MathF.Max(hix, sp[0]);
                    loy = MathF.Min(loy, sp[1]); hiy = MathF.Max(hiy, sp[1]);
                    loz = MathF.Min(loz, sp[2]); hiz = MathF.Max(hiz, sp[2]);
                    maxR = MathF.Max(maxR, sp[3]);
                }
            }
            if (FrameDump.Capturing)
                FrameDump.Row_(cls, lit, v, _oMat[i], mode, feat, dis, blend, noCull, slots, merge,
                               idxCount, _dOff[n], _dBase[n], r);
            _gLen[groups - 1]++;
            n++;
            if (noCull) Counters._twoSidedDraws++;
            if (_oGob[i] != IntPtr.Zero) Counters._drawsGob++;
            // #302: no shadow feed here any more. An instance part IS an ownership slot, and the bake walks
            // the ownership table -- so there is nothing to hand over and nothing that can be double-recorded.
            AlloyCensus.OwnGen[_oEntry[i]] = PassGen;
        }
        // #126: where each pass's batch range ends. Read off the batches themselves rather than tracked
        // through the cut above -- the routed order is class-ordered by construction, so the first batch of a
        // later class IS the previous range's end, and a class that staged nothing leaves an empty range
        // rather than someone else's batches.
        if (hix >= lox) CullPass.SetSpread(hix - lox, hiy - loy, hiz - loz, maxR);
        _gCutoutStart = _gOpaqueEnd = _gBlendEnd = _gWaterEnd = groups;
        for (int g = 0; g < groups; g++)
        {
            if (_gCls[g] > (byte)AlloyClass.Opaque && g < _gCutoutStart) _gCutoutStart = g;
            if (_gCls[g] > (byte)AlloyClass.Cutout && g < _gOpaqueEnd) _gOpaqueEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Blended && g < _gBlendEnd) _gBlendEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Water && g < _gWaterEnd) _gWaterEnd = g;
        }
        _drawN = n;
        if (dumping) FrameDump.Finish(groups, _log);
        FrameZones.Add(FrameZones.Zone.Restage, tRestage);
        return groups;
    }

    private const byte Skipped = 0, Staged = 1, Deferred = 2;
    /// <summary>Per-stage counters, one set per chunk, folded into the period statics after the join -- the
    /// hot ones every part touches. Rare skip counters go through <see cref="Inc"/> instead.</summary>
    internal struct StageCounters
    {
        public long OwnDissolve, NrmDeclared, NrmBound, RecStable, RecWritten;
        public long StFrozen, StSettled, StThawed;
        public long MxChased, MxCached, DisAsked, DisCached, DisBoxed, DisNoFeat, DisGobUnpierced, DisNoLit;
        public long PollMissMx, PollMissDis, PollMissBox;
        public bool CastChanged;
    }
    private static readonly StageCounters[] _stageCounters = new StageCounters[StagePool.MaxChunks];
    private static void FoldCounters(ref StageCounters c)
    {
        Counters._ownDissolve += c.OwnDissolve; Counters._nrmDeclared += c.NrmDeclared; Counters._nrmBound += c.NrmBound;
        Counters._recStable += c.RecStable; Counters._recWritten += c.RecWritten;
        _stFrozen += c.StFrozen; _stSettled += c.StSettled; _stThawed += c.StThawed;
        _mxChased += c.MxChased; _mxCached += c.MxCached; _disAsked += c.DisAsked; _disCached += c.DisCached;
        _disBoxed += c.DisBoxed; _disNoFeat += c.DisNoFeat; _disGobUnpierced += c.DisGobUnpierced; _disNoLit += c.DisNoLit;
        _pollMissMx += c.PollMissMx; _pollMissDis += c.PollMissDis; _pollMissBox += c.PollMissBox;
        if (c.CastChanged) CasterGen++;
        c = default;
    }
    private static void Inc(ref long x) => System.Threading.Interlocked.Increment(ref x);
    private static ulong Handle(int tid, bool main) => main ? HandleFor(tid) : HandleLookup(tid);
    /// <summary>One chunk of the parallel stage: a static partition of the routed range. Chunk 0 is the main
    /// thread's own share; every chunk runs as a worker (<c>main: false</c>) so the path is one path.</summary>
    internal static void StageRange(int w, int chunks)
    {
        int routed = _stageRouted;
        int from = (int)((long)routed * w / chunks), to = (int)((long)routed * (w + 1) / chunks);
        int* slotTid = stackalloc int[MapSlots];
        fixed (float* data = _dData)
            for (int k = from; k < to; k++)
                _pkVerdict[k] = StagePart(k, data, slotTid, _stageLighting, ref _stageCounters[w], main: false);
    }
    private static int _stageRouted;
    private static bool _stageLighting;
    /// <summary><c>/alloy threads N</c>: worker threads beside the main thread for the stage. 0 = serial.</summary>
    private static int _stageThreads => Renderer.Threads.V;
    private static long _stageDeferred, _stageFrames;
    private static readonly byte[] _pkVerdict = new byte[MaxOwned], _pkDis = new byte[MaxOwned];
    /// <summary>The per-part half of <see cref="BuildBatches"/>: chase the engine state a routed part depends on
    /// and write its record, sphere and cast admission -- everything that touches ONLY this slot's arrays. The
    /// batching half reads the result back off those arrays in routed order. Split so the two can run on
    /// different threads: this half is per-slot and order-free, the batch walk is neither.
    /// <para>The worker rule (ADR 0022): off the main thread (<paramref name="main"/> false) a part may take
    /// only paths whose side effects are this slot's arrays and <paramref name="c"/>. Anything that mints a GL
    /// object, logs, allocates or touches a shared collection returns <see cref="Deferred"/> with the counters
    /// rolled back, and the main thread re-runs the part after the join.</para></summary>
    private static byte StagePart(int k, float* data, int* slotTid, bool lightingOn, ref StageCounters c, bool main)
    {
        {
            int i = _pass[k];
            StageCounters snap = c;
            if (!main && AlloyCensus.WatchedEntry(_oEntry[i])) return Deferred;
            // unarmed ownership under bindless: the main path mints on raw engine names (#220), a worker cannot
            if (!main && _bindless && !OwnedTextures.Armed) return Deferred;
            int cls = _passCls[k];
            // #129: never stamp what the frame cannot draw -- see _waterArmed. Ahead of the stale check
            // below because it is a decision about the PASS, not about this part.
            if (cls == (int)AlloyClass.Water && !_waterArmed) { BreakChaseContinuity(); return Skipped; }
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) return Skipped;
            var part = new PartRef(tri);
            // #264: EVERY owned part is staged, and CullPass decides what survives. The gate that used to
            // sit here asked the engine's bucket walk "did you touch this part lately", which was enrolment
            // cadence wearing visibility's clothes (#243) -- and there is no walk to ask. A cull that cannot
            // run is now a BOOT refusal (AlloyGate), not a per-frame downgrade, because the only alternative
            // left is drawing the whole enumerated area every frame.
            // Lit parts sort last. One with the lighting rig unavailable this pass is skipped rather than
            // painted flat. #285: that skip is a HOLE, not a hand-back -- see `unlit-pass` on the [alloy]
            // line, which must read 0 on a pass that has a lighting rig at all.
            bool lit = _oLit[i];
            if (lit && !lightingOn) { Inc(ref Counters._skipNoLight); BreakChaseContinuity(); W(i, "skipped: lighting off this pass"); return Skipped; }
            // #322: identity was verified (and a mismatch revoked) by RouteParts this pass -- every engine
            // read below sits behind that verdict, not behind a second copy of the same four compares.
            bool samp = main && (k & (ChaseEvery - 1)) == 0;
            long tChase = samp ? FrameZones.Now : 0;
            // #126: a fading tile is no longer a reason to hand the part back. RouteParts has already moved
            // it into the blended run with the tile's factor folded into its alpha, which is what the engine
            // does with it in bucket 8 -- and bucket 8 is a bucket IterSight can suppress now.
            // #124: the camera dissolve is a FEATURE of the Alloy program now, not a reason to hand the part
            // back -- dissolving parts sit in bucket 1, which IterSight can suppress. #123's exemption still
            // stands: nokeyhole, roofkn and flowmap compile their FRAGMENT stage with KEYHOLING 0, so vanilla
            // draws them solid through a dissolve and so must we.
            // ...and only on a LIT part: every unlit fragment stage in base_shaders.bif compiles KEYHOLING 0
            // (fst, fst_sm, fsglu, fs, fstc), so vanilla draws an unlit part solid through the aperture.
            // Keeping the flag off them also keeps them out of the prepass exclusion below, which they would
            // otherwise pay for a discard their stage does not have.
            bool dis = cls <= (int)AlloyClass.Cutout ? _passDis[k] : Dissolves(i, tri, ref c);
            float dissolve = dis ? 1f : 0f;
            if (dis) c.OwnDissolve++;
            // #242: this slot's persistent record. Three states, cheapest first -- already verified by an
            // earlier pass THIS frame (nothing to re-read at all), unchanged since the last frame (the
            // texture re-fetch and the handle mints are skipped, which is the expensive half), or dirty.
            // A stale epoch forces the third: see InvalidateRecords for what can make every record wrong.
            bool stale = _recGen[i] != _recEpoch;
            bool verified = !stale && _recFrame[i] == _frameSeq;
            // The static set's fourth record state, below `verified` and cheaper than it: a settled member has
            // nothing left that a frame can move, so its record, sphere, handles and cast admission are last
            // frame's by construction and everything below is skipped. See StaticShape for the membership test
            // and StaticFrozen for the three stamps and the recheck stagger that bound its staleness.
            bool member = !stale && GpuStatic.On && StaticShape(i, cls, dis, dissolve, _passAlpha[k]);
            if (member) c.StSettled++;
            if (!verified && StaticFrozen(i, member))
            { verified = true; _recFrame[i] = _frameSeq; c.StFrozen++; }
            else if (!member && _stSettle[i] != 0) { _stSettle[i] = 0; c.StThawed++; }
            // #322: the colour cadence, staggered by slot. The measured Restage bulk is the per-part
            // engine chases, and the slow-drift half of them -- the mdl colour triples, self-illum, the gob
            // colour folds, the non-animated slot-map pointers -- moves over SECONDS (day-night, a scripted
            // glow), so it is read every ColourCadence frames per slot and reloaded from the record in
            // between. Everything fast-visible stays per frame: alpha, tile fade, the matrix, the DIFFUSE
            // pointer (animated textures), dissolve, instance state.
            // #364: _fullPoll belongs here. `/alloy poll 1` says it re-reads every engine term every frame
            // and its audit counters must read 0 -- but this term sat outside it, so the slow-drift fold
            // stayed on its stagger under a full poll and the knob could not A/B a colour-cadence bug at all.
            //
            // ...and an INSTANCED part is off the cadence entirely. #336 and #362 both tried to name the one
            // event that must bypass it (a highlight) and both missed, because Scene::AddHighlightGob
            // @0x462280 is two list writes and arms nothing -- whatever arms the gob's colour on a highlight
            // is somewhere else, and guessing at it twice is enough. The cadence's own justification is that
            // these terms "move over SECONDS", and the terms that can move at all are the GOB folds:
            // GobDiffuseMul (gob+0xf6/+0xfc, Gob::SetColorShifting) and GobEmissiveAdd (gob+0xf8/+0x110,
            // Gob::SetIllumination). A part with no gob folds neither -- both return their identity without
            // dereferencing anything -- and all it has left is mdl ambient/diffuse and part self-illum,
            // which are material statics that genuinely never move.
            //
            // So the split is by what CAN change, not by what we think just did. It is also cheaper than the
            // stagger it replaces: gob parts are ~9% of a dense area's population (112 instanced of 1245 on
            // the live [alloy] line) against the 12.5% a 1-in-8 cadence re-reads every frame.
            bool colour = stale || _fullPoll || _oGob[i] != IntPtr.Zero
                          || ((i + _frameSeq) & (ColourCadence - 1)) == 0;
            // MatStatic (Ghidra @0x450560): ambient = node+0xf4, diffuse = node+0xe8, emissive = part+0x7c.
            // ponytail: MatStatic also multiplies ambient+diffuse by AurGetTileColor() when the part's tile
            // record (part+0x98, +0x54) is set. Not reproduced -- add it if tiles read the wrong hue.
            IntPtr mdl = lit && colour ? part.Mdl.Ptr : IntPtr.Zero;
            // #302 (review): every content skip from here down also revokes the slot's SHADOW tenancy.
            // The record block never runs for a skipped part, so its mover stamps freeze at their last
            // values -- frozen-equal is a caster re-baking every view it stands in forever, frozen-unequal
            // is a shadow that stops tracking -- and the #302 contract is simpler than either: a part
            // nothing draws casts nothing. The stale rebuild on return stamps it a mover (newTenant), so
            // the views it re-enters re-bake. Cadenced with the colour reads -- the mdl is only chased on a
            // colour frame, so its absence is only observable then.
            if (lit && colour && mdl == IntPtr.Zero) { Inc(ref Counters._skipNoMdl); _recEnroll[i] = 0; SetCastOk(i, false, ref c); W(i, "skipped: no mdl node"); return Skipped; }

            var mat = new MaterialRef(_oMat[i]);
            int tid, slots;
            bool texDirty = false, nullTex = false;
            if (verified) { tid = _recTid[i]; slots = _recSlotMask[i]; LoadSlotTids(i, slotTid); }
            else
            {
                // texture: the engine name is re-fetched -- CAurTexture* is BORROWED, freed on area
                // transitions (never cache the pointer). #211 then maps that name to OUR copy, which cannot
                // be freed under the draw; every cell of a cycle texture is adopted at upload, so a step
                // resolves to a copy already resident.
                // #242: the POINTER is compared per frame; only a change re-resolves it to a tid and a
                // bindless handle. That is the ADR 0017 split's per-frame half -- the fetch stays, the two
                // hash probes behind it do not.
                // #336: ...on the colour cadence. The #330 RE found a cycling texture rewrites the GL name
                // INSIDE the CAurTexture (basic+0x6c), never the pointer -- so this compare never sees a
                // cycle. #331: the step reaches the record through TickCycleTextures (Alloy calls the
                // engine's Control itself; the M4 shim removed the Bind tail that used to) and the IsDirty
                // term below. What the compare does catch is a material re-bind (a re-skin, an equip: cold),
                // which can wait eight frames. A slot with no record yet, or under /alloy poll 1, still reads
                // every frame.
                // CX-25: a re-resolve reads a FRESH ref; the cached one may be a freed block re-used by a new texture
                bool cycled = _recCtl[i] != IntPtr.Zero && CycleTextures.IsDirty(_recCtl[i]);
                bool texPoll = stale || colour || _fullPoll || _recAur[i] == IntPtr.Zero || _recTexStale[i] || cycled;
                var aur = texPoll ? mat.Texture(0) : new TextureRef(_recAur[i]);
                if (aur.IsNull) { Inc(ref Counters._skipTex); Inc(ref Counters._skipTexNone); _recEnroll[i] = 0; SetCastOk(i, false, ref c); W(i, "skipped: no diffuse texture"); return Skipped; }
                texDirty = stale || aur.Ptr != _recAur[i] || _recTexStale[i] || cycled;
                // #322: slot maps ride the colour cadence -- no shipped normal/spec/height map animates, so
                // their pointer compares (8k+ chases a frame in a dense area) wait for the slot's colour
                // frame. The DIFFUSE compare above stays per frame; it is the animated one.
                for (int s = 0; !texDirty && lit && colour && s < MapSlots; s++)
                    if ((_oSlots[i] & SlotBit(s)) != 0 && mat.MapTexture(s).Ptr != _recAurS[i * MapSlots + s])
                        texDirty = true;
                if (!texDirty) { tid = _recTid[i]; slots = _recSlotMask[i]; LoadSlotTids(i, slotTid); }
                else
                {
                    int bb = OwnedTextures.Borrowed;
                    if (main) tid = aur.OwnedTid();
                    else if (!OwnedTextures.TryDraw(AurTex.Tid(aur.Ptr), out tid)) { c = snap; return Deferred; }
                    if (main && OwnedTextures.Borrowed != bb) OwnedTextures.NoteBorrowedName(aur.Ptr, tid);
                    // #317: and NOTE it, because this is the only place that still knows. AlloyCensus does
                    // the same at enrollment on the stated grounds that "enrollment is the Alloy-owned edge
                    // that notices" -- but enrollment is edge-triggered (#256), so a part that enrolls once
                    // and is skipped here every frame after is never re-asked, and one whose tid was FINE at
                    // enrollment and went to 0 later (an area transition retiring the owned copy) is never
                    // asked at all. Both read as "0 queued" beside a nonzero null-tex: an empty queue
                    // because nothing is asking, not because nothing is waiting. NoteUnbuilt is a dictionary
                    // probe that dedups and self-caps (attempts, re-arm, Never), so the per-frame cost of
                    // asking here is that count -- and that count is the bug.
                    if (tid == 0)
                    {
                        if (!main) { c = snap; return Deferred; }
                        OwnedTextures.NoteUnbuilt(aur.Ptr);
                        // ...and DRAW it meanwhile, on the white texel vanilla would draw it on (see
                        // OwnedTextures.WhiteTid). The skip here was the structural analysis's divergence A:
                        // since #264 nothing renders an unowned part, so refusing tid 0 deleted from the
                        // frame a part the engine would merely have rendered blank.
                        tid = OwnedTextures.WhiteTid();
                        if (tid == 0)
                        {
                            Inc(ref Counters._skipTex); Inc(ref Counters._skipTexNoId); _recEnroll[i] = 0; SetCastOk(i, false, ref c);
                            W(i, "skipped: diffuse has no GL id and no white stand-in"); return Skipped;
                        }
                        Inc(ref Counters._nullTexDraws);
                        nullTex = true;
                        W(i, "diffuse has no GL id -- drawn on the white stand-in");
                    }
                    // #220: a bindless handle must only ever name a texture ALLOY owns. A handle on an
                    // engine texture is a handle on an object the engine may delete or re-upload under us,
                    // which is exactly the corruption this fixes -- so a part whose copy was refused (a
                    // format we will not take) is NOT DRAWN rather than drawn through a handle we cannot
                    // vouch for (#285: there is no engine walk behind it; `not-owned` is the count).
                    // Bind-path batches are unaffected: they rebind the engine id every frame.
                    if (lit && _bindless && OwnedTextures.Armed)
                    {
                        // Two DIFFERENT faults behind one guard, and they have different fixes: a texture
                        // Alloy never adopted (it never reached the upload funnel -- a GPU-composited PLT, a
                        // cube, a movie frame) versus one it owns but could not mint a resident handle for.
                        bool owned = OwnedTextures.IsOwned(tid);
                        if (!owned || Handle(tid, main) == 0)
                        {
                            if (!main && owned) { c = snap; return Deferred; }   // the main thread may still mint it
                            Inc(ref Counters._skipTex); _recEnroll[i] = 0; SetCastOk(i, false, ref c);
                            if (owned) Inc(ref Counters._skipTexNoHandle); else Inc(ref Counters._skipTexNotOwned);
                            TexSkipDiag(owned, tid, aur);
                            W(i, owned ? "skipped: diffuse has no bindless handle"
                                       : "skipped: diffuse is not an Alloy-owned texture");
                            return Skipped;
                        }
                    }

                    // #120: the same re-fetch for whichever of the PBR slots this material declared. A slot
                    // that declared a map but resolves to nothing this frame drops out of the mask rather
                    // than leaving the shader sampling an empty unit.
                    slots = 0;
                    // #294: a map needs COORDINATES to be a map. On a VDO with no UV stream every sampler
                    // resolves to the one texel at the generic constant (0,0) -- the same value for every
                    // fragment of the mesh -- so the normal, specular, roughness, height and AO slots stop
                    // carrying material detail and start carrying whichever pixel happens to sit in the
                    // corner of the sheet. That is not the .mtr's intent, it is an artifact of sampling a 2D
                    // map with nothing to sample it by, and it is load-bearing: CxSynthPbr takes a declared
                    // roughness WHOLE, so one dark corner texel makes a frosted crystal a mirror.
                    // The env flag survives -- it is keyed on the normal and the reflection vector, not on
                    // uv -- and so does the diffuse, which the engine reads at the same constant for the same
                    // reason (it is where envLevel comes from). What is dropped is only what cannot mean
                    // anything, so the synthesis falls back to its generated floor exactly as it does for
                    // content that ships no maps at all.
                    bool mapsMeanNothing = !_vUv[_oVao[i]].Present;
                    for (int s = 0; s < MapSlots; s++)
                    {
                        slotTid[s] = 0;
                        _recAurS[i * MapSlots + s] = IntPtr.Zero;
                        if (lit && !mapsMeanNothing && (_oSlots[i] & SlotBit(s)) != 0)
                        {
                            var sa = mat.MapTexture(s);
                            int t;
                            int bbs = OwnedTextures.Borrowed;
                            if (main) t = sa.OwnedTid();
                            else if (!OwnedTextures.TryDraw(AurTex.Tid(sa.Ptr), out t)) { c = snap; return Deferred; }
                            if (main && OwnedTextures.Borrowed != bbs) OwnedTextures.NoteBorrowedName(sa.Ptr, t);
                            // ...and the same rule per slot, but a slot drops out of the mask instead of
                            // taking the part with it -- an unowned normal map costs that one map, not the
                            // whole draw.
                            if (t != 0 && _bindless && OwnedTextures.Armed
                                && (!OwnedTextures.IsOwned(t) || Handle(t, main) == 0))
                            { if (!main && OwnedTextures.IsOwned(t)) { c = snap; return Deferred; } t = 0; }
                            _recAurS[i * MapSlots + s] = sa.Ptr;   // stamped only past the gates: a deferred slot must re-resolve
                            if (t != 0) { slotTid[s] = t; slots |= SlotBit(s); }
                        }
                    }
                    if (lit) slots |= _oSlots[i] & EnvSlot;   // #121: no live texture to re-fetch, just the flag
                    // #146's zero-content test path, see _aoTest. Stands in for a texture11 nothing ships yet.
                    if (lit && _aoTest && !mapsMeanNothing) { slotTid[AoSlot] = tid; slots |= AoBit; }
                    // #331: registered on a pointer change only -- a cycle STEP re-resolves every record on
                    // the sheet, and re-asking the engine for a controller already on file would cost that
                    if (aur.Ptr != _recAur[i] || _recCtl[i] == IntPtr.Zero) _recCtl[i] = CycleTextures.Register(aur.Ptr);
                    // A record holding the white stand-in stays STALE: the engine writes a late GL name
                    // INSIDE the CAurTexture (basic+0x6c) without touching the pointer this cadence compares,
                    // so a cached stand-in would never re-resolve and the part would stay white for the life
                    // of the record. The cost is one re-resolve per frame per affected part, which is the
                    // null-tex count.
                    _recAur[i] = aur.Ptr; _recTid[i] = tid; _recSlotMask[i] = slots; _recTexStale[i] = nullTex;
                    for (int s = 0; s < MapSlots; s++) _recTidS[i * MapSlots + s] = slotTid[s];
                }
            }
            // Did the normal map declared by the .mtr actually reach the shader? The two numbers apart say
            // whether an area has no normal-mapped content at all or has it and the re-fetch above dropped it.
            if (lit && (_oSlots[i] & SlotBit(0)) != 0) { c.NrmDeclared++; if (slotTid[0] != 0) c.NrmBound++; }

            int src = i;
            // #322: index range read live from the VDO item tables only when the slot's cache stamp fell
            // (UnstampRecord fires on every re-tenant and drifted keep -- the only edges that re-point
            // _oVdo/_oItem -- and on every transient range failure; Repack revokes before the tables move),
            // then served from the slot cache.
            int idxCount;
            if (_cGen[i] == _recEpoch) idxCount = _cIdx[i];
            else
            {
                var vdo = new VdoRef(_oVdo[src]);
                long elem = vdo.ElemItems;
                long vert = vdo.VertItems;
                int item = _oItem[src];
                if (elem == 0 || vert == 0 || item < 0
                    || item >= vdo.ElemCount || item >= vdo.VertCount)
                { Inc(ref Counters._skipRange); UnstampRecord(i); SetCastOk(i, false, ref c); W(i, "skipped: item out of range"); return Skipped; }
                idxCount = *(int*)(elem + item * 8 + 4);
                if (idxCount <= 0) { Inc(ref Counters._skipRange); UnstampRecord(i); SetCastOk(i, false, ref c); W(i, "skipped: empty index range"); return Skipped; }
                _cIdx[i] = idxCount;
                _cOff[i] = (nint)((long)(uint)*(int*)(elem + item * 8) * 2);
                _cBase[i] = *(int*)(vert + item * 8);
                _cGen[i] = _recEpoch;
            }

            if (samp) FrameZones.AddSampled(FrameZones.Zone.Chase, tChase, ChaseEvery);
            int v = _oVao[src];
            if (!main && !VaoReady(v)) { c = snap; return Deferred; }
            if (!EnsureVao(v)) { Inc(ref Counters._skipVao); UnstampRecord(i); SetCastOk(i, false, ref c); W(i, "skipped: no VAO"); return Skipped; }
            int feat = _oBone[i] >= 0 ? (int)ShaderFeature.Skin : (int)_oFeat[i * 4];
            bool merge = _bindless && lit && cls != (int)AlloyClass.Water;

            // the per-draw record. The model matrix drives BOTH the world position the light loop shades at
            // and the world normal (mat3 of the same GLSL-visible matrix is the correct normal transform here
            // -- the raw bytes already read as the transpose), and the shaders reach uVP * model for clip.
            // #242: every field below is COMPARED before it is written, and the record is only re-sent to the
            // GPU if something actually moved. Which fields can move at all is ADR 0017's split table; the
            // compares here are the enforcement, and the self-check pins them.
            float* r = data + (long)i * DrawFloats;
            if (!verified)
            {
                if (samp) tChase = FrameZones.Now;
                bool rec = texDirty;
                if (stale) { WriteStable(r, i, lit); rec = true; }
                var pf = default(RecordFrameState);
                // rot.z carries #124's dissolve flag and rot.w #126's per-draw alpha multiplier (the part's
                // own alpha, times the tile fade factor where one applies): the record already had two dead
                // floats here, so both folds cost no stride growth and leave RecordSafe's vec4 count alone.
                pf.Dissolve = dissolve; pf.Alpha = _passAlpha[k];
                pf.HasModel = _oMx[i];
                bool newTenant = _recEnroll[i] != _oEnrollGen[i];
                // #330: read off the engine only if it can have moved -- see ChaseModel.
                if (pf.HasModel) pf.Model = ChaseModel(i, r, stale, newTenant, ref c);
                pf.Lit = lit;
                if (lit)
                {
                    if (colour)
                    {
                        var mdlRef = new MdlRef(mdl);
                        float* amb = mdlRef.Ambient;
                        float* dif = mdlRef.Diffuse;
                        float* emi = part.SelfIllum;
                        // #128: PartTriMesh::Mat @0x450960's remaining two instance terms, live. Lit-only
                        // because the decompile guards its three SetMaterialv calls behind IsEnabled(5) --
                        // an unlit part gets a flat constant colour and the instance's alpha, which is all
                        // the record above carries.
                        var cm = AlloyCensus.GobDiffuseMul(tri);
                        var ea = AlloyCensus.GobEmissiveAdd(tri);
                        pf.AmbR = amb[0]; pf.AmbG = amb[1]; pf.AmbB = amb[2];
                        pf.DifR = dif[0] * cm.X; pf.DifG = dif[1] * cm.Y; pf.DifB = dif[2] * cm.Z;
                        pf.EmiR = emi[0] + ea.X; pf.EmiG = emi[1] + ea.Y; pf.EmiB = emi[2] + ea.Z;
                    }
                    else
                    {   // #322: not a colour frame -- reload the record's own terms so the compare sees no
                        // change. Sound because !colour implies !stale: the record was built this epoch.
                        pf.AmbR = r[20]; pf.AmbG = r[21]; pf.AmbB = r[22];
                        pf.DifR = r[24]; pf.DifG = r[25]; pf.DifB = r[26];
                        pf.EmiR = r[28]; pf.EmiG = r[29]; pf.EmiB = r[30];
                    }
                    pf.Slots = slots;
                }
                // #123: outside the lit branch -- the FULLBRIGHT vertex stage switches on it too, so an unlit
                // wind part still sways. Only the fragment-side features (the roof anchor discard, the
                // flow-map slide) are lit-only, and neither of those parts is ever unlit.
                // #137: feat.y is this draw's bone base -- the MATRIX index of its first bone in the block,
                // so the stage indexes it with a single add. It REPLACES the .mtr feature, which is why a
                // posed skinmesh is the per-frame arm rather than a write after the .mtr copy.
                int fo = i * 4;
                pf.FeatX = _oBone[i] >= 0 ? (int)ShaderFeature.Skin : _oFeat[fo];
                pf.FeatY = _oBone[i] >= 0 ? _oBone[i] * BoneCount : _oFeat[fo + 1];
                // #302: the shadow's mover signal wants GEOMETRY motion, not "the record was re-sent" --
                // tile-fade alpha and the day-night ambient drift rewrite hundreds of records a frame, and
                // charging those as movers re-baked every cube every frame (the predicate skipped nothing).
                // So the model is compared HERE, before the write consumes the difference; a change of
                // tenant is the other true mover (the new part APPEARED in the views it stands in).
                if (samp) FrameZones.AddSampled(FrameZones.Zone.Chase, tChase, ChaseEvery);
                // the record moves at a tenth of a millimetre (it is what the shader draws); a CASTER moves at a
                // centimetre -- a 256 px cube over a 20 m light has 8 cm texels, and the harbour barrels bobbing
                // a few millimetres a frame held 13 moving-caster pages and 40 cube faces a frame with the camera
                // still (run 15). Creatures are bone movers and never pass through here.
                bool casterMoved = pf.HasModel && Moved(in *(Matrix4x4*)r, in pf.Model, CasterStep);
                // #392: prev rolls once per FRAME -- a second build of this record in the same frame (a
                // reflection subframe) must not catch prev up to the model and erase the mover's vectors.
                rec |= WritePerFrame(r, in pf, roll: _recFrame[i] != _frameSeq, fresh: stale || newTenant);
                // #153: this record's textures as resident handles -- diffuse at 0, material slots 1..7 in
                // the shader's macro order (slotTid[k] = material texture k+1). Only for lit records; unlit
                // parts draw on the bind path and never read the block. Re-minted only when the borrowed
                // CAurTexture* moved: an unchanged pointer is an unchanged copy is an unchanged handle.
                if (merge && texDirty)
                {
                    fixed (ulong* th = _dTexH)
                    {
                        ulong* hh = th + (long)i * 8;
                        // Both already minted and non-zero: the gate above refuses the part (and drops the
                        // slot) on a zero handle, so nothing reaches here that would sample handle 0 --
                        // which is a BLACK texture, not a missing one, and the reason that gate exists.
                        hh[0] = Handle(tid, main);
                        for (int t = 0; t < MapSlots; t++) hh[1 + t] = Handle(slotTid[t], main);
                        _texHDirty = true;
                    }
                }
                // #243: the cull's bounds ride the same edge as the record -- a part that did not move did
                // not move its sphere either. Written before the dirty mark so a caller reading the two
                // together never sees a fresh record beside last frame's bounds.
                if (rec) WriteSphere(i, in pf, tri, feat);
                if (rec) { WriteCasterSphere(i, in pf); NoteCasterTouched(i); }
                if (rec) MarkRecordDirty(i, ref c); else c.RecStable++;
                // Membership alone is not enough to freeze on: the record has to have STOPPED changing too,
                // which is what the settle run measures. Capped so a slot that has been still for an hour
                // cannot overflow the counter.
                _stSettle[i] = !rec && member ? Math.Min(_stSettle[i] + 1, SettleCeiling) : 0;
                _recGen[i] = _recEpoch; _recFrame[i] = _frameSeq; _recEnroll[i] = _oEnrollGen[i];
                // #302: a LEVEL the shadow pass compares against _recFrame, never an edge it consumes.
                if (casterMoved || newTenant) _recMovedAt[i] = _frameSeq;
                // #328: ...and the admission level beside it, on the same edge: a record landed for this
                // tenant, so the slot casts unless its tile is fading or its class cannot write depth.
                // #363: an unskinned blended part casts too, at its record alpha through the masked stage's
                // dither; a posed one has no mask stage and would cast its bind pose solid.
                SetCastOk(i, !_oFading[i] && (_oCls[i] <= (byte)AlloyClass.Cutout
                                              || (_oCls[i] == (byte)AlloyClass.Blended && _oBone[i] < 0)), ref c);
            }
            _pkDis[k] = dis ? (byte)1 : (byte)0;
            return Staged;
        }
    }

    /// <summary>#125: stage the sky's draws behind the batch records, in the engine's own order -- current
    /// sky, then the one fading in over it, then the cross-fade gob. One record per part, carrying the model
    /// matrix <see cref="SkyBox"/> captured off the engine's matrix stack and the role's alpha in
    /// <c>rot.w</c>, which is the per-draw alpha multiplier the fullbright stage already applies. Nothing
    /// here needs a new shader stage, and so nothing here can drift from the sow-topdata overrides.
    /// <para>Every term is re-read live: a part whose texture, index range or VAO does not resolve this
    /// frame is simply not staged, and the engine's own draw (which is still running, colour-masked only
    /// while we DID draw) is the fallback.</para></summary>
    private static void StageSky()
    {
        _skyN = 0; _seN = 0; _skyFirst = _drawN;
        IntPtr scene = SkyBox.Scene;
        // #91's rule again: only the enrolled world Scene's sky, never a GUI 3D panel's.
        if (scene == IntPtr.Zero || scene != _enrollScene) return;
        Span<SkyBox.Role> roles = stackalloc SkyBox.Role[3];
        int nr = SkyBox.Roles(scene, roles);
        SkyBox.NoteRoles(roles, nr);
        fixed (float* data = _dData)
        fixed (float* t3 = _skTint)
        for (int r = 0; r < nr; r++)
        {
            int staged = 0;
            for (int i = 0; i < SkyBox.Count; i++)
            {
                ref SkyBox.Part p = ref SkyBox.At(i);
                if (p.Gob != roles[r].Gob) continue;
                if (p.Vao < 0 || !SkyBox.Drawable(i) || _drawN >= MaxOwned || _skyN >= MaxSkyDraws)
                { staged = -1; break; }
                // #136: a part whose material names a shader no Alloy stage implements is drawn with the
                // ENGINE's compiled program, captured at the swallow. It takes no Alloy draw record and no
                // stage of ours -- everything it needs is already inside that program -- so it is staged to
                // its own list here and the role counts it as drawn.
                if (!p.Ported)
                {
                    if (!StageEngineSky(ref p, roles[r].Src, roles[r].Dst)) { staged = -1; break; }
                    staged++;
                    continue;
                }
                // borrowed CAurTexture*, re-fetched per frame like every other Alloy draw
                int tid = new MaterialRef(p.Mat).Texture(0).OwnedTid();
                var vdo = new VdoRef(p.Vdo);
                long elem = vdo.ElemItems;
                long vert = vdo.VertItems;
                if (tid == 0 || elem == 0 || vert == 0 || p.Item < 0
                    || p.Item >= vdo.ElemCount || p.Item >= vdo.VertCount)
                { staged = -1; break; }
                int idxCount = *(int*)(elem + p.Item * 8 + 4);
                if (idxCount <= 0 || !EnsureVao(p.Vao)) { staged = -1; break; }

                int n = _drawN;
                _dCount[n] = idxCount;
                _dOff[n] = (nint)((long)(uint)*(int*)(elem + p.Item * 8) * 2);
                _dBase[n] = *(int*)(vert + p.Item * 8);
                // #242: the sky's records live in the reserved block above the ownership slots and are
                // restaged every pass -- a captured matrix stack is per pass by construction, so there is
                // nothing here for a persistence scheme to save.
                int srec = _recOwnCap + _skyN;
                _dSlot[n] = srec;
                MarkRecordDirty(srec);
                float* rec = data + (long)srec * DrawFloats;
                *(Matrix4x4*)rec = p.Model;
                // identity texture rotation, no dissolve, the role's alpha; no feature, and the lit block is
                // never read because the sky takes the fullbright program (the engine draws it Disable(5)).
                // #222: Mat()'s two branches, in full. A fog-tinted gob is drawn in the scene's fog
                // colour at alpha 1.0 -- the alpha override is part of the same branch, not a separate
                // decision, and dropping it would tint the fade and then fade the tint away again.
                // #341: the band's colour is the fully-fogged colour the terrain under it converges to,
                // which is the raw fog colour only while /fog sky is 0. The dome parts feed the fog its sky
                // colour on the way past -- the band itself must not, or the fog would read its own output.
                bool tinted = SkyBox.FogTinted(p.Gob);
                float* tint = t3 + _skyN * 3;
                if (tinted) SceneFog.SkyfadeTint(tint);
                else { tint[0] = 1f; tint[1] = 1f; tint[2] = 1f; SceneFog.NoteSky((uint)tid, roles[r].Alpha); }
                rec[16] = 1f; rec[17] = 0f; rec[18] = 0f;
                rec[19] = tinted ? 1f : roles[r].Alpha;
                rec[40] = 0f; rec[41] = 0f; rec[42] = 0f; rec[43] = 0f;
                _skVao[_skyN] = p.Vao; _skTex[_skyN] = tid; _skMode[_skyN] = _primTable[p.Prim];
                _skSrc[_skyN] = roles[r].Src; _skDst[_skyN] = roles[r].Dst;
                _skyN++; _drawN = n + 1; staged++;
            }
            // All-owned-or-none, per #107 and exactly as the blended class is: a role that cannot stage
            // every one of its parts -- or has captured none of them yet -- would leave a permanent hole in
            // a sky the engine is being colour-masked out of. Hand the whole sky back for the frame instead,
            // which costs one visible vanilla sky and no hole at all.
            if (staged <= 0) { _drawN = _skyFirst; _skyN = 0; _seN = 0; SceneFog.EndSky(); return; }
        }
        SceneFog.EndSky();
    }

    /// <summary>#136: one sky part staged for the engine's own program. Everything the draw needs that is
    /// not already uniform state on that program: the geometry range, the primitive mode, the role's blend
    /// pair, and the material's textures -- read the same way every other Alloy draw reads them, because
    /// the engine binds material texture <i>k</i> to <c>texUnit</c><i>k</i> and a captured GL binding would
    /// have to come from the engine's context (#237: touching the active unit there desyncs its cache).
    /// <para>False means this part cannot be drawn this frame, and the caller hands the whole role back --
    /// the same all-owned-or-none rule the ported path follows, for the same reason.</para></summary>
    private static bool StageEngineSky(ref SkyBox.Part p, int src, int dst)
    {
        if (_seN >= MaxSkyDraws || p.EngProg == 0) return false;
        var vdo = new VdoRef(p.Vdo);
        long elem = vdo.ElemItems, vert = vdo.VertItems;
        if (elem == 0 || vert == 0 || p.Item < 0 || p.Item >= vdo.ElemCount || p.Item >= vdo.VertCount)
            return false;
        int idxCount = *(int*)(elem + p.Item * 8 + 4);
        if (idxCount <= 0) return false;
        var mat = new MaterialRef(p.Mat);
        for (int k = 0; k < MaxEngTex; k++)
        {
            // The ENGINE's own texture, never the owned copy: this is the engine's program replayed, and
            // the copy shares its texels but not the object state the engine sets on its own name at bind
            // time (filter, anisotropy) -- the dome's polar fan is the most anisotropic footprint in the
            // frame, and the difference showed as radial spokes off the zenith. Every unit is bound, 0
            // included: a unit the replay leaves untouched keeps whatever Alloy last bound there.
            var t = mat.Texture(k);
            _seTex[_seN * MaxEngTex + k] = t.IsNull ? 0 : t.ResolveTid();
        }
        // texUnit0 is the one every stock stage samples unconditionally; without it there is nothing to draw.
        if (_seTex[_seN * MaxEngTex] == 0) return false;
        _seProg[_seN] = p.EngProg;
        _seVao[_seN] = p.Vao;
        _seMode[_seN] = _primTable[p.Prim];
        _seCount[_seN] = idxCount;
        _seOff[_seN] = (nint)((long)(uint)*(int*)(elem + p.Item * 8) * 2);
        _seBase[_seN] = *(int*)(vert + p.Item * 8);
        _seSrc[_seN] = src; _seDst[_seN] = dst;
        _seN++;
        return true;
    }

    /// <summary>#136: the sky parts nothing in Alloy can shade, drawn with the program the ENGINE compiled
    /// for them -- sow-topdata's <c>fst_sky</c> chain, ~1900 lines of procedural cloud, star, sun and moon,
    /// running as itself rather than as a port that would drift from it.
    /// <para>What makes this legal where it is illegal for world geometry: the sky writes no depth, is not
    /// in the depth prepass, and carries no per-draw batching -- so #119's <c>gl_Position</c> invariance
    /// rule and #327's submission model, which forbid a stock stage everywhere else, do not reach it.</para>
    /// <para>The program's uniforms were uploaded in ENGINE context at the swallow
    /// (<see cref="SkyBox.NoteEngineDraw"/>) and are program-object state, which the share-listed 4.6
    /// context (#80) sees. Textures are the exception -- bindings are per-context -- so they are bound here,
    /// through DSA so the active unit (which the engine caches, #237) is never touched.</para>
    /// <para>ponytail: it draws under the depth state <see cref="SkyOwnedPass"/> establishes and is called
    /// from inside it, so the two share one preamble. Splitting them would mean stating that state twice
    /// and having it drift once.</para></summary>
    private static void SkyEnginePass()
    {
        uint boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        fixed (int* counts = _seCount, bases = _seBase)
        fixed (nint* offs = _seOff)
        for (int i = 0; i < _seN; i++)
        {
            uint src = Issue.BlendGl(_seSrc[i]), dst = Issue.BlendGl(_seDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            uint vao = EngineVao(_seVao[i]);
            if (vao == 0) continue;
            Gl.UseProgram(_seProg[i]);
            // every unit, 0 included: an unbound unit samples black under the engine, not a neighbour
            for (uint k = 0; k < MaxEngTex; k++) Gl.BindTextureUnit(k, (uint)_seTex[i * MaxEngTex + (int)k]);
            Gl.BindVertexArray(vao);
            Gl.MultiDrawElementsBaseVertex(_seMode[i], counts + i, GlUnsignedShort,
                                           (void**)(offs + i), 1, bases + i);
            Counters._batches++; Counters._draws++;
        }
    }

    /// <summary>The skybox, after the opaque set and before the blended one (ADR 0011 step 3, #125).
    /// <para><c>glDepthRange(1,1)</c> is what makes "after opaque" mean anything. The dome sits AT the
    /// camera, so its own depth is NEAR and it would pass the test everywhere -- which is why vanilla paints
    /// over bucket 0. Forced to the far plane and tested LEQUAL (the value <see cref="OpaqueOwnedPass"/>
    /// leaves bound), it survives only where nothing has written depth. <c>GL_DEPTH_CLAMP</c> because the
    /// matrices were captured under the ENGINE's sky projection (far 1000.0, <c>RenderSkyBoxes</c>) and are
    /// replayed under the world's: clipped, the dome would read as a hole at the horizon with nothing in the
    /// log. Depth range and clamp are 4.6-CONTEXT state the later passes share, so both are restored here.</para>
    /// <para>ponytail: the roles composite in LINEAR (ADR 0018: "there is no strand 3"). The settled sky is
    /// unaffected -- its role is <c>(SRC_ALPHA, ZERO)</c>, which never reads the destination -- but the
    /// cross-fade and the skyfade gob are genuine over-blends, and at partial alpha a linear over composites
    /// brighter than the engine's display-space one by <c>alpha^(1/2.2)/alpha</c> (1.5x at 0.5). Deliberate:
    /// the sky is Alloy's to author and the frame it lands in is linear. The three alphas are on the period
    /// line (<see cref="SkyBox.NoteRoles"/>); if the horizon band ever reads wrong, the upgrade is a per-role
    /// alpha correction, not a display-space scratch target (#261).</para>
    /// </summary>
    /// <para><paramref name="hosted"/> false (the probe capture) draws the owned dome alone: a hosted part runs
    /// the engine's program under the engine's matrices, and its decode reads the frame's HDR target.</para>
    /// <summary>The main view's inverse view-projection, noted at the sky pass for the resolve's horizon band
    /// (<see cref="PostChain"/>): a far-depth pixel below the band is fog, not sky. Default = none this view.</summary>
    internal static Matrix4x4 SkyInvVp;

    private static void SkyOwnedPass(Matrix4x4* vp, int* viewport, bool hosted = true)
    {
        if ((_skyN == 0 && (_seN == 0 || !hosted)) || Gl.DepthRange == null) return;
        Gl.UseProgram(Programs._prog46);
        Gl.Uniform1i(Programs._locTex, 0);
        Gl.Uniform1i(Programs._locViz, _lightViz);
        // uOit is program state and the OIT pass left it at 1: the dome would land as a near-zero weighted
        // accumulation write (gl_FragCoord.z = 1) -- a black sky.
        Gl.Uniform1i(Programs._locOit, 0);
        Gl.UniformMatrix4fv(Programs._locVp, 1, 0, (float*)vp);
        UploadFeatureTerms(Programs._locFeat, viewport);
        Gl.Uniform1f(Programs._locCut, -1f);          // the engine draws the sky with the discard off
        Gl.Uniform1f(Programs._locSkyFog, SceneFog.SkyBand);
        // DEPTH_CLAMP because the composed matrices sit under the ENGINE's sky projection (RenderSkyBoxes
        // replaces it with its own near/far) and are drawn under the world's: the dome can cross the world
        // far plane, and clipped it reads as a hole at the horizon with nothing in the log.
        Gl.DepthMask(0); Gl.DepthRange(1.0, 1.0);
        Gl.Enable(Gl.DEPTH_CLAMP);
        // #292: STATED, not inherited, and stated as the ENGINE's. This pass has no material to read
        // `twosided` off and no batch of its own, so it used to run under the pass-wide disable and would
        // otherwise now run under whatever the pass before it left -- which is not the same state twice: the
        // grass pass returns before its own preamble when an area has no grass, and the opaque range ends
        // two-sided only when its last batch was. Vanilla draws the sky BACK-CULLED: SetupOpenGL @0x42b060
        // enables cap 3 at startup, Scene::RenderSkyBoxes @0x466910 touches caps 0/1/2/5 but never 3, and it
        // runs after a Scene::RenderDrawBucket @0x46d060 that exits Enable(3)+SetCullFace(1). The dome is
        // the engine's own geometry replayed under the engine's own winding, so the state it is drawn
        // correctly in over there is the state it is drawn correctly in here.
        Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        int boundVao = -1, boundTex = -1;
        uint boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        fixed (float* t3 = _skTint)
        for (int i = 0; i < _skyN; i++)
        {
            uint src = Issue.BlendGl(_skSrc[i]), dst = Issue.BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (_skVao[i] != boundVao) { Gl.BindVertexArray(SkyVao(_skVao[i])); boundVao = _skVao[i]; }
            if (_skTex[i] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_skTex[i]); boundTex = _skTex[i]; }
            Gl.Uniform3fv(Programs._locTint, 1, t3 + i * 3);
            int s = _skyFirst + i;
            Gl.Uniform1i(Programs._locBase, s);
            Gl.MultiDrawElementsBaseVertex(_skMode[i], counts + s, GlUnsignedShort, (void**)(offs + s), 1, bases + s);
            Counters._batches++; Counters._draws++;
        }
        Gl.Uniform1f(Programs._locSkyFog, 0f);   // program state outlives the pass: no later unlit draw inherits the band
        // #136: the hosted-program parts last, inside the same depth/cull/blend preamble.
        if (hosted) SkyEnginePass();
        if (hosted && _seN > 0 && Programs._progSkyDecode != 0 && Gl.TextureBarrier != null && PostChain.HdrTex != 0)
        {
            // same depth range and test: exactly the pixels the hosted dome could have written
            Gl.TextureBarrier();
            Gl.Disable(Gl.BLEND);
            Gl.UseProgram(Programs._progSkyDecode);
            // unit 0, which this pass has already given to engine textures and every later pass rebinds;
            // 10..11 carry the shadow arrays the blended passes still sample
            Gl.BindTextureUnit(0, PostChain.HdrTex);
            Gl.Uniform1i(Programs._skyDecodeHdr, 0);
            UploadFog(Programs._skyDecodeFeat);
            Gl.Uniform1f(Programs._skyDecodeSkyFog, Matrix4x4.Invert(*vp, out Matrix4x4 invVp) ? SceneFog.SkyBand : 0f);
            Gl.UniformMatrix4fv(Programs._skyDecodeInvVp, 1, 0, (float*)&invVp);
            Gl.BindVertexArray(PostChain.FullscreenVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.Enable(Gl.BLEND);
            Counters._draws++;
        }
        Gl.Disable(Gl.DEPTH_CLAMP);
        Gl.DepthRange(0.0, 1.0);
    }
}
