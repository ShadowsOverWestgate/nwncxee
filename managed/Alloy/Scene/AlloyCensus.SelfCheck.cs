using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.PartTable;
using static CX.Alloy.BucketGate;
using static CX.Alloy.TexProbe;

namespace CX.Alloy;

internal static unsafe partial class AlloyCensus
{
    /// <summary>#128, off-device (run-demo.sh): the two halves of the instance model that fail SILENTLY.
    /// <para>The FOLD is a pile of hardcoded offsets into <c>PartTriMesh::Mat</c>'s gob, and every one of them
    /// has an identity that looks exactly like "this instance does not modulate" when it is really "we read
    /// the wrong field" -- an alpha that stays 1, a colour that stays white. Driven here against a synthetic
    /// gob whose fields are set one at a time, so a transposed offset reads as a failure rather than as a
    /// creature that never fades.</para>
    /// <para>The refused-gob SET is the all-owned-or-none sweep's whole memory: a lookup that misses does not
    /// error, it half-owns a creature. Insert, dedup, miss and the class-vs-gob split are checked directly.</para>
    /// False = do not arm the Alloy pass, per the house rule.</summary>
    internal static bool GobSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyCensus.GobSelfCheck");

        byte* gob = stackalloc byte[0x500];
        byte* owner = stackalloc byte[0x100];
        byte* part = stackalloc byte[0x100];
        for (int i = 0; i < 0x500; i++) gob[i] = 0;
        for (int i = 0; i < 0x100; i++) { owner[i] = 0; part[i] = 0; }
        // The typed views read these stand-ins exactly as they read a live object, and a pointer row to a
        // TRANSIENT class carries a setter, so the planted links go through the views too.
        var gv = new Gob((IntPtr)gob);
        var mv = new Model((IntPtr)owner);
        var pv = new Part((IntPtr)part);
        pv.Gob = gv;
        IntPtr p = (IntPtr)part;

        // an unarmed gob is the identity of all three terms -- the case every plain instance takes
        check.Expect(!GobUnownable((IntPtr)gob), "a bare gob is ownable");
        check.Expect(GobAlphaMul(p) == 1f, "alpha identity is 1");
        check.Expect(GobDiffuseMul(p) == System.Numerics.Vector3.One, "colour identity is (1,1,1) -- it is a MULTIPLY");
        check.Expect(GobEmissiveAdd(p) == default, "self-illum identity is 0 -- it is an ADD");
        // ...and so is a part with no gob at all, without dereferencing anything
        pv.Gob = default;
        check.Expect(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "a gobless part folds nothing");
        pv.Gob = gv;

        // the colour/alpha pair, armed together by gob+0xf6 and dead without it
        gv.Alpha = 0.5f;
        gv.Colour = new System.Numerics.Vector3(0.25f, 1f, 1f);
        check.Expect(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "unarmed values do not apply");
        gv.ColourMul = 1;
        check.Expect(GobAlphaMul(p) == 0.5f, "armed gob alpha multiplies");
        check.Expect(GobDiffuseMul(p) == new System.Numerics.Vector3(0.25f, 1f, 1f), "armed gob colour multiplies, in order");

        // the SECOND alpha multiply, armed on the gob's OWNER rather than on the gob -- the fade that moves a
        // whole creature into the transparent run, and the one term whose flag is not on the object it scales
        gv.FadeAlpha = 0.5f;
        check.Expect(GobAlphaMul(p) == 0.5f, "the fade alpha is dead without its owner");
        gv.Node = mv;
        check.Expect(GobAlphaMul(p) == 0.5f, "...and dead without the owner's fade bit");
        mv.OwnerFlags = GobOwnerFadeBit;
        check.Expect(GobAlphaMul(p) == 0.25f, "both multiplies compose");

        gv.SelfIllum = 1;
        gv.Emissive = new System.Numerics.Vector3(0f, 2f, 0f);
        check.Expect(GobEmissiveAdd(p) == new System.Numerics.Vector3(0f, 2f, 0f), "armed self-illum adds, in order");

        // ...and none of that made the instance unownable, which is the whole of #128 over #134
        check.Expect(!GobUnownable((IntPtr)gob), "a MODULATING instance is still ownable: the terms are folded, not refused");
        gv.Highlight = 1;
        // CX-91: gob+0x18 is the SKYBOX flag (Scene::SetSkyBox @0x45e260), whatever the build-file key calls
        // it, and a skybox gob is the sky's path, not the world walk's. Never a world-frame refusal.
        check.Expect(!GobUnownable((IntPtr)gob), "the gob+0x18 flag is not an ownership question");
        gv.Highlight = 0; gv.SceneTint = 1;
        check.Expect(GobUnownable((IntPtr)gob), "a scene tint is -- it REPLACES the surface with the fog colour, unlit");
        gv.SceneTint = 0;
        check.Expect(!GobHasEnvMap((IntPtr)gob), "a bare gob overrides no env map");
        gv.EnvOverride = (IntPtr)owner;
        check.Expect(!GobUnownable((IntPtr)gob), "a per-instance env override is OWNABLE -- it is the env flag, not a class");
        check.Expect(GobHasEnvMap((IntPtr)gob), "...and it reports as one, so the candidate takes #121's env path");
        check.Expect(!GobHasEnvMap(IntPtr.Zero), "a gobless part overrides nothing, without dereferencing");

