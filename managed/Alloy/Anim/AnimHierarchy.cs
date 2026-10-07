using System;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>
/// CX-106: one <c>AnimRun</c> applied to a part tree. Ports <c>AnimateHierarchy</c> @0x499990, the seven
/// <c>MdlNode*::InternalControl</c> bodies it dispatches through <see cref="EngineLayout.MdlNodeControl"/>,
/// and the animmesh vertex morph inside <c>MdlNodeAnimMesh::InternalControl</c> @0x499320.
/// </summary>
/// <remarks>
/// <para><b>A node's class is its control function pointer.</b> <c>MdlNode+0x18</c> is not a vtable slot but a
/// plain field the node's constructor fills with one of seven thunks, so identifying the class is a pointer
/// compare against seven resolved addresses -- no <c>As*</c> cast, no kind bits. A node whose pointer matches
/// none of them is handed straight back to it: that is the honest answer for content or an engine version
/// this build's table does not describe, and it is counted, not silent.</para>
/// <para>Five of the six subclass controllers do nothing but a discarded type-check cast before the base
/// loop, so they collapse into one branch. The two that differ:
/// <c>MdlNodeEmitter</c> calls <c>PartEmitter::CalculateRadius</c> AFTER its controllers, and
/// <c>MdlNodeAnimMesh</c> morphs vertices BEFORE them -- and returns without running them at all when the
/// weight is not positive.</para>
/// <para>Allocation-free. The recursion depth is this class's own counter rather than the engine's
/// <c>depth</c> global: nothing else reads that global, and an owned counter cannot be left non-zero by an
/// engine path that threw.</para>
/// </remarks>
internal static unsafe class AnimHierarchy
{
    /// <summary>The engine's recursion ceiling, and its shape: a node tree deeper than this stops being
    /// walked, it is not an error.</summary>
    private const int MaxDepth = 1000;
    /// <summary>The event <c>AnimateHierarchy</c> fires per controlled light part whose
    /// <see cref="EngineLayout.LightStaticLit"/> is zero. NUL-terminated for the engine's <c>char*</c>.</summary>
    private static ReadOnlySpan<byte> StaticLightEvent => "AnimateStaticLight\0"u8;

    private static IntPtr _fnBase, _fnAnimMesh, _fnCamera, _fnEmitter, _fnLight, _fnReference, _fnTriMesh;
    private static EmitterCalcRadiusFn _calcRadius;
    private static CtrlReportKeysFn _reportKeys;
    private static VdoGetItemDynamicDataFn _getItemDyn;
    private static VdoCommitItemDynamicDataFn _commitItemDyn;
    private static IntPtr* _pTraceRun;
    private static IntPtr _fnStaticLight;
    private static int _depth;

    internal static int Parts, Controllers, Morphs, HandedBack, Traced;
    /// <summary>AN0: <c>AnimateStaticLight</c> announcements fired this period. The survey had no number for
    /// it and it is one <c>Gob::DoEvent</c> -- a <c>CExoArrayList::Add</c> plus a <c>strcasecmp</c> per
    /// registered handler -- per controlled light part per RUN.</summary>
    internal static int StaticLights, StaticLightsSkipped;
    /// <summary>AN1: how many times the part-to-node pairing had to probe PAST its first candidate. The port's
    /// claim that "models and their animations are authored in the same node order, so the first probe almost
    /// always hits" was a belief; this is the reading that settles it, and a resolved-node column per animation
    /// is worth building only if it is large.</summary>
    internal static int PairProbes;

