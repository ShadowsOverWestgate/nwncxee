using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Services.Render.SceneLayout;
using static Sowx.Services.Render.PartTable;
using static Sowx.Services.Render.OwnershipCensus;
namespace Sowx.Services.Render;

/// <summary>#188: the engine's bucket walk, and what it is not allowed to draw. Four hooks --
/// <c>Scene::RenderDrawBucket</c>, the <c>GetFirstItem</c>/<c>GetNextItem</c> iterator pair and
/// <c>HasItemsOfType</c> -- brackets the walk, publishes the walk context every other module keys on
/// (bucket id, Scene, phase, generation), and SKIPS the items an owned pass already drew.
/// <para>This is the correctness surface of the whole owned pipeline: one wrong answer is a double-draw or
/// a hole. It is a separate module from the batcher for exactly that reason. Its suppression reads the
/// CENSUS (<c>OwnFlag</c>/<c>OwnGen</c>) and the TABLE (sightings, identity); the only batcher state it
/// reads is <c>_pIncluded</c>/<c>_gSubmitGen</c>, whose miss direction is "return the slot, let the engine
/// draw it" -- so a batcher that never armed can only make this gate skip LESS, never more.</para>
/// <para>Alloc-free: <see cref="OnNextItem"/> runs per bucket item per frame.</para></summary>
internal static unsafe class BucketGate
{
    /// <summary>Bucket-item vtable slot the RenderDrawBucket loop devirts to get the PartTriMesh from the
    /// bucket's Part (Ghidra @0x56d118: <c>(**(code**)(*part+0x60))(part)</c>).</summary>
    internal const int PartVtblGetTriMesh = 0x60;

    internal static delegate* unmanaged<IntPtr, int, void> _origBucket;
    internal static delegate* unmanaged<IntPtr, int, int, IntPtr> _origFirstItem;
    internal static delegate* unmanaged<IntPtr, int, int, IntPtr> _origNextItem;

    /// <summary>Did the walk hooks go in? The batcher's arming reads this; nothing here reads the
    /// batcher's.</summary>
    internal static bool Installed => _origBucket != null && _origFirstItem != null;

    /// <summary>Install the four walk hooks. Called from <see cref="BatchService.Initialize"/> in the
    /// original order -- the bucket bracket, then the iterator pair, then the water gate -- because hook
    /// install order is what decides the funchook chain each symbol ends up carrying.</summary>
    internal static void Install(HookService hooks)
    {
        _origBucket = (delegate* unmanaged<IntPtr, int, void>)hooks.Install(
            SymbolMap.RenderDrawBucket, (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnBucket);
        _origFirstItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)hooks.Install(
            SymbolMap.BucketGetFirstItem, (IntPtr)(delegate* unmanaged<IntPtr, int, int, IntPtr>)&OnFirstItem);
        _origNextItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)hooks.Install(
            SymbolMap.BucketGetNextItem, (IntPtr)(delegate* unmanaged<IntPtr, int, int, IntPtr>)&OnNextItem);
        // #129: the water block's own gate. Null leaves bucket 6 to the engine, which is exactly the
        // pre-#129 behaviour -- water still renders, it just costs the engine's second framebuffer copy.
        _origHasItems = (delegate* unmanaged<IntPtr, int, int>)hooks.Install(
            SymbolMap.BucketHasItemsOfType, (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&OnHasItems);
    }

    /// <summary>The walk context, published for the modules that key on it: nesting depth, the bucket being
    /// walked, its cull phase, and a generation bumped per walk and per phase.</summary>
    internal static int _inBucket, _bucketId = -1, _phase, _gen;

    /// <summary>Items this walk skipped, for the period line. A gate counter, not a batch one: it counts
    /// both the owned-pass suppressions and the batch-covered members.</summary>
    internal static long _iterSkip;

    /// <summary>Scene whose bucket walk is currently running. Camera::RenderScene is not exclusive to the
    /// world view -- GUI 3D panels drive the same Scene::RenderSinglePass path with their own Scene -- so a
    /// capture group is keyed by the Scene it was recorded from and ownership can never harvest across two (#91).</summary>
    internal static IntPtr _bucketScene;
    /// <summary>Whether <see cref="_bucketScene"/> is the world Scene, resolved while it is still live. Caster
    /// enrollment runs at the present with no Scene in hand, so it reads this instead of dereferencing a group's
    /// stored (possibly freed) Scene pointer.</summary>
    internal static bool _bucketWorld;

