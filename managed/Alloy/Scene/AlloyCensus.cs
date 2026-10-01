using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.PartTable;
using static CX.Alloy.BucketGate;
using static CX.Alloy.TexProbe;
namespace CX.Alloy;

/// <summary>#188: the ownership census -- which recorded parts the Alloy passes MAY take, and why the rest
/// were passed over. The ownership columns (<see cref="OwnFlag"/>, <see cref="OwnGen"/>,
/// the ownable-bucket list, the colour candidate feed
/// (<see cref="TryOwnCandidate"/>; #302 retired the separate caster feed -- the shadow bake walks the
/// ownership table), plus
/// the per-gate forensics and the <c>dumpframe</c>/<c>dumptex</c> triage that say what a refusal was.
/// <para>It classifies; it draws nothing and suppresses nothing. What it decides is read by the owned
/// passes (through the candidate feeds) and by <see cref="BucketGate"/> (through the columns), and it is
/// entirely independent of which pass, if any, ends up drawing the part.</para>
/// <para>Cold by construction: one call per table slot per enrollment, on a 60-frame cadence. The two
/// exceptions are named at their declarations (<see cref="GobUnownable"/> and the gob folds, which the
/// Alloy draw loop calls per part per frame) and allocate nothing.</para></summary>
internal static unsafe partial class AlloyCensus
{
    internal static ExtenderLog? _log;

    /// <summary>Read <c>dumptex</c> out of <c>cx.cfg</c> and arm the enrollment watch.
    /// <para>#264 took <c>dumpframe</c> with the engine walk it censused: its row was one per part the ENGINE
    /// drew, and the engine draws no part of the world any more. What survives is the half that never needed
    /// a draw -- <see cref="WatchAccepted"/> adopts the watch at enrollment, and the ENROLL/DRAW breadcrumbs
    /// follow the entry from there.</para></summary>
    internal static void Configure(ExtenderLog log)
    {
        _log = log;
        // #134: aim the watch at a texture rather than at a moment. Five rolls in a row fired while the
        // camera was somewhere else, because "300 frames after the table settled" is not "standing at the
        // thing we are debugging" and no warm-up rule can make it so.
        _dumpTex = Renderer.DumpTex.Boot.ToLowerInvariant();
        if (_dumpTex.Length == 0) _dumpTex = null;
        if (_dumpTex != null) log.Info($"AlloyCensus: dumptex watch \"{_dumpTex}\" (64 CLASS lines, SCAN/ENROLL/DRAW breadcrumbs)");
        _skipTex = Renderer.SkipTex.Boot.ToLowerInvariant();
        if (_skipTex.Length == 0) _skipTex = null;
    }

    /// <summary><c>dst == 2</c> is <c>GL_ONE</c>: the engine's own additive test on
    /// <see cref="EngineLayout.MatDstBlend"/>, and the one pair it turns the depth mask off for.</summary>
    internal const int BlendOne = 2;
    /// <summary><c>alloy.gradedblend</c> in cx.cfg: the graded-alpha fraction at or above which a cutout diffuse
    /// draws in the blended class instead. 1 = never (the #357 alpha-test-only behaviour).
    /// <para>0.15, not the 0.3 it shipped at: the tcn01 splotch decals (dark grime with a soft alpha skirt,
    /// which vanilla blends) scan at 0.29-0.30 and drawn alpha-tested their skirt is solid black; ivy sits
    /// at 0.45+ and was already over, and a binary-alpha tree is under 0.05 either way.</para></summary>
    internal static readonly float GradedBlend = Renderer.GradedBlend.Boot;
    /// <summary>The tilefade cap bucket -- Tracy scope "Tilefade Cap", fed from <c>capbuckets</c>.</summary>
    internal const int CapBucket = 7;
    /// <summary>#134: the dynamic gob pair (#103's taxonomy). Everything instanced lands in one of these two
    /// -- placeables, doors, and the tileset's own static objects -- and until the <c>shaped</c> gate was
    /// corrected nothing in them ever reached enrollment, so excluding them here cost nothing visible. They
    /// are ownable as a PAIR: the engine walks 2 then 3, and owning one without the other splits a single
    /// instance's geometry across two renderers.</summary>
    internal const int GobOpaqueBucket = 2, GobBlendBucket = 3;

    /// <summary><c>dumptex</c> (cx.cfg): log only parts whose diffuse texture name contains this, one frame's worth at a
    /// time, for as long as it takes the camera to find them.</summary>
    internal static string? _dumpTex;
    /// <summary><c>skiptex</c> (cx.cfg): refuse every part whose diffuse texture name contains this. Under the
    /// M4 shim a refused part draws nowhere -- colour, prepass, casters -- so this is the "is it that part" A/B.</summary>
    internal static string? _skipTex;
    private static int _classDiagLeft = 64;
    /// <summary>Table slot of the part <c>dumptex</c> last matched, published by the census for enrollment to
    /// recognise without dereferencing anything. -1 until the camera finds it.</summary>
    internal static int _watchEntry = -1;

    /// <summary>#82 ownership columns, driven by AlloyRenderer: 1 = the Alloy world pass owns this part, and
    /// the pass generation its Alloy draw last succeeded in. (#285: the "so the engine must not draw it too"
    /// half of this is history -- since #264 the engine draws no world part at all.) They live
    /// here so the iterator's existing <c>Find</c> serves both owners -- no second hash probe per part.</summary>
    internal static readonly byte[] OwnFlag = new byte[MaxParts];

    internal static readonly int[] OwnGen = new int[MaxParts];
    /// <summary>Pass generation in which the iterator last SUPPRESSED this part. Compared against
    /// <see cref="OwnGen"/> to audit the exactly-one-owner invariant: drawn but never suppressed in the
    /// same pass means the engine drew it too (double-draw) or nobody walked it (ghost geometry).</summary>

    /// <summary>#129: does the engine route this material to draw bucket 6, i.e. is this water?
    /// <para><c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880's own two terms, in its own order:
    /// <c>Material::HasBumpyShinyMap</c> gated on the global <c>g_nEnableBumpyWater</c> (which is what the
    /// engine tests before it looks at the SharedMaterial at all), else the <c>.mtr</c>'s
    /// <c>sample_framebuffer 2</c>. Value 1 of that same keyword is refraction glass and bucket 5, which is
    /// NOT water and which the Alloy pass still refuses.</para>
    /// <para>Read off the material rather than off the bucket on purpose: bucket membership is recomputed
    /// every frame while these two are material state, which is the rule <see cref="AlloyClasses"/>
    /// states for the whole classifier.</para></summary>
    internal static bool IsWater(IntPtr mat, IntPtr shared)
    {
        if (mat == IntPtr.Zero) return false;
        if (_bumpyWater != null && *_bumpyWater != 0 && Engine.HasBumpyShiny(new MaterialRef(mat))) return true;
        return shared != IntPtr.Zero && new SharedRef(shared).SampleFb == SampleFbWater;
    }

    /// <summary><c>sample_framebuffer</c>'s two values: 1 routes to draw bucket 5 (the late class), 2 to
    /// bucket 6 (water). Different classes, not degrees of one.</summary>
    internal const int SampleFbWater = 2;

    /// <summary>CX-90: <c>sample_framebuffer 1</c> -- draw bucket 5. The engine's own changelog
    /// (<c>lang/en/docs/patchnotes/87.8193.35.md</c>) states the whole of what it buys: "renders near the end
    /// of the frame. Allows rendering refractions and similar." It is a draw-ORDER keyword; the refraction is
    /// the material's own fragment shader's business, and the copy it would read is bound by the engine on
    /// texture units 11/12 as <c>texFBColor</c>/<c>texFBDepth</c>
    /// (<c>Camera::BindRenderSubframeTextures</c> @0x4a6ab0, called from <c>CopyRenderSubframe</c> @0x4a6b90).
    /// <para>No stock shader samples those from world geometry (only <c>fswater.shd</c> and
    /// <c>fsparticle.shd</c>'s depth tap do) and no stock <c>.mtr</c> declares the keyword at all, so there is
    /// no vanilla shading behaviour attached to it -- see <c>docs/alloy-refusals.md</c>.</para></summary>
    internal const int SampleFbLate = 1;

    /// <summary>CX-90: does a material's pair of late-draw keywords refuse the part outright? Pure, so the
    /// precedence is testable off-device: <c>volumetric</c> always does (the engine draws it TWICE per pass,
    /// back faces to depth and then front faces against that depth -- a two-pass trick no one-owner scheme
    /// survives, and <c>AddPartsTreeToDrawBuckets</c> files it in buckets 3 AND 5 accordingly), and so does
    /// any <c>sample_framebuffer</c> value that is neither water nor <see cref="SampleFbLate"/>.
    /// <para><paramref name="water"/> is <see cref="IsWater"/>'s verdict, not <c>sampleFb == 2</c>: the
    /// bumpy-shiny route reaches bucket 6 with the keyword absent.</para></summary>
    internal static bool RefusedByLateKeywords(int sampleFb, bool volumetric, bool water)
        => volumetric || (sampleFb != 0 && !water && sampleFb != SampleFbLate);

