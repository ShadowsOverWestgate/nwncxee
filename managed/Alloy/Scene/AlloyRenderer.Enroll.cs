using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#173: the OWNERSHIP WALK. One pass over the scene's parts decides what the Alloy renderer takes this generation -- material read, feature classification, texture and VDO interning, the per-VDO attribute harvest and the 4.6-side VAO built over it -- and revocation is its inverse. Everything here is cold: it runs on an enrollment edge, never per frame.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Revoke one slot: the part reverts to engine drawing from its next iterator sighting, and the
    /// slot joins the free list for a later newcomer. Idempotent -- an already-empty slot is left alone, so
    /// no sweep needs a guard of its own and no slot can enter the free list twice.</summary>
    /// <summary>CX-120: may this slot's ENGINE pointers be dereferenced? A live slot is not the same thing:
    /// a DB slot's <c>_oTri</c> is a deliberately non-canonical sentinel (so an unbranched deref faults
    /// loudly rather than reading garbage) and its <c>_oVdo</c>/<c>_oNode</c>/<c>_oMat</c> are null. Every
    /// enrolment-only walk tests THIS, not <c>_oTri != Zero</c> -- the distinction is what the first DB-mode
    /// EndFrame died on twice, in Revoke and then in MeshCensus.</summary>
    private static bool EngineSlot(int i) => _oTri[i] != IntPtr.Zero && _oDbPart[i] < 0;

    /// <summary>#308: retire a slot WITHOUT touching its entry's OwnFlag or the revoke counter. The
    /// enrollment sweeps need this variant: a PartTable entry can change hands between enrollments, so by
    /// sweep time <c>_oEntry[i]</c> may name an entry a DIFFERENT slot claimed this scan -- clearing its
    /// flag would un-suppress a part Alloy is about to draw. The scan already bulk-cleared every flag and
    /// re-set the claimed ones, so the sweeps have no flag left to clear.</summary>
    private static void FreeSlot(int i)
    {
        // CX-120 (R1-26): a vacated slot carries no scene-database tenant either, whichever mode freed it.
        _oDbPart[i] = -1; _oDbRecipe[i] = -1;
        _oTri[i] = IntPtr.Zero;
        // CX-9: PoseSkinned only walks the skinned list, which drops this slot at the next rebuild
        int b = _oBone[i];
        if ((uint)b < (uint)_boneTop && _boneOwner[b] == i) FreeBoneSlot(b);
        _oBone[i] = -1;
        SetCastOk(i, false);   // #328: a vacated slot casts nothing, and the shadow side re-collects on it
        LightmapBake.Vacate(i);   // #674: its bake record reads unbaked
        _ownLive--;
        _freeSlots[_freeCount++] = i;
    }

    /// <summary>#308: stable ownership slots. A part KEEPS its slot across enrollments -- the slot index is
    /// the identity every persistent, slot-keyed table hangs off (the per-draw records, both sphere tables,
    /// the shadow tenancy stamps, the cull buffers), and the dense renumbering that used to happen here was
    /// the tenancy flap behind the shadow flicker: one part entering or leaving the candidate set shifted
    /// every slot scanned after it, each shifted slot was refused by the same-frame shadow bracket for want
    /// of a record, and the flap ghosted its views on the way out and re-baked them on the way back.
    /// <para>The hash maps a live tenant (its PartTriMesh*) to its slot for the next scan, generation-stamped
    /// so it never needs clearing; the free list feeds newcomers the slots that sweeps vacated; the claim
    /// stamp is how the post-scan sweep tells a re-claimed slot from a departed one.</para></summary>
    private static readonly int[] _oClaim = new int[MaxOwned];
    private static readonly int[] _freeSlots = new int[MaxOwned];
    private static int _freeCount, _ownLive;
    // MaxOwned MUST stay below 1<<SlotHashBits or HashLiveSlots's probe loop never finds an empty bucket
    private const int SlotHashBits = 19;   // 1<<19 = 524288, >= 3x MaxOwned: probe chains stay short
    private static readonly int[] _shSlot = new int[1 << SlotHashBits];
    private static readonly int[] _shGen = new int[1 << SlotHashBits];

    private static int SlotHash(IntPtr tri)
        => (int)((ulong)(long)tri * 0x9E3779B97F4A7C15UL >> (64 - SlotHashBits));

    /// <summary>A newcomer's slot: a vacated one if any sweep left one, the next high-water slot otherwise.
    /// Never overflows past MaxOwned -- the capacity gate refuses on the LIVE count, and every slot below the
    /// high-water mark is either live or on the free list.
    /// <para>#329: only slots below <see cref="_freeAvail"/> are offered. The walk now spans frames, and a
    /// part death between two of them (NotePartDeath -> Revoke) pushes a slot ABOVE the floor; handing that
    /// slot out inside the same walk would re-tenant it in the scan that vacated it, which is the case
    /// #308's lapse-in-place shadow story rules out. The hole left below the floor is filled from the top.</para></summary>
    private static int AllocSlot()
    {
        if (_freeAvail == 0) return _ownCount++;
        int s = _freeSlots[--_freeAvail];
        _freeSlots[_freeAvail] = _freeSlots[--_freeCount];
        return s;
    }

    /// <summary>#329: the free-list floor a walk snapshots at its begin -- see <see cref="AllocSlot"/>.</summary>
    private static int _freeAvail;

    /// <summary>#329: the free-list floor, checked once at Initialize on the empty table: a walk begins over
    /// two vacated slots, a death mid-walk frees a third, and only the first two may be handed out.</summary>
    internal static void EnrollSelfCheck(ExtenderLog log)
    {
        _freeSlots[0] = 7; _freeSlots[1] = 8; _freeCount = 2; _freeAvail = 2;
        _freeSlots[_freeCount++] = 9;
        bool ok = AllocSlot() == 8 && AllocSlot() == 7 && AllocSlot() == _ownCount - 1
                  && _freeCount == 1 && _freeSlots[0] == 9 && _freeAvail == 0;
        log.Info(ok ? "AlloyRenderer.EnrollSelfCheck: passed" : "AlloyRenderer.EnrollSelfCheck: FAILED -- a mid-walk vacated slot was re-offered inside the walk");
        _freeCount = 0; _freeAvail = 0; _ownCount = 0;

    }

    private static void RevokeAll(string why)
    {
        // #209: above the empty-table guard, because the ownership count says what ALLOY took and nothing
        // about what the passes captured -- and for beams, particles and grass this walk is the only
        // teardown there is. See docs/owned-resource-lifetimes.md.
        Issue.WipeHandles();
        // #212: above the guard for the same reason the registry walk is. The registry's Revoke deletes the
        // owned buffer copies, and the VAOs built over them are only invalidated here -- an enrollment that
        // took nothing (_ownCount == 0) with slots left from an earlier one would otherwise keep binding a
        // VAO whose buffers were just freed, permanently, with the map empty so no upload can re-copy them.
        // The VAO cache keys on VDO pointers that may now be freed: retire every slot for reuse. The GL
        // objects can only be deleted inside the bracket, so the rebuild happens there (EnsureVao).
        PassRegistry.Global.Revoke();
        CullPass.Revoke();
        CycleTextures.Clear();
        // The slot space belongs to the FEED: every slot is joined to a live part through SceneDb.PartSlot and is
        // released when the game destroys the part, never here. The GL-side teardown above is all a revoke is.
        if (_ownCount > 0) _dbRevokeAll++;
    }

    /// <summary>#329: the walk's atomic tail -- the sweeps, the handle wipe and the order rebuild, all in
    /// one frame, so the census the draw side reads goes from one complete state to the next.</summary>
    /// <summary>The four-pass radix over every live slot: texture key, feature/primitive, VAO sort key, class.
    /// O(live) integer passes -- cheap enough to re-run after an incremental enrolment too.</summary>
    private static void RebuildOrder()
    {
        _litCount = 0; _skinCount = 0;
        Array.Clear(_sortCount, 0, MaxVdos * 4);
        Array.Clear(_texSortCount, 0, MaxMats);
        Array.Clear(_featPrimCount);
        Array.Clear(_clsCount); Array.Clear(_clsStart); Array.Clear(_clsEnd);
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            _sortCount[SortKey(i)]++;
            _texSortCount[_oTexKey[i]]++;
            _featPrimCount[FeatPrimKey(i)]++;
            _clsCount[_oCls[i]]++;
            if (_oLit[i]) _litCount++;
            if (DbSlot(i) ? (DrawRecipe.Flags(_oDbRecipe[i]) & RecipeFlag.Skinned) != 0 : _vSkin[_oVao[i]])
                _skinOrder[_skinCount++] = i;
        }
        // Four-digit LSD radix into _order, every digit counting-sorted (stable): texture, then #327's
        // (feature, primitive), then (lit, #292's no-cull bit, VDO), then #126's class. The result walks
        // grouped by CLASS -- so each pass owns a contiguous batch range -- and inside a class by (lit, cull,
        // VDO, feature, primitive) with equal textures contiguous, which is the whole enrollment-static half
        // of the batch cut key. The draw loop then cuts a batch on a key change instead of searching for one;
        // the one per-frame term left, the dissolve bit, is RouteParts's partition.
        int at = 0;
        for (int k = 0; k < MaxMats; k++) { int n = _texSortCount[k]; _texSortCount[k] = at; at += n; }
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) _orderTmp[_texSortCount[_oTexKey[i]]++] = i;
        int live = at;
        at = 0;
        for (int k = 0; k < FeatPrimKeys; k++) { int n = _featPrimCount[k]; _featPrimCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_featPrimCount[FeatPrimKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < MaxVdos * 4; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _order[k]; _orderTmp[_sortCount[SortKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < AlloyClasses.Count; k++) { int n = _clsCount[k]; _clsStart[k] = at; _clsCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_clsCount[_oCls[i]]++] = i; }
        // ...and the cursor each class's fill left behind IS its end index.
        for (int k = 0; k < AlloyClasses.Count; k++) _clsEnd[k] = _clsCount[k];
        _orderCount = live;
        // CX-104: the area index rides the same edge. Enrolment is the only thing that changes which slot holds
        // which part, so the grid is rebuilt here and never maintained incrementally.
        BuildIndex();
    }

    /// <summary>Counting-sort key: lit parts occupy the upper half so one program bind covers each half.</summary>
    /// <summary>The middle radix digit: (lit, no-cull, VDO). #292 put the cull bit UNDER the lit one -- see
    /// <see cref="SortKeyOf"/> -- so that inside a class the two-sided parts of each lighting mode run
    /// contiguously and the cut in <c>BuildBatches</c> flips face culling at most twice, without ever
    /// splitting a program's run in half.</summary>
    private static int SortKey(int i)
        => SortKeyOf(_oNoCull[i], _oLit[i], _oDbPart[i] >= 0 ? DbVaoKey(i) : _oVao[i]);

    /// <summary>#327: the (feature, primitive) radix digit, with the skin override BuildBatches applies to
    /// the feature. A fresh tenant is unposed at enrollment (its bone slot lands on the first pose after),
    /// so a skinmesh sorts under its content feature until the next enrollment -- one extra cut, briefly, on
    /// the rare part class that is skinned at all.</summary>
    private static int FeatPrimKey(int i)
        => FeatPrimKeyOf(_oBone[i] >= 0 ? (int)ShaderFeature.Skin : (int)_oFeat[i * 4], _oPrim[i]);

    /// <summary>#120: harvest what the part's .mtr declared -- which of the extra texture slots carry a
    /// map, and the four stock PBR parameters by name (plus, #123, the parameters of whichever custom shader
    /// <paramref name="feat"/> named). Deliberately reads the STOCK vocabulary
    /// (<c>Specularity</c>, <c>Roughness</c>, <c>Metallicness</c>, <c>CustomSpecularColor</c>) so content
    /// authored for the engine's own shaders works here unchanged; a name we do not know is left alone for
    /// whoever does. Zero means "not declared" in the engine's own shaders too, so it is what the synthesis
    /// path in the fragment stage keys on.
    /// <para>Cold: one call per enrolled part per enrollment. Material parameters are parsed once when the
    /// .mtr loads and do not animate, so nothing re-reads them per frame.</para></summary>
    /// <summary>Mask bit of map slot <paramref name="s"/>: its index, except for the AO slot -- see
    /// <see cref="AoBit"/> for why that one is out of line.</summary>
    internal static int SlotBit(int s) => s == AoSlot ? AoBit : 1 << s;

    /// <summary>Fold one <c>List&lt;MaterialParameter&gt;</c> into the staged PBR pair at
    /// <paramref name="o"/> and the #123 feature vec4 at <paramref name="f"/>, matching the stock names and
    /// this part's feature vocabulary. Later calls override earlier ones.</summary>
    private static void Apply(IntPtr list, int count, int o, int f)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        int feat = (int)_oFeat[f];
        var first = new MaterialParameter(list);
        for (int k = 0; k < count; k++)
        {
            MaterialParameter mp = first[k];
            byte* rec = (byte*)mp.Ptr;
            // The value is a UNION its own discriminator decides the type of, and a declaration may state
            // several components, so it is reached as a pointer rather than as the one float it is typed as.
            float* p = (float*)Unsafe.AsPointer(ref mp.Value);
            int comps = mp.Comps;
            // #123: iRoofInterior is the one int-valued parameter any of this reads, and the roof shaders
            // compare it to 1. Taken before the float-only gate below, where it would be skipped. The value
            // sits at the SAME +0x44 the floats do -- BindShaderParameters @0x4c5480 sends the type-1 branch
            // to SetCustomParameterValuei(prog, loc, *(int*)(rec + 0x44)), the type-0 branch to
            // SetCustomParameterValuef with the same address as a float*. Only the read type differs.
            if (mp.Type != 0)
            {
                if (feat == (int)ShaderFeature.RoofKn || feat == (int)ShaderFeature.RoofKnM)
                    if (NameIs(rec, "iRoofInterior") && comps >= 1) _oFeat[f + 3] = *(int*)p;
                // ADR 0026: CxOrm is a flag, and a flag is naturally written `parameter int`
                if (NameIs(rec, "CxOrm") && comps >= 1) _oParam[o + ParamExt + 9] = *(int*)p != 0 ? 1f : 0f;
                continue;                                     // otherwise: 1 = int-valued, none of ours are
            }
            // #123: the feature parameters, by the names the seven community shaders declare. Which three
            // land in the record depends on the feature, so one vec4 covers all of them.
            switch (feat)
            {
                case (int)ShaderFeature.WindGrass:
                case (int)ShaderFeature.WindLeaf:
                case (int)ShaderFeature.WindPlant:
                    if (NameIs(rec, "fSwaySpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fSwayStrength") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    if (NameIs(rec, "fTilesetMeshOffsetZ") && comps >= 1) { _oFeat[f + 3] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.RoofKn:
                case (int)ShaderFeature.RoofKnM:
                    if (NameIs(rec, "fRoofOffsetZ") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fRoofCameraClipZ") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.Flowmap:
                    if (NameIs(rec, "fSlideSpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    break;
            }
            // ADR 0026: the Alloy vocabulary -- glTF's metal/rough core and its clearcoat, sheen and
            // emissive-strength extensions under Cx names. Vanilla parses these, fails to link each to a
            // uniform once at load (one log line) and never binds them: the stock parameters stay the
            // material's vanilla view, these its Alloy one.
            if (ReadExt(rec, p, comps, o + ParamExt)) continue;
            if (NameIs(rec, "Specularity") && comps >= 1) _oParam[o] = p[0];
            else if (NameIs(rec, "Roughness") && comps >= 1) _oParam[o + 1] = p[0];
            else if (NameIs(rec, "Metallicness") && comps >= 1) _oParam[o + 2] = p[0];
            else if (NameIs(rec, "CustomSpecularColor") && comps >= 3)
            {
                _oParam[o + 4] = p[0]; _oParam[o + 5] = p[1]; _oParam[o + 6] = p[2];
                // Black IS the engine's "no custom colour" sentinel (inc_material.shd compares against
                // COLOR_BLACK), so a declared black must not turn every highlight off.
                _oParam[o + 7] = p[0] != 0f || p[1] != 0f || p[2] != 0f ? 1f : 0f;
            }
        }
    }

    /// <summary>ADR 0026: one MaterialParameter against the Cx vocabulary, into the three extension vec4s at
    /// <paramref name="x"/>: (CxMetallic, CxRoughness, CxClearcoat, CxClearcoatRoughness), (CxSheenColor.rgb,
    /// CxSheenRoughness), (CxEmissiveStrength, CxOrm, _, _). Values are clamped to their legal range here, so
    /// a typo'd 50 reads as the maximum rather than as an HDR blow-out. True = the name was one of ours.</summary>
    private static bool ReadExt(byte* rec, float* p, int comps, int x)
    {
        if (comps < 1) return false;
        float v = Math.Clamp(p[0], 0f, 1f);
        if (NameIs(rec, "CxMetallic")) _oParam[x] = v;
        else if (NameIs(rec, "CxRoughness")) _oParam[x + 1] = v;
        else if (NameIs(rec, "CxClearcoat")) _oParam[x + 2] = v;
        else if (NameIs(rec, "CxClearcoatRoughness")) _oParam[x + 3] = v;
        else if (NameIs(rec, "CxSheenColor") && comps >= 3)
        { _oParam[x + 4] = v; _oParam[x + 5] = Math.Clamp(p[1], 0f, 1f); _oParam[x + 6] = Math.Clamp(p[2], 0f, 1f); }
        else if (NameIs(rec, "CxSheenRoughness")) _oParam[x + 7] = v;
        else if (NameIs(rec, "CxEmissiveStrength")) _oParam[x + 8] = Math.Clamp(p[0], 0f, MaxEmissiveStrength);
        else if (NameIs(rec, "CxOrm")) _oParam[x + 9] = p[0] > 0.5f ? 1f : 0f;
        else return false;
        return true;
    }

    /// <summary>ADR 0026: the extension floats at <paramref name="x"/> before a material is read. -1 = "not
    /// declared" for every value a 0 is legal for (a dielectric, a mirror); the layer weights and the sheen
    /// colour are 0 = absent, which is also their default.</summary>
    private static void ResetExt(int x)
    {
        for (int k = 0; k < ParamFloats - ParamExt; k++) _oParam[x + k] = 0f;
        _oParam[x] = _oParam[x + 1] = _oParam[x + 3] = -1f;   // CxMetallic, CxRoughness, CxClearcoatRoughness
        _oParam[x + 7] = _oParam[x + 8] = -1f;                // CxSheenRoughness, CxEmissiveStrength
    }

    /// <summary>ADR 0026, off-device: a forged <c>MaterialParameter</c> list through <see cref="Apply"/> -- a
    /// declared 0 stays 0 (a dielectric is not "undeclared"), an absent name keeps its -1, an int-typed
    /// <c>CxOrm</c> counts, an out-of-range value clamps, and the stock names still land where they did.
    /// Uses slot 0 as scratch, so it runs before anything enrolls.</summary>
    internal static bool MaterialExtSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.MaterialExtSelfCheck");
        const int n = 6;
        byte* list = stackalloc byte[n * EngineLayout.ParamStride];   // a record SIZE: the forged list is n records long
        new Span<byte>(list, n * EngineLayout.ParamStride).Clear();
        PutParam(list, 0, "CxMetallic", 0, 1, 0f);
        PutParam(list, 1, "cxclearcoat", 0, 1, 2f);
        PutParam(list, 2, "CxSheenColor", 0, 3, 0.2f, 0.3f, 0.4f);
        PutParam(list, 3, "CxOrm", 1, 1, 0f);
        MaterialParameter orm = new MaterialParameter((IntPtr)list)[3];
        *(int*)Unsafe.AsPointer(ref orm.Value) = 1;      // type 1 reads the value union as an int
        PutParam(list, 4, "Specularity", 0, 1, 0.5f);
        PutParam(list, 5, "CxEmissiveStrengthX", 0, 1, 7f);
        int o = 0, x = ParamExt;
        _oFeat[0] = 0f;
        _oParam[0] = _oParam[1] = _oParam[2] = 0f;
        ResetExt(x);
        Apply((IntPtr)list, n, o, 0);
        check.Expect(_oParam[x] == 0f, "a declared CxMetallic 0 reads as undeclared");
        check.Expect(_oParam[x + 1] == -1f, "an absent CxRoughness lost its -1");
        check.Expect(_oParam[x + 2] == 1f, "CxClearcoat is not case-folded and clamped to 1");
        check.Expect(_oParam[x + 4] == 0.2f && _oParam[x + 5] == 0.3f && _oParam[x + 6] == 0.4f, "CxSheenColor's three components");
        check.Expect(_oParam[x + 9] == 1f, "an int-typed CxOrm 1 is not read");
        check.Expect(_oParam[x + 8] == -1f, "a name that only starts with CxEmissiveStrength matched it");
        check.Expect(_oParam[o] == 0.5f, "the stock Specularity no longer lands in pbr.x");
        for (int k = 0; k < ParamFloats; k++) _oParam[k] = 0f;
        return check.Ok;
    }

    /// <summary>One forged float/int record for <see cref="MaterialExtSelfCheck"/>.</summary>
    private static void PutParam(byte* list, int k, string name, int type, int comps, float a, float b = 0f, float c = 0f)
    {
        MaterialParameter mp = new MaterialParameter((IntPtr)list)[k];
        byte* rec = (byte*)mp.Ptr;
        for (int i = 0; i < name.Length; i++) rec[i] = (byte)name[i];
        float* v = (float*)Unsafe.AsPointer(ref mp.Value);
        v[0] = a; v[1] = b; v[2] = c;
        mp.Type = type;
        mp.Comps = (ushort)comps;
    }

    /// <summary>The ceiling on <c>CxEmissiveStrength</c>: well into bloom territory, short of the RGBA16F
    /// target's 65504 once an 8-bit illumination texel of 1.0 is multiplied through.</summary>
    private const float MaxEmissiveStrength = 100f;

    /// <summary>Case-insensitive ASCII compare of a MaterialParameter's name field against a literal, over
    /// the 0x40 bytes the engine's own <c>strncasecmp</c> compares. Allocation-free, and cold anyway.
    /// <para>Internal rather than private since #148: <see cref="Particles"/> reads its own <c>.mtr</c>
    /// parameter off the same record layout, and two spellings of "compare a material parameter name" is two
    /// different answers to a case-folding question.</para></summary>
    internal static bool NameIs(byte* rec, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            byte a = rec[i], b = (byte)name[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
        }
        return rec[name.Length] == 0;
    }


}