        // the refused-gob set, over pointers it never dereferences
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);
        check.Expect(!GobRefused((IntPtr)gob), "an empty set refuses nothing");
        check.Expect(!GobRefused(IntPtr.Zero), "and a gobless part is never in it");
        OwnRefuse(2, (IntPtr)gob);
        OwnRefuse(2, (IntPtr)gob);
        check.Expect(GobRefused((IntPtr)gob) && _refGobCount == 1, "a gob is remembered once, however often it is refused");
        check.Expect(!GobRefused((IntPtr)owner), "and only the gob that was refused");
        check.Expect(OwnRefusedByClass[2] == 0, "a GOB refusal never trips the class-wide blended drop");
        OwnRefuse(2, IntPtr.Zero);
        check.Expect(OwnRefusedByClass[2] == 1, "...and a static refusal still does");
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);

        // CX-105: the INACTIVE-LOD exemption. SceneWalk enumerates a gob's alternate roots while a level
        // change is registered, so their parts become candidates -- and one refusable part on an alternate LOD
        // used to drop the WHOLE CREATURE for the pending window. Vanilla draws nothing for that part
        // (Scene::AddPartsTreeToDrawBuckets @0x460880 walks gob+0x90 alone), so by the census's own rule -- the
        // one the StHidden exemption states -- it must veto neither its gob nor its class.
        // The active root must still veto: this is the check that says the exemption is narrow.
        int le = MaxParts - 3;
        byte savedState = _pState[le]; IntPtr savedGob = _pGob[le], savedRoot = _pRoot[le];
        bool savedAlt = _pLodAlt[le];
        int savedNoVetoed = _ownNoVetoed;
        IntPtr active = (IntPtr)owner, alternate = (IntPtr)part;
        gv.PartTree = new CX.Engine.Part(active);
        _pGob[le] = (IntPtr)gob; _pLodAlt[le] = true; _pRoot[le] = active;
        check.Expect(!InactiveLodPart(le), "a part of the root the gob IS showing is not exempt");
        _pRoot[le] = alternate;
        check.Expect(InactiveLodPart(le), "...and one of a root it is not showing is");
        _pLodAlt[le] = false;
        check.Expect(!InactiveLodPart(le), "a part the walk did not reach down an LOD root is never exempt");
        _pLodAlt[le] = true; _pGob[le] = IntPtr.Zero;
        check.Expect(!InactiveLodPart(le), "...nor a gobless one, without dereferencing");
        _pGob[le] = (IntPtr)gob; _pRoot[le] = IntPtr.Zero;
        check.Expect(!InactiveLodPart(le), "...nor one with no recorded root");

        // ...and the latch that carries that verdict into every gate: neither arm of OwnRefuse may fire.
        _ownNoVeto = true;
        OwnRefuse(2, (IntPtr)gob);
        OwnRefuse(2, IntPtr.Zero);
        check.Expect(!GobRefused((IntPtr)gob) && _refGobCount == 0,
                     "an inactive-LOD refusal does not drop its creature");
        check.Expect(OwnRefusedByClass[2] == 0,
                     "...and does not trip the class-wide blended drop either");
        check.Expect(_ownNoVetoed == savedNoVetoed + 2, "...and is counted, so the census still reports it");
        _ownNoVeto = false;
        OwnRefuse(2, (IntPtr)gob);
        check.Expect(GobRefused((IntPtr)gob), "a refusal on the ACTIVE root still drops its creature");
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);
        _ownNoVetoed = savedNoVetoed;
        _pState[le] = savedState; _pGob[le] = savedGob; _pRoot[le] = savedRoot; _pLodAlt[le] = savedAlt;
        gv.PartTree = default;

        // CX-90: the late keywords' precedence. Off-device because the live signal is a class that renders
        // or does not -- and the two values of one keyword are different classes, which is exactly the
        // confusion a wrong answer here would restore.
        check.Expect(!RefusedByLateKeywords(0, false, false), "an ordinary material passes");
        check.Expect(!RefusedByLateKeywords(SampleFbLate, false, false), "sample_framebuffer 1 is ownable: it asks for draw order, not shading");
        check.Expect(!RefusedByLateKeywords(SampleFbWater, false, true), "sample_framebuffer 2 is water, which the water class draws");
        check.Expect(!RefusedByLateKeywords(0, false, true), "and so is bumpy-shiny water, which declares no keyword at all");
        check.Expect(RefusedByLateKeywords(0, true, false), "volumetric is refused: the engine draws it twice per pass");
        check.Expect(RefusedByLateKeywords(SampleFbLate, true, false), "volumetric beats the late keyword");
        check.Expect(RefusedByLateKeywords(3, false, false), "an unknown sample_framebuffer value is refused, not guessed at");
        check.Expect(OwnableBucket(SceneWalk.SampleBucket), "...and bucket 5 is in the ownable set");

        // #285: the reject cross. The value of this instrument is entirely in the two axes not being
        // transposed -- a note that reads "empty-item/Water" for a water part refused by the VDO gate is worse
        // than no note, because it sends the next reader to the wrong gate.
        Array.Clear(OwnRejByReason);
        check.Expect(RejCrossNote().Length == 0, "no refusals, no by-gate note");
        RejReason(RejVdo, (int)AlloyClass.Water);
        RejReason(RejItem, (int)AlloyClass.Cutout);
        RejReason(RejItem, (int)AlloyClass.Cutout);
        check.Expect(RejCrossNote() == " [by gate: vdo/Water=1 empty-item/Cutout=2]", $"the cross reads gate/class: {RejCrossNote()}");
        RejReason(RejCount - 1, AlloyClasses.Count);   // out of range: dropped, never a corrupted neighbour
        check.Expect(RejCrossNote() == " [by gate: vdo/Water=1 empty-item/Cutout=2]", "an out-of-range class is dropped");
        Array.Clear(OwnRejByReason);

        // The table-pressure decision, which is the whole of the "renderer keeps falling back to vanilla in
        // big areas" fix and is otherwise only observable on a live client in a big area. Runs on the real
        // arrays and restores them: nothing is recorded yet at self-check time, so there is nothing to lose,
        // but the counters are put back regardless in case that ever stops being true.
        int savedParts = _partCount, savedGen = TableGen;
        bool savedSat = _saturated;
        try
        {
            // a table full of the LIVE is a big area: saturate, never wipe
            for (int i = 0; i < 64; i++) _pState[i] = StActive;
            for (int i = 64; i < 96; i++) _pState[i] = StDead;
            _saturated = false; Log = null;
            OnTablePressure();
            check.Expect(_saturated && TableGen == savedGen, "a mostly-LIVE table saturates and is NOT wiped");
            check.Expect(_satLive == 64 && _satDead == 32, "...and the census it reports is the one it measured");
            // ...and a table full of the DEAD is transition debris: wipe, which is what the wipe is for
            for (int i = 0; i < 64; i++) _pState[i] = StDead;
            OnTablePressure();
            check.Expect(!_saturated && TableGen == savedGen + 1, "a mostly-DEAD table is wiped as transition debris");
        }
        finally
        {
            Array.Clear(_pState); Array.Clear(_pPart); Array.Clear(OwnFlag);
            _partCount = savedParts; TableGen = savedGen; _saturated = savedSat;
            Log = log;
        }

        // #221: the recycled-address path. Live it looks like "Alloy silently released the character" with
        // no revoke, no refusal and no counter moving, and it only heals on a table wipe -- so it is asserted
        // here, on the real arrays, rather than trusted to a session that has to be caught in the act.
        savedParts = _partCount; int savedReclaims = _reclaims;
        try
        {
            _partCount = 0; _reclaims = 0;
            IntPtr p1 = (IntPtr)0x4000;
            int e1 = Insert(p1);
            check.Expect(e1 >= 0 && Find(p1) == e1, "a fresh part inserts and is found");
            _pState[e1] = StActive;
            check.Expect(MarkFreed(p1) == e1, "Part::~Part finds the entry that held the dying pointer");
            check.Expect(Find(p1) < 0, "...and the address stops resolving to its tombstone");
            int e2 = Insert(p1);
            check.Expect(e2 == e1 && Find(p1) == e2, "a part recycled onto the same address re-records into that slot");
            check.Expect(_pState[e2] == StEmpty && OwnFlag[e2] == 0 && _partCount == 1,
              "...as a slot indistinguishable from a fresh one, and without double-counting the part");
            // and the load factor must not gate reuse: _partCount only ever climbs, so a long session
            // otherwise reaches a point where every recycled pointer is refused forever
            _pState[e2] = StActive; MarkFreed(p1);
            _partCount = MaxParts;
            check.Expect(Insert(p1) == e1, "a freed slot is reusable even with the table's load factor exhausted");
            check.Expect(Insert((IntPtr)0x8000) < 0, "...while a genuinely new pointer is still refused there");
            check.Expect(_reclaims == 2, "and each reuse is counted");
        }
        finally
        {
            Array.Clear(_pState); Array.Clear(_pPart); Array.Clear(_pFreed); Array.Clear(OwnFlag);
            _partCount = savedParts; _reclaims = savedReclaims;
        }

        log.Info(check.Ok ? "AlloyCensus.GobSelfCheck: passed" : "AlloyCensus.GobSelfCheck: FAILED");
        return check.Ok;
    }
}