    /// <summary>Resolve the seven class thunks and the three engine leaves. False = the runtime must not arm
    /// (an unresolved thunk would make every node of that class read as "unknown").</summary>
    internal static bool Attach(ExtenderLog log)
    {
        _fnBase = Native.Sym(SymbolMap.MdlNodeControlFn);
        _fnAnimMesh = Native.Sym(SymbolMap.MdlNodeAnimMeshControlFn);
        _fnCamera = Native.Sym(SymbolMap.MdlNodeCameraControlFn);
        _fnEmitter = Native.Sym(SymbolMap.MdlNodeEmitterControlFn);
        _fnLight = Native.Sym(SymbolMap.MdlNodeLightControlFn);
        _fnReference = Native.Sym(SymbolMap.MdlNodeReferenceControlFn);
        _fnTriMesh = Native.Sym(SymbolMap.MdlNodeTriMeshControlFn);
        _calcRadius = EngineFn.EmitterCalcRadius;
        _reportKeys = EngineFn.CtrlReportKeys;
        _getItemDyn = EngineFn.VdoGetItemDynamicData;
        _commitItemDyn = EngineFn.VdoCommitItemDynamicData;
        _pTraceRun = (IntPtr*)Native.Sym(SymbolMap.AnimTraceRun);
        // NOT an arming condition: this identity only decides whether an announcement can be SKIPPED, and a
        // build that stops exporting it should cost the skip, not the whole owned runtime.
        _fnStaticLight = Native.Sym(SymbolMap.AnimateStaticLight);
        if (_fnStaticLight == IntPtr.Zero)
            log.Always("AnimHierarchy: AnimateStaticLight did not resolve -- every animated static light "
                     + "announces itself every frame, as the engine does; /anim staticlight has nothing to skip");
        return _fnBase != IntPtr.Zero && _fnAnimMesh != IntPtr.Zero && _fnCamera != IntPtr.Zero
            && _fnEmitter != IntPtr.Zero && _fnLight != IntPtr.Zero && _fnReference != IntPtr.Zero
            && _fnTriMesh != IntPtr.Zero && _calcRadius != null && _reportKeys != null
            && _getItemDyn != null && _commitItemDyn != null && _pTraceRun != null;
    }

    /// <summary>
    /// <c>AnimateHierarchy(part, node, tPrev, tCur, weight)</c>: control this part from this animation node,
    /// then pair each child part with the animation child that carries the same node id and recurse. The
    /// sibling scan starts at <c>i % n</c> and wraps once, exactly as the engine's. Whether the first probe
    /// almost always hits -- the belief a resolved-node column per animation would be built against -- is not
    /// asserted here: <see cref="PairProbes"/> counts the probes past it and the period line prints them.
    /// </summary>
    internal static void Walk(IntPtr part, IntPtr node, float tPrev, float tCur, float w)
    {
        _depth++;
        var p = new Part(part);
        if (p.AnimateGate == 0)
        {
            Parts++;
            Node(node, part, tPrev, tCur, w);
            IntPtr light = p.AsPartLight().Ptr;
            if (light != IntPtr.Zero && new PartLight(light).StaticLit == 0)
            {
                IntPtr gob = p.Gob.Ptr;
                if (gob != IntPtr.Zero)
                {
                    if (!Heard(gob)) StaticLightsSkipped++;
                    else
                    {
                        StaticLights++;
                        fixed (byte* name = StaticLightEvent) new Gob(gob).DoEvent(name, light);
                    }
                }
            }
        }
        if (_depth < MaxDepth && p.ChildCount > 0)
        {
            var n = new MdlNode(node);
            for (int i = 0; i < p.ChildCount; i++)
            {
                int nc = n.ChildCount;
                if (nc <= 0) continue;
                // Re-read both arrays each step, as the engine does: the AnimateStaticLight event above can
                // in principle reshape the tree under us.
                IntPtr* parts = (IntPtr*)p.Children;
                if (parts == null) break;
                IntPtr cp = parts[i];
                if (cp == IntPtr.Zero) continue;
                IntPtr* nodes = (IntPtr*)n.Children;
                if (nodes == null) break;
                int want = new Part(cp).NodeId, j = i;
                IntPtr cn = nodes[i % nc];
                while (new MdlNode(cn).NodeId != want)
                {
                    PairProbes++;
                    if (++j == i + nc) { cn = IntPtr.Zero; break; }
                    cn = nodes[j % nc];
                }
                if (cn != IntPtr.Zero) Walk(cp, cn, tPrev, tCur, w);
            }
        }
        _depth--;
    }

