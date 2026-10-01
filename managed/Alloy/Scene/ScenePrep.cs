using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: the side effects of the engine's per-frame scene preparation that are NOT render products,
/// driven by Alloy once <c>alloy.skipbuckets=3</c> stands <c>ManageSceneBSP</c> @0x47fa80 down.
/// <para>CX-103/104/105 took everything that walk produced FOR THE FRAME -- the world matrices, the area
/// index, the LOD choice, the visible-gob list, the tile fade ramp, the grass bins, the emitter, trigger and
/// beam populations. What was left behind is the handful of things the walk also did for the rest of the
/// client, and the knob could not be a default while they had no owner:</para>
/// <list type="bullet">
/// <item><b>The in-view byte, <c>gob+0x1b</c>.</b> <c>ManageSceneBSP</c> ran it in two phases: step 3 cleared
/// it on every gob in <c>Scene+0x130</c> (<c>Gob::DisableInViewVolume</c>, vtable +0x2b8), then the BSP visitor
/// @0x481000 set it (<c>Gob::EnableInViewVolume</c>, +0x2b0) on every gob a visited node held that
/// <c>GobOutside</c> said was inside the frustum -- a test that loses scaled gobs, so this one sets it from
/// <see cref="Outside(IntPtr, EngineList)"/> instead. It is not a render flag: <c>Gob::Animate</c> @0x49d8c0 skips
/// a gob's whole animation step while it is clear, and so do Alloy's own
/// <c>AnimRuntime.Visible</c> and <c>PoseService</c>. Frozen, an off-screen creature never animates
/// again once the camera turns away. The two phases are kept separate because Enable PROPAGATES to the
/// gob's attachments (<c>DoEvent("propagateviewvolume")</c>), so setting and clearing in one pass would let a
/// later clear undo a propagated set.</item>
/// <item><b><c>gobbucket</c>.</b> Step 11 filled it from the byte above, and Alloy stopped reading it in
/// CX-104 -- but <c>Scene::HitCheckGob</c> @0x45f860 takes the <c>gobbucket</c> branch UNCONDITIONALLY on a
/// world scene (<c>Scene+0x188 != 0</c>), and that is the client's whole gob-picking path: the cursor shape,
/// tooltips, left/right click, targeting mode, drag-and-drop. Frozen, creatures, placeables and doors go
/// unclickable while terrain clicks keep working -- which is the signature to look for.</item>
/// <item><b>The three <c>LightManager</c> steps</b> the walk made around its traverse (vtable +0x38, +0x40,
/// +0x10). <c>UpdateLights</c> is the only per-frame writer of a light's WORLD position anywhere in the client
/// and Alloy reads that field in five places, so it is called, not skipped. See
/// <see cref="SymbolMap.LightMgrUpdateLights"/> for each one's verdict.</item>
/// </list>
/// <para><b>What is deliberately NOT replaced,</b> having been traced to a render-only consumer: the BSP
/// RE-FILING of moved parts and gobs (<c>SceneBSPUpdatePart</c> / <c>SceneBSPUpdateGob</c>, step 7) touches
/// only the node lists the visitor itself reads -- picking walks the node's STATIC part lists, which step 7
/// never re-files, and <c>gobbucket</c>, which is owned above; <c>Camera::SetFadePlanes</c>'s two planes at
/// <c>Camera+0x508</c> have no reader anywhere in the binary; <c>Camera+0x4e8</c>, the frustum, is an INPUT
/// built by <c>Camera::RenderScene</c> and stays current; and <c>Scene+0x1a0</c> plus the fade plane at
/// <c>Camera+0x4f8</c> feed only <c>ProcessTriMeshParts</c>, which disappears with the walk and whose job is
/// <see cref="TileFadeRamp"/>'s.</para>
/// <para>Runs once per world frame at the top of <see cref="RspShim.Run"/>, inside the same
/// <c>Scene::Render</c> call the engine's own walk ran in. Allocation-free; three passes over the area's gob
/// array, two of them a single call each -- which is what <c>ManageSceneBSP</c>'s steps 3 and 11 were.</para>
///
/// <para><b>GS1: every per-frame pass over <c>Scene+0x130</c>, in frame order</b> -- because the reason there
/// is more than one is ORDERING, and the map is what says which of them can be one.
/// <list type="bullet">
/// <item><c>AnimRuntime.OnSceneAnimate</c> (<c>Scene::Animate</c>, the game UPDATE, ahead of
/// <c>Scene::Render</c>) walks the array BACKWARDS and re-reads <c>Scene+0x138</c> every step, because a
/// handler it calls may destroy gobs. It cannot share a snapshot with anything: on its own thread of control
/// the array is mutating.</item>
/// <item>This class, three phases, at the top of the shim. The clear and the set may not fold (Enable
/// propagates to attachments), and the bucket fill reads the byte AFTER every propagation -- so three.
/// The frustum phase also cuts the visible-gob list, see <see cref="InView"/>.</item>
/// <item><c>AlloyRenderer.SampleVisibleGobs</c>, the next call: nothing left to walk on the armed path (the
/// phase above cut its list), and its own walk on every other.</item>
/// <item><c>GobLod.DriveSkipped</c>, inside the frame anchor: <c>Gob::ProcessGob</c> per gob and the LOD
/// choice per gob the frustum let through -- which since !790 means the gob the SCALED sphere let through, so
/// a scaled placeable whose pivot is off screen now gets its LOD chosen as well as its animation and its
/// clicks. A pass of its own because <c>ProcessGob</c> runs game-visible code and the LOD commit walks part
/// trees -- the gob set can differ from the one this class swept, so it reads the array live.</item>
/// <item><c>GobXform.Tick</c>, later in the anchor: the moved set. Also its own pass, and deliberately: it
/// compares each gob's raw placement against the value it read LAST frame, so sampling it earlier would
/// simply move the detection a phase earlier and report a write made between the two a frame late.</item>
/// <item><c>GrassField.WalkPushers</c> and <c>DoorFade.Tick</c> walk the VISIBLE list, not the array
/// (<c>grass pushvis 0</c> is the one A/B that puts the whole array back).</item>
/// </list></para>
/// </summary>
internal static unsafe class ScenePrep
{
    /// <summary>The floor <c>ManageSceneBSP</c> puts under the light-candidacy radius before it hands
    /// <c>Scene+0x148</c> (the fog end) to <c>SetupLightsForThisPositionAndRadius</c>. Invisible in the
    /// decompilation -- read off the disassembly at 0x480093, <c>movss</c> of 0x42340000 then <c>maxss</c>
    /// against the field.</summary>
    private const float LightRadiusFloor = 45f;

