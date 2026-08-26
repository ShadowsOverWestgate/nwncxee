using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Services.Render;

/// <summary>#173: BATCH BUILDING. Revalidating every owned part against the live scene, routing it to this pass's class, sorting the blended remainder back-to-front, and staging the per-draw records and multidraw commands the issue phase submits. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class OwnedDrawService
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
    /// and clear per pass. If a scene ever lands enough blended parts to show on the [owned] line's colour
    /// time, that is the upgrade.</para></summary>
    private static int RouteParts()
    {
        int n = 0, nb = 0;
        int opaqueEnd = _clsEnd[(int)OwnedClass.Cutout];   // classes are contiguous: opaque then cutout
        for (int k = _clsStart[(int)OwnedClass.Opaque]; k < opaqueEnd; k++)
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
        for (int k = _clsStart[(int)OwnedClass.Blended]; k < _clsEnd[(int)OwnedClass.Blended]; k++)
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
        { _pass[n] = _blend[a]; _passCls[n] = (byte)OwnedClass.Blended; _passAlpha[n] = _blendAlpha[a]; n++; }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        for (int k = _clsStart[(int)OwnedClass.Water]; k < _clsEnd[(int)OwnedClass.Water]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)OwnedClass.Water;
            _passAlpha[n] = IdentityOk(i, tri) ? LiveAlpha(tri) : 1f;
            // #147: measured here rather than at enroll -- routing is where a water part is certainly live
            // and certainly has its node, and the registry ignores one it already holds.
            WaterBodies.Note(tri, _oNode[i]);
            n++;
        }
        for (int k = _clsStart[(int)OwnedClass.Additive]; k < _clsEnd[(int)OwnedClass.Additive]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)OwnedClass.Additive;
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
    /// <see cref="BuildBatches"/>.</para></summary>
    /// <summary>#145: ...and the entry must still be a LIVE recording of this part before any of that is
    /// read. The three compares below are reads of the part itself, which is only sound while the part
    /// exists -- a destroyed one can keep every field long enough to pass them, and then the gob chase in
    /// <see cref="LiveAlpha"/> walks a freelist word. <c>Part::~Part</c> is what makes this answer no.</summary>
    private static bool IdentityOk(int i, IntPtr tri)
        => BatchService.EntryLive(_oEntry[i], tri)
           && *(IntPtr*)(tri + PartNode) == _oNode[i] && *(IntPtr*)(tri + PartMaterial) == _oMat[i]
           && *(int*)(tri + PartTexRotate) == _oRotBits[i];

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
        if (!ok) log.Info("OwnedDrawService.SortSelfCheck: FAILED -- the blended order is not back-to-front");
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
    private static float PartAlpha(IntPtr tri) => *(float*)(tri + PartAlphaOff);

    /// <summary>#128: this frame's whole alpha multiplier for a part -- its own animated alpha, the fade
    /// factor of the tile it stands on, and the instance's two multiplies. Every term is read live and none
    /// is cached at capture, which is what the creature case demands: a gob can fade a whole creature into
    /// the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(IntPtr tri)
        => PartAlpha(tri) * BatchService.TileFadeFactor(tri) * BatchService.GobAlphaMul(tri);

    /// <summary>Squared distance from THIS pass's eye to the part's world centre -- the blended sort key.
    /// Derived exactly as the caster cull derives it and for the same reason (live-verified there): a
    /// matrix-class part's centre is its model matrix translation, and everything else is world-space
    /// geometry whose bbox centre already is the answer.</summary>
    private static float EyeDist2(int i)
    {
        float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i];
        if (_oMx[i])
        {
            float* m = (float*)(_oNode[i] + NodeMatrix);
            cx = m[12]; cy = m[13]; cz = m[14];
        }
        float dx = cx - _eyePos.X, dy = cy - _eyePos.Y, dz = cz - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Phase A of the owned block: walk this pass's order (<see cref="RouteParts"/>), revalidate
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
        // #134: routing walks the CLASS RANGES, not the ownership table -- a part whose class range was
        // never built simply never appears, and no skip site can report on it. Say so once.
        if (_watchDrawLeft > 0)
        {
            bool routedWatch = false;
            for (int k = 0; k < routed && !routedWatch; k++) routedWatch = BatchService.WatchedEntry(_oEntry[_pass[k]]);
            if (!routedWatch)
            {
                bool owned = false;
                for (int i = 0; i < _ownCount && !owned; i++)
                    owned = _oTri[i] != IntPtr.Zero && BatchService.WatchedEntry(_oEntry[i]);
                if (owned) { _watchDrawLeft--; _log?.Info("[dumpframe] DRAW never routed -- owned, but RouteParts did not include it"); }
            }
        }
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = 0;
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
            if (cls == (int)OwnedClass.Water && !_waterArmed) continue;
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            // Geometry the engine has stopped walking -- culled by its own visibility, while ownership
            // (rebuilt every EnrollEvery frames) still holds it. Drawing it was ~44% of the owned draw count
            // live, all of it invisible, and it is exactly what the one-owner audit was reporting as
            // UNMATCHED: not a double draw, a ghost. The engine's own sighting window is the predictor, and
            // a part that comes back simply goes unstamped for one frame and is drawn by the engine.
            if (!BatchService.SightedRecently(_oEntry[i]))
            {
                _skipUnseen++; _unseenBucket[_oBucket[i]]++;
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
            // #128: instance state the owned programs cannot express -- a highlight, a scene tint, a per-gob
            // env override. SKIPPED, not revoked: a highlight lasts exactly as long as the cursor sits on the
            // placeable, and revoking would spend a whole re-enrollment on a hover. Unstamped means the
            // iterator does not suppress it, so the engine's own walk draws it highlighted, which is vanilla.
            if (!BatchService.InstanceOwnable(tri))
            { _skipGob++; W(i, "skipped: instance state unownable this pass"); continue; }
            // #152 colour LOD. Stamped THEN skipped: the stamp suppresses the engine's own draw, so the part
            // is rendered by nobody -- handing it back would just move the cost, not remove it. Water is
            // exempt (#129's stamp invariant: a stamped water part must be one the copy machinery drew), and
            // a zero radius means "bounds unknown", never "small".
            // TILES are exempt too (TileData* at part+0x98, the CheckFadePlane discriminator): a small tile
            // part is a patch of ground or wall, and culling it is a HOLE in a surface. Free-standing
            // clutter -- placeables, gobs, decor, everything with no tile record -- pops out at a couple of
            // pixels, which is the artifact-free half of the trade. This is what makes SOWX_LOD_PX safe to
            // raise: the tileset shell of the area stays whole at any threshold.
            if (_lodPx > 0f && cls != (int)OwnedClass.Water && _oR[i] > 0f
                && *(IntPtr*)(tri + PartTile) == IntPtr.Zero)
            {
                float d2 = EyeDist2(i);
                float px = _oR[i] * _lodScale;
                if (px * px < _lodPx * _lodPx * d2)
                { _lodSkipped++; BatchService.OwnGen[_oEntry[i]] = PassGen; W(i, "skipped: sub-pixel LOD"); continue; }
            }
            // #126: a fading tile is no longer a reason to hand the part back. RouteParts has already moved
            // it into the blended run with the tile's factor folded into its alpha, which is what the engine
            // does with it in bucket 8 -- and bucket 8 is a bucket IterSight can suppress now.
            // #124: the camera dissolve is a FEATURE of the owned program now, not a reason to hand the part
            // back -- dissolving parts sit in bucket 1, which IterSight can suppress. #123's exemption still
            // stands: nokeyhole, roofkn and flowmap compile their FRAGMENT stage with KEYHOLING 0, so vanilla
            // draws them solid through a dissolve and so must we.
            // ...and only on a LIT part: every unlit fragment stage in base_shaders.bif compiles KEYHOLING 0
            // (fst, fst_sm, fsglu, fs, fstc), so vanilla draws an unlit part solid through the aperture.
            // Keeping the flag off them also keeps them out of the prepass exclusion below, which they would
            // otherwise pay for a discard their stage does not have.
            float dissolve = lit && !ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])
                             && BatchService.KeyholeDissolves(tri) ? 1f : 0f;
            if (dissolve != 0f) _ownDissolve++;
            // MatStatic (Ghidra @0x450560): ambient = node+0xf4, diffuse = node+0xe8, emissive = part+0x7c.
            // ponytail: MatStatic also multiplies ambient+diffuse by AurGetTileColor() when the part's tile
            // record (part+0x98, +0x54) is set. Not reproduced -- add it if tiles read the wrong hue.
            IntPtr mdl = lit ? *(IntPtr*)(tri + PartMdlNode) : IntPtr.Zero;
            if (lit && mdl == IntPtr.Zero) { _skipNoMdl++; W(i, "skipped: no mdl node"); continue; }

            // texture: re-fetch per frame -- CAurTexture* is BORROWED, freed on area transitions (never
            // cache), and the per-frame fetch is exactly what keeps animated textures cycling
            long aur = _getTexture(_oMat[i], 0);
            if (aur == 0) { _skipTex++; W(i, "skipped: no diffuse texture"); continue; }
            int tid = AurTex.Tid((IntPtr)aur);
            if (tid == 0) { _skipTex++; W(i, "skipped: diffuse has no GL id"); continue; }

            // #120: the same re-fetch for whichever of the PBR slots this material declared. A slot that
            // declared a map but resolves to nothing this frame drops out of the mask rather than leaving
            // the shader sampling an empty unit.
            int slots = 0;
            for (int s = 0; s < MapSlots; s++)
            {
                slotTid[s] = 0;
                if (lit && (_oSlots[i] & SlotBit(s)) != 0)
                {
                    long a = _getTexture(_oMat[i], s + 1);
                    int t = AurTex.Tid((IntPtr)a);
                    if (t != 0) { slotTid[s] = t; slots |= SlotBit(s); }
                }
            }
            if (lit) slots |= _oSlots[i] & EnvSlot;   // #121: no live texture to re-fetch, just the flag
            // #146's zero-content test path, see _aoTest. Stands in for a texture7 nothing ships yet.
            if (lit && _aoTest) { slotTid[AoSlot] = tid; slots |= AoBit; }

            // index range re-read live from the VDO item tables (Repack revokes before they move)
            IntPtr vdo = _oVdo[i];
            long elem = (long)*(IntPtr*)(vdo + VdoElemItems);
            long vert = (long)*(IntPtr*)(vdo + VdoVertItems);
            int item = _oItem[i];
            if (elem == 0 || vert == 0 || item < 0
                || item >= *(int*)(vdo + VdoElemCount) || item >= *(int*)(vdo + VdoVertCount))
            { _skipRange++; W(i, "skipped: item out of range"); continue; }
            int idxCount = *(int*)(elem + item * 8 + 4);
            if (idxCount <= 0) { _skipRange++; W(i, "skipped: empty index range"); continue; }

            int v = _oVao[i];
            if (!EnsureVao(v)) { _skipVao++; W(i, "skipped: no VAO"); continue; }
            uint mode = _primTable[_oPrim[i]];
            // #123: the feature joins the batch key. Not for binding -- it rides in the per-draw record --
            // but so the depth prepass can drop a whole batch: a displacing feature must not write its
            // undisplaced depth, and the prepass has no per-draw granularity.
            int feat = (int)_oFeat[i * 4];
            // #137: a posed skinmesh overrides whatever content feature the material named -- see
            // ShaderFeature.Skin for why the two share the field, and why that trade is safe on shipped
            // content. Joining the cut key here is what lets the prepass drop the whole skinned batch.
            if (_oBone[i] >= 0) feat = (int)ShaderFeature.Skin;
            // #124: ...and the dissolve bit joins it for the same reason -- the keyhole stage discards.
            bool dis = dissolve != 0f;
            // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
            int blend = OwnedClasses.Blends(cls) ? (_oSrc[i] << 8) | _oDst[i] : 0;
            // #153: under bindless a LIT batch no longer splits on its textures -- the handles ride the
            // record, so one multidraw spans every texture in the class. Feat/dis stay in the key only for
            // the depth prepass's whole-batch drop; unlit parts keep the bind path and the full key.
            bool merge = _bindless && lit && cls != (int)OwnedClass.Water;
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
            if (BatchService.WatchTex[_oEntry[i]]) _watchSubmitted++;
            _dCount[n] = idxCount;
            _dOff[n] = (nint)((long)(uint)*(int*)(elem + item * 8) * 2);
            _dBase[n] = *(int*)(vert + item * 8);

            // the per-draw record. The model matrix drives BOTH the world position the light loop shades at
            // and the world normal (mat3 of the same GLSL-visible matrix is the correct normal transform here
            // -- the raw bytes already read as the transpose), and the shaders reach uVP * model for clip.
            float* r = data + n * DrawFloats;
            *(Matrix4x4*)r = _oMx[i] ? *(Matrix4x4*)(_oNode[i] + NodeMatrix) : Matrix4x4.Identity;
            // rot.z carries #124's dissolve flag and rot.w #126's per-draw alpha multiplier (the part's own
            // alpha, times the tile fade factor where one applies): the record already had two dead floats
            // here, so both folds cost no stride growth and leave RecordSafe's vec4 count alone.
            r[16] = _oCos[i]; r[17] = _oSin[i]; r[18] = dissolve; r[19] = _passAlpha[k];
            if (lit)
            {
                float* amb = (float*)(mdl + MdlAmbient);
                float* dif = (float*)(mdl + MdlDiffuse);
                float* emi = (float*)(tri + PartSelfIllum);
                r[20] = amb[0]; r[21] = amb[1]; r[22] = amb[2]; r[23] = 1f;
                // #128: PartTriMesh::Mat @0x450960's remaining two instance terms, live. Lit-only because
                // the decompile guards its three SetMaterialv calls behind IsEnabled(5) -- an unlit part gets
                // a flat constant colour and the instance's alpha, which is all the record above carries.
                var cm = BatchService.GobDiffuseMul(tri);
                var ea = BatchService.GobEmissiveAdd(tri);
                r[24] = dif[0] * cm.X; r[25] = dif[1] * cm.Y; r[26] = dif[2] * cm.Z; r[27] = 1f;
                r[28] = emi[0] + ea.X; r[29] = emi[1] + ea.Y; r[30] = emi[2] + ea.Z; r[31] = 1f;
                int o = i * 8;
                r[32] = _oParam[o]; r[33] = _oParam[o + 1]; r[34] = _oParam[o + 2]; r[35] = slots;
                r[36] = _oParam[o + 4]; r[37] = _oParam[o + 5]; r[38] = _oParam[o + 6]; r[39] = _oParam[o + 7];
            }
            // #147: water reuses the material-colour slot for its BODY. The water stage reads none of
            // amb/dif/emi (its liquid colour is the diffuse's own average, #129), and the alternative --
            // an eighth vec4 -- would grow the stride for one class and force every override to be
            // re-emitted. Overwrites the lit branch above, which ran for a lit water batch.
            if (cls == (int)OwnedClass.Water)
            {
                var wind = AreaWind();
                // Engine wind units -> metres/second, so the shader's growth law is fed the quantity it is
                // written in. See FrameSnapshot.WindScale for why this is a dial and not a constant.
                float speed = wind.Length() * FrameSnapshot.WindScale;
                if (WaterBodies.Body(tri, wind, out float fetch, out float size, out float extent))
                { r[24] = fetch; r[25] = size; r[26] = extent; r[27] = speed; }
                else { r[24] = 0f; r[25] = 0f; r[26] = 0f; r[27] = speed; }
            }
            // #123: outside the lit branch -- the FULLBRIGHT vertex stage switches on it too, so an unlit
            // wind part still sways. Only the fragment-side features (the roof anchor discard, the flow-map
            // slide) are lit-only, and neither of those parts is ever unlit.
            int fo = i * 4;
            r[40] = _oFeat[fo]; r[41] = _oFeat[fo + 1]; r[42] = _oFeat[fo + 2]; r[43] = _oFeat[fo + 3];
            // #137: feat.y is this draw's bone base -- the MATRIX index of its first bone in the block, so
            // the stage indexes it with a single add. Written after the copy above, which it replaces.
            if (_oBone[i] >= 0) { r[40] = (int)ShaderFeature.Skin; r[41] = _oBone[i] * BoneCount; }
            // #153: this record's textures as resident handles -- diffuse at 0, material slots 1..7 in the
            // shader's macro order (slotTid[k] = material texture k+1). Only for lit records; unlit parts
            // draw on the bind path and never read the block.
            if (merge)
            {
                fixed (ulong* th = _dTexH)
                {
                    ulong* hh = th + (long)n * 8;
                    hh[0] = HandleFor(tid);
                    for (int t = 0; t < MapSlots; t++) hh[1 + t] = HandleFor(slotTid[t]);
                }
            }
            _gLen[groups - 1]++;
            n++;
            // #151: an owned rigid INSTANCE part leaves the engine walk, and with it the walk-side shadow
            // capture -- its shadow has to be fed from here. Skinmeshes are already fed by PoseSkinned's
            // bracket; parts the caster CENSUS still carries (table-eligible) would be double-recorded, so
            // only the census's rejects -- the moving creature pieces that probation killed -- recast here.
            if (_oGob[i] != IntPtr.Zero && _oBone[i] < 0
                && !BatchService.CasterCensusCovers(_oEntry[i]))
                CasterPass.RecordOwnedInstance(tri, _oNode[i], _oVdo[i], _oItem[i], _oPrim[i]);
            BatchService.OwnGen[_oEntry[i]] = PassGen;
        }
        // #126: where each pass's batch range ends. Read off the batches themselves rather than tracked
        // through the cut above -- the routed order is class-ordered by construction, so the first batch of a
        // later class IS the previous range's end, and a class that staged nothing leaves an empty range
        // rather than someone else's batches.
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = groups;
        for (int g = 0; g < groups; g++)
        {
            if (_gCls[g] > (byte)OwnedClass.Cutout && g < _gOpaqueEnd) _gOpaqueEnd = g;
            if (_gCls[g] > (byte)OwnedClass.Blended && g < _gBlendEnd) _gBlendEnd = g;
            if (_gCls[g] > (byte)OwnedClass.Water && g < _gWaterEnd) _gWaterEnd = g;
        }
        _drawN = n;
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
        Span<SkyBox.Role> roles = stackalloc SkyBox.Role[3];
        int nr = SkyBox.Roles(scene, roles);
        fixed (float* data = _dData)
        for (int r = 0; r < nr; r++)
        {
            int staged = 0;
            for (int i = 0; i < SkyBox.Count; i++)
            {
                ref SkyBox.Part p = ref SkyBox.At(i);
                if (p.Gob != roles[r].Gob) continue;
                if (p.Vao < 0 || !SkyBox.Fresh(i) || _drawN >= MaxOwned || _skyN >= MaxSkyDraws)
                { staged = -1; break; }
                // borrowed CAurTexture*, re-fetched per frame like every other owned draw
                long aur = _getTexture(p.Mat, 0);
                int tid = AurTex.Tid((IntPtr)aur);
                IntPtr vdo = p.Vdo;
                long elem = (long)*(IntPtr*)(vdo + VdoElemItems);
                long vert = (long)*(IntPtr*)(vdo + VdoVertItems);
                if (tid == 0 || elem == 0 || vert == 0 || p.Item < 0
                    || p.Item >= *(int*)(vdo + VdoElemCount) || p.Item >= *(int*)(vdo + VdoVertCount))
                { staged = -1; break; }
                int idxCount = *(int*)(elem + p.Item * 8 + 4);
                if (idxCount <= 0 || !EnsureVao(p.Vao)) { staged = -1; break; }

                int n = _drawN;
                _dCount[n] = idxCount;
                _dOff[n] = (nint)((long)(uint)*(int*)(elem + p.Item * 8) * 2);
                _dBase[n] = *(int*)(vert + p.Item * 8);
                float* rec = data + n * DrawFloats;
                *(Matrix4x4*)rec = p.Model;
                // identity texture rotation, no dissolve, the role's alpha; no feature, and the lit block is
                // never read because the sky takes the fullbright program (the engine draws it Disable(5)).
                rec[16] = 1f; rec[17] = 0f; rec[18] = 0f; rec[19] = roles[r].Alpha;
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
    /// rather than left for someone else to find.</para></summary>
    private static void SkyOwnedPass(Matrix4x4* vp, int* viewport)
    {
        if (_skyN == 0 || Gl.DepthRange == null) return;
        Gl.UseProgram(_prog46);
        Gl.Uniform1i(_locTex, 0);
        Gl.Uniform1i(_locViz, _lightViz);
        Gl.UniformMatrix4fv(_locVp, 1, 0, (float*)vp);
        UploadFeatureTerms(_locFeat, viewport);
        Gl.Uniform1f(_locCut, -1f);          // the engine draws the sky with the discard off
        Gl.DepthMask(0);
        Gl.DepthRange(1.0, 1.0);
        Gl.Enable(Gl.DEPTH_CLAMP);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        int boundVao = -1, boundTex = -1;
        uint boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int i = 0; i < _skyN; i++)
        {
            uint src = BlendGl(_skSrc[i]), dst = BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (_skVao[i] != boundVao) { Gl.BindVertexArray(_vVao46[_skVao[i]]); boundVao = _skVao[i]; }
            if (_skTex[i] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_skTex[i]); boundTex = _skTex[i]; }
            int s = _skyFirst + i;
            Gl.Uniform1i(_locBase, s);
            Gl.MultiDrawElementsBaseVertex(_skMode[i], counts + s, GlUnsignedShort, (void**)(offs + s), 1, bases + s);
            _batches++; _draws++;
        }
        Gl.Disable(Gl.DEPTH_CLAMP);
        Gl.DepthRange(0.0, 1.0);
        // The vanilla site reads this to decide whether to colour-mask the engine's own copy: it is set only
        // after the owned sky actually reached the GPU this pass, never on intent.
        SkyBox.MarkDrawn(PassGen);
    }
}