    /// <summary>
    /// AN6: would this gob's <c>AnimateStaticLight</c> announcement reach anything that acts on it?
    /// <para><b>Who hears it.</b> <c>Gob::DoEvent</c> @0x49d760 walks the gob's own <c>CallBack</c> array
    /// (<see cref="EngineLayout.GobCallbacks"/>) in reverse and fires an entry whose inline name is empty or
    /// begins with <c>*</c> -- a wildcard, which <c>CAurBehaviorAttach</c>'s constructor registers as
    /// <c>UpdateScenesAttach</c> -- or matches the event name case-insensitively. A gob with NO entries runs
    /// no handler at all: the call is one <c>CExoArrayList::Add</c> of a self <c>SafePointer</c> and its
    /// removal, and nothing else. Most animated gobs are in exactly that state; the only registrar of this
    /// event's name is <c>ProcessStaticObject</c> @0x4619e0, on tile static objects.</para>
    /// <para><b>Who acts on it.</b> The stock handler <c>AnimateStaticLight</c> @0x47d820 is
    /// <c>UpdateStaticLighting</c> @0x47d6a0: a <c>BSPSphereCheck</c> of the tile BSP into a malloc'd list,
    /// whose hits it appends (deduplicated) to the global <c>staticlightbucket</c>. That bucket has ONE
    /// consumer, <c>DoStaticLightBuckets</c> @0x47e0b0, and one caller for it, <c>ManageSceneBSP</c>
    /// @0x47fa80 -- which <c>alloy.skipbuckets 3</c> stands down. So on an Alloy frame the announcement walks
    /// a BSP and allocates to fill a list nothing ever drains.</para>
    /// <para>Note that the engine's two other callers of <c>UpdateStaticLighting</c> --
    /// <c>Scene::SetTileLights</c> @0x45edf0 and <c>Scene::SetSingleTileFlame</c> @0x45ef30 -- both compare
    /// the new colour against the light's own before they call. This announcement is the outlier that fires
    /// unconditionally, not the rule.</para>
    /// </summary>
    private static bool Heard(IntPtr gob)
    {
        // No identity, no skip: the announcement is made, which is the engine's own behaviour.
        if (!AnimKnobs.StaticLight.V || _fnStaticLight == IntPtr.Zero) return true;
        var g = new Gob(gob);
        int n = g.CallbackCount;
        IntPtr* list = (IntPtr*)g.Callbacks;
        if (n <= 0 || list == null) return false;
        // ...and with the bucket path live, the stock handler still has something to fill.
        bool drains = Renderer.SkipBuckets.Boot != SkipAll;
        for (int i = 0; i < n; i++)
        {
            byte* cb = (byte*)list[i];
            if (cb == null) continue;
            // A wildcard or an empty name hears every event whatever it is named, so it always pins.
            if (*cb == 0 || *cb == (byte)'*') return true;
            // Otherwise an entry only fires if its NAME matches -- and only the stock handler is the one whose
            // work the bucket path can throw away. The test is therefore the handler POINTER, so a third party
            // that registered its own function under this event's name pins exactly rather than by assumption.
            if (*(IntPtr*)(cb + EngineLayout.CallBackFn) == _fnStaticLight) { if (drains) return true; continue; }
            if (Named(cb)) return true;
        }
        return false;
    }

    /// <summary>Does this entry's name match the event, as <c>Gob::DoEvent</c>'s <c>strcasecmp</c> would? Only
    /// reached for an entry that is NOT the stock handler, where a match means something unknown to us is
    /// listening and the announcement must be made.</summary>
    private static bool Named(byte* name)
    {
        ReadOnlySpan<byte> want = StaticLightEvent;
        for (int i = 0; i < want.Length; i++)
        {
            byte a = name[i], b = want[i];
            if (a >= (byte)'A' && a <= (byte)'Z') a += 32;
            if (b >= (byte)'A' && b <= (byte)'Z') b += 32;
            if (a != b) return false;
            if (a == 0) return true;
        }
        return true;
    }