    /// <summary>The value of <c>alloy.skipbuckets</c> this class exists for.</summary>
    internal const int SkipAll = 3;

    private static EngineList _gobBucket;

    /// <summary>Every symbol and row this needs resolved? False is not a fallback -- it REFUSES the skip
    /// (<see cref="Needed"/>), so the engine's own walk keeps running and nothing goes stale quietly.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The first thing that did not resolve, or null.</summary>
    internal static string? Missing { get; private set; }

    /// <summary>Has <see cref="Resolve"/> run? <see cref="Note"/> must not report a refusal before it has --
    /// services initialise ahead of the renderer's passes, so the boot order alone would print one.</summary>
    private static bool _resolved;

    internal static void Resolve(ExtenderLog log)
    {
        _resolved = true;
        _gobBucket = new EngineList(Native.Sym(SymbolMap.GobBucket));
        Missing = EngineFn.GobDisableInViewVolume == null ? SymbolMap.GobDisableInViewVolume
                : EngineFn.GobEnableInViewVolume == null ? SymbolMap.GobEnableInViewVolume
                : EngineFn.GobListAdd == null ? SymbolMap.GobListAdd
                : _gobBucket.IsNull ? SymbolMap.GobBucket
                : EngineFn.LightMgrUpdateLights == null ? SymbolMap.LightMgrUpdateLights
                : EngineFn.LightMgrSetupLights == null ? SymbolMap.LightMgrSetupLights
                : EngineFn.LightMgrPrioritizeLights == null ? SymbolMap.LightMgrPrioritizeLights
                : EngineLayout.CamViewVolume == 0 ? "Camera.viewVolume"
                : null;
        Ready = Missing == null;
        if (!Ready)
            log.Info($"ScenePrep: {Missing} did not resolve -- alloy.skipbuckets=3 is refused this session and "
                   + "the engine's own ManageSceneBSP keeps running");
    }

