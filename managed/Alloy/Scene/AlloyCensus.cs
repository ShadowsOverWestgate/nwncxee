using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;
using static Sowx.Alloy.PartTable;
using static Sowx.Alloy.BucketGate;
using static Sowx.Alloy.TexProbe;
namespace Sowx.Alloy;

/// <summary>#188: the ownership census -- which recorded parts the Alloy passes MAY take, and why the rest
/// were passed over. The ownership columns (<see cref="OwnFlag"/>, <see cref="OwnGen"/>,
/// <see cref="OwnSupGen"/>), the ownable-bucket list, the colour candidate feed
/// (<see cref="TryOwnCandidate"/>) and the caster candidate feed (<see cref="TryCasterCandidate"/>), plus
/// the per-gate forensics and the <c>dumpframe</c>/<c>dumptex</c> triage that say what a refusal was.
/// <para>It classifies; it draws nothing and suppresses nothing. What it decides is read by the owned
/// passes (through the candidate feeds) and by <see cref="BucketGate"/> (through the columns), and it is
/// entirely independent of whether <see cref="BatchService"/> is submitting anything.</para>
/// <para>Cold by construction: one call per table slot per enrollment, on a 60-frame cadence. The two
/// exceptions are named at their declarations (<see cref="InstanceOwnable"/> and the gob folds, which the
/// Alloy draw loop calls per part per frame) and allocate nothing.</para></summary>
internal static unsafe class AlloyCensus
{
    internal static ExtenderLog? _log;

    /// <summary>Read <c>dumpframe</c> and <c>dumptex</c> out of <c>sowx.cfg</c> and arm the triage census.
    /// #126: the warm-up counts in PRESENTS, not "the next pass" -- the first census fired on the freshest
    /// possible table and read as enrollment lag rather than steady state.</summary>
    internal static void Configure(ExtenderLog log)
    {
        _log = log;
        string dumpAt = Settings.Text("dumpframe", "");
        _dumpAt = dumpAt.Length == 0 ? -1 : int.TryParse(dumpAt, out int df) && df > 1 ? df : 300;
        // #134: aim the census at a texture instead of at a moment. Five rolls in a row fired while the
        // camera was somewhere else, because "300 frames after the table settled" is not "standing at the
        // thing we are debugging" and no warm-up rule can make it so.
        _dumpTex = Settings.Text("dumptex", "");
        if (_dumpTex.Length == 0) _dumpTex = null;
        _dumpAll = _dumpTex == "*";
        if (_dumpTex != null) _dumpAt = _dumpAt < 0 ? 0 : _dumpAt;
    }

    /// <summary>The world bucket-0 edge: open the triage census if the table has settled for long enough,
    /// and disarm so it only ever runs once. Called from the gate, which is where that edge is.</summary>
    internal static void BeginDump()
    {
        if (_frame - _wipeFrame < _dumpAt) return;
        _dumpAt = -1; _dumpLeft = DumpMax;
        _log?.Info(_dumpTex == null
            ? $"[dumpframe] begin, {_frame - _wipeFrame} frames after the table settled"
            : $"[dumpframe] begin, watching for \"{_dumpTex}\" until {DumpMax} rows or the module ends");
    }

    /// <summary>#126: the material's blend pair -- source factor at +0x90, destination at +0x94, both
    /// <c>Aurora::AuroraBlendModes</c> (Ghidra <c>Material::SetBlendingMode</c> @0x4c5c50 does
    /// <c>SetBlendFunc(*(mat+0x90), *(mat+0x94))</c>). <c>dst == 2</c> is <c>GL_ONE</c>, which is the
    /// engine's own additive test and the one pair it turns the depth mask off for.</summary>
    internal const int MatSrcBlend = 0x90, MatDstBlend = 0x94, BlendOne = 2;
    /// <summary>#126: the three <c>.mtr</c> keywords on the SharedMaterial that steer bucket membership
    /// (<c>SharedMaterial::ParseField</c> @0x4c6760): <c>transparency</c> at +0x1f0, <c>sample_framebuffer</c>
    /// at +0x1f4 (1 = bucket 5, 2 = water) and <c>volumetric</c> at +0x1f8.</summary>
    internal const int SharedTransparency = 0x1f0, SharedSampleFb = 0x1f4, SharedVolumetric = 0x1f8;
    /// <summary>The tilefade cap bucket -- Tracy scope "Tilefade Cap", fed from <c>capbuckets</c>.</summary>
    internal const int CapBucket = 7;
    /// <summary>#134: the dynamic gob pair (#103's taxonomy). Everything instanced lands in one of these two
    /// -- placeables, doors, and the tileset's own static objects -- and until the <c>shaped</c> gate was
    /// corrected nothing in them ever reached enrollment, so excluding them here cost nothing visible. They
    /// are ownable as a PAIR: the engine walks 2 then 3, and owning one without the other splits a single
    /// instance's geometry across two renderers.</summary>
    internal const int GobOpaqueBucket = 2, GobBlendBucket = 3;

    /// <summary>#123: the <c>customShaderVS</c>/<c>FS</c> resrefs the .mtr named, as inline 17-byte NUL-
    /// terminated buffers. Ghidra <c>SharedMaterial::ParseField</c> @0x4c6760 parses them to exactly
    /// <c>this+0x188</c> and <c>this+0x199</c> (GS at +0x1aa, hence the 0x11 stride), and
    /// <c>SharedMaterial::Init</c> @0x4c6ff0 hands those same three addresses to
    /// <c>GetCustomShaderProgram</c>, which confirms them.
    /// <para>This is what a material is classified by, NOT the compiled programs it caches at +0x1c0 (the
    /// discard-capable build) and +0x1c8 (the NO_DISCARD, Early-Z-friendly one that
    /// <c>PartTriMesh::Mat</c> @0x450960 picks for an opaque part that cannot dissolve). Those are filled in
    /// by Init and stay null until it runs, whereas the names are parsed the moment the .mtr loads. An empty
    /// VS name is the real "pure texture redirect" test: such a shared material adds no program at all, and
    /// the Alloy pass reproduces it exactly.</para></summary>
    internal const int SharedVsName = 0x188, SharedNameLen = 0x11;
    /// <summary>#125: <c>customShaderFS</c>, the next 17-byte resref slot after
    /// <see cref="SharedVsName"/> (SharedMaterial::ParseField @0x4c6760 writes +0x188/+0x199/+0x1aa for
    /// VS/FS/GS). A material can name a fragment shader and no vertex shader.</summary>
    internal const int SharedFsName = 0x199;

