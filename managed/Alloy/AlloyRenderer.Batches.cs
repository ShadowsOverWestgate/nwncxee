using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

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
    /// <para>ponytail: insertion sort, i.e. O(n^2) in the blended count. That count is the small minority of
    /// a scene by construction, and the alternative (a radix over quantised depth) is a bucket table to size
    /// and clear per pass. If a scene ever lands enough blended parts to show on the [alloy] line's colour
    /// time, that is the upgrade.</para></summary>
    private static int RouteParts()
    {
        int n = 0, nb = 0;
        int opaqueEnd = _clsEnd[(int)AlloyClass.Cutout];   // classes are contiguous: opaque then cutout
        for (int k = _clsStart[(int)AlloyClass.Opaque]; k < opaqueEnd; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) { W(i, "skipped: slot vacated (revoked or class dropped)"); continue; }
            // A part whose identity no longer matches is a recycled (or freed) pointer, and the reads below
            // CHASE one -- part+0x98 to the TileData, then its fade factor. The staging walk revalidates and
            // revokes; this route only has to not dereference garbage before it does, so a mismatch is left
            // in the opaque run for BuildBatches to throw out.
            if (!IdentityOk(i, tri)) { _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; n++; continue; }
            // The fade overlay, and the part's own animated alpha with it: while its tile fades, an opaque
            // or cutout part IS a bucket-8 part -- the engine folds the tile's factor into the part's own
            // alpha for the length of the draw (RenderDrawBucket @0x46d060, bucket 8 only) and draws it
            // blended. So does this pass. Reading the part alpha live here (rather than trusting the class
            // from the last enrollment) is also what keeps a part whose alpha animated below 1 from standing
            // solid for up to an EnrollEvery cadence.
            float alpha = LiveAlpha(tri);
            if (alpha >= 1f) { _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; n++; continue; }
            _ownFade++;
            _blend[nb] = i; _blendAlpha[nb] = alpha; _blendKey[nb] = EyeDist2(i); nb++;
        }
        for (int k = _clsStart[(int)AlloyClass.Blended]; k < _clsEnd[(int)AlloyClass.Blended]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _blend[nb] = i;
            // ...and the sort key is skipped too on a failed identity: EyeDist2 reads the enrolled NODE's
            // matrix for a matrix-class part, and the staging walk is about to revoke this entry anyway, so
            // where it would have sorted does not matter.
            bool live = IdentityOk(i, tri);
            _blendAlpha[nb] = live ? LiveAlpha(tri) : 1f;
            _blendKey[nb] = live ? EyeDist2(i) : 0f; nb++;
        }
        SortBlended(_blend, _blendKey, _blendAlpha, nb);
        for (int a = 0; a < nb; a++)
        { _pass[n] = _blend[a]; _passCls[n] = (byte)AlloyClass.Blended; _passAlpha[n] = _blendAlpha[a]; n++; }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        for (int k = _clsStart[(int)AlloyClass.Water]; k < _clsEnd[(int)AlloyClass.Water]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Water;
            _passAlpha[n] = IdentityOk(i, tri) ? LiveAlpha(tri) : 1f;
            // #147: measured here rather than at enroll -- routing is where a water part is certainly live
            // and certainly has its node, and the registry ignores one it already holds.
            WaterBodies.Note(tri, _oNode[i]);
            n++;
        }
        for (int k = _clsStart[(int)AlloyClass.Additive]; k < _clsEnd[(int)AlloyClass.Additive]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Additive;
            // Additive fades too: the engine routes an additive part on a fading tile into bucket 8 like any
            // other, and IterSight suppresses it there, so a glow left at full brightness through a fade
            // would be ours to explain.
            _passAlpha[n] = IdentityOk(i, tri) ? LiveAlpha(tri) : 1f;
            n++;
        }
        return n;
    }

    /// <summary>Is this slot's part still the one that enrolled? The same single-level compare
    /// <see cref="BuildBatches"/> revokes on -- node, material and texture rotation read straight off the
    /// part. Reading a stale part is the accepted risk model here; chasing a POINTER out of one is not, so
    /// every route-time dereference sits behind this.
    /// <para>#128: pointer identity ONLY. An instance's live shading state used to be folded in here, which
    /// was the wrong place twice over -- a false answer left the part in the OPAQUE run at alpha 1 (drawn
    /// wrong, not handed back) and the staging walk never re-asked. It is a skip of its own now, in
    /// <see cref="BuildBatches"/>.</para>
    /// <para>#145: ...and the entry must still be a LIVE recording of this part before any of that is
    /// read. The three compares below are reads of the part itself, which is only sound while the part
    /// exists -- a destroyed one can keep every field long enough to pass them, and then the gob chase in
    /// <see cref="LiveAlpha"/> walks a freelist word. <c>Part::~Part</c> is what makes this answer no.</para></summary>
    private static bool IdentityOk(int i, IntPtr tri)
    {
        var part = new PartRef(tri);
        return PartTable.EntryLive(_oEntry[i], tri)
               && part.Node.Ptr == _oNode[i] && part.Material.Ptr == _oMat[i]
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

    /// <summary>#128: this frame's whole alpha multiplier for a part -- its own animated alpha, the fade
    /// factor of the tile it stands on, and the instance's two multiplies. Every term is read live and none
    /// is cached at capture, which is what the creature case demands: a gob can fade a whole creature into
    /// the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(IntPtr tri)
        => PartAlpha(tri) * PartTable.TileFadeFactor(tri) * AlloyCensus.GobAlphaMul(tri);

    /// <summary>Squared distance from THIS pass's eye to the part's world centre -- the blended sort key.
    /// Derived exactly as the caster cull derives it and for the same reason (live-verified there): a
    /// matrix-class part's centre is its model matrix translation, and everything else is world-space
    /// geometry whose bbox centre already is the answer.</summary>
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
        internal bool Water;
        internal float BodyFetch, BodySize, BodyExtent, BodySpeed;
        internal float FeatX, FeatY;
    }

    /// <summary>#242: the per-frame half of the record, compared into place. Returns whether anything
    /// actually moved -- which is the whole scheme: a field rewritten with the value it already held does
    /// not make its record a mover, and only movers are re-sent.</summary>
    private static bool WritePerFrame(float* r, in RecordFrameState f)
    {
        bool m = false;
        if (f.HasModel && *(Matrix4x4*)r != f.Model) { *(Matrix4x4*)r = f.Model; m = true; }
        m |= St(r + 18, f.Dissolve) | St(r + 19, f.Alpha);
        if (f.Lit)
        {
            m |= St(r + 20, f.AmbR) | St(r + 21, f.AmbG) | St(r + 22, f.AmbB)
                 | St(r + 28, f.EmiR) | St(r + 29, f.EmiG) | St(r + 30, f.EmiB)
                 | St(r + 35, f.Slots);
            // ...but not the diffuse triple on a LIT WATER batch: those three floats are the body below, and
            // writing both would leave the record dirty every frame no matter what moved -- which is the one
            // way this scheme can be defeated without rendering anything wrong.
            if (!f.Water)
                m |= St(r + 24, f.DifR) | St(r + 25, f.DifG) | St(r + 26, f.DifB);
        }
        if (f.Water)
            m |= St(r + 24, f.BodyFetch) | St(r + 25, f.BodySize)
                 | St(r + 26, f.BodyExtent) | St(r + 27, f.BodySpeed);
        m |= St(r + 40, f.FeatX) | St(r + 41, f.FeatY);
        return m;
    }

    /// <summary>#242: the stable half -- written when the slot is built and never again while it holds the
    /// same part. The texture rotation cannot change without failing <c>IdentityOk</c> (which revokes), a
    /// .mtr does not animate, and the three alpha ones are literal constants. An identity model matrix is
    /// here too: a part whose class does not stage a node has no live matrix to re-read.</summary>
    private static void WriteStable(float* r, int i, bool lit)
    {
        if (!_oMx[i]) *(Matrix4x4*)r = Matrix4x4.Identity;
        r[16] = _oCos[i]; r[17] = _oSin[i];
        int fo = i * 4;
        r[42] = _oFeat[fo + 2]; r[43] = _oFeat[fo + 3];
        if (!lit) return;
        r[23] = 1f; r[27] = 1f; r[31] = 1f;
        int o = i * 8;
        r[32] = _oParam[o]; r[33] = _oParam[o + 1]; r[34] = _oParam[o + 2];
        r[36] = _oParam[o + 4]; r[37] = _oParam[o + 5]; r[38] = _oParam[o + 6]; r[39] = _oParam[o + 7];
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
            ok = WritePerFrame(r, in f) && !WritePerFrame(r, in f);
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
            f.Model = was;
            WritePerFrame(r, in f);
            if (!mm) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a moved model matrix does not "
                              + "re-send its record; every creature and door would freeze where it stood");
            ok &= mm;
        }
        // #129/#147: the water body, on its own record. Its four floats share the diffuse slot with the lit
        // terms, so a state that is both would be testing the overlap rather than the split.
        float[] wrec = new float[DrawFloats];
        var w = default(RecordFrameState);
        w.Water = true; w.Alpha = 1f;
        w.BodyFetch = 11f; w.BodySize = 12f; w.BodyExtent = 13f; w.BodySpeed = 14f;
        fixed (float* r = wrec)
        {
            bool wok = WritePerFrame(r, in w) && !WritePerFrame(r, in w);
            if (!wok) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a water record is a mover every "
                               + "frame, or never one");
            ok &= wok
                  & Perturb(r, ref w, ref w.BodyFetch, log, "water fetch")
                  & Perturb(r, ref w, ref w.BodySize, log, "water size")
                  & Perturb(r, ref w, ref w.BodyExtent, log, "water extent")
                  & Perturb(r, ref w, ref w.BodySpeed, log, "water speed");
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
    private static void UnstampRecord(int i) => _recGen[i] = 0;   // 0 = never built; the epoch starts at 1

    /// <summary>#242: this record moved and has to reach the GPU again. Overflow is not a silent drop -- it
    /// promotes the frame to a wholesale upload, which is correct and merely more expensive.</summary>
    private static void MarkRecordDirty(int slot)
    {
        _recWritten++;
        if (_recDirtyN < _recDirty.Length) _recDirty[_recDirtyN++] = slot;
        else _recDirtyAll = true;
    }

    /// <summary>#242: every persistent record is suspect -- rebuild them all on the next frame that draws.
    /// The edges that need it are the ones no per-part compare can see: an enrollment (slots reshuffle under
    /// the table, so slot N is a different part), any bindless handle wipe or context loss (a record holding
    /// a dead handle is #220's wall-sampling-a-portrait), and the debug toggles that change what a record
    /// means. See ADR 0017's "Step 2, as built".</summary>
    internal static void InvalidateRecords() => System.Threading.Interlocked.Increment(ref _recEpoch);
                                              // Interlocked: /draw runs on the input thread

    /// <summary>Phase A of the Alloy block: walk this pass's order (<see cref="RouteParts"/>), revalidate
    /// every part, and stage the survivors into the multidraw argument arrays plus one per-draw SSBO record
    /// each -- cutting a batch whenever the (class, program, VDO, texture, primitive, blend) key changes.
    /// Primitive mode is in the key because one multidraw carries a single mode; the sort does not group by
    /// it, so a mixed-mode VDO just yields more batches. Returns the batch count, leaving the draw count in
    /// <see cref="_drawN"/> and the per-class batch ranges in <see cref="_gOpaqueEnd"/>/<see cref="_gBlendEnd"/>.
    /// <para>OwnGen is stamped here, which is only honest because the issue phase cannot fail per part: the
    /// entry-point guard ran in <see cref="DrawOwned"/> and EnsureVao ran here.</para></summary>
    private static int BuildBatches(bool lightingOn)
    {
        int groups = 0, n = 0;
        int keyVao = -1, keyTex = -1, keyFeat = -1, keyCls = -1, keyBlend = -1;
        uint keyMode = 0;
        bool keyLit = false, keyDis = false;
        int routed = RouteParts();
        bool dumping = FrameDump.Begin();
        // #134: routing walks the CLASS RANGES, not the ownership table -- a part whose class range was
        // never built simply never appears, and no skip site can report on it. Say so once.
        if (_watchDrawLeft > 0)
        {
            bool routedWatch = false;
            for (int k = 0; k < routed && !routedWatch; k++) routedWatch = AlloyCensus.WatchedEntry(_oEntry[_pass[k]]);
            if (!routedWatch)
            {
                bool owned = false;
                for (int i = 0; i < _ownCount && !owned; i++)
                    owned = _oTri[i] != IntPtr.Zero && AlloyCensus.WatchedEntry(_oEntry[i]);
                if (owned) { _watchDrawLeft--; _log?.Info("[dumpframe] DRAW never routed -- owned, but RouteParts did not include it"); }
            }
        }
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = 0;
        // #242: the persistent buffer grows to the high-water ownership count and never shrinks. A growth
        // reallocates it, so everything in it goes back up on this frame rather than only the movers.
        if (_ownCount > _recOwnCap) { _recOwnCap = _ownCount; _recDirtyAll = true; }
        // #120: this part's live slot-1..5 tids. Declared ONCE, outside the loop -- a stackalloc per
        // iteration grows the frame by the part count.
        int* slotTid = stackalloc int[MapSlots];
        fixed (float* data = _dData)
        for (int k = 0; k < routed; k++)
        {
            int i = _pass[k];
            int cls = _passCls[k];
            // #129: never stamp what the frame cannot draw -- see _waterArmed. Ahead of the stale check
            // below because it is a decision about the PASS, not about this part.
            if (cls == (int)AlloyClass.Water && !_waterArmed) continue;
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            var part = new PartRef(tri);
            // Geometry the engine has stopped walking -- culled by its own visibility, while ownership
            // (rebuilt every EnrollEvery frames) still holds it. Drawing it was ~44% of the Alloy draw count
            // live, all of it invisible, and it is exactly what the one-owner audit was reporting as
            // UNMATCHED: not a double draw, a ghost. The engine's own sighting window is the predictor, and
            // a part that comes back simply goes unstamped for one frame and is drawn by the engine.
            if (!PartTable.InBucketsRecently(_oEntry[i]))
            {
                _skipUnseen++; _unseenBucket[_oBucket[i]]++;
                if (_oGob[i] != IntPtr.Zero) _skipUnseenGob++;
                W(i, "skipped: engine-culled");
                continue;
            }
            // Lit parts sort last. One with the lighting rig unavailable this pass is skipped, not painted
            // flat -- the engine's own walk then draws it, which is its vanilla look.
            bool lit = _oLit[i];
            if (lit && !lightingOn) { _skipNoLight++; W(i, "skipped: lighting off this pass"); continue; }
            // identity + dynamic state: a recycled pointer, a re-rotated tile, a fading or
            // keyhole-dissolving part is not ours this frame -- the engine's own loop body draws it
            if (!IdentityOk(i, tri)) { _skipIdentity++; W(i, "skipped: identity changed -- revoked"); Revoke(i); continue; }
            // #128: instance state the Alloy programs cannot express -- a highlight, a scene tint, a per-gob
            // env override. SKIPPED, not revoked: a highlight lasts exactly as long as the cursor sits on the
            // placeable, and revoking would spend a whole re-enrollment on a hover. Unstamped means the
            // iterator does not suppress it, so the engine's own walk draws it highlighted, which is vanilla.
            if (!AlloyCensus.InstanceOwnable(tri))
            { _skipGob++; W(i, "skipped: instance state unownable this pass"); continue; }
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
            float dissolve = lit && !ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])
                             && PartTable.KeyholeDissolves(tri) ? 1f : 0f;
            if (dissolve != 0f) _ownDissolve++;
            // MatStatic (Ghidra @0x450560): ambient = node+0xf4, diffuse = node+0xe8, emissive = part+0x7c.
            // ponytail: MatStatic also multiplies ambient+diffuse by AurGetTileColor() when the part's tile
            // record (part+0x98, +0x54) is set. Not reproduced -- add it if tiles read the wrong hue.
            IntPtr mdl = lit ? part.Mdl.Ptr : IntPtr.Zero;
            if (lit && mdl == IntPtr.Zero) { _skipNoMdl++; W(i, "skipped: no mdl node"); continue; }

            // #242: this slot's persistent record. Three states, cheapest first -- already verified by an
            // earlier pass THIS frame (nothing to re-read at all), unchanged since the last frame (the
            // texture re-fetch and the handle mints are skipped, which is the expensive half), or dirty.
            // A stale epoch forces the third: see InvalidateRecords for what can make every record wrong.
            bool stale = _recGen[i] != _recEpoch;
            bool verified = !stale && _recFrame[i] == _frameSeq;
            var mat = new MaterialRef(_oMat[i]);
            int tid, slots;
            bool texDirty = false;
            if (verified) { tid = _recTid[i]; slots = _recSlotMask[i]; LoadSlotTids(i, slotTid); }
            else
            {
                // texture: the engine name is re-fetched per frame -- CAurTexture* is BORROWED, freed on
                // area transitions (never cache the pointer), and the per-frame fetch is exactly what keeps
                // animated textures cycling. #211 then maps that name to OUR copy, which cannot be freed
                // under the draw; an animated texture cycles engine names, so each animation frame gets its
                // own copy.
                // #242: the POINTER is compared per frame; only a change re-resolves it to a tid and a
                // bindless handle. That is the ADR 0017 split's per-frame half -- the fetch stays, the two
                // hash probes behind it do not.
                var aur = mat.Texture(0);
                if (aur.IsNull) { _skipTex++; _skipTexNone++; W(i, "skipped: no diffuse texture"); continue; }
                texDirty = stale || aur.Ptr != _recAur[i];
                for (int s = 0; !texDirty && lit && s < MapSlots; s++)
                    if ((_oSlots[i] & SlotBit(s)) != 0 && mat.Texture(s + 1).Ptr != _recAurS[i * MapSlots + s])
                        texDirty = true;
                if (!texDirty) { tid = _recTid[i]; slots = _recSlotMask[i]; LoadSlotTids(i, slotTid); }
                else
                {
                    tid = aur.OwnedTid();
                    if (tid == 0) { _skipTex++; _skipTexNoId++; W(i, "skipped: diffuse has no GL id"); continue; }
                    // #220: a bindless handle must only ever name a texture ALLOY owns. A handle on an
                    // engine texture is a handle on an object the engine may delete or re-upload under us,
                    // which is exactly the corruption this fixes -- so a part whose copy was refused (a
                    // format we will not take) goes back to the engine walk rather than being drawn through
                    // a handle we cannot vouch for. Bind-path batches are unaffected: they rebind the engine
                    // id every frame, which is what they always did.
                    if (lit && _bindless && OwnedTextures.Armed)
                    {
                        // Two DIFFERENT faults behind one guard, and they have different fixes: a texture
                        // Alloy never adopted (it never reached the upload funnel -- a GPU-composited PLT, a
                        // cube, a movie frame) versus one it owns but could not mint a resident handle for.
                        bool owned = OwnedTextures.IsOwned(tid);
                        if (!owned || HandleFor(tid) == 0)
                        {
                            _skipTex++;
                            if (owned) _skipTexNoHandle++; else _skipTexNotOwned++;
                            TexSkipDiag(owned, tid, aur);
                            W(i, owned ? "skipped: diffuse has no bindless handle"
                                       : "skipped: diffuse is not an Alloy-owned texture");
                            continue;
                        }
                    }

                    // #120: the same re-fetch for whichever of the PBR slots this material declared. A slot
                    // that declared a map but resolves to nothing this frame drops out of the mask rather
                    // than leaving the shader sampling an empty unit.
                    slots = 0;
                    for (int s = 0; s < MapSlots; s++)
                    {
                        slotTid[s] = 0;
                        _recAurS[i * MapSlots + s] = IntPtr.Zero;
                        if (lit && (_oSlots[i] & SlotBit(s)) != 0)
                        {
                            var sa = mat.Texture(s + 1);
                            _recAurS[i * MapSlots + s] = sa.Ptr;
                            int t = sa.OwnedTid();
                            // ...and the same rule per slot, but a slot drops out of the mask instead of
                            // taking the part with it -- an unowned normal map costs that one map, not the
                            // whole draw.
                            if (t != 0 && _bindless && OwnedTextures.Armed
                                && (!OwnedTextures.IsOwned(t) || HandleFor(t) == 0)) t = 0;
                            if (t != 0) { slotTid[s] = t; slots |= SlotBit(s); }
                        }
                    }
                    if (lit) slots |= _oSlots[i] & EnvSlot;   // #121: no live texture to re-fetch, just the flag
                    // #146's zero-content test path, see _aoTest. Stands in for a texture7 nothing ships yet.
                    if (lit && _aoTest) { slotTid[AoSlot] = tid; slots |= AoBit; }
                    _recAur[i] = aur.Ptr; _recTid[i] = tid; _recSlotMask[i] = slots;
                    for (int s = 0; s < MapSlots; s++) _recTidS[i * MapSlots + s] = slotTid[s];
                }
            }
            // Did the normal map declared by the .mtr actually reach the shader? The two numbers apart say
            // whether an area has no normal-mapped content at all or has it and the re-fetch above dropped it.
            if (lit && (_oSlots[i] & SlotBit(0)) != 0) { _nrmDeclared++; if (slotTid[0] != 0) _nrmBound++; }

            int src = i;
            // index range re-read live from the VDO item tables (Repack revokes before they move)
            var vdo = new VdoRef(_oVdo[src]);
            long elem = vdo.ElemItems;
            long vert = vdo.VertItems;
            int item = _oItem[src];
            if (elem == 0 || vert == 0 || item < 0
                || item >= vdo.ElemCount || item >= vdo.VertCount)
            { _skipRange++; UnstampRecord(i); W(i, "skipped: item out of range"); continue; }
            int idxCount = *(int*)(elem + item * 8 + 4);
            if (idxCount <= 0) { _skipRange++; UnstampRecord(i); W(i, "skipped: empty index range"); continue; }

            int v = _oVao[src];
            if (!EnsureVao(v)) { _skipVao++; UnstampRecord(i); W(i, "skipped: no VAO"); continue; }
            uint mode = _primTable[_oPrim[i]];
            // #123: the feature joins the batch key. Not for binding -- it rides in the per-draw record --
            // but so the depth prepass can drop a whole batch: a displacing feature must not write its
            // undisplaced depth, and the prepass has no per-draw granularity.
            int feat = (int)_oFeat[i * 4];
            // #137: a posed skinmesh overrides whatever content feature the material named -- see
            // ShaderFeature.Skin for why the two share the field, and why that trade is safe on shipped
            // content. Joining the cut key here is what lets a whole batch be skinned or not -- #262 turned
            // that from "the prepass drops it" into "the prepass runs it under its own bone arm".
            if (_oBone[i] >= 0) feat = (int)ShaderFeature.Skin;
            // #124: ...and the dissolve bit joins it for the same reason -- the keyhole stage discards.
            bool dis = dissolve != 0f;
            // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
            int blend = AlloyClasses.Blends(cls) ? (_oSrc[i] << 8) | _oDst[i] : 0;
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
            if (!sameSlots || lit != keyLit || v != keyVao || tidKey != keyTex || mode != keyMode
                || feat != keyFeat || dis != keyDis || cls != keyCls || blend != keyBlend)
            {
                _gStart[groups] = n; _gLen[groups] = 0;
                _gLit[groups] = lit; _gVao[groups] = v; _gTex[groups] = tid; _gMode[groups] = mode;
                _gFeat[groups] = feat; _gDis[groups] = dis;
                _gCls[groups] = (byte)cls; _gSrc[groups] = _oSrc[i]; _gDst[groups] = _oDst[i];
                for (int s = 0; s < MapSlots; s++) _gTexN[groups * MapSlots + s] = slotTid[s];
                groups++;
                keyLit = lit; keyVao = v; keyTex = tidKey; keyMode = mode; keyFeat = feat; keyDis = dis;
                keyCls = cls; keyBlend = blend;
            }
            W(i, "SUBMITTED");
            if (AlloyCensus.WatchTex[_oEntry[i]]) _watchSubmitted++;
            _dCount[n] = idxCount;
            _dOff[n] = (nint)((long)(uint)*(int*)(elem + item * 8) * 2);
            _dBase[n] = *(int*)(vert + item * 8);
            // #242: the record this draw reads lives at the OWNERSHIP slot and outlives the frame; the draw
            // order does not, so the draw carries the slot rather than the record.
            _dSlot[n] = i;

            // the per-draw record. The model matrix drives BOTH the world position the light loop shades at
            // and the world normal (mat3 of the same GLSL-visible matrix is the correct normal transform here
            // -- the raw bytes already read as the transpose), and the shaders reach uVP * model for clip.
            // #242: every field below is COMPARED before it is written, and the record is only re-sent to the
            // GPU if something actually moved. Which fields can move at all is ADR 0017's split table; the
            // compares here are the enforcement, and the self-check pins them.
            float* r = data + (long)i * DrawFloats;
            if (!verified)
            {
                bool rec = texDirty;
                if (stale) { WriteStable(r, i, lit); rec = true; }
                var pf = default(RecordFrameState);
                // rot.z carries #124's dissolve flag and rot.w #126's per-draw alpha multiplier (the part's
                // own alpha, times the tile fade factor where one applies): the record already had two dead
                // floats here, so both folds cost no stride growth and leave RecordSafe's vec4 count alone.
                pf.Dissolve = dissolve; pf.Alpha = _passAlpha[k];
                pf.HasModel = _oMx[i];
                if (pf.HasModel) pf.Model = new NodeRef(_oNode[i]).Model;
                pf.Lit = lit;
                if (lit)
                {
                    var mdlRef = new MdlRef(mdl);
                    float* amb = mdlRef.Ambient;
                    float* dif = mdlRef.Diffuse;
                    float* emi = part.SelfIllum;
                    // #128: PartTriMesh::Mat @0x450960's remaining two instance terms, live. Lit-only because
                    // the decompile guards its three SetMaterialv calls behind IsEnabled(5) -- an unlit part
                    // gets a flat constant colour and the instance's alpha, which is all the record above
                    // carries.
                    var cm = AlloyCensus.GobDiffuseMul(tri);
                    var ea = AlloyCensus.GobEmissiveAdd(tri);
                    pf.AmbR = amb[0]; pf.AmbG = amb[1]; pf.AmbB = amb[2];
                    pf.DifR = dif[0] * cm.X; pf.DifG = dif[1] * cm.Y; pf.DifB = dif[2] * cm.Z;
                    pf.EmiR = emi[0] + ea.X; pf.EmiG = emi[1] + ea.Y; pf.EmiB = emi[2] + ea.Z;
                    pf.Slots = slots;
                }
                // #147: water reuses the material-colour slot for its BODY. The water stage reads none of
                // amb/dif/emi (its liquid colour is the diffuse's own average, #129), and the alternative --
                // an eighth vec4 -- would grow the stride for one class and force every override to be
                // re-emitted. Overwrites the lit terms above, which ran for a lit water batch.
                if (cls == (int)AlloyClass.Water)
                {
                    var wind = AreaWind();
                    // Engine wind units -> metres/second, so the shader's growth law is fed the quantity it
                    // is written in. See FrameSnapshot.WindScale for why this is a dial and not a constant.
                    pf.Water = true;
                    pf.BodySpeed = wind.Length() * FrameSnapshot.WindScale;
                    if (WaterBodies.Body(tri, wind, out float fetch, out float size, out float extent))
                    { pf.BodyFetch = fetch; pf.BodySize = size; pf.BodyExtent = extent; }
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
                rec |= WritePerFrame(r, in pf);
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
                        hh[0] = HandleFor(tid);
                        for (int t = 0; t < MapSlots; t++) hh[1 + t] = HandleFor(slotTid[t]);
                    }
                }
                if (rec) MarkRecordDirty(i); else _recStable++;
                _recGen[i] = _recEpoch; _recFrame[i] = _frameSeq;
            }
            if (FrameDump.Capturing)
                FrameDump.Row_(cls, lit, v, _oMat[i], mode, feat, dis, blend, slots, merge,
                               idxCount, _dOff[n], _dBase[n], r);
            _gLen[groups - 1]++;
            n++;
            if (_oGob[i] != IntPtr.Zero) _drawsGob++;
            // #151: an Alloy rigid INSTANCE part leaves the engine walk, and with it the walk-side shadow
            // capture -- its shadow has to be fed from here. Skinmeshes are already fed by PoseSkinned, which
            // has their solved matrices in hand; parts the caster CENSUS still carries (table-eligible) would
            // be double-recorded, so only the census's rejects recast here -- see CasterCensusCovers for what
            // #258 moved out of that set.
            if (_oGob[i] != IntPtr.Zero && _oBone[i] < 0
                && !AlloyCensus.CasterCensusCovers(_oEntry[i]))
                CasterPass.RecordOwnedInstance(tri, _oNode[i], _oVdo[i], _oItem[i], _oPrim[i]);
            AlloyCensus.OwnGen[_oEntry[i]] = PassGen;
        }
        // #126: where each pass's batch range ends. Read off the batches themselves rather than tracked
        // through the cut above -- the routed order is class-ordered by construction, so the first batch of a
        // later class IS the previous range's end, and a class that staged nothing leaves an empty range
        // rather than someone else's batches.
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = groups;
        for (int g = 0; g < groups; g++)
        {
            if (_gCls[g] > (byte)AlloyClass.Cutout && g < _gOpaqueEnd) _gOpaqueEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Blended && g < _gBlendEnd) _gBlendEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Water && g < _gWaterEnd) _gWaterEnd = g;
        }
        _drawN = n;
        if (dumping) FrameDump.Finish(groups, _log);
        return groups;
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
        _skyN = 0; _skyFirst = _drawN;
        IntPtr scene = SkyBox.Scene;
        // #91's rule again: only the enrolled world Scene's sky, never a GUI 3D panel's.
        if (scene == IntPtr.Zero || scene != _enrollScene) return;
        // #261: the engine's sky draw is only silent under the cutover (the shim swallows the draw funnels).
        // With ownframe=0 there is no colour mask left to suppress it with, so the owned sky stands aside
        // and the engine paints its own at its own step -- one pre-cutover frame, unchanged.
        if (!RspShim.Armed) return;
        Span<SkyBox.Role> roles = stackalloc SkyBox.Role[3];
        float* fog = stackalloc float[3];
        SkyBox.FogColor(scene, fog);
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
                bool tinted = SkyBox.FogTinted(p.Gob);
                float* tint = t3 + _skyN * 3;
                if (tinted) { tint[0] = fog[0]; tint[1] = fog[1]; tint[2] = fog[2]; }
                else { tint[0] = 1f; tint[1] = 1f; tint[2] = 1f; }
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
            if (staged <= 0) { _drawN = _skyFirst; _skyN = 0; return; }
        }
    }

    /// <summary>ADR 0011 step 3 (#125): the skybox, after the opaque set and before the blended one.
    /// <para><c>glDepthRange(1,1)</c> is what makes "after opaque" mean anything. The dome sits AT the
    /// camera, so its own depth is NEAR and it would pass the test everywhere -- which is exactly why vanilla
    /// paints over bucket 0. Forced to the far plane and tested LEQUAL (the value
    /// <see cref="OpaqueOwnedPass"/> leaves bound), it survives only where nothing has written depth, so
    /// every sky pixel the world covers is rejected before it shades.</para>
    /// <para><c>GL_DEPTH_CLAMP</c> because the matrices were captured under the ENGINE's sky projection
    /// (far 1000.0, <c>RenderSkyBoxes</c>) and are replayed under the world's: the dome can cross the world
    /// far plane, and clipped it would read as a hole at the horizon with nothing in the log.</para>
    /// <para>Depth range and clamp are 4.6-CONTEXT state the later passes share, so both are restored here
    /// rather than left for someone else to find.</para>
    /// <para>ponytail: the roles composite in LINEAR, and #261 deleted the display-space scratch that used
    /// to make them match vanilla exactly (ADR 0018: "there is no strand 3"). The settled sky is unaffected
    /// -- its role is <c>(SRC_ALPHA, ZERO)</c>, which never reads the destination -- but the cross-fade and
    /// the skyfade gob are genuine over-blends, and at partial alpha a linear over composites brighter than
    /// the engine's display-space one by <c>alpha^(1/2.2)/alpha</c> (1.5x at 0.5). Deliberate: the sky is
    /// Alloy's to author now and the frame it lands in is linear. The three alphas are on the period line
    /// (<see cref="SkyBox.NoteRoles"/>) precisely so this is a number and not a suspicion; if the horizon
    /// band ever reads wrong, the upgrade is a per-role alpha correction, not the scratch target back.</para>
    /// </summary>
    private static void SkyOwnedPass(Matrix4x4* vp, int* viewport)
    {
        if (_skyN == 0 || Gl.DepthRange == null) return;
        Gl.UseProgram(_prog46);
        Gl.Uniform1i(_locTex, 0);
        Gl.Uniform1i(_locViz, _lightViz);
        Gl.UniformMatrix4fv(_locVp, 1, 0, (float*)vp);
        UploadFeatureTerms(_locFeat, viewport);
        Gl.Uniform1f(_locCut, -1f);          // the engine draws the sky with the discard off
        // DEPTH_CLAMP because the composed matrices sit under the ENGINE's sky projection (RenderSkyBoxes
        // replaces it with its own near/far) and are drawn under the world's: the dome can cross the world
        // far plane, and clipped it reads as a hole at the horizon with nothing in the log.
        Gl.DepthMask(0); Gl.DepthRange(1.0, 1.0);
        Gl.Enable(Gl.DEPTH_CLAMP);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        int boundVao = -1, boundTex = -1;
        uint boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        fixed (float* t3 = _skTint)
        for (int i = 0; i < _skyN; i++)
        {
            uint src = BlendGl(_skSrc[i]), dst = BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (_skVao[i] != boundVao) { Gl.BindVertexArray(SkyVao(_skVao[i])); boundVao = _skVao[i]; }
            if (_skTex[i] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_skTex[i]); boundTex = _skTex[i]; }
            Gl.Uniform3fv(_locTint, 1, t3 + i * 3);
            int s = _skyFirst + i;
            Gl.Uniform1i(_locBase, s);
            Gl.MultiDrawElementsBaseVertex(_skMode[i], counts + s, GlUnsignedShort, (void**)(offs + s), 1, bases + s);
            _batches++; _draws++;
        }
        Gl.Disable(Gl.DEPTH_CLAMP);
        Gl.DepthRange(0.0, 1.0);
    }
}