    [UnmanagedCallersOnly]
    internal static void OnBucket(IntPtr scene, int id)
    {
        int prevId = _bucketId;
        IntPtr prevScene = _bucketScene;
        bool prevWorld = _bucketWorld;
        _inBucket++; _bucketId = id; _bucketScene = scene; _gen++;
        _bucketWorld = OwnedDrawService.IsWorldScene(scene);
        // The census arms on a world bucket-0 entry, which is the only edge in the frame that says "the
        // engine is about to walk the world". Guarded rather than called, so an unarmed census costs the
        // walk one compare.
        if (_dumpAt >= 0 && _bucketWorld && id == 0) OwnershipCensus.BeginDump();
        // #132: the owned block no longer hangs off this walk -- the frame anchor moved to
        // Scene::FadeDoors, the last engine call before RenderDrawBucket(0). All this hook still owes the
        // owned pass is the capture/suppression bracket around the engine's own walk.
        if (_origBucket != null) _origBucket(scene, id);
        _inBucket--; _bucketId = prevId; _bucketScene = prevScene; _bucketWorld = prevWorld;
    }

    /// <summary>#129: the whole of the engine's water block, gated on one answer.
    /// <para><c>Scene::RenderSinglePass</c> @0x46e5f0 wraps its water work in
    /// <c>if (HasItemsOfType(mgr, 6))</c>, and inside that block it does exactly two things:
    /// <c>Camera::CopyRenderSubframe</c> -- its SECOND full colour+depth snapshot of the frame -- and
    /// <c>RenderDrawBucket(6)</c>. Answering "no" removes both together, which is what makes the ticket's
    /// one-copy claim structural: there is no second suppression to keep in step with the first, and no way
    /// for the copy to survive a change to the walk.</para>
    /// <para>The answer is "no" only when every part in the bucket is one the owned pass DREW this pass, and
    /// that is checked against the bucket's live contents rather than against our own tables. The difference
    /// matters: with the walk suppressed, a water part that appears later (an area transition, a placeable
    /// that swims into view, anything sighted after the last enrollment) never runs its vanilla draw, and the
    /// capture funnel that would record it hangs off exactly that draw. Checked against our tables it would
    /// be in the bucket, suppressed, unowned and invisible -- permanently, since nothing would ever record
    /// it. Checked against the bucket, the newcomer simply makes this frame the engine's; it gets recorded
    /// through the normal funnel and the owned pass takes over a pass or two later.</para>
    /// <para>Only type 6. Type 5 is the framebuffer-sampling class, which is still the engine's.</para></summary>
    [UnmanagedCallersOnly]
    internal static int OnHasItems(IntPtr mgr, int id)
    {
        int has = _origHasItems != null ? _origHasItems(mgr, id) : 0;
        if (id != OwnedDrawService.WaterBucket || has == 0 || !Live) return has;
        // The walk runs FIRST and unconditionally, because it carries the sighting -- short-circuiting on
        // FrameSnapshot.Ready would starve exactly the parts a stood-down copy is about to hand back, and they
        // would then be stale on the frame the copy recovers.
        bool covered = AllBucketPartsOwned(mgr, id);
        if (!covered || !FrameSnapshot.Ready) { _engineWaterWalks++; return has; }
        _ownedWaterWalks++;
        return 0;
    }