    /// <summary>#126 triage: one-shot census of every part the ENGINE draws in one world pass -- which
    /// bucket it was captured in, why ownership passed it over, and what its diffuse texture actually is.
    /// Armed by <c>dumpframe</c> in <c>sowx.cfg</c>, fires on the next world bucket-0 entry and disarms. Cold by
    /// construction (it only ever runs on the one frame) and allocating is fine there.</summary>
    internal static int _dumpAt = -1, _dumpLeft;
    /// <summary><c>dumptex</c> (sowx.cfg): log only parts whose diffuse texture name contains this, one frame's worth at a
    /// time, for as long as it takes the camera to find them.</summary>
    internal static string? _dumpTex;
    internal static int _dumpTexFrame = -1, _dumpTexRows;
    /// <summary>Census sampling for <c>dumptex</c>: every Nth frame, up to M matching parts of it.</summary>
    internal const int DumpTexEvery = 120, DumpTexRows = 16, DumpAllRows = 200;
    /// <summary><c>dumptex=*</c> -- census every engine-drawn part of the sampled frame, not one texture's.</summary>
    internal static bool _dumpAll;
    /// <summary>Table slot of the part <c>dumptex</c> last matched, published by the census for enrollment to
    /// recognise without dereferencing anything. -1 until the camera finds it.</summary>
    internal static int _watchEntry = -1;

    /// <summary>#82 ownership columns, driven by AlloyRenderer: 1 = the Alloy world pass owns this part
    /// (the batcher must never draw it), and the pass generation its Alloy draw last succeeded in. They live
    /// here so the iterator's existing <c>Find</c> serves both owners -- no second hash probe per part.</summary>
    internal static readonly byte[] OwnFlag = new byte[MaxParts];

    internal static readonly int[] OwnGen = new int[MaxParts];
    /// <summary>Pass generation in which the iterator last SUPPRESSED this part. Compared against
    /// <see cref="OwnGen"/> to audit the exactly-one-owner invariant: drawn but never suppressed in the
    /// same pass means the engine drew it too (double-draw) or nobody walked it (ghost geometry).</summary>
    internal static readonly int[] OwnSupGen = new int[MaxParts];

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

    /// <summary><c>sample_framebuffer</c>'s two values: 1 routes to draw bucket 5 (refraction glass), 2 to
    /// bucket 6 (water). Different classes, not degrees of one.</summary>
    internal const int SampleFbWater = 2;

    /// <summary>Draw buckets the Alloy pass takes parts from. 0/1 are the static tile pair (#82), and #126
    /// adds the three residual static classes named by <see cref="AlloyClass"/>: 4 additive, 7 the
    /// tilefade cap, 8 the fade buckets. #134 and #128 then added the dynamic gob pair 2/3 -- drawn from
    /// Scene::RenderDynamicGeometry rather than from RenderSinglePass's own walk, but through the same
    /// RenderDrawBucket this service brackets, which is why owning placeables and then creatures needed no new
    /// capture point. What stays the engine's is 5, framebuffer-sampling geometry, and 6, water, both of
    /// which the Alloy pass has no program for and both of which <see cref="TryOwnCandidate"/> also rejects
    /// on the material so a mis-bucketed part cannot slip through.
    /// <para>Membership here and suppression in <c>AlloyRenderer.IterSight</c> are ONE list: suppressing a
    /// bucket we do not draw drops geometry, and drawing one we do not suppress double-draws it.</para></summary>
    internal static bool OwnableBucket(int bucket)
        => bucket == 0 || bucket == 1 || bucket == GobOpaqueBucket || bucket == GobBlendBucket
           || bucket == 4 || bucket == 7 || bucket == AlphaBucket
           || (bucket == AlloyRenderer.WaterBucket && FrameSnapshot.Usable);

    /// <summary>#82 ownership enrollment: is the part in table slot <paramref name="e"/> in the ownable-now
    /// set (#72)? Non-skinned, no bumpy-shiny (both settled at Record), plus --
    /// checked here because they are material/instance state, not capture state -- no gob
    /// modifiers, indexed VDO, an <see cref="OwnableBucket"/>, a live index range, and sighted within the
    /// last frame. Neither a SharedMaterial nor the _nm/_sm map slots reject any more -- see the notes at
    /// their tests.
    /// <para>#126: opacity classifies rather than rejects. What still rejects on the material is the pair the
    /// Alloy pass has no program for -- <c>sample_framebuffer</c> (buckets 5/6) -- and <c>volumetric</c>,
    /// which the engine files in TWO buckets and draws twice per pass, so owning it would break the
    /// one-owner invariant by construction.</para>
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
                       $"cls={c.Cls} state={_pState[e]} seenAgo={_frame - _pSeen[e]} " +
                       $"bucket={(_pState[e] == StActive ? _gBucket[_pGroup[e]] : -1)} " +
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

    /// <summary>Engine draws of a matching part, per frame -- counted before every gate, so a part drawn
    /// outside the bucket walk still lands here.</summary>
    internal static int WatchEngineDraws;