    /// <summary>The <c>alloy.skipbuckets</c> value that stands <c>ManageSceneBSP</c> down whole.</summary>
    private const int SkipAll = 3;

    /// <summary>The node's own controller pass, dispatched on its class (its
    /// <see cref="EngineLayout.MdlNodeControl"/> thunk).
    /// <para>Seven thunks, not eight. An eighth exists as a SYMBOL -- <c>MdlNodeTriggerControl</c> @0x4d9ec0,
    /// a bare tail-call into <c>MdlNode::InternalControl</c> -- but nothing installs it: a binary-wide sweep
    /// of every store of a <c>MdlNode*Control</c> address finds only these seven, and
    /// <c>MdlNodeTrigger::MdlNodeTrigger</c> @0x4d9ed0 writes <c>+0x98</c>, <c>+0x8</c>, <c>+0x20</c> and
    /// <c>+0x28</c> and never <c>+0x18</c>, so a trigger node keeps the <c>MdlNodeControl</c> that
    /// <c>MdlNode::MdlNode</c> put there (@0x43ab4e) and takes the base path below. Recorded because the
    /// symbol table makes it look like a gap in this table.</para></summary>
    private static void Node(IntPtr node, IntPtr part, float tPrev, float tCur, float w)
    {
        IntPtr fn = *(IntPtr*)(node + EngineLayout.MdlNodeControl);
        if (fn == _fnBase || fn == _fnCamera || fn == _fnLight || fn == _fnReference || fn == _fnTriMesh)
        {
            Controllers += Base(node, part, tPrev, tCur, w);
            return;
        }
        if (fn == _fnEmitter)
        {
            Controllers += Base(node, part, tPrev, tCur, w);
            IntPtr pe = new Part(part).AsPartEmitter().Ptr;
            if (pe != IntPtr.Zero) _calcRadius(pe);
            return;
        }
        if (fn == _fnAnimMesh)
        {
            // The engine's own first act, and it skips the controller loop too: a non-positive weight means
            // this run contributes nothing to an animmesh at all.
            if (!(w > 0f)) return;
            Morph(node, part, tCur, w);
            Controllers += Base(node, part, tPrev, tCur, w);
            return;
        }
        HandedBack++;
        ((delegate* unmanaged<IntPtr, IntPtr, float, float, float, void>)fn)(node, part, tPrev, tCur, w);
    }

    /// <summary><c>MdlNode::InternalControl</c>: every controller of the node, in declared order, with the
    /// trace channel behind it while a flag-0x100 run is live.</summary>
    private static int Base(IntPtr node, IntPtr part, float tPrev, float tCur, float w)
    {
        var n = new MdlNode(node);
        IntPtr ctrl = n.Controllers;
        if (ctrl == IntPtr.Zero) return 0;
        int count = n.ControllerCount;
        bool trace = *_pTraceRun != IntPtr.Zero;
        for (int i = 0; i < count; i++)
        {
            IntPtr c = ctrl + i * EngineLayout.NewControllerStride;
            AnimKeys.Control(c, part, node, tCur, w);
            // The trace channel reports per-key position/orientation samples back through DoEvent; it is
            // armed only by AnimRun flag 0x100, which nothing in ordinary play sets.
            if (trace) { Traced++; _reportKeys(c, part, node, tPrev, tCur); }
        }
        return count;
    }