    /// <summary>Is every part currently filed in <paramref name="id"/> one the owned pass drew this pass?
    /// <para>Reads the bucket arrays directly. <c>DrawBucketManager::GetDrawBucketByItemType</c> @0x47bd60
    /// is <c>this + (type*3 + phase)*0x10</c> and <c>GetNextItem</c> @0x47be90 reads {<c>Part** items</c>
    /// @+0, <c>int count</c> @+8} out of it, so the layout is not inferred. The ITERATOR pair is deliberately
    /// not used: <c>GetFirstItem</c> resets a cursor at manager+0x300 that the engine's own walk owns.</para>
    /// <para>Each entry is a <c>Part*</c>; the recorded identity is its <c>PartTriMesh</c>, resolved through
    /// vtable slot 0x60 exactly as <see cref="OnNextItem"/> does. Cold-ish: a handful of parts, once per
    /// frame, no allocation.</para></summary>
    internal static bool AllBucketPartsOwned(IntPtr mgr, int id)
    {
        // Walked to the END even once the answer is known: the walk is also where every part in the bucket
        // gets its sighting stamped, and an early return would starve exactly the parts it skipped.
        bool all = true;
        for (int phase = 0; phase < BucketPhases; phase++)
        {
            IntPtr bucket = mgr + (id * BucketPhases + phase) * BucketStride;
            IntPtr* items = *(IntPtr**)bucket;
            int count = *(int*)(bucket + BucketCount);
            if (items == null) continue;
            for (int i = 0; i < count; i++)
            {
                IntPtr part = items[i];
                if (part == IntPtr.Zero) continue;
                IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)
                              (*(IntPtr*)(*(IntPtr*)part + PartVtblGetTriMesh)))(part);
                if (tri == IntPtr.Zero) { all = false; continue; }
                int e = Find(tri);
                if (e < 0) { all = false; continue; }
                // THE SIGHTING, and it has to happen here or the class starves itself. Every other
                // _pSeen write is on the vanilla draw or on the bucket iterator -- and suppressing the
                // bucket-6 walk removes BOTH for water. Without this the liveness signal decays after one
                // frame, the staging walk drops every water part as "engine-culled", the gate hands the
                // frame back, the engine draws and re-sights, and the class oscillates on a two-frame
                // period: visibly, water flickering on and off. Presence in the bucket IS the sighting the
                // walk would have given -- the engine's own BSP visibility put it there this frame.
                _pSeen[e] = _frame;
                if (OwnFlag[e] == 0 || OwnGen[e] != OwnedDrawService.PassGen) { all = false; continue; }
                // ...and the SUPPRESSION stamp, for the same reason. The audit's double-draw signature is
                // "owned this pass and not suppressed", and IterSight is what normally clears it -- but
                // IterSight only ever runs from the walk this gate is about to remove. Left unstamped, every
                // owned water part reports as UNMATCHED on every successful pass, which is exactly the alarm
                // that has to stay quiet to be worth anything. Stamped here rather than in a second walk:
                // for an owned part the engine's own walk would have stamped it too.
                OwnSupGen[e] = OwnedDrawService.PassGen;
            }
        }
        return all;
    }

    /// <summary>Bucket-array geometry, from <c>GetDrawBucketByItemType</c> @0x47bd60 (stride 0x10, three
    /// phases per item type) and <c>GetNextItem</c> @0x47be90 (items at +0, count at +8).</summary>
    internal const int BucketPhases = 3, BucketStride = 0x10, BucketCount = 8;
    internal static delegate* unmanaged<IntPtr, int, int> _origHasItems;
    /// <summary>#129's acceptance counter: frames the ENGINE walked bucket 6 (and therefore took its own
    /// framebuffer copy), and frames the owned pass took it over instead.</summary>
    internal static long _engineWaterWalks, _ownedWaterWalks;

    [UnmanagedCallersOnly]
    internal static IntPtr OnFirstItem(IntPtr mgr, int id, int phase)
    {
        if (phase != _phase) { _phase = phase; _gen++; }
        return _origFirstItem != null ? _origFirstItem(mgr, id, phase) : IntPtr.Zero;
    }

    /// <summary>ADR 0006 phase 3: iteration-level ownership. Members already covered by this walk's
    /// multi-draw are skipped HERE, so the engine's loop body (matrix push, SetShaderParamv, fog/light/cull
    /// toggles, dissolve test, SetUniforms dirty-scan) never runs for them at all -- previously every skipped
    /// member still paid all of that and we only suppressed the terminal draw. The slot's end sentinel derefs
    /// to null, exactly the caller's own exit test, so looping past skipped slots is transparent. Any
    /// revalidation failure (recycled pointer, re-rotated/animated texture, tile fade, keyhole dissolve) falls
    /// through to the vanilla loop body, where OnPartDraw dirties/demotes as before -- worst case is one
    /// z-identical duplicate opaque draw that frame.</summary>
    [UnmanagedCallersOnly]
    internal static IntPtr OnNextItem(IntPtr mgr, int id, int phase)
    {
        for (;;)
        {
            IntPtr slot = _origNextItem(mgr, id, phase);
            if (slot == IntPtr.Zero) return slot;
            IntPtr part = *(IntPtr*)slot;
            if (part == IntPtr.Zero || !Live || _inBucket == 0)
                return slot;
            IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)(*(IntPtr*)part + PartVtblGetTriMesh)))(part);
            if (tri == IntPtr.Zero) return slot;
            int e = Find(tri);
            if (e < 0) return slot;
            // #82: a part the owned world pass drew this pass is skipped HERE (exactly-one-owner). The
            // sighting is also its liveness signal, exactly as for batched members below.
            if (OwnFlag[e] != 0)
            {
                _pSeen[e] = _frame;
                if (OwnedDrawService.IterSight(e, _bucketId)) continue;
                return slot;
            }
            // ...and only THEN the batcher's own suppression, which bucket 8 stays out of (#126 moved the
            // gate here: the owned branch above must be able to suppress a fading part, which is a bucket-8
            // part by definition).
            if (_bucketId == AlphaBucket) return slot;
            if (_pState[e] != StActive || _pOwnOnly[e] || _pIncluded[e] != _gen) return slot;
            if (*(IntPtr*)(tri + PartNode) != _pNode[e] || *(IntPtr*)(tri + PartMaterial) != _pMat[e]) return slot;
            if (*(int*)(tri + PartTexRotate) != _pRot[e] || (IntPtr)_getTexture(_pMat[e], 0) != _pTex0[e]) return slot;
            IntPtr tile = *(IntPtr*)(tri + PartTile);
            if (tile != IntPtr.Zero && Fading(tile)) return slot;
            if (_keyhole != null && _camSlot != IntPtr.Zero)
            {
                IntPtr cam = *(IntPtr*)_camSlot;
                if (cam != IntPtr.Zero && _keyhole(cam, tri) != 0) return slot;
            }
            _pSeen[e] = _frame;   // the iterator sighting IS the liveness signal now
            _iterSkip++;
        }
    }
}