    internal static bool TryOwnCandidateInner(int e, ref AlloyCandidate c)
    {
        // the candidate struct is reused across the whole enrollment scan, and enrollment reads Gob back on a
        // REFUSAL -- so a gate that fires before the part loads must not leave the previous entry's gob behind
        c.Gob = IntPtr.Zero;
        if (!Active) return Gate("inactive");
        if (_pState[e] != StActive)
        {
            if (!_ownDry && _pGob[e] != IntPtr.Zero) _ownInstDead++;
            // #128: a part that died before enrollment could classify it still belongs to a creature, and a
            // creature is all-owned-or-none. The commonest case is a multi-draw part, which dies at Complete
            // as "multidraw-or-unindexed" -- own its siblings and one arm of that creature interleaves a
            // second renderer through every fade. Marked from the copy taken at Record, never from the dead
            // part, and only while it is still drawing, so a despawn clears itself within a frame.
            // ...and the gob must still be the one we copied. An identity-changed entry keeps its old _pGob
            // while a RECYCLED part at that address goes on refreshing the sighting, so without this a reused
            // gob pointer would sweep a live creature every enrollment. Same single-level read the death
            // branch already does, on a part that drew within the frame. The multi-draw case this mark exists
            // for passes it unchanged.
            if (!_ownDry && _pGob[e] != IntPtr.Zero && _pSeen[e] >= _frame - 1
                && _pPart[e] != IntPtr.Zero && new PartRef(_pPart[e]).Gob.Ptr == _pGob[e])
                OwnRefuse(0, _pGob[e]);
            return Gate("state");
        }
        if (OwnFlag[e] != 0) return Gate("OWNED");
        if (!_ownDry) { _ownSeen++; if (_pGob[e] != IntPtr.Zero) _ownInstSeen++; }
        // #236: the ENROLLMENT window is not the DRAW window. Alloy already refuses to draw an owned part
        // the engine stopped walking (the SightedRecently gate in BuildBatches, which costs a compare and a
        // continue), so ownership of an off-screen part is free -- but this gate used the same one-frame
        // window and therefore DROPPED it at every enrollment. Measured live: stale was the only non-zero
        // rejection, 5465 of 12533 active, and 5025 of those had been walked inside the cadence. That is
        // ~5000 parts losing ownership for being behind the camera, re-acquired only at the next enrollment
        // -- which nothing pulls forward, because a returning part is already StActive and never reaches
        // Complete. It is the whole of the residual camera-spin pop-in.
        if (_pSeen[e] < _frame - AlloyRenderer.OwnSightingWindow)
        {
            if (!_ownDry && _pGob[e] != IntPtr.Zero) _ownInstStale++;
            Bump(ref _ownRejStale);
            return Gate("stale");
        }
        // ...and the window it widens is a LIVENESS window, so what it bounded has to be re-proven rather
        // than assumed. The captured node/material were last checked against the live part by the walk, which
        // may now be a whole window ago; every gate below dereferences them. StActive plus the #145 dtor hook
        // says the PART is alive, not that it still wears these. A mismatch is the walk's own
        // "identity-changed" case arriving early -- refuse, and let the next walk recapture.
        if (_pPart[e] != IntPtr.Zero && _pSeen[e] < _frame - 1)
        {
            var live = new PartRef(_pPart[e]);
            if (live.Node.Ptr != _pNode[e] || live.Material.Ptr != _pMat[e])
            { Bump(ref _ownRejStaleMoved); return Gate("stale-identity"); }
        }
        int g = _pGroup[e];
        if (!OwnableBucket(_gBucket[g])) { Bump(ref _ownRejBucket); return Gate("bucket"); }
        // #91: a part recorded from another Scene (a GUI 3D panel) must never enter the world table -- the
        // Alloy block draws the whole table in one go, so one foreign part is area geometry in a portrait.
        if (_gScene[g] != _ownScene) { Bump(ref _ownRejScene); return Gate("scene"); }
        IntPtr v = _pVdo[e], mat = _pMat[e], part = _pPart[e];
        var p = new PartRef(part); var m = new MaterialRef(mat); var vd = new VdoRef(v);
        // Published the moment the part is in hand, and kept through every refusal from here down: creature
        // colour is all-owned-or-none per instance (#107/#128), so a refusal has to be attributable to its
        // gob. The gates ABOVE this line need no attribution -- a stale or foreign-scene part is not drawing.
        c.Gob = p.Gob.Ptr;
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
        // #126: sample_framebuffer and volumetric reject on the MATERIAL, ahead of everything else, because
        // they are the two classes the Alloy pass has no answer for: the first is buckets 5/6 (refraction
        // glass and water, both of which read a framebuffer copy this pass does not take yet), and the second
        // the engine files in two buckets and draws twice per pass, which no one-owner scheme survives. Both
        // are SharedMaterial keywords parsed straight out of the .mtr (SharedMaterial::ParseField @0x4c6760).
        // #129: ...except sample_framebuffer 2, which IS water and which the Alloy pass now draws. The two
        // values are different classes, not degrees of one: 1 is refraction glass (draw bucket 5) and 2 is
        // water (bucket 6), and Scene::AddPartsTreeToDrawBuckets @0x460880 branches on exactly that.
        bool water = IsWater(mat, shared);
        if (water) NoteWaterMaterial(mat, shared);
        if (!s.IsNull && ((s.SampleFb != 0 && !water) || s.Volumetric))
        { Bump(ref _ownRejFbSample); return Gate("fb/volumetric"); }
        // ...and the classification itself, which from here on is what a refusal is counted AGAINST: the
        // blended class is all-owned-or-none (#107 s2), so every gate below has to say which class it ate.
        c.Src = m.SrcBlend; c.Dst = m.DstBlend;
        // The cap bucket overrides the material: RenderSinglePass draws bucket 7 with Disable(0) and
        // Disable(1) -- blending and the alpha discard both off -- so a cap is an opaque draw whatever its
        // material declares.
        c.Cls = _gBucket[g] == CapBucket ? (int)AlloyClass.Opaque
              : (int)AlloyClasses.Of(water, c.Dst == BlendOne, Engine.IsOpaque(p),
                    !s.IsNull && s.Transparency,
                    p.Alpha < 1f);
        if (!_ownDry) _ownByClass[c.Cls]++;
        if (v == IntPtr.Zero || vd.Mode != 1 || _pTex0[e] == IntPtr.Zero)
        { Refuse(ref _ownRejVdo, c); return Gate("vdo"); }
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
        { Refuse(ref _ownRejGob, c); return Gate("gob-unownable"); }
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
        if (elem == 0 || it < 0 || it >= vd.ElemCount || it >= vd.VertCount)
        { Refuse(ref _ownRejItem, c); return Gate("item"); }
        if (*(int*)(elem + it * 8 + 4) <= 0) { Refuse(ref _ownRejItem, c); return Gate("item-range"); }

        c.Bucket = _gBucket[g];
        c.Tri = part; c.Node = _pNode[e]; c.Mat = mat; c.Vdo = v; c.Tex0 = _pTex0[e];
        c.Item = it; c.Prim = _pPrim[e]; c.RotBits = _pRot[e]; c.Mx = _pMx[e];
        c.Lit = _gLight[g];
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
    internal static void Refuse(ref int counter, in AlloyCandidate c)
    { if (!_ownDry) { counter++; OwnRefuse(c.Cls, c.Gob); } }
    internal static string? _ownGate;
    /// <summary>Set while the census asks the real predicate a hypothetical: same gates, no counters, no
    /// unknown-shader notes.</summary>
    internal static bool _ownDry;

    /// <summary>Which gate would refuse this entry, by name -- the real chain, run for its answer only.</summary>
    internal static string WhyNotOwnable(int e)
    {
        _ownDry = true;
        var scratch = default(AlloyCandidate);
        scratch.Cls = -1;
        bool ok = TryOwnCandidate(e, ref scratch);
        _ownDry = false;
        string cls = scratch.Cls < 0 ? "?" : ((AlloyClass)scratch.Cls).ToString();
        return $"{(ok ? "ownable-but-not-enrolled" : _ownGate ?? "?")} cls={cls}";
    }

    /// <summary>Gob fields that change how a part shades, all read by <c>PartTriMesh::Mat</c> @0x450960
    /// except the env override, which <c>PartTriMesh::Draw</c> @0x4513d0 reads to reroute the whole draw to
    /// <c>RenderEnvironmentMapped</c>. The two flags ARM a modulation; the values beside them are the
    /// modulation, and <c>gob+0xfc</c> is the colour whose 12 bytes end exactly where the alpha begins.</summary>
    internal const int GobHighlight = 0x18, GobSceneTint = 0x19, GobColourMul = 0xf6, GobSelfIllum = 0xf8,
                      GobFadeOwner = 0x68, GobEnvOverride = 0x480,
                      GobColour = 0xfc, GobAlpha = 0x108, GobFadeAlpha = 0x10c, GobEmissive = 0x110;
    /// <summary>The byte on the gob's owner (<c>gob+0x68</c>) whose bit 3 arms a second alpha multiply
    /// (<c>gob+0x10c</c>) -- Ghidra <c>PartTriMesh::Mat</c>: <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.</summary>
    internal const int GobOwnerFlags = 0x8e, GobOwnerFadeBit = 8;

    /// <summary>#128: does this instance carry state no Alloy program can reproduce? TWO cases, and only
    /// two -- the highlight modes, which do not shade at all but paint a selection colour sampled from the
    /// Scene.
    /// <para>Everything else <c>PartTriMesh::Mat</c> @0x450960 does with a gob is arithmetic on terms the
    /// SowxDraw record already carries -- an alpha multiply, a component-wise colour multiply and a self-illum
    /// add -- so #128 FOLDS them (see <see cref="GobAlphaMul"/> and its two neighbours) rather than refusing
    /// the part. That fold is what makes creatures ownable at all: a creature fades through <c>gob+0x108</c>,
    /// so under #134's rule "modulating" and "on screen and interesting" were the same population.</para>
    /// <para>#134's own finding still holds and is why the flags below are read as VALUES, not as arms:
    /// <c>gob+0xf6</c> is set on essentially every instance in the game while its colour is (1,1,1). The
    /// cases here refuse whatever their values, because neither is a multiply at all.</para>
    /// <para>The per-gob ENV OVERRIDE used to refuse here and was the gate that actually kept the whole
    /// instanced population out -- a live census put it on 2814 of 3110 instanced part draws, against 296
    /// owned, so #134's placeables were never really owned either. It is not a special class:
    /// <c>PartTriMesh::Draw</c> @0x4513d0 routes on <c>gob+0x480 || Material::HasEnvMap</c> into ONE function,
    /// and <c>RenderEnvironmentMapped</c> @0x451040 picks gob+0x480 else mat+0x40 else the scene's own
    /// <c>currentscene+0x1e8</c>, binds it, and then calls <c>RenderFlat</c> anyway. It is the env FLAG with a
    /// different source for the texture, and #121's Alloy path already replaces that texture with the per-area
    /// IBL probe. So it sets <see cref="AlloyCandidate.Env"/> now, exactly as a material env map does.
    /// <para>ponytail: RenderEnvironmentMapped also passes <c>part+0xa0 * gob+0x108</c> as the ambient
    /// intensity to <c>SetPixelShaderAmbientColor</c> -- the reflection's STRENGTH. Not reproduced; the owned
    /// record stages ambient.w as 1. Fold it into that slot if instanced content ever reads too shiny.</para></para>
    /// Cold at enrollment, hot once per Alloy part per frame via <see cref="InstanceOwnable"/>.</summary>
    internal static bool GobUnownable(IntPtr gob)
    {
        var g = new GobRef(gob);
        return g.Highlight || g.SceneTint;
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
        float a = gob.ColourMulArmed ? gob.Alpha : 1f;
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

    /// <summary>Hot (once per Alloy part per frame): may this part's instance still be drawn by the owned
    /// pass at all? A part with no gob trivially may, which is the tile case and the only load most calls do.
    /// <para>#128: the states this asks about are TRANSIENT -- a placeable highlights for exactly as long as
    /// the cursor is over it -- so the draw loop SKIPS on a false rather than revoking, and the engine's own
    /// walk draws it highlighted. Revoking would spend a full re-enrollment on a hover.</para></summary>
    internal static bool InstanceOwnable(IntPtr part)
    {
        IntPtr gob = new PartRef(part).Gob.Ptr;
        return gob == IntPtr.Zero || !GobUnownable(gob);
    }

    /// <summary>#93 caster enrollment: is the part in table slot <paramref name="e"/> a rigid shadow caster?
    /// Deliberately looser than <see cref="TryOwnCandidate"/> -- a depth pass has no material, so env maps,
    /// SharedMaterial, gob modifiers and the missing diffuse slot are all irrelevant. What remains is what
    /// the depth draw actually needs: an indexed (mode-1) VDO with a live index range, a class that casts
    /// (opaque, or -- since the cutout caster below -- alpha-tested), and a recent sighting. Buckets 0..4 pass, which
    /// is exactly what makes tiles (0/1) and placeables (2/3/4) one set with no kind branching; skinned parts
    /// fall out at the mode-1 test and stay #96's problem. Cold: one call per table slot per enrollment.</summary>
    /// <summary>#151: will the caster CENSUS carry this entry? The cheap prefix of
    /// <see cref="TryCasterCandidate"/>'s acceptance -- state, sighting, bucket, world -- used by the owned
    /// pass to avoid double-recording a part the table already casts. An entry probation KILLED (the moving
    /// creature piece) fails here, which is exactly the population the per-frame recast exists for.</summary>
    internal static bool CasterCensusCovers(int e)
    {
        if ((uint)e >= MaxParts || !Active || _pState[e] != StActive) return false;
        if (_pSeen[e] < _frame - 1) return false;
        int g = _pGroup[e];
        return _gBucket[g] <= MaxCasterBucket && _gWorld[g];
    }

    internal static bool TryCasterCandidate(int e, ref CasterCandidate c)
    {
        if (!Active || _pState[e] != StActive) return false;
        if (_pSeen[e] < _frame - 1) return false;
        int g = _pGroup[e];
        if (_gBucket[g] > MaxCasterBucket) return false;
        // #91, the caster half: a GUI 3D panel's parts sit at that panel's node coordinates, so enrolling one
        // as a caster paints a phantom shadow into the world from geometry that exists only in a portrait.
        // Caster enrollment runs at the present with no Scene in hand, hence the flag rather than a comparison.
        if (!_gWorld[g]) return false;
        IntPtr v = _pVdo[e], part = _pPart[e];
        var p = new PartRef(part); var vd = new VdoRef(v);
        if (v == IntPtr.Zero || vd.Mode != 1) return false;
        // #126 follow-up -- the cutout caster. "Opaque geometry casts, transparent geometry does not" was
        // the vanilla-shaped rule, and vanilla only ever cast authored stencil volumes, so foliage cast
        // NOTHING and the question never came up. A rasterizing caster pass can do better: the 0.2 cutout
        // that makes a leaf a leaf in the colour pass makes it a leaf in the shadow map too.
        // So the class decides, not the opacity bit:
        //   Opaque  -- casts solid, no texture fetch (the majority).
        //   Cutout  -- casts ALPHA-TESTED. New, and the whole point of this.
        //   Blended -- does not cast. A binary shadow map can only say "fully shadowed" about a surface that
        //              is by definition partly see-through; that wants transmittance (M4), not a lie.
        //   Additive-- does not cast. It emits light.
        IntPtr mat = _pMat[e];
        var m = new MaterialRef(mat);
        var s = m.Shared;
        if (!s.IsNull && (s.SampleFb != 0 || s.Volumetric)) return false;
        bool env = !m.IsNull && Engine.HasEnvMap(m);
        var cls = m.IsNull ? AlloyClass.Opaque
                // ...never water: the sample_framebuffer refusal above already sent it back, and bucket 6 is
                // above MaxCasterBucket anyway, so nothing in the water class reaches the caster pass.
                : AlloyClasses.Of(false,
                                         m.DstBlend == BlendOne, Engine.IsOpaque(p),
                      !s.IsNull && s.Transparency,
                      p.Alpha < 1f);
        if (cls != AlloyClass.Opaque && cls != AlloyClass.Cutout) return false;
        long elem = vd.ElemItems;
        int it = _pItem[e];
        if (elem == 0 || it < 0 || it >= vd.ElemCount || it >= vd.VertCount) return false;
        if (*(int*)(elem + it * 8 + 4) <= 0) return false;

        c.Tri = part; c.Node = _pNode[e]; c.Mat = _pMat[e]; c.Vdo = v;
        c.Item = it; c.Prim = _pPrim[e]; c.Mx = _pMx[e];
        // WHETHER a caster is alpha-tested is decided by its texture, inside the bracket (CasterPass's
        // TexHasAlpha) -- not here, and never from the engine's opacity flags, which short-circuit on an env
        // map, on the gob alpha and on their own memo. What IS decided here is the THRESHOLD, and only one
        // authoring makes a difference to it: an env-mapped material's diffuse alpha is reflectivity rather
        // than coverage (inc_standard.shd:248), so it must not be allowed to cut holes in a solid surface's
        // shadow at the cutout threshold.
        c.EnvAlpha = env;
        return true;
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
        while (len < SharedNameLen - 1 && vs[len] != 0) len++;
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
    internal static int _ownInstSeen, _ownInstStale, _ownInstDead;
    /// <summary>Instanced parts ever Recorded. Session-cumulative on purpose: a zero here means instances are
    /// not reaching PartTriMesh::Draw at all, which is a different bug from every gate below.</summary>
    internal static int _pInstRecorded;

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
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyCensus.GobSelfCheck FAIL: {w}"); } }

        byte* gob = stackalloc byte[0x500];
        byte* owner = stackalloc byte[0x100];
        byte* part = stackalloc byte[0x100];
        for (int i = 0; i < 0x500; i++) gob[i] = 0;
        for (int i = 0; i < 0x100; i++) { owner[i] = 0; part[i] = 0; }
        *(IntPtr*)(part + PartGob) = (IntPtr)gob;
        IntPtr p = (IntPtr)part;

        // an unarmed gob is the identity of all three terms -- the case every plain instance takes
        E(!GobUnownable((IntPtr)gob), "a bare gob is ownable");
        E(GobAlphaMul(p) == 1f, "alpha identity is 1");
        E(GobDiffuseMul(p) == System.Numerics.Vector3.One, "colour identity is (1,1,1) -- it is a MULTIPLY");
        E(GobEmissiveAdd(p) == default, "self-illum identity is 0 -- it is an ADD");
        // ...and so is a part with no gob at all, without dereferencing anything
        *(IntPtr*)(part + PartGob) = IntPtr.Zero;
        E(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "a gobless part folds nothing");
        *(IntPtr*)(part + PartGob) = (IntPtr)gob;

        // the colour/alpha pair, armed together by gob+0xf6 and dead without it
        *(float*)(gob + GobAlpha) = 0.5f;
        *(float*)(gob + GobColour) = 0.25f;
        *(float*)(gob + GobColour + 4) = 1f;
        *(float*)(gob + GobColour + 8) = 1f;
        E(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "unarmed values do not apply");
        *(short*)(gob + GobColourMul) = 1;
        E(GobAlphaMul(p) == 0.5f, "armed gob alpha multiplies");
        E(GobDiffuseMul(p) == new System.Numerics.Vector3(0.25f, 1f, 1f), "armed gob colour multiplies, in order");

        // the SECOND alpha multiply, armed on the gob's OWNER rather than on the gob -- the fade that moves a
        // whole creature into the transparent run, and the one term whose flag is not on the object it scales
        *(float*)(gob + GobFadeAlpha) = 0.5f;
        E(GobAlphaMul(p) == 0.5f, "the fade alpha is dead without its owner");
        *(IntPtr*)(gob + GobFadeOwner) = (IntPtr)owner;
        E(GobAlphaMul(p) == 0.5f, "...and dead without the owner's fade bit");
        owner[GobOwnerFlags] = GobOwnerFadeBit;
        E(GobAlphaMul(p) == 0.25f, "both multiplies compose");

        *(short*)(gob + GobSelfIllum) = 1;
        *(float*)(gob + GobEmissive + 4) = 2f;
        E(GobEmissiveAdd(p) == new System.Numerics.Vector3(0f, 2f, 0f), "armed self-illum adds, in order");

        // ...and none of that made the instance unownable, which is the whole of #128 over #134
        E(!GobUnownable((IntPtr)gob), "a MODULATING instance is still ownable: the terms are folded, not refused");
        gob[GobHighlight] = 1;
        E(GobUnownable((IntPtr)gob), "a highlight is not");
        gob[GobHighlight] = 0; gob[GobSceneTint] = 1;
        E(GobUnownable((IntPtr)gob), "nor a scene tint");
        gob[GobSceneTint] = 0;
        E(!GobHasEnvMap((IntPtr)gob), "a bare gob overrides no env map");
        *(IntPtr*)(gob + GobEnvOverride) = (IntPtr)owner;
        E(!GobUnownable((IntPtr)gob), "a per-instance env override is OWNABLE -- it is the env flag, not a class");
        E(GobHasEnvMap((IntPtr)gob), "...and it reports as one, so the candidate takes #121's env path");
        E(!GobHasEnvMap(IntPtr.Zero), "a gobless part overrides nothing, without dereferencing");

        // the refused-gob set, over pointers it never dereferences
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);
        E(!GobRefused((IntPtr)gob), "an empty set refuses nothing");
        E(!GobRefused(IntPtr.Zero), "and a gobless part is never in it");
        OwnRefuse(2, (IntPtr)gob);
        OwnRefuse(2, (IntPtr)gob);
        E(GobRefused((IntPtr)gob) && _refGobCount == 1, "a gob is remembered once, however often it is refused");
        E(!GobRefused((IntPtr)owner), "and only the gob that was refused");
        E(OwnRefusedByClass[2] == 0, "a GOB refusal never trips the class-wide blended drop");
        OwnRefuse(2, IntPtr.Zero);
        E(OwnRefusedByClass[2] == 1, "...and a static refusal still does");
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);

        // #149: the group-key intern. The linear walk it replaced IS the specification, so every lookup here
        // is done BOTH ways and the two must agree -- including the pair a hash gets wrong on its own: a
        // matrix differing in one float (a different group) and a -0/+0 pair (the SAME group, because
        // -0f != 0f is false, so the comparison calls them equal while their bits differ).
        int savedG = _groupCount, savedB = _bucketId, savedPh = _phase;
        IntPtr savedSc = _bucketScene;
        try
        {
            _groupCount = 0; _bucketId = 0; _phase = 0; _bucketScene = IntPtr.Zero;
            _gSlot.AsSpan().Fill(-1);
            float* m = stackalloc float[16];
            for (int i = 0; i < 16; i++) m[i] = i;

            E(SelfGroup(1, m, E) == 0 && _groupCount == 1, "a fresh key mints group 0");
            E(SelfGroup(1, m, E) == 0 && _groupCount == 1, "...and the same key interns to it, not to a second");
            E(SelfGroup(2, m, E) == 1 && _groupCount == 2, "a different VDO is a different group");
            m[7] = 99f;
            E(SelfGroup(1, m, E) == 2 && _groupCount == 3, "a matrix differing in ONE float is a different group");
            m[7] = 0f;
            int plus = SelfGroup(1, m, E);
            m[7] = -0f;
            E(SelfGroup(1, m, E) == plus, "-0 and +0 in the matrix are the SAME group");

            // ...and the SHIFT spreads across the whole table. This one is invisible to any correctness
            // oracle -- a mis-bound shift still returns the right group, it just crams every key into the
            // first 1/N of the slots and turns O(1) probing back into a linear walk. It has now been a live
            // bug twice (TexKey's hardcoded >>53, and this table inherited the pattern), so it is asserted
            // rather than trusted: distinct keys must reach the upper half of the table.
            int top = 0;
            for (int i = 0; i < 256; i++)
            {
                ulong hh = GroupHash((IntPtr)(0x1000 + i * 0x30), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                     IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, false, false, false, m);
                int sl = (int)(hh * 0x9E3779B97F4A7C15UL >> (64 - GroupHashBits));
                if (sl > top) top = sl;
            }
            E(top >= GroupHashSize / 4,
              $"the group hash spreads over the whole table (highest of 256 slots {top} of {GroupHashSize}) " +
              "-- a shift not bound to GroupHashBits clusters every key into the low slots");
        }
        finally
        {
            _groupCount = savedG; _bucketId = savedB; _phase = savedPh; _bucketScene = savedSc;
            _gSlot.AsSpan().Fill(-1);
        }

        // The table-pressure decision, which is the whole of the "renderer keeps falling back to vanilla in
        // big areas" fix and is otherwise only observable on a live client in a big area. Runs on the real
        // arrays and restores them: nothing is recorded yet at self-check time, so there is nothing to lose,
        // but the counters are put back regardless in case that ever stops being true.
        int savedParts = _partCount, savedGroups = _groupCount, savedGen = TableGen;
        bool savedSat = _saturated;
        try
        {
            // a table full of the LIVE is a big area: saturate, never wipe
            for (int i = 0; i < 64; i++) _pState[i] = StActive;
            for (int i = 64; i < 96; i++) _pState[i] = StDead;
            _saturated = false; Log = null;
            OnTablePressure();
            E(_saturated && TableGen == savedGen, "a mostly-LIVE table saturates and is NOT wiped");
            E(_satLive == 64 && _satDead == 32, "...and the census it reports is the one it measured");
            // ...and a table full of the DEAD is transition debris: wipe, which is what the wipe is for
            for (int i = 0; i < 64; i++) _pState[i] = StDead;
            OnTablePressure();
            E(!_saturated && TableGen == savedGen + 1, "a mostly-DEAD table is wiped as transition debris");
        }
        finally
        {
            Array.Clear(_pState); Array.Clear(_pPart); Array.Clear(OwnFlag);
            _partCount = savedParts; _groupCount = savedGroups; TableGen = savedGen; _saturated = savedSat;
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
            E(e1 >= 0 && Find(p1) == e1, "a fresh part inserts and is found");
            _pState[e1] = StActive;
            E(MarkFreed(p1) == e1, "Part::~Part finds the entry that held the dying pointer");
            E(Find(p1) < 0, "...and the address stops resolving to its tombstone");
            int e2 = Insert(p1);
            E(e2 == e1 && Find(p1) == e2, "a part recycled onto the same address re-records into that slot");
            E(_pState[e2] == StEmpty && OwnFlag[e2] == 0 && _partCount == 1,
              "...as a slot indistinguishable from a fresh one, and without double-counting the part");
            // and the load factor must not gate reuse: _partCount only ever climbs, so a long session
            // otherwise reaches a point where every recycled pointer is refused forever
            _pState[e2] = StActive; MarkFreed(p1);
            _partCount = MaxParts;
            E(Insert(p1) == e1, "a freed slot is reusable even with the table's load factor exhausted");
            E(Insert((IntPtr)0x8000) < 0, "...while a genuinely new pointer is still refused there");
            E(_reclaims == 2, "and each reuse is counted");

            // ...and the slot leaves its old group's member chain on the way out. Complete pushes and
            // nothing else ever removes, so a slot reused while still linked either points _pNext at itself
            // -- an unbounded submit walk on the RENDER thread -- or splices its old group into its new one.
            int sg = _groupCount;
            _groupCount = 1; _gFirst[0] = e1; _gCount[0] = 3;
            _pGroup[e1] = 0; _pNext[e1] = 40; _pGroup[40] = 0; _pNext[40] = 41; _pGroup[41] = 0; _pNext[41] = -1;
            _pState[e1] = StActive; MarkFreed(p1);
            E(Insert(p1) == e1, "the reuse under test still takes the same slot");
            E(_gFirst[0] == 40 && _pNext[41] == -1 && _gCount[0] == 2,
              "a reused HEAD leaves the group chain, which stays walkable and loses exactly one member");
            E(_pNext[e1] == -1, "...and the slot itself links to nothing, so re-completing it cannot self-loop");
            // the same from the middle of the chain, which is the splice case
            _gFirst[0] = 40; _pNext[40] = e1; _pNext[e1] = 41; _gCount[0] = 3; _pGroup[e1] = 0;
            _pState[e1] = StActive; MarkFreed(p1);
            E(Insert(p1) == e1 && _pNext[40] == 41 && _gCount[0] == 2,
              "a reused MIDDLE member is spliced out, not out of the chain's other links");
            _groupCount = sg; _gFirst[0] = -1; _gCount[0] = 0;
        }
        finally
        {
            Array.Clear(_pState); Array.Clear(_pPart); Array.Clear(_pFreed); Array.Clear(OwnFlag);
            Array.Clear(_pGroup); Array.Clear(_pNext);
            _partCount = savedParts; _reclaims = savedReclaims;
        }

        log.Info(ok ? "AlloyCensus.GobSelfCheck: passed" : "AlloyCensus.GobSelfCheck: FAILED");
        return ok;
    }