    /// <summary>
    /// Which morph frame each of the two pools uses, and whether it lerps into the next one.
    /// <para><b>The frame index is SHARED and the POSITION pool clamps it in place</b> before the UV pool
    /// reads it (<c>cmovge r13d,ecx</c> @0x49975e and <c>xor r13d,r13d</c> @0x499940, both ahead of the UV
    /// decision at 0x49986f). Clamping each pool against its own frame count independently -- which is what
    /// the code looks like it should do -- extrapolates the UVs where the engine holds a key and holds them
    /// where the engine extrapolates. It only shows on a node whose two pools have different frame counts,
    /// which is why this is its own function with its own check.</para>
    /// </summary>
    internal static void Frames(ref int frame, int vf, int tf, out bool lerpPos, out bool lerpUv, out int uvFrame)
    {
        lerpPos = false;
        if (vf != 0)
        {
            if (frame < 0) frame = 0;
            else if (frame < vf - 1) lerpPos = true;
            else if (frame >= vf) frame = vf - 1;
        }
        // A frame still negative here means the positions never ran (no morph vertex frames at all); the
        // engine forces it to 0 and takes the exact key (@0x49987b).
        int uf = frame < 0 ? 0 : frame;
        lerpUv = frame >= 0 && uf < tf - 1;
        uvFrame = lerpUv ? uf : Math.Min(uf, tf - 1);
    }

    /// <summary>
    /// The animmesh vertex morph: pick the morph frame from <c>tCur / samplePeriod</c>, lerp between it and
    /// the next inside the pool, and blend the result into the part's DYNAMIC vertex item by
    /// <paramref name="w"/>. Positions and UVs only -- no normals, no tangents, no colours.
    /// </summary>
    private static void Morph(IntPtr node, IntPtr part, float tCur, float w)
    {
        IntPtr pa = new Part(part).AsPartAnimMesh();
        if (pa == IntPtr.Zero) return;
        var mn = new MdlNodeAnimMesh(node);
        float period = mn.SamplePeriod;
        // Exact-zero test, as the engine's: a zero period parks every morph on frame 0.
        int frame = period == 0f ? 0 : (int)(tCur / period);
        var vp = new PartTriMesh(pa).VertexPrimitive;
        if (vp.IsNull) return;
        int item = vp.Item;
        IntPtr vdo = vp.Vdo.Ptr;
        if (item == -1 || vdo == IntPtr.Zero) return;
        byte* raw = _getItemDyn(vdo, (uint)item);
        if (raw == null) return;
        float* dst = (float*)raw;
        int verts = mn.MorphVertexCount;
        int vf = mn.VertFrames, tf = mn.UvFrames;
        bool dirty = false;
        float u = 1f - w;

        Frames(ref frame, vf, tf, out bool lerpPos, out bool lerpUv, out int uvFrame);

        float* pool = (float*)mn.MorphVerts;
        if (vf != 0 && verts != 0 && pool != null)
        {
            float a = lerpPos ? tCur / period - frame : 0f;
            for (int v = 0; v < verts; v++)
            {
                float* b = pool + (vf * v + frame) * 3;
                float x = lerpPos ? b[0] * (1f - a) + b[3] * a : b[0];
                float y = lerpPos ? b[1] * (1f - a) + b[4] * a : b[1];
                float z = lerpPos ? b[2] * (1f - a) + b[5] * a : b[2];
                float* o = dst + v * EngineLayout.MorphStrideFloats;
                if (o[0] == x && o[1] == y && o[2] == z) continue;
                dirty = true;
                o[0] = o[0] * u + x * w;
                o[1] = o[1] * u + y * w;
                o[2] = o[2] * u + z * w;
            }
        }

        float* uvs = (float*)mn.MorphUvs;
        if (tf != 0 && verts != 0 && uvs != null)
        {
            int f = uvFrame;
            float a = lerpUv ? tCur / period - f : 0f;
            for (int v = 0; v < verts; v++)
            {
                float* b = uvs + (tf * v + f) * 2;
                float s = lerpUv ? b[0] * (1f - a) + b[2] * a : b[0];
                float t = lerpUv ? b[1] * (1f - a) + b[3] * a : b[1];
                float* o = dst + v * EngineLayout.MorphStrideFloats + 3;
                if (o[0] == s && o[1] == t) continue;
                dirty = true;
                o[0] = o[0] * u + s * w;
                o[1] = o[1] * u + t * w;
            }
        }

        if (!dirty) return;
        Morphs++;
        _commitItemDyn(vdo, (uint)item, 0xffffffffu);
    }
}