    /// <summary>Draw buckets the Alloy pass takes parts from. 0/1 are the static tile pair (#82), and #126
    /// adds the three residual static classes named by <see cref="AlloyClass"/>: 4 additive, 7 the
    /// tilefade cap, 8 the fade buckets. #134 and #128 then added the dynamic gob pair 2/3 -- drawn from
    /// Scene::RenderDynamicGeometry rather than from RenderSinglePass's own walk, but through the same
    /// RenderDrawBucket this service brackets, which is why owning placeables and then creatures needed no new
    /// capture point. CX-90 adds 5, the late class: <c>sample_framebuffer 1</c> asks for draw ORDER and
    /// nothing else, so its parts are classified off their material like every other part and drawn in the
    /// pass that material implies -- what <see cref="TryOwnCandidate"/> still rejects there is
    /// <c>volumetric</c> (<see cref="RefusedByLateKeywords"/>).
    /// <para>Membership here and suppression in <c>AlloyRenderer.IterSight</c> are ONE list: suppressing a
    /// bucket we do not draw drops geometry, and drawing one we do not suppress double-draws it.</para></summary>
    internal static bool OwnableBucket(int bucket)
        => bucket == 0 || bucket == 1 || bucket == GobOpaqueBucket || bucket == GobBlendBucket
           || bucket == 4 || bucket == 7 || bucket == AlphaBucket || bucket == SceneWalk.SampleBucket
           || (bucket == AlloyRenderer.WaterBucket && FrameSnapshot.Usable);

    /// <summary>#82 ownership enrollment: is the part in table slot <paramref name="e"/> in the ownable-now
    /// set (#72)? Non-skinned, no bumpy-shiny (both settled at Record), plus --
    /// checked here because they are material/instance state, not capture state -- no gob
    /// modifiers, indexed VDO, an <see cref="OwnableBucket"/>, a live index range, and sighted within the
    /// last frame. Neither a SharedMaterial nor the _nm/_sm map slots reject any more -- see the notes at
    /// their tests.
    /// <para>#126: opacity classifies rather than rejects. CX-90 narrowed the material refusal to
    /// <see cref="RefusedByLateKeywords"/>: <c>volumetric</c>, which the engine files in TWO buckets and draws
    /// twice per pass, so owning it would break the one-owner invariant by construction.</para>
    /// Cold: one call per table slot per enrollment.</summary>
    internal static bool TryOwnCandidate(int e, ref AlloyCandidate c)
    {
        // #134: dumptex follows its texture into ENROLLMENT too. The census runs inside the bucket walk
        // and enrollment runs before it, so an entry can be ownable at one and refused at the other -- which
        // is exactly the shape of "ownable-but-not-enrolled every frame while the counters report nothing
        // refused". One line per matching entry per enrollment, and enrollment is every 60 frames.
        bool got = TryOwnCandidateInner(e, ref c);
        // Enrollment identifies the watched part by ENTRY INDEX, which the census hands it from the draw
        // side. Matching on the texture name here needed a Material and then a CAurTexture dereference, and
        // this table holds area-transition debris whose pointers are freed -- two crashes. Guarding that
        // with "live and freshly sighted" made it safe and useless in the same stroke: STALE is the state
        // under suspicion, so the guard silenced exactly the line worth reading.
        if (e == _watchEntry && !_ownDry)
            _log?.Info($"[dumpframe] ENROLL entry={e} -> {(got ? "candidate" : _ownGate ?? "?")} " +
                       $"cls={c.Cls} state={_pEnt[e].State} " +
                       $"bucket={(_pEnt[e].State == StActive ? _pBucket[e] : -1)} " +
                       $"vdo=0x{(ulong)_pVdo[e]:x} item={_pItem[e]} ownflagBefore={OwnFlag[e]}");
        return got;
    }

    /// <summary>Is this the table slot the census is watching? Index comparison only -- see the note in
    /// <see cref="TryOwnCandidate"/> for why nothing here may dereference a Material or a texture.</summary>
    internal static bool WatchedEntry(int e) => e >= 0 && e == _watchEntry;