    // per-gate enrollment forensics: which predicate is eating the candidates? (cold, scan-scoped)
    internal static int _ownSeen, _ownRejStale, _ownRejStaleMoved, _ownRejBucket, _ownRejVdo, _ownRejGob, _ownRejFbSample,
                      _ownWithMaps, _ownRejItem, _ownSharedPlain, _ownSharedShader, _ownRejScene,
                      _ownWithEnv, _ownSharedFeature;
    internal static IntPtr _ownScene;

    /// <summary>Reset the enrollment reject counters and set the Scene this scan enrolls for; call once
    /// before an enrollment scan. Only groups recorded from <paramref name="scene"/> are candidates (#91).</summary>
    internal static void OwnScanBegin(IntPtr scene)
    {
        _ownScene = scene;
        _ownSeen = _ownRejStale = _ownRejStaleMoved = _ownRejBucket = _ownRejVdo = _ownRejGob = _ownWithEnv =
            _ownWithMaps = _ownRejItem = _ownSharedPlain = _ownSharedShader = _ownRejScene =
            _ownSharedFeature = _ownRejFbSample = _ownInstSeen = _ownInstStale = _ownInstDead = 0;
            _engineWaterWalks = _ownedWaterWalks = 0;
        Array.Clear(_ownByClass); Array.Clear(OwnRefusedByClass);
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        _unknownCount = 0;
    }