    /// <summary>Is the engine's own scene walk still needed this frame? <c>true</c> whenever anything would
    /// read a bucket only that walk fills -- one of the five <see cref="Buckets"/> A/B switches handing its
    /// class back to the engine, the validator that compares against the engine's lists, or the
    /// <c>Scene+0x208</c> cross-check. Those are diagnostics and bisects: against a FROZEN global they would
    /// answer confidently and wrongly, so they arm the walk instead.
    /// <para>This is the predicate <c>EngineZonesService</c> gates the skip on, which is why
    /// <c>/buckets grassbins 0</c> still restores the engine's grass end to end -- <c>CollectBins</c> and
    /// <c>AurProcessActiveBins</c>, the only builder of the engine's grass vertex objects, come back with the
    /// walk.</para></summary>
    internal static bool Needed
        => !Ready
        || !Buckets.TileFade.V || !Buckets.GrassBins.V || !Buckets.Emitters.V
        || !Buckets.Triggers.V || !Buckets.Beams.V
        || Buckets.Validate.V || Renderer.BucketCheck.V;

    private static long _frames, _cleared, _inView, _bucketed, _held;
    /// <summary>Frames whose engine light SELECTION was skipped (the registry is the reader) against frames
    /// that still ran it (<c>light.registry 0</c>). Reported on the prep clause.</summary>
    private static long _selSkipped, _selRan;

    /// <summary>Once per world frame, ahead of everything that reads what it writes. A no-op unless the skip
    /// is actually armed and taken this frame.</summary>
    internal static void Tick(IntPtr scene)
    {
        if (!Ready || Renderer.SkipBuckets.Boot != SkipAll || Needed) return;
        if (scene == IntPtr.Zero || !AlloyRenderer.IsWorldScene(scene)) return;
        _frames++;
        var sc = new Scene(scene);
        Lights(sc);
        InView(sc);
    }

    /// <summary><c>ManageSceneBSP</c>'s own <c>LightManager</c> work, in its order and with its arguments: the
    /// scene focus (<c>Scene+0x190</c>) and <c>max(45, fog end)</c>. <c>UpdateLights</c> always; the candidate
    /// list and its priorities only while the engine's own selection still has a reader, which is
    /// <c>light.registry 0</c>.
    /// <para><b>SL4, and the shadow-list worry was unfounded</b> (MEASURED): the area-load static-projection
    /// bake does NOT read the list <c>PrioritizeShadow</c> builds. <c>BSPCreateStaticProjections</c> @0x47e890
    /// calls <c>GetShadowLights</c> with mode <b>2</b> (@0x47e8ec <c>mov $0x2,%esi</c>), and mode 2 returns
    /// early off the MASTER list <c>+0x10/+0x18</c> without touching <c>+0x50</c> at all. The proof is in the
    /// load order: <c>CNWCArea::LoadArea</c> creates the Scene and runs <c>PostProcessLS</c> ->
    /// <c>BSPCreateStaticProjections</c> BEFORE the area's first <c>Scene::Render</c>, when both lists are
    /// still zero from the <c>LightManager</c> ctor. Every other consumer of <c>+0x40</c> and <c>+0x50</c> --
    /// <c>PrioritizeIllumination</c>, <c>PrioritizeShadow</c>, <c>GetShadowLights(0|1)</c>,
    /// <c>GetLensFlareLights</c>, <c>EnableLights</c>, <c>EnableLightsDynamicOnly</c> -- is reached only from
    /// <c>Scene::RenderSinglePass</c> or <c>ManageSceneBSP</c>, both of which Alloy replaces, and Alloy calls
    /// <c>EnableLights</c> itself nowhere (<c>LightFade</c> is CX-85's port). <c>countlights</c> @0x19abb60 is
    /// write-only. <c>PartLight+0x98/+0x9c</c>, the two fields <c>PrioritizeLights</c> writes, are read only by
    /// <c>GetShadowLights(0|1)</c> and the sort's comparator. A dying light is still pulled out of both lists
    /// by <c>RemoveLight</c> through <c>SceneBSPRemovePart</c>, so freezing them leaves nothing dangling.</para>
    /// <para><b><c>UpdateLights</c> is the one that cannot go, and not for want of trying.</b> MEASURED
    /// (objdump @0x4c0725): per master light it makes a VIRTUAL call through <c>PartLight</c> vtable
    /// <c>+0x20</c> = <c>Part::GetWorld</c> @0x437930 into <c>PartLight+0xac</c> -- which is
    /// <see cref="EngineLayout.LightDir"/>, the field EVERY Alloy light read goes through: the registry's
    /// candidate filter and score, the sun direction <c>FrameRead</c> derives, the cluster upload, the cube
    /// fits. Its only other writers are <c>AddLight</c> (once, at insertion) and
    /// <c>BSPCreateStaticProjections</c> (far lights, at area load), so on a frame that skipped this call
    /// every light would shade at the position it last held. It is also not composable from Alloy's own
    /// state: <c>GobXform</c> composes world forms for OWNED trimesh parts, and a <c>PartLight</c> is not one
    /// -- reproducing the value means walking the light's own parent chain (<c>part+0x48</c>, scale then quat
    /// then translate per level) and then the gob's adjusted scale/orientation/position, i.e. the same work
    /// for the same 1422 lights. The value is pure (a hard-zeroed accumulator, every input read live), so
    /// there is nothing wrong with the idea -- there is just nothing in it.</para></summary>
    private static void Lights(Scene sc)
    {
        LightManager lm = sc.LightManager;
        if (lm.IsNull) return;
        System.Numerics.Vector3 focus = sc.SkyPos;
        float radius = MathF.Max(LightRadiusFloor, sc.FogEnd);
        lm.LightMgrUpdateLights();
        // The candidate list and its priorities are the engine's own selection, which LightRegistry.Select
        // replaces term for term -- so they are refilled only for the frame that reads them.
        if (LightRegistry.Live) { _selSkipped++; return; }
        lm.LightMgrSetupLights((IntPtr)(&focus), radius);
        lm.LightMgrPrioritizeLights((IntPtr)(&focus));
        _selRan++;
    }