    /// <summary>#134: adopt the watch from ENROLLMENT rather than from the census. Once a part is owned it
    /// stops reaching the engine's draw -- which is the point -- so the census that named the watch goes
    /// silent exactly when the interesting half of its life begins. Called only for a candidate the gates
    /// have already accepted, so the Material is live and the name lookup is safe.</summary>
    internal static bool WatchAccepted(int e, IntPtr mat)
    {
        if (_dumpTex == null || mat == IntPtr.Zero) return false;
        TextureRef t = new MaterialRef(mat).Texture(0);
        if (t.IsNull || !t.ResolveName().Contains(_dumpTex, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (_watchEntry < 0) _watchEntry = e;
        WatchTex[e] = true;
        return true;
    }

    /// <summary>#134: entries whose diffuse matched <c>dumptex</c> at the last enrollment. A POPULATION, not a
    /// specimen: following one part answered "is this part owned" four rolls running while the question was
    /// "how much of what I am looking at is".</summary>
    internal static readonly bool[] WatchTex = new bool[MaxParts];


    internal static bool TryOwnCandidateInner(int e, ref AlloyCandidate c)
    {
        // the candidate struct is reused across the whole enrollment scan, and enrollment reads Gob back on a
        // REFUSAL -- so a gate that fires before the part loads must not leave the previous entry's gob behind
        c.Gob = IntPtr.Zero;
        // ...and the veto latch with it: the previous entry's answer must not absorb this one's refusal.
        _ownNoVeto = false;
        // CX-92: a HELD hidden entry is not a death and must not refuse its creature. part+0x34 == 0 is the
        // engine's own subtree gate (Scene::AddPartsTreeToDrawBuckets @0x460880 and Gob::PartDraw @0x451d50
        // both return on it before recursing), so vanilla draws nothing for it either and every sibling on
        // the gob still draws -- the same exemption the geometry-less case gets below, for the same reason.
        if (_pEnt[e].State == StHidden) { if (!_ownDry) _ownInstHidden++; return Gate("hidden"); }
        if (_pEnt[e].State != StActive)
        {
            if (!_ownDry && _pGob[e] != IntPtr.Zero) _ownInstDead++;
            // #128: a part that died before enrollment could classify it still belongs to a creature, and a
            // creature is all-owned-or-none. The commonest case is a multi-draw part, which dies at Complete
            // as "multidraw-or-unindexed" -- own its siblings and one arm of that creature interleaves a
            // second renderer through every fade. Marked from the copy taken at Record, never from the dead
            // part, and only while it is still drawing, so a despawn clears itself within a frame.
            // ...and the gob must still be the one we copied. An identity-changed entry keeps its old _pGob
            // while a RECYCLED part at that address answers to a different gob, so without this a reused
            // pointer would sweep a live creature every enrollment. Same single-level read the death branch
            // already does.
            // #264: the "and it drew within the frame" half of this test WAS load-bearing, and dropping it
            // outright was wrong -- the first live run refused 44 creatures whole off entries the sweep had
            // already found unreachable. Its record-set equivalent is the walk generation: an entry the last
            // walk still reached is one whose part is genuinely in the scene, which is what "still drawing"
            // was standing in for. A swept-unreachable entry says nothing about its gob and must not veto it.
            // ...and NOT for a primitive the engine's own totals call empty. That entry is unbound because
            // VertexPrimitiveFlat::SetupArrays @0x458920 took its zero-totals exit (item -1, VDO null), and
            // RenderFlat @0x450eb0 opens with `if (vp+0x58 == 0) return;` -- so vanilla draws nothing for it
            // and every sibling on the gob still draws. There is no second renderer to interleave, nothing
            // was lost to own, and refusing the instance turns a part that costs nothing into the whole
            // object missing. Its healing condition is the object being re-created, which is why the symptom
            // reads as permanent.
            if (!_ownDry && _pGob[e] != IntPtr.Zero && _pWalkGen[e] == SceneWalk.Gen
                && _pEnt[e].Part != IntPtr.Zero && new PartRef(_pEnt[e].Part).Gob.Ptr == _pGob[e])
            {
                if (_pEnt[e].State == StUnbound && SceneWalk.RecordHasNoGeometry(e)) _ownInstNoGeom++;
                // CX-105: ...and an unbound or dead part of a root the gob is not showing is not its
                // creature's problem either. The guard above has just proved _pGob[e] is the live part's gob,
                // which is what makes the dereference safe here.
                else if (InactiveLodPart(e)) _ownNoVetoed++;
                else { NoteInstRefusing(e); OwnRefuse(0, _pGob[e]); }
            }
            return Gate("state");
        }
        if (OwnFlag[e] != 0) return Gate("OWNED");
        if (!_ownDry) { _ownSeen++; if (_pGob[e] != IntPtr.Zero) _ownInstSeen++; }
        // #256: THE SIGHTING WINDOW IS GONE. It was an inference -- "the engine has not walked this part
        // lately, so it is probably not here any more" -- and #236 had already had to widen it to 60 frames
        // to stop it dropping ~5000 of 12533 active parts per enrollment for being behind the camera.
        // Liveness is now a FACT the scene walk establishes: a record exists exactly while the part is
        // reachable from the Scene, and an unreachable one is swept (SceneWalk.Sweep) or destroyed
        // (Part::~Part). Candidacy therefore asks nothing about visibility at all. What the camera can see
        // is a per-FRAME question, asked at draw time against the bucket arrays.
        // The captured node/material were last checked against the live part by the scene walk, which may be
        // several frames ago; every gate below dereferences them. StActive plus the #145 dtor hook says the
        // PART is alive, not that it still wears these. A mismatch is the walk's own "identity-changed" case
        // arriving early. #264: asked of EVERY candidate now. It used to be skipped for a freshly sighted
        // part, and there are no sightings; enrollment is edge-triggered (#256), so the cost is one pointer
        // pair per candidate per edge.
        // CX-91: DRIFT IS NOT A REFUSAL. #256 refused and queued the re-read, which was right while an
        // unowned part still drew vanilla; since #264 the frame between the notice and the next Rebind() tick
        // is the part missing. The re-read happens HERE instead and the candidate carries on with the
        // refreshed columns, so only a record the rebind could not make usable is still counted.
        if (_pEnt[e].Part != IntPtr.Zero)
        {
            var live = new PartRef(_pEnt[e].Part);
            // ...and the VDO's item table is part of that identity: SetupArrays' RemoveItem and a re-minted
            // VDO block both leave the cached item naming a slot the object no longer has.
            if (live.Node.Ptr != _pNode[e] || live.Material.Ptr != _pMat[e] || !PartTable.ItemResident(e))
            {
                if (_ownDry || !SceneWalk.RebindNow(e, live))
                { Bump(ref _ownRejStaleMoved); return Gate("stale-identity"); }
            }
        }
        if (!OwnableBucket(_pBucket[e])) { Bump(ref _ownRejBucket); return Gate("bucket"); }
        // #91: a part recorded from another Scene (a GUI 3D panel) must never enter the world table -- the
        // Alloy block draws the whole table in one go, so one foreign part is area geometry in a portrait.
        if (_pScene[e] != _ownScene) { Bump(ref _ownRejScene); return Gate("scene"); }
        if (_skipTex != null && _pTex0[e] != IntPtr.Zero
            && TexName(_pTex0[e]).Contains(_skipTex, StringComparison.OrdinalIgnoreCase)) return Gate("skiptex");
        // #338: the gate every Part*::Draw override runs before it draws (PartTriMesh, PartAnimMesh,
        // PartAABB, PartDanglyMesh, PartSkin all call PartTriMesh::IsPartRenderable @0x450e60), and that
        // Scene::AddPartsTreeToDrawBuckets @0x460880 runs before every colour-bucket add. Alloy walks the
        // world, not the buckets (#255), so this is the only place that filter exists on our side -- and
        // without it the parts CombineParts @0x473d30 has already merged away enroll and draw their absorbed
        // geometry as solid black over the combined mesh that replaced it. Refusing them costs no caster:
        // the engine never draws them from any path either, and the flag never flips back (see IsRenderable).
        if (!PartTable.IsRenderable(_pEnt[e].Part)) { Bump(ref _ownRejUnrenderable); return Gate("unrenderable"); }
        IntPtr v = _pVdo[e], mat = _pMat[e], part = _pEnt[e].Part;
        var p = new PartRef(part); var m = new MaterialRef(mat); var vd = new VdoRef(v);
        // Published the moment the part is in hand, and kept through every refusal from here down: creature
        // colour is all-owned-or-none per instance (#107/#128), so a refusal has to be attributable to its
        // gob. The gates ABOVE this line need no attribution -- a stale or foreign-scene part is not drawing.
        c.Gob = p.Gob.Ptr;
        c.LodRoot = PartTable._pLodAlt[e] ? PartTable._pRoot[e] : IntPtr.Zero;
        // CX-105: from here on the gob is known and is the live part's, so the inactive-LOD question can be
        // asked -- and it must be asked BEFORE the first gate that can refuse with it.
        _ownNoVeto = c.LodRoot != IntPtr.Zero && c.Gob != IntPtr.Zero
                     && new Gob(c.Gob).PartTree.Ptr != c.LodRoot;
        // A SharedMaterial is NOT a reason to hand a part back. Ghidra Material::GetTexture @0x4c5810
        // resolves every slot through it already (shared+0xd8+i*0x10+8, else the material's own), so the
        // texture we captured is the right one. All it adds on top is a custom shader program
        // (SharedMaterial::BindCustomShader @0x4c5590) -- and that early-returns when both program slots are
        // null, so a redirect-only shared material costs nothing at all. #123 takes the next slice: the seven
        // SoW custom programs are recognised by their customShaderVS name and reproduced as feature switches
        // in the Alloy program, so they are exact too. What is left over is still approximated with the
        // generic lit program -- the deliberate trade, one renderer on screen beats a correct half -- and the
        // three-way split is what says how much of it there is, and which shaders.
        var s = m.Shared;
        IntPtr shared = s.Ptr;
        // CX-90: the material refusal is down to volumetric alone -- the .mtr keywords are parsed by
        // SharedMaterial::ParseField @0x4c6760 and spent by Scene::AddPartsTreeToDrawBuckets @0x460880, and
        // the precedence + the reason each value is or is not ownable live in RefusedByLateKeywords.
        bool water = IsWater(mat, shared);
        if (water) NoteWaterMaterial(mat, shared);
        if (!s.IsNull && RefusedByLateKeywords(s.SampleFb, s.Volumetric, water))
        { Bump(ref _ownRejFbSample); return Gate("volumetric"); }
        // ...and the classification itself, which from here on is what a refusal is counted AGAINST: the
        // blended class is all-owned-or-none (#107 s2), so every gate below has to say which class it ate.
        c.Src = m.SrcBlend; c.Dst = m.DstBlend;
        // #292: no material means no `twosided` keyword, which is the engine's default and so culls.
        c.TwoSided = !s.IsNull && s.TwoSided;
        // The cap bucket overrides the material: RenderSinglePass draws bucket 7 with Disable(0) and
        // Disable(1) -- blending and the alpha discard both off -- so a cap is an opaque draw whatever its
        // material declares.
        c.Cls = _pBucket[e] == CapBucket ? (int)AlloyClass.Opaque
              : (int)AlloyClasses.Of(water, c.Dst == BlendOne, Engine.IsOpaque(p),
                    !s.IsNull && s.Transparency,
                    p.Alpha < 1f);
        // CX-54: a cutout whose diffuse is not built yet is not a cutout, it is an unanswered question.
        // Ghidra Material::GetIsOpaque @0x4c6190 asks an unloaded diffuse and returns "not opaque" WITHOUT
        // writing either memo, so Of() falls through to Cutout and the engine re-answers once it lands.
        // Until then the part draws the OPAQUE white stand-in the 0.2 discard cannot hole, so the two
        // classes look identical and only the cutout one gives up early-Z. Tested on the condition that
        // picks the stand-in (tid 0), under the gate that arms TexStream.Landed -- the re-enrol on arrival.
        if (c.Cls == (int)AlloyClass.Cutout && TexStream.On)
        {
            var d0 = m.Texture(0);
            if (!d0.IsNull && AurTex.Tid(d0.Ptr) == 0) c.Cls = (int)AlloyClass.Opaque;
        }
        // A cutout whose diffuse is mostly GRADED alpha (a light shaft, a ground decal) is a wash vanilla
        // blends in bucket 3; alpha-tested it is a solid slab with a ragged 0.2 rim. Blend it instead.
        c.Wash = false;
        bool gradedOk = OwnedTextures.TryGraded(_pTex0[e], out float graded);
        if (c.Cls == (int)AlloyClass.Cutout && gradedOk && graded >= GradedBlend)
        { c.Cls = (int)AlloyClass.Blended; c.Wash = true; }
        // alloy.dumptex=- names the parts with NO diffuse; alloy.dumptex=env every MATERIAL-env-mapped part (a glass sphere, #363; the gob override is every creature)
        if (_dumpTex != null && !_ownDry && _classDiagLeft > 0
            && (_dumpTex == "env" ? Engine.HasEnvMap(m)
                                  : TexName(_pTex0[e]).Contains(_dumpTex, StringComparison.OrdinalIgnoreCase))
            && --_classDiagLeft >= 0)
            _log?.Info($"[dumpframe] CLASS entry={e} tex={TexName(_pTex0[e])} slots={SlotNames(m)} scanned={gradedOk} graded={graded:F2} opaque={Engine.IsOpaque(p)} env={Engine.HasEnvMap(m)}/{GobHasEnvMap(c.Gob)} transparency={!s.IsNull && s.Transparency} alpha={p.Alpha:F2} blend={m.SrcBlend}/{m.DstBlend} dst={c.Dst} -> cls={(AlloyClass)c.Cls} {PartForensic(e)}");
        if (!_ownDry) _ownByClass[c.Cls]++;
        // CX-91: a material with NO diffuse slot is not a refusal -- the engine draws it untextured.
        // Material::BindAllStandardTextures @0x4c6e30 answers a null slot with
        // SetActiveTextureAndBind(unit, 4, 0), which is GLRender::SetTextureStageEnabled(unit, 0)
        // @0x4c93d0 -- the stage drops out of m_bEnabledTextureStages and contributes no multiply -- and
        // PartTriMesh::Draw @0x4513d0 then calls RenderFlat anyway. That is the white stand-in the draw loop
        // already has for a live diffuse with no GL id, so this enrols and takes it.
        if (!_ownDry && _pTex0[e] == IntPtr.Zero) _ownNoDiffuse++;
        // ...and the structural pair stays as a guard. Both are impossible for a bound record (SceneWalk
        // releases a non-mode-1 VDO and the drift gate above re-reads a vacated one), so a nonzero count here
        // is a table bug and not content.
        if (v == IntPtr.Zero || vd.Mode != 1)
        { Refuse(ref _ownRejVdo, c, RejVdo); return Gate("vdo"); }
        // #284: a diffuse that is a live texture with no GL id is a part the draw loop will draw on the white
        // stand-in (null-tex), and the engine has no retry -- its queue is one-shot and drains whether or not the build
        // found a decoded image. Enrolment is the Alloy-owned edge that notices; OwnedTextures owns the
        // retry, and the budget. Enrolled anyway: the id may well be there by the time the draw runs.
        if (!_ownDry)
        {
            var diffuse = m.Texture(0);
            if (!diffuse.IsNull && AurTex.Tid(diffuse.Ptr) == 0) OwnedTextures.NoteUnbuilt(diffuse.Ptr);
        }
        c.Feat = 0;
        if (!s.IsNull)
        {
            byte* vs = s.VsName;
            if (*vs == 0) Bump(ref _ownSharedPlain);
            else if ((c.Feat = Classify(vs)) != 0) Bump(ref _ownSharedFeature);
            else { Bump(ref _ownSharedShader); if (!_ownDry) NoteUnknown(vs); }
        }
        // #134/#128: an instance is ownable, and so is a MODULATED one -- PartTriMesh::Mat @0x450960's three
        // terms are folded live by the draw loop now. What still refuses is the handful of instance state no
        // Alloy program can express at all.
        if (c.Gob != IntPtr.Zero && GobUnownable(c.Gob))
        { Refuse(ref _ownRejGob, c, RejSceneTint); return Gate("scene-tint"); }
        // Slots 1/2 are the normal and spec/roughness maps of the fslit_nm/_sm variants. #120 made the owned
        // lit stage read them (and slots 3/4/5) rather than bind slot 0 alone, so this is now a coverage
        // count of how much of the Alloy set is PBR content -- never a reason to reject.
        if (!m.Texture(1).IsNull || !m.Texture(2).IsNull) Bump(ref _ownWithMaps);
        // #128: the engine's own routing condition, both halves -- PartTriMesh::Draw @0x4513d0 takes
        // RenderEnvironmentMapped when EITHER the instance overrides the env map or the material declares one.
        c.Env = Engine.HasEnvMap(m) || GobHasEnvMap(c.Gob);
        if (c.Env) Bump(ref _ownWithEnv);
        long elem = vd.ElemItems;
        int it = _pItem[e];
        // The out-of-range half of this pair moved to the drift gate (CX-91), which re-reads instead of
        // refusing; what is left is the item the VDO holds with ZERO indices in it, and that IS a correct
        // skip: RenderFlat @0x450eb0 passes GetItemElementSize straight to RenderVertexData as the index
        // count, so vanilla issues a draw of nothing for it too.
        if (elem == 0 || it < 0 || it >= vd.ElemCount || it >= vd.VertCount
            || *(int*)(elem + it * 8 + 4) <= 0)
        { Refuse(ref _ownRejItem, c, RejItem); return Gate("empty-item"); }
        // #397: the stock ashlw_072.mdl carries ShLwMain_070 AND ShLwMain_072 -- two coincident meshes, same
        // parent, same local transform, same counts, different diffuse. The engine draws both in tree order
        // and the LATER one wins the depth tie every frame; Alloy's order is not tree order, so the shield
        // flickered. Refuse the earlier twin, as the engine's tie does.
        if (c.Gob != IntPtr.Zero && DupSibling(part, e, vd, it)) { Bump(ref _ownRejDupSibling); return Gate("dup-sibling"); }

        c.Bucket = _pBucket[e];
        c.Tri = part; c.Node = _pNode[e]; c.Mat = mat; c.Vdo = v; c.Tex0 = _pTex0[e];
        c.Item = it; c.Prim = _pPrim[e]; c.RotBits = _pRot[e]; c.Mx = _pMx[e];
        c.Lit = _pLight[e];
        _ownGate = null;
        return true;
    }

    /// <summary>Record which gate refused, and refuse. #134: the census used to ask a hand-written MIRROR of
    /// the chain above the same question, and the mirror's own doc comment warned that the two would be
    /// worthless the day they disagreed. They disagreed within the week -- the mirror kept a blanket gob
    /// reject that the real predicate had already narrowed to "gob MODULATES", so the census blamed a gate
    /// nobody was failing and cost a round trip. There is one chain now.</summary>
    internal static bool Gate(string why) { _ownGate = why; return false; }
    internal static void Bump(ref int counter) { if (!_ownDry) counter++; }
    internal static void Refuse(ref int counter, in AlloyCandidate c, int reason)
    { if (!_ownDry) { counter++; RejReason(reason, c.Cls); OwnRefuse(c.Cls, c.Gob); } }

    /// <summary>#285: the reject tail, decomposed. Every gate PAST the classification already had a named
    /// total and a per-class total, and neither said which gate ate which class -- "87 cutout / 87 water
    /// refused after classifying" was a hole nobody could attribute without a Ghidra session. This is the
    /// cross, and it is the only reason the two axes exist separately.</summary>
    internal static readonly int[] OwnRejByReason = new int[RejCount * AlloyClasses.Count];
    /// <summary>The gates that refuse a part AFTER it has a class. Index into <see cref="OwnRejByReason"/>;
    /// <see cref="RejNames"/> is the printed name of each.</summary>
    internal const int RejVdo = 0, RejSceneTint = 1, RejItem = 2, RejTableFull = 3, RejNoVao = 4, RejCount = 5;
    private static readonly string[] RejNames = { "vdo", "scene-tint", "empty-item", "table-full", "no-vao-slot" };
    internal static void RejReason(int reason, int cls)
    { if (cls >= 0 && cls < AlloyClasses.Count) OwnRejByReason[reason * AlloyClasses.Count + cls]++; }
    internal static string? _ownGate;
    /// <summary>Set while the census asks the real predicate a hypothetical: same gates, no counters, no
    /// unknown-shader notes.</summary>
    internal static bool _ownDry;

    /// <summary>Which gate would refuse this entry, by name -- the real chain, run for its answer only.</summary>
    internal static string WhyNotOwnable(int e)
    {
        // A dry run rewrites the per-entry latches, and this one is called from the INPUT thread (/alloy
        // holes). _ownGate is only a label, but absorbing a real refusal would cost a creature -- so the
        // veto latch is restored, not merely re-set by the next census call.
        bool savedNoVeto = _ownNoVeto;
        _ownDry = true;
        var scratch = default(AlloyCandidate);
        scratch.Cls = -1;
        bool ok = TryOwnCandidate(e, ref scratch);
        _ownDry = false;
        _ownNoVeto = savedNoVeto;
        string cls = scratch.Cls < 0 ? "?" : ((AlloyClass)scratch.Cls).ToString();
        return $"{(ok ? "ownable-but-not-enrolled" : _ownGate ?? "?")} cls={cls}";
    }

    /// <summary>The bit in <see cref="EngineLayout.NodeOwnerFlags"/> that arms a second alpha multiply by
    /// <see cref="EngineLayout.GobFadeAlpha"/> -- Ghidra <c>PartTriMesh::Mat</c>:
    /// <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.</summary>
    internal const int GobOwnerFadeBit = 8;

    /// <summary>Does this instance carry state no Alloy program can reproduce? ONE case -- the SCENE TINT
    /// (<c>gob+0x19</c>), which <c>PartTriMesh::Mat</c> @0x450960 draws as
    /// <c>SetColor4f(scene+0x14c..0x154, 1.0)</c> under <c>Disable(5)</c>: the whole surface replaced by the
    /// scene's FOG COLOUR (<c>Scene::SetFogColor</c> @0x45e220 is that field's setter), unlit, the gob alpha
    /// discarded. COUNTED, not owned -- <c>scene-tint</c> on the census line (#128, #285).
    /// <para>CX-91: and it is a CORRECT SKIP, not a hole in the world frame. The flag has exactly two
    /// writers -- <c>Gob::Gob</c> (0) and <c>Scene::SetSkyFade</c> @0x45e2b0 (1) -- and the only object ever
    /// passed to the latter is the <c>skyfade1</c> model <c>CNWCArea::LoadSkyBox</c> makes. That gob is never
    /// <c>Gob::SetScene</c>'d, so it is not in <c>Scene+0x130</c> and the walk cannot reach it; and the engine
    /// draws it from <c>Scene::RenderSkyBoxes</c> @0x466910 alone, outside every draw bucket, which is the
    /// sky's own path (#125/#136). A world-table draw of it would be a second sky at world depth.
    /// <para>Everything else <c>Mat</c> does with a gob is arithmetic on terms the record already carries --
    /// an alpha multiply, a component-wise colour multiply and a self-illum add -- so those are FOLDED
    /// (<see cref="GobAlphaMul"/> and its two neighbours) rather than refused; a creature fades through
    /// <c>gob+0x108</c>, so refusing modulation would refuse every creature on screen. The flags are read as
    /// VALUES, not as arms: <c>gob+0xf6</c> is set on essentially every instance while its colour is (1,1,1).
    /// <c>gob+0x18</c> is not refused either -- and CX-91 read its writers: it is the SKYBOX flag
    /// (<c>Scene::SetSkyBox</c> @0x45e260), not a highlight, and all it costs is <c>Mat</c>'s
    /// white-unlit skybox branch on a gob the world walk cannot reach (#285).</para>
    /// <para>The per-gob ENV OVERRIDE is not a special class: <c>PartTriMesh::Draw</c> @0x4513d0 routes on
    /// <c>gob+0x480 || Material::HasEnvMap</c> into ONE function, and <c>RenderEnvironmentMapped</c> @0x451040
    /// picks gob+0x480 else mat+0x40 else <c>currentscene+0x1e8</c>, binds it, then calls <c>RenderFlat</c>.
    /// It is the env FLAG with a different texture source, and Alloy replaces that texture with the per-area
    /// IBL probe anyway, so it sets <see cref="AlloyCandidate.Env"/> exactly as a material env map does.
    /// ponytail: RenderEnvironmentMapped also passes <c>part+0xa0 * gob+0x108</c> as the ambient intensity
    /// (the reflection's STRENGTH); the owned record stages ambient.w as 1. Fold it into that slot if
    /// instanced content ever reads too shiny.</para>
    /// Cold, at enrollment only: <c>gob+0x19</c> is the SKYFADE flag, written by <c>Gob::Gob</c> (0) and
    /// <c>Scene::SetSkyFade</c> @0x45e2b0 (1, once per area load on the <c>skyfade1</c> gob before it has
    /// parts) and never cleared, so the enrollment refusal is the whole answer (#330).</summary>
    internal static bool GobUnownable(IntPtr gob)
    {
        var g = new GobRef(gob);
        return g.SceneTint;
    }

    /// <summary>Does this instance override the env map (<c>gob+0x480</c>)? The engine's own routing test in
    /// <c>PartTriMesh::Draw</c> @0x4513d0, ORed with the material's own -- either one sends the draw through
    /// <c>RenderEnvironmentMapped</c>, and the Alloy pass answers both the same way.</summary>
    internal static bool GobHasEnvMap(IntPtr gob)
        => gob != IntPtr.Zero && new GobRef(gob).EnvOverride != IntPtr.Zero;

    /// <summary>The instance's live alpha multiplier: <c>PartTriMesh::Mat</c>'s two multiplies onto
    /// <c>part+0xa0</c>, armed by <c>gob+0xf6</c> and by bit 3 of the owner byte at <c>*(gob+0x68)+0x8e</c>.
    /// Read EVERY frame and never at capture -- this is the field that fades a whole creature into the
    /// transparent run for as little as a single frame, which is why #128 could not cache opacity.</summary>
    internal static float GobAlphaMul(IntPtr part)
    {
        GobRef gob = new PartRef(part).Gob;
        if (gob.IsNull) return 1f;
        float a = gob.ColourMulArmed && !PlaceableHold.Held(gob.Ptr) ? gob.Alpha : 1f;
        GobRef owner = gob.FadeOwner;
        if (!owner.IsNull && owner.OwnerFades)
            a *= gob.FadeAlpha;
        return a;
    }

    /// <summary>The instance's live colour multiply -- <c>cmul</c> @0x5b3a00 onto the diffuse term, armed by
    /// the same <c>gob+0xf6</c>. A component-wise product, so the identity is (1,1,1).</summary>
    internal static System.Numerics.Vector3 GobDiffuseMul(IntPtr part)
    {
        GobRef gob = new PartRef(part).Gob;
        if (gob.IsNull || !gob.ColourMulArmed) return System.Numerics.Vector3.One;
        float* c = gob.Colour;
        return new System.Numerics.Vector3(c[0], c[1], c[2]);
    }

    /// <summary>The instance's live self-illumination term, armed by <c>gob+0xf8</c>. An ADD, so the identity
    /// is zero -- getting that backwards paints every plain instance white.</summary>
    internal static System.Numerics.Vector3 GobEmissiveAdd(IntPtr part)
    {
        GobRef gob = new PartRef(part).Gob;
        if (gob.IsNull || !gob.SelfIllumArmed) return default;
        float* e = gob.Emissive;
        return new System.Numerics.Vector3(e[0], e[1], e[2]);
    }




    /// <summary>Highest draw bucket a shadow caster may live in. 0/1 are the static tile buckets and 2/3 the
    /// dynamic gob pair (opaque/transparent, drawn inside <c>Scene::RenderDynamicGeometry</c>); above that
    /// nothing casts -- 5 is Sample Framebuffer, 6 is water, 7 the tilefade cap, 8 Fade (#103 named these off
    /// the engine's own Tracy scope literals; the older "5+ is water, 8 is per-item alpha" note was wrong).
    /// ponytail: 4 is Additive and arguably should not cast either, but narrowing this is a behaviour change,
    /// not a comment fix -- decide it when the Alloy pipeline takes the residual buckets.</summary>
    internal const int MaxCasterBucket = 4;

    /// <summary>#125: does this material's SharedMaterial name a custom shader the Alloy pass does not
    /// implement? For world geometry an unknown custom shader is owned anyway and drawn with the plain
    /// stage, which is usually a small difference on one part. The sky is the case where it is not: a
    /// dynamic skybox IS its fragment shader (sow-topdata's <c>fst_sky</c> is ~1900 lines of procedural
    /// gradient, clouds, stars, sun and moon over a near-black night texture), so drawing it flat sends the
    /// diffuse alone and the sky comes out black.</summary>
    internal static bool HasUnportedShader(IntPtr mat)
    {
        if (mat == IntPtr.Zero) return false;
        SharedRef s = new MaterialRef(mat).Shared;
        if (s.IsNull) return false;
        // The VS name decides when there is one -- that is #123's model, and a recognised feature covers
        // its fragment half by construction. What the VS test alone MISSES is a .mtr that names only
        // customShaderFS (+0x199, the next resref slot after +0x188 per SharedMaterial::ParseField
        // @0x4c6760): none of the seven features is authored that way, so an FS with no VS is a custom
        // program by definition -- and the black sky is a fragment shader.
        byte* vs = s.VsName;
        if (*vs != 0) return Classify(vs) == 0;
        return s.HasFs;
    }

    /// <summary>#123: match a SharedMaterial's <c>customShaderVS</c> resref against the seven the owned
    /// program reproduces as feature switches. Case-insensitive over the whole NUL-terminated name, so
    /// <c>shd_roofkn_vs</c> cannot swallow <c>shd_roofknm_vs</c>. Cold: one call per shared-material part per
    /// enrollment scan.</summary>
    internal static int Classify(byte* vs)
    {
        var names = ShaderFeatures.VsNames;
        for (int f = 1; f < names.Length; f++)
        {
            string n = names[f];
            int i = 0;
            for (; i < n.Length; i++)
            {
                byte a = vs[i], b = (byte)n[i];
                if (a >= 'A' && a <= 'Z') a += 32;
                if (a != b) break;
            }
            if (i == n.Length && vs[i] == 0) return f;
        }
        return 0;
    }

    /// <summary>Keep the first few unrecognised custom-shader names so the diag can NAME what is still
    /// approximated rather than only counting it -- a number says work remains, a name says which shader.</summary>
    internal static void NoteUnknown(byte* vs)
    {
        if (_unknownCount >= MaxUnknown) return;
        int len = 0;
        while (len < EngineLayout.SharedNameLen - 1 && vs[len] != 0) len++;   // a LENGTH, not a place: the buffer's own size
        // Dedupe on the RAW BYTES first. Every shared-material part reaches this, so allocating a string per
        // part and throwing all but the first away would put hundreds of allocations in an enrollment scan
        // for a diag line that holds at most six.
        for (int i = 0; i < _unknownCount; i++)
        {
            string k = _unknown[i];
            if (k.Length != len) continue;
            int j = 0;
            while (j < len && k[j] == (char)vs[j]) j++;
            if (j == len) return;
        }
        _unknown[_unknownCount++] = System.Text.Encoding.ASCII.GetString(vs, len);
    }

    /// <summary>Distinct unrecognised custom-shader names the diag names. A handful is a report; the whole
    /// set would be a wall, and past a handful the answer is "port more features", not "list them all".</summary>
    internal const int MaxUnknown = 6;
    internal static readonly string[] _unknown = new string[MaxUnknown];
    internal static int _unknownCount;

    /// <summary>#126: candidates seen per <see cref="AlloyClass"/>, and the ones a gate refused after
    /// they were classified. The second array is what the all-owned-or-none rule reads: a class with a
    /// refusal cannot be owned in part, because its draw order is load-bearing (#107 s2). AlloyRenderer
    /// adds its OWN refusals (capacity, VAO slot, unshadeable lit) to it, so one array covers both scans.</summary>
    internal static readonly int[] _ownByClass = new int[AlloyClasses.Count];
    internal static readonly int[] OwnRefusedByClass = new int[AlloyClasses.Count];
    /// <summary>#128: gobs with at least one refused part this enrollment, open-addressed (power of two,
    /// linear probe). Creature colour is all-owned-or-none per INSTANCE, so a creature carrying one part we
    /// cannot draw -- a true skinmesh cloak, an env override, a full table -- has to go back to the engine
    /// entire; this set is what the enrollment sweep asks. Sized well above the instance count of a crowded
    /// exterior, and never dereferenced: a Gob* here is an identity, not an object.</summary>
    /// <summary>#128 coverage: how the INSTANCED population fares through the enrollment scan, which the
    /// per-gate totals cannot answer -- they mix instances in with ten times as many tiles, and the two gates
    /// an instance actually dies at (the state gate, and staleness) are the two that say nothing about kind.
    /// Counted off <see cref="_pGob"/>, which is a copy taken at Record and never dereferenced, so it is safe
    /// on exactly the dead and stale entries this is asking about.</summary>
    internal static int _ownInstSeen, _ownInstDead;
    /// <summary>...and the half of <see cref="_ownInstDead"/> that actually COSTS a creature: a
    /// not-active entry only refuses its gob when it was in this frame's draw buckets and still points at
    /// the same gob. A dead part nobody is drawing is free; a dead part the engine IS drawing takes the
    /// whole instance vanilla through the all-owned-or-none sweep. Reporting the two as one number reads as
    /// "creatures die a lot" when the question is "does anything a creature is DRAWING fail to bind".</summary>
    internal static int _ownInstRefusing;
    /// <summary>...and the part of that population held BACK from refusing: an unbound entry whose primitive
    /// the engine's own totals call empty. Vanilla draws nothing for it and keeps its siblings, so neither
    /// does this. A number that tracks a missing object is the one that says this exemption is what saved it.</summary>
    internal static int _ownInstNoGeom;
    /// <summary>CX-92: entries HELD hidden on the engine's <c>part+0x34</c> subtree gate this scan. Exempt
    /// from the all-owned-or-none refusal for the geometry-less case's reason -- vanilla draws nothing for
    /// them either -- and counted so "a creature is short one limb" can be told from "nothing is hidden".</summary>
    internal static int _ownInstHidden;
    /// <summary>Which gate killed the parts in <see cref="_ownInstRefusing"/>. The one thing the #128
    /// coverage line could not say, and the only thing that separates "the sweep is doing its job" from a
    /// class the binder cannot take -- <c>_pDeath</c> has held the answer per entry all along, unaggregated.
    /// Eight distinct reasons is more than the binder has.</summary>
    private const int MaxInstWhy = 8;
    private static readonly string[] _instWhy = new string[MaxInstWhy];
    private static readonly int[] _instWhyN = new int[MaxInstWhy];

    /// <summary>Tally one instance part that is about to refuse its gob, by the gate that got it. Cold: at
    /// most once per table slot per enrollment.</summary>
    private static void NoteInstRefusing(int e)
    {
        _ownInstRefusing++;
        string why = _pEnt[e].State switch
        {
            StDead => _pDeath[e] ?? "dead",
            StUnbound => "unbound",
            StStale => "stale",
            StEmpty => "empty",
            _ => "?",
        };
        for (int i = 0; i < MaxInstWhy; i++)
        {
            if (_instWhy[i] == null) { _instWhy[i] = why; _instWhyN[i] = 1; return; }
            if (_instWhy[i] == why) { _instWhyN[i]++; return; }
        }
    }

    /// <summary>The tally above, rendered most-costly-first-ish (insertion order; there are at most eight).
    /// Empty string when nothing refused, so the coverage line stays quiet on a healthy scan.</summary>
    private static string InstWhyNote()
    {
        string s = "";
        for (int i = 0; i < MaxInstWhy && _instWhy[i] != null; i++)
            s += (s.Length == 0 ? "" : " ") + _instWhy[i] + "=" + _instWhyN[i];
        return s.Length == 0 ? "" : $" [{s}]";
    }
    /// <summary>Instanced parts ever enumerated. Session-cumulative on purpose: a zero here means the scene
    /// walk is not reaching gob part trees at all, which is a different bug from every gate below.</summary>
    internal static long _pInstRecorded;

    internal const int RefGobHash = 4096;
    internal static readonly IntPtr[] _refGob = new IntPtr[RefGobHash];
    internal static int _refGobCount;
    /// <summary>The set filled up, so "not in the set" no longer means "not refused". The sweep falls back to
    /// dropping EVERY instanced part rather than half-owning a creature -- correct, drastic, and loud on the
    /// period line, which is the right shape for a state that should not occur.</summary>
    internal static bool RefusedGobOverflow;

    /// <summary>Count one enrollment refusal: against the GOB when the part instances off one (the per-instance
    /// sweep takes it from there), against its CLASS when it does not. The two must not be conflated -- the
    /// class-wide blended drop exists for statics, and letting one tinted creature trip it would flap every
    /// blended wall in the area back to vanilla for an enrollment.</summary>
    internal static void OwnRefuse(int cls, IntPtr gob)
    {
        // CX-105: a part of an INACTIVE LOD root may not veto anything. Neither arm -- not the gob (the
        // all-owned-or-none sweep would drop the whole creature) and not the class (the class-wide blended
        // drop would flap every blended wall in the area) -- because the part is one vanilla draws nothing
        // for: Scene::AddPartsTreeToDrawBuckets @0x460880 walks gob+0x90 alone. The same rule the StHidden
        // exemption states, applied to the other subtree the engine gates off. See NoVeto.
        if (_ownNoVeto) { _ownNoVetoed++; return; }
        if (gob == IntPtr.Zero) { OwnRefusedByClass[cls]++; return; }
        if (_refGobCount * 2 >= RefGobHash) { RefusedGobOverflow = true; return; }
        int h = (int)(((ulong)gob >> 4) & (RefGobHash - 1));
        while (_refGob[h] != IntPtr.Zero)
        {
            if (_refGob[h] == gob) return;
            h = (h + 1) & (RefGobHash - 1);
        }
        _refGob[h] = gob; _refGobCount++;
    }

    /// <summary>CX-105: is the candidate the census is holding a part of an INACTIVE LOD root -- i.e. one the
    /// engine's own draw never reaches? Set once per entry, read by <see cref="OwnRefuse"/>, so every gate in
    /// the chain and every later gate in <c>SeatSlot</c> is covered by one test rather than by a flag threaded
    /// through each of them. The census is single-threaded and runs once per entry per enrollment, which is
    /// what makes a latch sound here (the same shape as <see cref="_ownGate"/> and <see cref="_ownDry"/>).
    /// <para>Load-bearing since CX-105's deferred swap: <see cref="SceneWalk"/> enumerates a gob's alternate
    /// roots while a level change is registered, so their parts become candidates for those two frames. Without
    /// this, one refusable part on an alternate LOD dropped the whole creature for the pending window -- and a
    /// creature vanishing is worse than any LOD pop. It is also a latent fix for the pre-CX-105 enumeration,
    /// where all three roots were candidates permanently.</para></summary>
    private static bool _ownNoVeto;
    /// <summary>Refusals the rule above absorbed, for the census line: a steady nonzero count is alternate-LOD
    /// geometry Alloy cannot own, which is exactly why the gob must not be judged by it.</summary>
    internal static int _ownNoVetoed;

    /// <summary>CX-105: does entry <paramref name="e"/> belong to a root its gob is NOT showing?
    /// <para><see cref="PartTable._pLodAlt"/> is set only for a part the walk reached down one of a gob's three
    /// LOD roots, so an appended item, a tile part and a gob with no alternates all answer false and keep
    /// vetoing exactly as before. A part of the ACTIVE root also answers false, which is what keeps a real
    /// refusal on the tree being drawn from being hidden.</para>
    /// <para>Dereferences the gob, so every caller must already have established that the gob is the live
    /// part's (the active-state path reads <c>c.Gob</c> straight off it; the dead-state path is inside the
    /// guard that compares them).</para></summary>
    internal static bool InactiveLodPart(int e)
    {
        if (!PartTable._pLodAlt[e]) return false;
        IntPtr gob = _pGob[e], root = PartTable._pRoot[e];
        if (gob == IntPtr.Zero || root == IntPtr.Zero) return false;
        return new Gob(gob).PartTree.Ptr != root;
    }

    /// <summary>Did any part of this instance get refused this enrollment? Cold: once per Alloy slot, in the
    /// enrollment sweep.</summary>
    internal static bool GobRefused(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return false;
        if (RefusedGobOverflow) return true;
        int h = (int)(((ulong)gob >> 4) & (RefGobHash - 1));
        while (_refGob[h] != IntPtr.Zero)
        {
            if (_refGob[h] == gob) return true;
            h = (h + 1) & (RefGobHash - 1);
        }
        return false;
    }

    /// <summary>Instances the sweep will drop whole this enrollment -- the number that says how much of the
    /// creature population one unownable class is taking with it.</summary>
    internal static int RefusedGobs => _refGobCount;

    /// <summary>#397: does a LATER sibling of <paramref name="part"/> draw the same geometry at the same
    /// place? Same parent, byte-equal local pos/rot/scale, a renderable visible tri-mesh, and the same element
    /// and vertex item counts, and (#317) byte-equal vertex positions when the VDO can be read -- two faces
    /// of one door leaf share everything but the positions. Gob parts only (tiles are BSP items), and only
    /// parents with a small fan-out. ponytail: an intentional coincident overlay (a decal mesh over its base)
    /// with EQUAL counts and positions would lose its base here; dup-sibling on the rejects line says how
    /// often this fires.</summary>
    private static bool DupSibling(IntPtr part, int e, in VdoRef vd, int it)
    {
        IntPtr parent = new Part(part).Parent.Ptr;
        if (parent == IntPtr.Zero) return false;
        IntPtr* kids = (IntPtr*)new Part(parent).Children;
        int n = new Part(parent).ChildCount;
        if (kids == null || n < 2 || n > DupSiblingMaxFanOut) return false;
        int mine = -1;
        for (int i = 0; i < n; i++) if (kids[i] == part) { mine = i; break; }
        if (mine < 0) return false;
        long elem = vd.ElemItems, vert = vd.VertItems;
        int myElems = *(int*)(elem + it * 8 + 4), myVerts = vert != 0 ? *(int*)(vert + it * 8 + 4) : 0;
        for (int j = mine + 1; j < n; j++)
        {
            IntPtr sib = kids[j];
            if (sib == IntPtr.Zero || sib == part) continue;
            IntPtr tri = new Part(sib).AsPartTriMesh().Ptr;
            if (tri == IntPtr.Zero || new Part(tri).Visible == 0 || !PartTable.IsRenderable(tri)) continue;
            // pos (3), rot (4), scale (1) sit contiguously from the position: one 32-byte compare, which is
            // why this reads the BLOCK rather than the three typed fields.
            if (!new ReadOnlySpan<byte>(Unsafe.AsPointer(ref new Part(part).Pos), DupSiblingXformBytes)
                    .SequenceEqual(new ReadOnlySpan<byte>(Unsafe.AsPointer(ref new Part(tri).Pos), DupSiblingXformBytes))) continue;
            int es = PartTable.Find(tri);
            if (es < 0 || _pVdo[es] == IntPtr.Zero || _pItem[es] < 0) continue;
            var sv = new VdoRef(_pVdo[es]); int si = _pItem[es];
            long se = sv.ElemItems, svv = sv.VertItems;
            if (se == 0 || si >= sv.ElemCount) continue;
            if (*(int*)(se + si * 8 + 4) != myElems) continue;
            if (svv != 0 && vert != 0 && *(int*)(svv + si * 8 + 4) != myVerts) continue;
            // #317: same transform and counts is not the same mesh -- a door leaf's two faces pass both
            if (_pVdo[es] == _pVdo[e] && AlloyRenderer.SamePositions(_pVdo[e], it, si, myVerts) == false) continue;
            if (WatchedEntry(e)) _log?.Info($"[dumpframe] ENROLL dup-sibling: entry={e} loses to entry={es} (the later twin)");
            return true;
        }
        return false;
    }
    private const int DupSiblingMaxFanOut = 32;   // not-an-offset: sibling scan bound
    private const int DupSiblingXformBytes = 32;   // not-an-offset: pos+rot+scale span from PartPos (0x10..0x30)
    internal static int _ownRejDupSibling;

    // per-gate enrollment forensics: which predicate is eating the candidates? (cold, scan-scoped)
    internal static int _ownSeen, _ownRejStaleMoved, _ownRejBucket, _ownRejVdo, _ownRejGob, _ownRejFbSample,
                      _ownRejUnrenderable,
                      _ownWithMaps, _ownRejItem, _ownSharedPlain, _ownSharedShader, _ownRejScene,
                      _ownWithEnv, _ownSharedFeature;
    /// <summary>CX-91: candidates enrolled with no diffuse slot at all, drawn on the white stand-in the way
    /// a disabled texture stage draws in vanilla. Coverage, never a refusal.</summary>
    internal static int _ownNoDiffuse;
    internal static IntPtr _ownScene;

    /// <summary>Reset the enrollment reject counters and set the Scene this scan enrolls for; call once
    /// before an enrollment scan. Only groups recorded from <paramref name="scene"/> are candidates (#91).</summary>
    internal static void OwnScanBegin(IntPtr scene)
    {
        _ownScene = scene;
        _ownSeen = _ownRejStaleMoved = _ownRejBucket = _ownRejVdo = _ownRejGob = _ownWithEnv =
            _ownWithMaps = _ownRejItem = _ownSharedPlain = _ownSharedShader = _ownRejScene =
            _ownNoVetoed = 0;
            _ownSharedFeature = _ownRejFbSample = _ownInstSeen = _ownInstDead = _ownInstRefusing = _ownInstNoGeom = _ownInstHidden =
            _ownRejUnrenderable = _ownRejDupSibling = _ownNoDiffuse = 0;
        Array.Clear(_instWhy); Array.Clear(_instWhyN);
        Array.Clear(_ownByClass); Array.Clear(OwnRefusedByClass); Array.Clear(OwnRejByReason);
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        _unknownCount = 0;
    }

    /// <summary>#285: which gate ate which class, printed only where the cross is nonzero. Without it the
    /// two axes are a riddle -- a hole large enough to notice cannot be attributed to a reason.</summary>
    private static string RejCrossNote()
    {
        string s = "";
        for (int r = 0; r < RejCount; r++)
            for (int cl = 0; cl < AlloyClasses.Count; cl++)
            {
                int n = OwnRejByReason[r * AlloyClasses.Count + cl];
                if (n != 0) s += $" {RejNames[r]}/{(AlloyClass)cl}={n}";
            }
        return s.Length == 0 ? "" : $" [by gate:{s}]";
    }

    /// <summary>Why the last enrollment scan rejected what it rejected -- one line, cold.</summary>
    internal static string OwnScanDiag() =>
        $"active={_ownSeen} rejects: stale-identity={_ownRejStaleMoved} bucket={_ownRejBucket} vdo={_ownRejVdo} " +
        $"scene-tint={_ownRejGob} empty-item={_ownRejItem} foreign-scene={_ownRejScene} " +
        $"unrenderable={_ownRejUnrenderable} dup-sibling={_ownRejDupSibling} " +
        $"| no-diffuse={_ownNoDiffuse} owned on the white stand-in " +
        // #128: the instanced population's own fate. "Creatures are not owned" reads identically to
        // "creatures are not in the table"; these three tell them apart without another round trip.
        $"| instances: {_ownInstSeen} live, {_ownInstDead} dead before the scan " +
        $"({_ownInstRefusing} of them DRAWING, i.e. taking their whole creature OUT OF THE FRAME{InstWhyNote()}), " +
        $"{_ownInstNoGeom} geometry-less parts left on their instance, " +
        $"{_ownNoVetoed} inactive-LOD refusals absorbed, " +
        $"{_ownInstHidden} held hidden on part+0x34, " +
        $"{_pInstRecorded} recorded ever, " +
        $"volumetric={_ownRejFbSample}, " +
        // #126: the classification, as coverage. Cutout-vs-blended is the split the whole transparency
        // model turns on, so it is reported rather than inferred from the draw counts.
        $"classes: {_ownByClass[0]} opaque / {_ownByClass[1]} cutout / {_ownByClass[2]} blended / " +
        $"{_ownByClass[(int)AlloyClass.Water]} water / " +
        $"{_ownByClass[(int)AlloyClass.Additive]} additive (refused after classifying: " +
        $"{OwnRefusedByClass[1]} cutout / {OwnRefusedByClass[2]} blended / " +
        $"{OwnRefusedByClass[(int)AlloyClass.Water]} water / " +
        $"{OwnRefusedByClass[(int)AlloyClass.Additive]} additive" + RejCrossNote() + "), " +
        $"owned-with-unused-maps={_ownWithMaps}, env-mapped={_ownWithEnv}, " +
        $"shared-material={_ownSharedPlain} exact + {_ownSharedFeature} by feature switch (#123) + " +
        $"{_ownSharedShader} approximated" +
        (_unknownCount == 0 ? "" : $" ({string.Join(", ", _unknown, 0, _unknownCount)})");


    /// <summary>Triage: every gob term the gate and the fold read, printed rather than reduced to a bool.
    /// "The instance modulates" is not actionable; "it modulates because its owner's fade bit is set" is.</summary>
    internal static string GobTerms(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return "gobmod=-";
        var g = new GobRef(gob);
        IntPtr owner = g.FadeOwner.Ptr;
        // the flag fields print their VALUE, so they stay raw: GobRef's bool would print "hlTrue" for "hl1"
        return $"gobmod=hl{g.View.Highlight}/tint{g.View.SceneTint}" +
               $"/col{g.View.ColourMul}/emi{g.View.SelfIllum}" +
               $"/env{(g.EnvOverride != IntPtr.Zero ? 1 : 0)}" +
               $"/own{(owner == IntPtr.Zero ? -1 : new Model(owner).OwnerFlags & GobOwnerFadeBit)}" +
               $" gmul=a{g.Alpha:0.00}/a2{g.FadeAlpha:0.00}" +
               $" gcol={g.Colour[0]:0.00},{g.Colour[1]:0.00},{g.Colour[2]:0.00}" +
               $" gemi={g.Emissive[0]:0.00},{g.Emissive[1]:0.00},{g.Emissive[2]:0.00}";
    }

    /// <summary>#129 diagnosis: name each distinct water material once. The stage's liquid colour is the
    /// AVERAGE of texture 0, so which texture that is decides the whole body colour of a body of water --
    /// and the two routes into bucket 6 (bumpy-shiny versus <c>sample_framebuffer 2</c>) are different
    /// content with different conventions. Cheap: a set lookup on the handful of water materials an area has.
    /// Deduped on the texture pointer first so a frame-path repeat resolves no name (CX-18).
    /// ponytail: never cleared, so a session that visits many areas keeps a few dozen entries; a recycled
    /// pointer may skip one diagnostic line.</summary>
    internal static void NoteWaterMaterial(IntPtr mat, IntPtr shared)
    {
        var m = new MaterialRef(mat);
        TextureRef t0 = m.Texture(0);
        if (!_waterTex.Add(t0.Ptr)) return;
        string name = t0.ResolveName();
        if (!_waterMats.Add(name)) return;
        bool bumpy = _bumpyWater != null && *_bumpyWater != 0 && Engine.HasBumpyShiny(m);
        int fb = shared == IntPtr.Zero ? -1 : new SharedRef(shared).SampleFb;
        _log?.Info($"[water] material tex0=\"{name}\" tid={t0.ResolveTid()} {TexFormat(t0.ResolveTid())} " +
                   $"route={(bumpy ? "bumpy-shiny" : "sample_framebuffer")} sample_fb={fb} " +
                   $"blend={m.SrcBlend}/{m.DstBlend} " +
                   $"env={Engine.HasEnvMap(m)} {TexTopMip(t0.ResolveTid())} " +
                   $"{AlloyRenderer.DescribeWind()}");
    }

    internal static readonly HashSet<string> _waterMats = new();
    private static readonly HashSet<IntPtr> _waterTex = new();

    /// <summary>Every bound material slot as <c>n:name</c>, for the class diagnostic.</summary>
    private static string SlotNames(MaterialRef m)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < MaterialExtraSlots.First; i++) { var t = m.Texture(i); if (!t.IsNull && AurTex.IsLive(t.Ptr)) sb.Append(i).Append(':').Append(TexName(t.Ptr)).Append(' '); }
        for (int i = MaterialExtraSlots.First; i < MaterialExtraSlots.First + MaterialExtraSlots.Count; i++)
        { var t = m.TextureAt(i); if (!t.IsNull && AurTex.IsLive(t.Ptr)) sb.Append(i).Append(':').Append(TexName(t.Ptr)).Append(' '); }
        return sb.Length == 0 ? "none" : sb.ToString().TrimEnd();
    }
    /// <summary>#397: where a watched part sits -- its node name, the ancestor chain with each part's
    /// <c>+0x34</c> visible byte, the gob, and whether the gob's append-roots list holds it. Two parts
    /// naming one attach node is the doubled-shield shape. Cold: dumptex lines only.</summary>
    internal static string PartForensic(int e)
    {
        IntPtr part = _pEnt[e].Part;
        if (part == IntPtr.Zero) return "part=0";
        static string Nm(IntPtr p)
        {
            MdlNode n = new Part(p).Node;
            return n.IsNull ? "?" : Marshal.PtrToStringAnsi((IntPtr)Unsafe.AsPointer(ref n.Name)) ?? "?";
        }
        var sb = new System.Text.StringBuilder();
        sb.Append($"part=0x{part:x} name={Nm(part)} vis=");
        IntPtr gob = new Part(part).Gob.Ptr;
        int depth = 0; string chain = "";
        for (IntPtr p = part; p != IntPtr.Zero && depth < 16; p = new Part(p).Parent.Ptr, depth++)
        { sb.Append(new Part(p).Visible); if (depth == 1) chain = Nm(p); }
        sb.Append($" parent={chain} gob=0x{gob:x}");
        if (gob != IntPtr.Zero)
        {
            IntPtr* roots = (IntPtr*)new Gob(gob).AppendRoots;
            int n = new Gob(gob).AppendCount, at = -1;
            for (int i = 0; roots != null && i < n; i++) if (roots[i] == part || roots[i] == new Part(part).Parent.Ptr) at = i;
            sb.Append($" appendroots={n} self-or-parent-at={at} root=0x{_pRoot[e]:x} bsp={_pBsp[e]}");
        }
        return sb.ToString();
    }

    internal static string TexName(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "-";
        IntPtr real = new CAurTexture(tex).GetReferencedTexture().Ptr;
        if (real == IntPtr.Zero) real = tex;
        CAurTextureBasic basic = new CAurTexture(real).AsCAurTextureBasic();
        return basic.IsNull ? "?" : Marshal.PtrToStringAnsi((IntPtr)Unsafe.AsPointer(ref basic.Name)) ?? "?";
    }

}