    /// <summary>Why the last enrollment scan rejected what it rejected -- one line, cold.</summary>
    internal static string OwnScanDiag() =>
        $"active={_ownSeen} rejects: stale={_ownRejStale} (unwalked for {AlloyRenderer.OwnSightingWindow}+ frames) stale-identity={_ownRejStaleMoved} bucket={_ownRejBucket} vdo={_ownRejVdo} " +
        $"gob={_ownRejGob} item={_ownRejItem} foreign-scene={_ownRejScene} " +
        // #128: the instanced population's own fate. "Creatures are not owned" reads identically to
        // "creatures are not in the table"; these three tell them apart without another round trip.
        $"| instances: {_ownInstSeen} live ({_ownInstStale} of them stale), {_ownInstDead} dead before the scan, " +
        $"{_pInstRecorded} recorded ever, " +
        $"fb-sampling/volumetric={_ownRejFbSample}, " +
        // #126: the classification, as coverage. Cutout-vs-blended is the split the whole transparency
        // model turns on, so it is reported rather than inferred from the draw counts.
        $"classes: {_ownByClass[0]} opaque / {_ownByClass[1]} cutout / {_ownByClass[2]} blended / " +
        $"{_ownByClass[(int)AlloyClass.Water]} water / " +
        $"{_ownByClass[(int)AlloyClass.Additive]} additive (refused after classifying: " +
        $"{OwnRefusedByClass[1]} cutout / {OwnRefusedByClass[2]} blended / " +
        $"{OwnRefusedByClass[(int)AlloyClass.Water]} water / " +
        $"{OwnRefusedByClass[(int)AlloyClass.Additive]} additive), " +
        // #129: the ticket's own criterion, as a counter. The engine takes a copy per bucket-6 walk it runs,
        // so "engine water walks" IS "engine framebuffer copies for water" -- and the number that has to be
        // zero while Alloy water is drawing.
        $"water walks: {_ownedWaterWalks} owned / {_engineWaterWalks} engine (each engine walk is one " +
        $"engine framebuffer copy), " +
        $"owned-with-unused-maps={_ownWithMaps}, env-mapped={_ownWithEnv}, " +
        $"shared-material={_ownSharedPlain} exact + {_ownSharedFeature} by feature switch (#123) + " +
        $"{_ownSharedShader} approximated" +
        (_unknownCount == 0 ? "" : $" ({string.Join(", ", _unknown, 0, _unknownCount)})");