    /// <summary>The in-view byte and <c>gobbucket</c>, in <c>ManageSceneBSP</c>'s own three phases. The
    /// bucket's population is the BYTE and not the frustum test directly, deliberately: an attached gob
    /// outside the frustum whose host is inside has the byte set by propagation, and the engine's step 11
    /// picked it up.
    /// <para>GS1: the frustum sweep also cuts Alloy's VISIBLE-GOB list
    /// (<see cref="AlloyRenderer.VisibleGobsFrom"/>). That list used to be its own pass over the same array
    /// one call later, and every gob it tested is one this sweep already has in cache for
    /// <see cref="Outside(IntPtr, EngineList)"/>. The test itself is still the renderer's
    /// (<see cref="AlloyRenderer.GobWithinFar"/>), which is also what its 1-in-64 re-walk audits. It carries no
    /// scale factor of its own, and the two tests in this loop therefore differ on that: see
    /// <see cref="AlloyRenderer.GobWithinFar"/> for the decompile that says the fields are already scaled, and
    /// the note on <see cref="Outside(IntPtr, EngineList)"/> for what that leaves open about !790.</para>
    /// </summary>
    private static void InView(Scene sc)
    {
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount;
        if (gobs == null || n <= 0) return;
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return;
        EngineList planes = cam.ViewVolume;
        Vector3 eye = cam.Pos;
        float far = cam.Far;

        for (int i = 0; i < n; i++)
            if (gobs[i] != IntPtr.Zero) new Gob(gobs[i]).GobDisableInViewVolume();
        _cleared += n;

        // The visible list is published INCREMENTALLY, interleaved with this sweep's two engine calls -- and
        // GobEnableInViewVolume runs DoEvent("propagateviewvolume"), i.e. game-registered handlers. No consumer
        // reads the list before this returns, and a handler that moved a gob would have moved it for the
        // standalone pass a moment later just the same; what the fusion is exposed to is the gob ARRAY changing
        // inside one Scene::Render, which the sampler's 1-in-64 re-walk is there to catch.
        AlloyRenderer.VisibleGobsFrom(sc.Ptr, n);
        for (int i = 0; i < n; i++)
        {
            IntPtr gob = gobs[i];
            if (gob == IntPtr.Zero) continue;
            // In scene order and BEFORE the frustum verdict: the visible list is deliberately not
            // frustum-culled (a creature just off-camera bends the grass it stands in). It carries NO scale
            // factor of its own -- gob+0x178/+0x17c already have the gob's scale in them, see GobWithinFar.
            if (AlloyRenderer.GobWithinFar(gob, eye, far)) AlloyRenderer.OfferVisibleGob(gob);
            if (Outside(gob, planes)) continue;
            new Gob(gob).GobEnableInViewVolume();
            _inView++;
        }

        _gobBucket.Count = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr gob = gobs[i];
            if (gob == IntPtr.Zero) continue;
            var g = new Gob(gob);
            if (g.InView == 0) continue;
            // Every engine reader of the bucket dereferences gob+0x68 -- the gob sort comparator and
            // Gob::HitCheck both -- so a modelless gob must not reach it.
            if (g.Node.IsNull) { _held++; continue; }
            EngineFn.GobListAdd(_gobBucket.Ptr, gob);
            _bucketed++;
        }
    }

    /// <summary><c>GobOutside</c> @0x47a4f0 with its one fault fixed. The engine scales the radius
    /// (<c>GetAdjustedScale() * gob+0x178</c>) but not the local centre <c>gob+0x17c</c>, so a scaled gob's sphere
    /// stays anchored by its pivot, and a placeable scaled up goes out of view -- unclickable, not animated --
    /// while its body is still on screen. The same three virtuals (+0x2d8/+0x2e0/+0x2e8), the offset scaled.
    /// <para><b>OPEN (merge of !790, evidence in <see cref="AlloyRenderer.GobWithinFar"/>):</b> this scales two
    /// fields that <c>Gob::GetMinimumSphere</c> @0x5370a0 -- their only writer past the constructor -- has
    /// ALREADY multiplied by <c>GetAdjustedScale</c>, so the sphere it tests grows as <c>s^2</c>, not <c>s</c>.
    /// The engine's own <c>GobOutside</c> is inconsistent the other way (centre <c>s</c>, radius <c>s^2</c>), so
    /// "the centre is not scaled" is true only RELATIVE to the radius. For <c>s &gt; 1</c> this over-includes,
    /// which is why it cures the vanishing pillars; for a shrunken gob it would under-include. The same fact is
    /// already stated independently by AN4's own gate (<c>AnimRuntime.UnitScale</c>: "<c>Gob::GetMinimumSphere</c>
    /// multiplies both the radius and the centre by it", which is WHY a lerping scale cannot skip a refresh).
    /// Left exactly as !790 shipped it -- a merge is not the place to change main's live behaviour -- and
    /// reported.</para>
    /// </summary>
    private static bool Outside(IntPtr gob, EngineList planes)
    {
        var g = new Gob(gob);
        AurXform.V3 p = AurXform.AdjustedPosition(gob);
        return Outside(new Vector3(p.X, p.Y, p.Z), AurXform.AdjustedOrientation(gob), g.GetAdjustedScale(),
                       g.Centre, g.Radius, (float*)planes.Items, planes.Count);
    }

    /// <summary>The pure half: the gob's local sphere through <c>Scale(s) x Rot(q) x Translate(p)</c> (the gob
    /// term of <see cref="GobXform"/>'s composition), then <c>SphereAbovePlane</c> @0x5b3340 over each
    /// {n, d} plane -- <c>dot(n, c) + d - r &gt; 0</c> against any one is outside.</summary>
    internal static bool Outside(Vector3 pos, Quaternion rot, float scale, Vector3 centre, float radius,
                                 float* planes, int n)
    {
        Vector3 c = pos + Vector3.Transform(centre * scale, rot);
        float r = radius * scale;
        for (int k = 0; k < n; k++, planes += 4)
            if (planes[0] * c.X + planes[1] * c.Y + planes[2] * c.Z + planes[3] - r > 0f) return true;
        return false;
    }

    /// <summary>The clause the <c>buckets</c> knob group prints.</summary>
    internal static string Note()
        => !_resolved ? "scene prep not resolved yet"
         : !Ready ? $"scene prep stood down ({Missing} unresolved)"
         : Renderer.SkipBuckets.Boot != SkipAll ? "engine scene walk runs (alloy.skipbuckets < 3)"
         : Needed ? "engine scene walk RE-ARMED (a /buckets A/B or a validator wants its lists)"
         : _frames == 0 ? "owned, no world frame yet"
         : $"owned over {_frames} frame(s): {_cleared / _frames} gob(s) swept, {_inView / _frames} in view, "
           + $"{_bucketed / _frames} picked into gobbucket"
           + (_held > 0 ? $", {_held} modelless gob(s) held out" : "")
           // SL4: UpdateLights always runs (it writes the position every Alloy light read goes through); the
           // engine's own selection runs only for the frame that still reads it.
           + $", engine light selection skipped on {_selSkipped} frame(s), ran on {_selRan}";

    /// <summary>The sweep as pure bookkeeping, so the phase order can be pinned to numbers with no engine at
    /// all. <paramref name="inside"/> is each gob's frustum verdict and <paramref name="host"/>[i] names the
    /// gob whose Enable propagates onto i (-1 for none, which is every gob that is not an attachment).
    /// <paramref name="phases"/> false folds the clear and the set into ONE pass, which is the shape this
    /// class deliberately does not use.</summary>
    internal static void Sweep(ReadOnlySpan<bool> inside, ReadOnlySpan<int> host, Span<bool> flag, bool phases)
    {
        if (phases)
        {
            for (int i = 0; i < flag.Length; i++) flag[i] = false;
            for (int i = 0; i < flag.Length; i++) if (inside[i]) Enable(i, host, flag);
            return;
        }
        for (int i = 0; i < flag.Length; i++) { flag[i] = false; if (inside[i]) Enable(i, host, flag); }
    }

    /// <summary><c>Gob::EnableInViewVolume</c> as bookkeeping: set this one, then everything hanging off it.
    /// The already-set early-out is what makes it terminate on a cycle, and it is also the truth -- a gob
    /// that is already in view has already propagated.</summary>
    private static void Enable(int i, ReadOnlySpan<int> host, Span<bool> flag)
    {
        if (flag[i]) return;
        flag[i] = true;
        for (int k = 0; k < flag.Length; k++) if (host[k] == i) Enable(k, host, flag);
    }

    /// <summary>Off-device: the light radius floor, that the in-view sweep MUST be two-phase, and that a scaled
    /// gob's sphere scales its centre. The sweep is the footgun -- gob 0 is in view and gob 1 is its
    /// attachment, out of view, so a folded pass clears gob 1 after gob 0's Enable propagated onto it and the
    /// attachment stops animating. The sphere case is a pillar (local centre 2.5 up, radius 2.5) scaled 4x
    /// with only its top in view: <c>GobOutside</c>'s unscaled centre calls it out. No engine object is
    /// touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = LightRadiusFloor == 45f && MathF.Max(LightRadiusFloor, 40f) == 45f
               && MathF.Max(LightRadiusFloor, 200f) == 200f && SkipAll == 3;
        float* above15 = stackalloc float[] { 0f, 0f, -1f, 15f };
        Vector3 centre = new(0f, 0f, 2.5f);
        ok &= !Outside(Vector3.Zero, Quaternion.Identity, 4f, centre, 2.5f, above15, 1)
              && Outside(Vector3.Zero, Quaternion.Identity, 1f, centre, 2.5f * 4f, above15, 1);
        ReadOnlySpan<bool> inside = [true, false, false];
        ReadOnlySpan<int> host = [-1, 0, -1];
        Span<bool> flag = [false, false, true];
        Sweep(inside, host, flag, phases: true);
        ok &= flag[0] && flag[1] && !flag[2];
        Sweep(inside, host, flag, phases: false);
        bool foldedLosesAttachment = flag[0] && !flag[1] && !flag[2];
        ok &= foldedLosesAttachment;
        ok &= VisibleHandoff(log);
        log.Info(ok ? $"ScenePrep.SelfCheck: passed (light radius floor {LightRadiusFloor}, skip step {SkipAll}; "
                    + "the two-phase sweep keeps a propagated set that a folded pass loses; a scaled gob's "
                    + "sphere scales its centre)"
                    : "ScenePrep.SelfCheck: FAILED -- the light radius floor, the in-view phase order or the "
                    + "scaled in-view sphere drifted");
        return ok;
    }

    /// <summary>GS1: the hand-off this class's frustum phase now makes -- the visible-gob list, cut in passing
    /// instead of by a second pass over the same array. Off-device: synthetic gob addresses, a synthetic scene,
    /// no engine object touched. The rule worth a check is the LATCH, because getting it wrong is silent both
    /// ways: a list claimed for a scene nobody asked about would freeze <c>DoorFade</c> and the grass push on
    /// one frame's population, and a list never claimed would have <c>SampleVisibleGobs</c> walk the array
    /// again for the answer it is already holding.</summary>
    private static bool VisibleHandoff(ExtenderLog log)
    {
        var check = new Checker(log, "ScenePrep.SelfCheck[visgobs]");
        IntPtr fake = (IntPtr)0x1234, a = (IntPtr)0x81000, b = (IntPtr)0x82000;
        AlloyRenderer.VisibleGobsFrom(fake, 7);
        check.Expect(!AlloyRenderer.VisibleGobs(out _, out int n0) && n0 == 0,
                     "a declared but unfilled list reads empty rather than last frame's");
        AlloyRenderer.OfferVisibleGob(a);
        AlloyRenderer.OfferVisibleGob(b);
        bool got = AlloyRenderer.VisibleGobs(out IntPtr* items, out int n);
        check.Expect(got && n == 2 && items[0] == a && items[1] == b,
                     "the offered gobs read back in scene order -- which is what the live audit compares");
        // ...and the latch is per (frame, scene): a stray declaration cannot be consumed by a real frame,
        // which is exactly why it is keyed on the scene pointer and not a bool.
        check.Expect(!AlloyRenderer.VisibleGobsFilledFor(IntPtr.Zero),
                     "no scene claims a list, so the sampler always walks");
        check.Expect(AlloyRenderer.VisibleGobsFilledFor(fake), "the scene the phase declared for claims it");
        AlloyRenderer.VisibleGobsFrom(IntPtr.Zero, 0);   // leave nothing behind for the first live frame
        FarReadsTheGobSphere(check);
        return check.Ok;
    }

    /// <summary>!790 (merge), off-device: the far test reads the gob's sphere and does NOT re-scale it. The two
    /// fields it uses carry <c>GetAdjustedScale</c> already -- <c>Gob::GetMinimumSphere</c> @0x5370a0 is their
    /// only writer past the constructor and multiplies both by it (<c>src/Gob.c:2371-2376</c>) -- so a second
    /// factor would give an <c>s^2</c> sphere. This pins that decision in code, because a future reader looking
    /// at !790's scaled frustum test in the same loop will ask why this one is not, and nothing else answers.
    /// <para>A synthetic gob in unmanaged scratch: every field the test reads is a plain load, so no vtable is
    /// needed and no engine object is touched.</para></summary>
    private static void FarReadsTheGobSphere(Checker check)
    {
        int bytes = Math.Max(Math.Max(EngineLayout.GobPosition, EngineLayout.GobRot),
                             Math.Max(EngineLayout.GobCentre, EngineLayout.GobRadius)) + 32;
        IntPtr blk = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
        try
        {
            new Span<byte>((void*)blk, bytes).Clear();
            var g = new Gob(blk);
            g.Rot = new Quat { W = 1f, X = 0f, Y = 0f, Z = 0f };
            g.Position = new Vector3(20f, 0f, 0f);
            g.Radius = 1f;
            // The RADIUS term reaches: the sphere's near face is at 19, so a far plane at 19.5 keeps it and one
            // at 18.5 does not. A test that dropped the radius would keep neither.
            g.Centre = Vector3.Zero;
            check.Expect(AlloyRenderer.GobWithinFar(blk, Vector3.Zero, 19.5f)
                         && !AlloyRenderer.GobWithinFar(blk, Vector3.Zero, 18.5f),
                         "the far cut is against the sphere's NEAR face, radius included");
            // The CENTRE OFFSET is taken through the rotation, exactly once: offset 4 back towards the eye puts
            // the near face at 15. A doubled or squared offset would put it somewhere else entirely.
            g.Centre = new Vector3(-4f, 0f, 0f);
            check.Expect(AlloyRenderer.GobWithinFar(blk, Vector3.Zero, 15.5f)
                         && !AlloyRenderer.GobWithinFar(blk, Vector3.Zero, 14.5f),
                         "the gob's centre offset moves the sphere, applied once -- the fields already carry "
                         + "the gob's scale, so re-scaling here would square it");
            // A gob the engine has given a sphere for and a far plane of 0 (no camera answer) admits everything.
            check.Expect(AlloyRenderer.GobWithinFar(blk, Vector3.Zero, 0f),
                         "no camera answer admits every gob, as the standalone walk did");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(blk); }
    }
}
