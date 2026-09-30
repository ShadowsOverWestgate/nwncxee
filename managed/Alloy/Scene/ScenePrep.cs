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

    /// <summary>The three <c>LightManager</c> vcalls, in the engine's order and with the engine's arguments:
    /// the scene focus (<c>Scene+0x190</c>) and <c>max(45, fog end)</c>. <c>UpdateLights</c> is the load-bearing
    /// one; the other two are here because their products still have readers -- the <c>light.registry 0</c>
    /// A/B reads the candidate list directly, and the shadow list <c>PrioritizeShadow</c> rebuilds is what the
    /// area-load static-projection bake reads. Keeping a cheap engine call beats an argument about whether a
    /// skipped frame can precede that bake.</summary>
    private static void Lights(Scene sc)
    {
        LightManager lm = sc.LightManager;
        if (lm.IsNull) return;
        System.Numerics.Vector3 focus = sc.SkyPos;
        float radius = MathF.Max(LightRadiusFloor, sc.FogEnd);
        lm.LightMgrUpdateLights();
        lm.LightMgrSetupLights((IntPtr)(&focus), radius);
        lm.LightMgrPrioritizeLights((IntPtr)(&focus));
    }

    /// <summary>The in-view byte and <c>gobbucket</c>, in <c>ManageSceneBSP</c>'s own three phases. The
    /// bucket's population is the BYTE and not the frustum test directly, deliberately: an attached gob
    /// outside the frustum whose host is inside has the byte set by propagation, and the engine's step 11
    /// picked it up.</summary>
    private static void InView(Scene sc)
    {
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount;
        if (gobs == null || n <= 0) return;
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return;
        EngineList planes = cam.ViewVolume;

        for (int i = 0; i < n; i++)
            if (gobs[i] != IntPtr.Zero) new Gob(gobs[i]).GobDisableInViewVolume();
        _cleared += n;

        for (int i = 0; i < n; i++)
        {
            IntPtr gob = gobs[i];
            if (gob == IntPtr.Zero) continue;
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
    /// while its body is still on screen. The same three virtuals (+0x2d8/+0x2e0/+0x2e8), the offset scaled.</summary>
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
           + (_held > 0 ? $", {_held} modelless gob(s) held out" : "");

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
        log.Info(ok ? $"ScenePrep.SelfCheck: passed (light radius floor {LightRadiusFloor}, skip step {SkipAll}; "
                    + "the two-phase sweep keeps a propagated set that a folded pass loses; a scaled gob's "
                    + "sphere scales its centre)"
                    : "ScenePrep.SelfCheck: FAILED -- the light radius floor, the in-view phase order or the "
                    + "scaled in-view sphere drifted");
        return ok;
    }
}