    internal const int DumpMax = 6000;
    /// <summary>CAurTextureBasic's 32-byte name field (Ghidra CAurTextureBasic::CAurTextureBasic @0x487250:
    /// <c>strncpy(this + 0x88, name, 0x1f)</c>). Reached the way AurTextureMessage @0x488b10 reaches it --
    /// vtable slot 0x18 resolves a reference texture to the real one, slot 0x10 is AsCAurTextureBasic.</summary>
    internal const int TexBasicName = 0x88, TexVtblAsBasic = 0x10, TexVtblReferenced = 0x18;

    /// <summary>Triage: every gob term the gate and the fold read, printed rather than reduced to a bool.
    /// "The instance modulates" is not actionable; "it modulates because its owner's fade bit is set" is.</summary>
    internal static string GobTerms(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return "gobmod=-";
        var g = new GobRef(gob);
        IntPtr owner = g.FadeOwner.Ptr;
        // the flag fields print their VALUE, so they stay raw: GobRef's bool would print "hlTrue" for "hl1"
        return $"gobmod=hl{*(byte*)(gob + GobHighlight)}/tint{*(byte*)(gob + GobSceneTint)}" +
               $"/col{*(short*)(gob + GobColourMul)}/emi{*(short*)(gob + GobSelfIllum)}" +
               $"/env{(g.EnvOverride != IntPtr.Zero ? 1 : 0)}" +
               $"/own{(owner == IntPtr.Zero ? -1 : *(byte*)(owner + GobOwnerFlags) & GobOwnerFadeBit)}" +
               $" gmul=a{g.Alpha:0.00}/a2{g.FadeAlpha:0.00}" +
               $" gcol={g.Colour[0]:0.00},{g.Colour[1]:0.00},{g.Colour[2]:0.00}" +
               $" gemi={g.Emissive[0]:0.00},{g.Emissive[1]:0.00},{g.Emissive[2]:0.00}";
    }

    /// <summary>#129 diagnosis: name each distinct water material once. The stage's liquid colour is the
    /// AVERAGE of texture 0, so which texture that is decides the whole body colour of a body of water --
    /// and the two routes into bucket 6 (bumpy-shiny versus <c>sample_framebuffer 2</c>) are different
    /// content with different conventions. Cheap: a set lookup on the handful of water materials an area has.
    /// ponytail: never cleared, so a session that visits many areas keeps a few dozen strings.</summary>
    internal static void NoteWaterMaterial(IntPtr mat, IntPtr shared)
    {
        var m = new MaterialRef(mat);
        TextureRef t0 = m.Texture(0);
        string name = t0.ResolveName();
        if (!_waterMats.Add(name)) return;
        bool bumpy = _bumpyWater != null && *_bumpyWater != 0 && Engine.HasBumpyShiny(m);
        int fb = shared == IntPtr.Zero ? -1 : new SharedRef(shared).SampleFb;
        _log?.Info($"[water] material tex0=\"{name}\" tid={t0.ResolveTid()} {TexFormat(t0.ResolveTid())} " +
                   $"route={(bumpy ? "bumpy-shiny" : "sample_framebuffer")} sample_fb={fb} " +
                   $"blend={m.SrcBlend}/{m.DstBlend} " +
                   $"env={Engine.HasEnvMap(m)} {TexTopMip(t0.ResolveTid())} " +
                   $"{WaterBodies.Describe()} {AlloyRenderer.DescribeWind()}");
    }

    internal static readonly HashSet<string> _waterMats = new();

    internal static string TexName(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "-";
        IntPtr real = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)tex + TexVtblReferenced))(tex);
        if (real == IntPtr.Zero) real = tex;
        IntPtr basic = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)real + TexVtblAsBasic))(real);
        return basic == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(basic + TexBasicName) ?? "?";
    }

    /// <summary>One census line for one engine-drawn part. Everything the four open questions need at once:
    /// which bucket the engine is drawing it in, which bucket we CAPTURED it in (they differ, and ownership
    /// keys on the capture), what state its table entry is in, what class it classified as, and whether its
    /// diffuse carries alpha the caster mask could have used.</summary>
    internal static void DumpPart(IntPtr part)
    {
        var p = new PartRef(part);
        var m = p.Material;
        if (_dumpTex != null)
        {
            TextureRef t = m.IsNull ? default : m.Texture(0);
            // #134: "*" matches every part. Four rolls asked "is the ivy owned" of a substring that named
            // exactly ONE part in the area -- the answer was yes every time and the thing on screen was
            // other geometry. A name you have to guess is not a filter, it is a hypothesis.
            if (!_dumpAll
                && (t.IsNull || !t.ResolveName().Contains(_dumpTex, System.StringComparison.OrdinalIgnoreCase)))
            { _dumpLeft++; return; }                       // refund: only matches spend the budget
            WatchEngineDraws++;
            // #134: a WHOLE sampled frame, not one row of it. One row per frame made every roll read as
            // "the ivy", singular, while the thing on screen is a dozen parts -- and the one row happened to
            // be an Alloy one for four rolls running. Sample every DumpTexEvery frames, up to DumpTexRows
            // parts, so the population is visible rather than a representative of it.
            if (_frame != _dumpTexFrame)
            {
                if (_frame - _dumpTexFrame < DumpTexEvery) { _dumpLeft++; return; }
                _dumpTexFrame = _frame; _dumpTexRows = 0;
            }
            if (_dumpTexRows++ >= (_dumpAll ? DumpAllRows : DumpTexRows)) { _dumpLeft++; return; }
        }
        NodeRef node = p.Node;
        int e = Find(part);
        if (e >= 0) _watchEntry = e;
        string ent = e < 0 ? "unrecorded" : $"entry={e} st={_pState[e]} died={(_pState[e] == StDead ? _pDeath[e] ?? "?" : "-")} own={OwnFlag[e]} ownonly={_pOwnOnly[e]} capbucket={(_pState[e] == StActive ? _gBucket[_pGroup[e]] : -1)} seen={_frame - _pSeen[e]}";
        TextureRef t0 = m.IsNull ? default : m.Texture(0);
        int tid = t0.ResolveTid();
        SharedRef shared = m.Shared;
        _log?.Info($"[dumpframe] bucket={_bucketId} inbucket={_inBucket} dangly={IsDangly(part)} skin={BatchService._inSkinDraw} why={(e < 0 ? "unrecorded" : WhyNotOwnable(e))} part=0x{(ulong)part:x} vtbl=0x{(ulong)p.Vtbl:x} {ent} " +
                   $"tex=\"{t0.ResolveName()}\" tid={tid} texalpha={(tid > 0 && CasterPass.TexHasAlpha(tid))} {TexFormat(tid)} " +
                   $"opaque={(!m.IsNull && Engine.IsOpaque(p))} env={(!m.IsNull && Engine.HasEnvMap(m))} " +
                   $"blend={(m.IsNull ? 0 : m.SrcBlend)}/{(m.IsNull ? 0 : m.DstBlend)} " +
                   $"alpha={p.Alpha:0.00} gob=0x{(ulong)p.Gob.Ptr:x} " +
                   $"node=0x{(ulong)node.Ptr:x} nvtbl=0x{(ulong)(node.IsNull ? IntPtr.Zero : *(IntPtr*)node.Ptr):x} " +
                   $"n10=0x{(ulong)(node.IsNull ? IntPtr.Zero : *(IntPtr*)(node.Ptr + 0x10)):x} " +
                   $"nflags=0x{(node.IsNull ? 0 : node.Flags):x} {GobTerms(p.Gob.Ptr)} " +
                   $"transp={(!shared.IsNull && shared.Transparency)} " +
                   $"shvs=\"{(shared.IsNull ? "-" : Marshal.PtrToStringAnsi((IntPtr)shared.VsName) ?? "")}\"");
    }
}
