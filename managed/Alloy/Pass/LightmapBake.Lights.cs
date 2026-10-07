using System;
using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The static-light side of the bake (#674): the three-branch rule (#654/#664/#659) classified once
/// per <c>PartLight</c> identity, the per-frame static snapshot the bake reads, the static-set fingerprint
/// that is the one re-key edge (#668), and the <c>CAurScene::ComputeStaticLighting</c> hook that voids the
/// classification. Every reader here runs on the light walk (the shadow bracket) and allocates nothing.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Classification table: open addressing on the PartLight pointer. 0 = unknown, 1 = static,
    /// 2 = dynamic. Voided whole at the world edge, the hook edge and a tile change.
    /// <para>Sized from <see cref="LightRegistry.Capacity"/>, because it is keyed on the SAME population: a
    /// 2048-slot table with an 8-slot probe window was the shape that dropped lights wholesale in
    /// <c>LightRegistry</c> before it was sized properly (1422 live lights spilling 148502 times in one
    /// session), and the probe is the whole table here for the same reason -- a key whose window happens to be
    /// full must not be refused while the table has room. Only the MEMO is lost on a refusal, so the defect was
    /// re-classifying a light every walk rather than a dark lamp; it is still a bound that can be hit without
    /// anything saying so, which is what <see cref="_clsFull"/> is for.</para></summary>
    private const int ClsSlots = LightRegistry.Capacity;
    private static readonly IntPtr[] _clsId = new IntPtr[ClsSlots];
    private static readonly byte[] _cls = new byte[ClsSlots];
    /// <summary>The static set is ONE walk: CX-56 makes it the first walk after <c>CNWCArea::LoadArea</c>
    /// returned non-zero (<see cref="_loadArmed"/>) whose static lights have all settled
    /// (<see cref="Settled"/>), where it used to be the first walk 30 past the AreaLoaded send and 60 past the
    /// last light to appear -- a second of frame counting for an answer the load's own message already has.
    /// Closed, the set is final for the area: a light that appears later is dynamic, whatever its shape, and a
    /// server tile change reopens it (<see cref="OnTileChanged"/>).</summary>
    private static bool _closed, _reopen;
    private static int _walks;
    private static readonly HashSet<IntPtr> _lateLogged = new();
    /// <summary>The area under the walk changed since the last close: the pages are the previous area's and
    /// rewind at this close (an in-module area change is no world edge -- run 30 kept 12 pages of the last
    /// area's rects resident under the next one and halved its density for them).</summary>
    private static bool _areaChanged;
    /// <summary>The first refused lights of an area, by node name, once: what the refused set IS (run 8: xtul's
    /// module marks every one of them Static, so the three-branch rule's premise is wrong somewhere).</summary>
    private static int _whyLeft;
    /// <summary>Classifications the table had no room for, so the verdict was recomputed instead of memoised.
    /// With the table sized from <see cref="LightRegistry.Capacity"/> and the probe covering it, this can only
    /// move when every slot is taken -- i.e. it is a sizing fact, not a hash accident. <see cref="Describe"/>
    /// prints it.</summary>
    private static int _clsFull;

    /// <summary>CX-56: <c>CNWCArea::LoadArea</c> @0x70e010 has RETURNED non-zero, so every light of this area
    /// is in the master list -- its tile loop, <c>SetSourceLightColor</c>'s gobs and both placeable blocks are
    /// all synchronous, and the AreaLoaded send comes from a later message
    /// (CX-131). Lowered at the call's ENTRY, because a walk that runs inside
    /// <c>LoadArea</c> (its own <c>UpdateScreen</c> ticks) sees a half-spawned area. It stays raised for the
    /// whole area, so a reopen -- the area edge's <see cref="Revoke"/>, a tile change -- costs the close one
    /// walk instead of the 90 the walk counts used to.</summary>
    private static volatile bool _loadArmed;
    /// <summary>...and its refusal: <c>LoadArea</c> returned 0. Its error paths destroy the tile array, so
    /// there is no set to close and none is closed on the old walk-count path either -- the bake stays
    /// un-armed for this area (<c>SetHash</c> 0) and the hold has nothing to wait for.</summary>
    private static volatile bool _loadRefused;
    /// <summary>How many area loads the engine has refused this session; <see cref="Describe"/>'s honesty.</summary>
    internal static int LoadRefusals;
    /// <summary>Walks the close may spend waiting for a static light's gob to finish blending its animation in
    /// (<see cref="Settled"/>). <c>Gob::Animate</c> advances a weight by <c>dt/blendTime</c> from 0, so a
    /// settle is one blend time -- run 26 measured 0.2 s, a dozen walks. Past this the set closes WITHOUT the
    /// lights that are still blending (they stay dynamic) and the line is loud: a blend that never finishes
    /// would otherwise hold the loading screen to its cap.</summary>
    private const int BlendCeiling = 60;
    private static int _regCountSeen = -1;
    /// <summary>Every gob the area's placeable-light registry has ever named this area. The registry is filled
    /// once from the area message and EMPTIES as each placeable adopts its light gob (CNWCPlaceable::LoadLight
    /// @0x8eb140 re-parents it and removes the record with bFree 0: CX-131), so
    /// the live registry told Classify "placeable" only until the adoption -- the light then re-judged
    /// dynamic, the class table voided on every count change, and the static set churned for as long as
    /// adoptions ran (run 21's three re-keys on a zoom).</summary>
    private static readonly HashSet<IntPtr> _regGobs = new();
    private static IntPtr _clsArea;

    /// <summary>The area's static set over the UNCUT master list, accumulated across walks (#654: the
    /// fingerprint must not fold the culled set): position, LIT radius, the same colour the light SSBO carries.</summary>
    private static Vector4[] _stPosR = new Vector4[256];
    private static Vector3[] _stCol = new Vector3[256];
    private static int _stN;
    /// <summary>The walk's census: static / animated (>1-key radius, colour or multiplier track) / dynamic,
    /// per frame.</summary>
    internal static int CensusStatic, CensusAnimated, CensusDynamic, CensusTile, CensusPlaceable;
    /// <summary>The part of <see cref="CensusAnimated"/> that the gob's RUNNING animation drives rather than
    /// its model node (<see cref="AnimationDrives"/>) -- the flickering torches CX-56 was baking.</summary>
    internal static int CensusAnimRun;
    /// <summary>Static lights this walk refused because their gob is still blending: the close waits on it.</summary>
    internal static int CensusUnsettled;
    /// <summary>The census of the closing walk: after the close the walk counts only static / dynamic.</summary>
    private static int _closeStatic, _closeTile, _closePlaceable, _closeAnimated, _closeAnimRun, _closeDynamic;

    /// <summary>The closed static set: copied off the snapshot on the edge, read by the bake and the keys.</summary>
    private static Vector4[] _setPosR = new Vector4[256];
    private static Vector3[] _setCol = new Vector3[256];
    private static int _setN;
    /// <summary>The closed set's identity, 0 = none. Every edge that moves the set assigns it AFTER the arrays,
    /// so the setter is where a live light's cached set index goes stale (<see cref="SetIndexOf"/>).</summary>
    internal static ulong SetHash { get => _setHash; private set { _setHash = value; _setIdxPtr.Clear(); } }
    private static ulong _setHash;

    /// <summary>CX-56: the static lights a baked surface holds a mover share for -- page C's four bytes, one per
    /// light of the surface's own pick (<see cref="PickLights"/>). Past them a light falls to the live estimate.</summary>
    internal const int Channels = 4;
    /// <summary>A live light's index in <see cref="_setPosR"/> by object, -1 = none; resolved once per set.</summary>
    private static readonly Dictionary<IntPtr, int> _setIdxPtr = new(512);

    /// <summary>CX-56: the set index of a static light the walk just recorded -- the set entry within
    /// <see cref="Near"/> of it, cached by object; -1 before the set closes or when it has no entry. The index
    /// is what a surface's pick names, so the lit stage can tell whose share a byte of page C is. Hot (every
    /// walk), allocation-free once each light is cached.</summary>
    private static int SetIndexOf(IntPtr light, Vector3 pos, float adjR)
    {
        if (_setHash == 0) return -1;
        if (_setIdxPtr.TryGetValue(light, out int c)) return c;
        c = -1;
        for (int l = 0; l < _setN && c < 0; l++)
            if (Near(_setPosR[l], pos, adjR)) c = l;
        _setIdxPtr[light] = c;
        return c;
    }

    /// <summary>CX-56: the <see cref="Channels"/> static lights that light the sphere (<paramref name="c"/>,
    /// <paramref name="r"/>) most -- estimated luminance at the sphere's nearest point through the loop's own
    /// curve -- as set indices, strongest first, -1 past the lights that touch it. Ties break on the light's
    /// position, never its index, so the pick is the lights' own and the rect's key (which holds every touching
    /// light and the sphere) names the same pick in every session. Channel k of the rect's page C texels is
    /// pick k's share.</summary>
    private static void PickLights(Vector3 c, float r, Span<int> pick)
    {
        Span<float> best = stackalloc float[Channels];
        pick.Fill(-1); best.Fill(-1f);
        Vector2 at = AlloyLights.Atten;
        for (int l = 0; l < _setN; l++)
        {
            if (!Touches(l, c, r)) continue;
            Vector4 pr = _setPosR[l];
            float d = MathF.Max((new Vector3(pr.X, pr.Y, pr.Z) - c).Length() - r, 0f);
            float f2 = MathF.Min(d * d / MathF.Max(pr.W * pr.W, 1e-6f), 1f);
            Vector3 col = _setCol[l];
            float w = (1f - f2) / MathF.Max(at.X + at.Y * f2, 1e-6f) * (0.2126f * col.X + 0.7152f * col.Y + 0.0722f * col.Z);
            int k = Channels;
            while (k > 0 && (pick[k - 1] < 0 || Beats(w, l, best[k - 1], pick[k - 1]))) k--;
            if (k >= Channels) continue;
            for (int j = Channels - 1; j > k; j--) { pick[j] = pick[j - 1]; best[j] = best[j - 1]; }
            pick[k] = l; best[k] = w;
        }
    }

    /// <summary>Does light <paramref name="a"/> at weight <paramref name="wa"/> rank above <paramref name="b"/>?
    /// Weight first, then position -- a total order that does not depend on how the set is listed.</summary>
    private static bool Beats(float wa, int a, float wb, int b)
    {
        if (wa != wb) return wa > wb;
        Vector4 pa = _setPosR[a], pb = _setPosR[b];
        if (pa.X != pb.X) return pa.X < pb.X;
        if (pa.Y != pb.Y) return pa.Y < pb.Y;
        if (pa.Z != pb.Z) return pa.Z < pb.Z;
        return pa.W > pb.W;
    }
    /// <summary><c>ComputeStaticLighting</c> hook edges this period (the period line prints them).</summary>
    private static int _cslEdges;

    private const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;
    private static ulong Mix(ulong h, long v) { h ^= (ulong)v; return h * FnvPrime; }
    private static long Q(float v, float step) => (long)MathF.Round(v / step);

    /// <summary>The static lights of this area by quantised position and reach, filled by the walks before the
    /// set closes and read-only after.</summary>
    private static readonly Dictionary<ulong, int> _seen = new(512);
    /// <summary>The same set by light object, so a light refused after the close can say whether it is a new
    /// object or one of the set that moved or changed reach (the drift diagnostic, 24 lines an area).</summary>
    private static readonly Dictionary<IntPtr, int> _seenPtr = new(512);
    private static int _lateLeft;
    /// <summary>Lights the walk dropped before Record (flags off, no range, no intensity, black), per frame.</summary>
    internal static int CensusSkipped;

    /// <summary>An area edge: the next walk is a NEW area, whatever the area pointer says -- run 31's server
    /// transition freed one CNWCArea and malloc handed the next one the same address, so the pointer compare
    /// below saw nothing, the plaza was baked against the harbour's set and the harbour's pages and charts
    /// stayed resident under it. Raised by <c>LoadArea</c>'s entry (CX-56: the area really does change there),
    /// by the AreaLoaded send and again by <see cref="EndAreaEdge"/> after that send's <see cref="Revoke"/>.
    /// <para>ORDER: the LoadArea edge lands first and the send's lands second, on the SAME area. They do not
    /// fight, because what closes the set is <see cref="_loadArmed"/> and not a walk count: the send's edge
    /// (and the revoke inside it, which rewinds the pages anyway) costs the close exactly one walk, and the
    /// set it re-closes is the same set -- every light was there at <c>LoadArea</c>'s return.</para></summary>
    internal static void OnAreaLoaded() => _edgePending = true;
    private static volatile bool _edgePending;
    /// <summary><c>CNWCArea::SetTile</c>: one tile's model and lights re-spawn -- the set is walked again.</summary>
    internal static void OnTileChanged() => _reopen = true;

    /// <summary>Restart the per-frame snapshot. Called from the walk's top, beside <c>BeginAreaLights</c>.</summary>
    internal static void BeginWalk(CNWCArea area)
    {
        CensusStatic = CensusAnimated = CensusDynamic = CensusTile = CensusPlaceable = CensusSkipped = CensusUnsettled = CensusAnimRun = 0;
        if (area.Ptr != _clsArea || _edgePending) { _clsArea = area.Ptr; _edgePending = false; _areaChanged = true; VoidClasses(); _regGobs.Clear(); _regCountSeen = -1; _whyLeft = 8; _lateLeft = 24; _lateLogged.Clear(); Reopen(); }
        if (_reopen) { _reopen = false; VoidClasses(); Reopen(); }
        if (_closed) return;
        // open, every walk is the whole set again: the lights the engine re-sets after the edge must not leave
        // their first reading in the set beside their real one
        _seen.Clear(); _seenPtr.Clear(); _stN = 0;
        _walks++;
        int reg = area.IsNull ? 0 : area.PlaceableLightCount;
        if (reg != _regCountSeen)
        {
            _regCountSeen = reg;
            if (!area.IsNull && area.PlaceableLights != IntPtr.Zero && reg > 0 && reg < 65536)
                for (int i = 0; i < reg; i++)
                {
                    CNWCPlaceableObjectLight rec = area.PlaceableLight(i);
                    if (!rec.IsNull) _regGobs.Add(rec.Gob.Ptr);
                }
        }
    }

    /// <summary>Has <see cref="IsStatic"/> already given this light identity a verdict? A probe, never a classify.</summary>
    private static bool Classified(IntPtr light)
    {
        int h = ClsHash(light);
        for (int k = 0; k < ClsSlots; k++)
        {
            IntPtr id = _clsId[(h + k) & (ClsSlots - 1)];
            if (id == light) return true;
            if (id == IntPtr.Zero) return false;
        }
        return false;
    }

    /// <summary>Fibonacci hash of the pointer with the four constant low bits dropped -- malloc hands back
    /// aligned blocks, so hashing those clusters every key into a sixteenth of the table. The same key shape
    /// <see cref="LightRegistry"/> uses on the same pointers.</summary>
    private static int ClsHash(IntPtr p)
        => (int)(((ulong)(long)p >> 4) * 0x9E3779B97F4A7C15UL >> (64 - ClsBits)) & (ClsSlots - 1);

    /// <summary>not-an-offset: <see cref="ClsSlots"/> as a shift, for <see cref="ClsHash"/>.</summary>
    private const int ClsBits = 14;

    private static void VoidClasses() { Array.Clear(_clsId); Array.Clear(_cls); }
    private static void Reopen() { _closed = false; _walks = 0; }

    /// <summary>The three-branch rule for one light: <c>gob == 0</c> (a static tile's node, or a Static
    /// placeable's own model light, both spawned gob-less) || the gob carries tile data (an animated tile
    /// model) || the gob is in the area's placeable-light registry (a <c>placeables.2da</c> light); and no
    /// radius/colour/multiplier controller with more than one key, on its model node OR on the animation the
    /// gob is running (<paramref name="byRun"/>). Ambient-class lights are never static (the caller
    /// excludes them). Cached per identity; a full table classifies without caching.</summary>
    internal static bool IsStatic(PartLight light, CNWCArea area, out bool animated, out bool placeable, out bool byRun)
    {
        animated = placeable = byRun = false;
        int h = ClsHash(light.Ptr);
        int free = -1;
        // The whole table, not a window: the chain ends at the first empty slot, so this is O(run length) in
        // practice and cannot refuse a key while a slot is free anywhere.
        for (int k = 0; k < ClsSlots; k++)
        {
            int j = (h + k) & (ClsSlots - 1);
            if (_clsId[j] == light.Ptr)
            {
                byte c = _cls[j];
                animated = (c & 4) != 0; placeable = (c & 8) != 0; byRun = (c & 0x10) != 0;
                return (c & 3) == 1;
            }
            if (_clsId[j] == IntPtr.Zero) { if (free < 0) free = j; break; }
        }
        bool st = Classify(light, area, out animated, out placeable, out byRun);
        if (!st && _whyLeft > 0 && _log != null)
        {
            _whyLeft--;
            Gob gob = new Part(light.Ptr).Gob;
            MdlNode node = new Part(light.Ptr).Node;
            string name = node.IsNull ? "?" : node.NameText ?? "?";
            _log.Info($"LightmapBake: refused light -- node '{name}' gob {gob.Ptr:x} tileData {(gob.IsNull ? IntPtr.Zero : gob.TileData.Ptr):x} animated {animated}{(byRun ? " (its running animation drives it)" : "")}");
        }
        if (free >= 0)
        {
            _clsId[free] = light.Ptr;
            _cls[free] = (byte)((st ? 1 : 2) | (animated ? 4 : 0) | (placeable ? 8 : 0) | (byRun ? 0x10 : 0));
        }
        else _clsFull++;
        return st;
    }

    private static bool Classify(PartLight light, CNWCArea area, out bool animated, out bool placeable, out bool byRun)
    {
        placeable = byRun = false;
        Gob gob = new Part(light.Ptr).Gob;
        bool tile = gob.IsNull || !gob.TileData.IsNull;
        if (!tile) placeable = _regGobs.Contains(gob.Ptr);
        animated = Drives(new Part(light.Ptr).Node);
        if (!animated && (byRun = AnimationDrives(light.Ptr))) animated = true;
        // present at the closed edge and not animated = the area's light, whatever spawned it: the candles and
        // model lights on placeables the registry never named (run 24: 39 static of 97, 58 live on every lit
        // fragment, cutout 5 ms) -- only a light carried by a creature is a mover by construction
        return !animated && (tile || placeable || !CreatureRooted(gob.Ptr));
    }

    /// <summary>Does this node carry a <c>radius</c>, <c>color</c> or <c>multiplier</c> track with more than
    /// one key -- i.e. a value that keeps moving rather than settling? The controller's id IS the raw
    /// <c>Part</c> byte offset it writes (<c>MdlNodeLight::InternalParseField</c> @0x44baf0), so it compares
    /// against the light's own layout rows.</summary>
    private static bool Drives(MdlNode node)
    {
        if (node.IsNull) return false;
        int cnt = node.ControllerCount;
        NewController recs = node.Controller;
        if (recs.IsNull || cnt <= 0 || cnt >= 256) return false;
        for (int i = 0; i < cnt; i++)
        {
            NewController c = recs[i];
            if (c.KeyCount <= 1) continue;
            if (c.Id == EngineLayout.LightRange || c.Id == EngineLayout.LightColour || c.Id == EngineLayout.LightIntensity) return true;
        }
        return false;
    }

    /// <summary>CX-56: is this light's reach, colour or multiplier driven by an animation its gob is RUNNING?
    /// <para>That is a different node tree from the model's. <c>Gob::Animate</c> (Linux @0x49d8c0, Windows
    /// @0x14004dbd0) calls <c>AnimateHierarchy(gob+0x90, anim+0x50, prev, time, blend)</c>, and
    /// <c>AnimateHierarchy</c> (Linux @0x499990, Windows rva 0x4fe90) descends the part tree and the
    /// ANIMATION's node tree in step, handing each paired node's controllers to
    /// <c>MdlNode::InternalControl</c> -&gt; <c>NewController::Control</c>, which writes the part's own light
    /// fields. So a torch's flicker loop -- a multi-key <c>radius</c> track in its animation -- never settles,
    /// however long its blend has been at 1: <see cref="Settled"/> answers "the cross-fade is over", not "the
    /// value is final". Baking such a light froze one point of the pulse into the lightmap and then ran the
    /// pulse live on top of it (the CX-56 "late light DRIFTED, reach 12,47 -&gt; 14,16" lines).</para>
    /// <para>Hot: a bounded pointer walk over a tree a few dozen nodes wide, allocating nothing, and
    /// <see cref="IsStatic"/> caches the verdict per light identity.</para></summary>
    private static bool AnimationDrives(IntPtr part)
    {
        Gob gob = new Part(part).Gob;
        if (gob.IsNull) return false;
        int n = gob.RunCount;
        if (n <= 0 || n > 64) return false;
        // AnimateHierarchy pairs the animation's ROOT node with the part tree's root and never tests its id,
        // so any root the light hangs under is the right entry -- including the LOD alternates Gob::SetLODModel
        // rotates through gob+0x90.
        IntPtr root = part;
        for (int hop = 0; hop < PairDepth; hop++)
        {
            IntPtr up = new Part(root).Parent.Ptr;
            if (up == IntPtr.Zero) break;
            root = up;
        }
        for (int i = 0; i < n; i++)
        {
            AnimRun run = gob.Run(i);
            if (run.IsNull) continue;
            Animation anim = run.Animation;
            if (anim.IsNull) continue;
            MdlNode node = anim.Node;
            if (!node.IsNull && PairDrives(root, node.Ptr, part, 0)) return true;
        }
        return false;
    }

    /// <summary>Depth and fan-out ceilings on <see cref="PairDrives"/>: the engine's own recursion guard is
    /// 1000 frames deep, which is a runaway detector rather than a shape -- an MDL hierarchy is a handful of
    /// levels. A tree past either ceiling is left un-judged, not guessed at.</summary>
    private const int PairDepth = 64, PairFanout = 4096;

    /// <summary><c>AnimateHierarchy</c>'s pairing, walked to one part: a part's child matches the animation
    /// node's child with the same node number (<see cref="EngineLayout.PartNodeId"/> is copied from
    /// <see cref="EngineLayout.MdlNodeId"/> by <c>CreateInstanceTreeR</c>), scanning the animation's siblings
    /// from <c>i % n</c> and wrapping once, first match wins. Returns whether the node paired with
    /// <paramref name="target"/> drives it -- and a part whose <see cref="EngineLayout.PartAnimateGate"/> is
    /// set is one the engine's walk skips, so nothing drives it.</summary>
    private static bool PairDrives(IntPtr part, IntPtr node, IntPtr target, int depth)
    {
        if (part == target) return new Part(part).AnimateGate == 0 && Drives(new MdlNode(node));
        if (depth >= PairDepth) return false;
        int pc = new Part(part).ChildCount, nc = new MdlNode(node).ChildCount;
        if (pc <= 0 || nc <= 0 || pc > PairFanout || nc > PairFanout) return false;
        IntPtr* kids = (IntPtr*)new Part(part).Children;
        IntPtr* anim = (IntPtr*)new MdlNode(node).Children;
        if (kids == null || anim == null) return false;
        for (int i = 0; i < pc; i++)
        {
            IntPtr child = kids[i];
            if (child == IntPtr.Zero) continue;
            int id = new Part(child).NodeId;
            for (int k = 0; k < nc; k++)
            {
                IntPtr cand = anim[(i + k) % nc];
                if (cand == IntPtr.Zero || new MdlNode(cand).NodeId != id) continue;
                if (PairDrives(child, cand, target, depth + 1)) return true;
                break;   // the engine pairs a child with the FIRST matching sibling and no other
            }
        }
        return false;
    }

    /// <summary>AlloyRenderer.CreaturePart's chain walk on a light's gob: its own gob, or any gob up its
    /// attachment chain, is a creature (a torch in a hand is its own gob hanging off the body's).</summary>
    private static bool CreatureRooted(IntPtr g)
    {
        for (int hop = 0; hop < 8 && g != IntPtr.Zero; hop++)
        {
            if (SceneRay.IsCreature(g)) return true;
            CAurBehavior behavior = new Gob(g).Attach;
            if (behavior.IsNull) return false;
            g = behavior.Target.Ptr;
        }
        return false;
    }

    /// <summary>One master-list light, classified and -- when static -- folded into the snapshot. Called for
    /// every point light the walk admits, BEFORE its fade and cull, with the lightmap on or off (GI keys its
    /// cache on the same fingerprint). Returns the static verdict for the SSBO bit.</summary>
    /// <summary>CX-120: the same bookkeeping for a light that has no <c>PartLight</c> -- a DB light, whose
    /// identity is its own record rather than an engine pointer. The id is only ever a KEY below, so a
    /// synthetic one serves (and unlike the engine's it is not recycled by an allocator) -- but the static
    /// VERDICT cannot be asked of it, since <see cref="IsStatic"/> and <see cref="Settled"/> walk the engine
    /// object behind the pointer. The database already knows the answer (a tile light's <c>LitDynamic</c>, an
    /// object's <c>ObjStatic</c>), so it is handed in.</summary>
    /// <summary>Can a baked page exist in this mode at all? See the <c>Produces</c> test in
    /// <see cref="Record(PartLight,CNWCArea,Vector3,float,Vector3,bool,out int,int)"/>.</summary>
    // CX-120: the OFFER is the edge, not the rect, and that is not a looseness -- it is what breaks a
    // circle. A light only joins the static set once Produces says a page can exist; SetHash is that
    // set's hash; and Enrol -- the only thing that mints a rect -- refuses while SetHash is 0. Keyed on
    // the rect, the three wait on each other for ever and the bake never starts (measured: charts built,
    // no rect, no page). The offer is the first moment a page is possible, and the loading-screen hold
    // (#675) is what covers the gap between it and the first landed page.
    internal static bool Produces => _rectN > 0 || MeshChart.Offered > 0;

    internal static bool Record(IntPtr id, CNWCArea area, Vector3 pos, float adjR, Vector3 color, bool ambient,
                               bool isStatic, out int setIndex)
        => Record(new PartLight(id), area, pos, adjR, color, ambient, out setIndex, isStatic ? 1 : 0);

    /// <param name="dbStatic">-1 asks the engine object behind <paramref name="light"/> (engine mode); 0 or 1
    /// is a caller's own verdict and nothing dereferences the pointer (DB mode).</param>
    internal static bool Record(PartLight light, CNWCArea area, Vector3 pos, float adjR, Vector3 color, bool ambient,
                               out int setIndex, int dbStatic = -1)
    {
        setIndex = -1;
        ulong id = Mix(Mix(Mix(Mix(FnvOffset, Q(pos.X, 0.05f)), Q(pos.Y, 0.05f)), Q(pos.Z, 0.05f)), Q(adjR, 0.05f));
        if (ambient) { CensusDynamic++; return false; }
        // CX-120: a STATIC light is lit from a baked page and from nowhere else -- the live loop passes over
        // it on purpose. Calling one static where no page can ever exist is therefore the same as switching
        // it off, and in DB mode no page can: nothing enrols, so no rect is ever minted (see
        // LightmapBake.Hold's NoRectSource). Every light stays dynamic until the arena ranges are charted,
        // which is what keeps the tiles lit meanwhile. Self-retiring: the first chart offered ends it.
        if (!Produces) { CensusDynamic++; return false; }
        // in the closed set = static, with the colour it was baked under (it is in every touching key);
        // anything else after the close is dynamic, whatever it is: a light that appears after load moved
        if (_seen.ContainsKey(id)) { CensusStatic++; setIndex = SetIndexOf(light.Ptr, pos, adjR); return true; }
        if (_closed)
        {
            // the same object a hair off its close-time place (a bobbing lantern, a reach re-adjusted by the
            // engine) is still the light the bake holds; a far move is a reused address or a scripted move
            if (_seenPtr.TryGetValue(light.Ptr, out int at) && Near(_stPosR[at], pos, adjR)) { CensusStatic++; setIndex = SetIndexOf(light.Ptr, pos, adjR); return true; }
            CensusDynamic++;
            // a light the open walk already classified dynamic (animated, creature-borne) is not news
            if (dbStatic < 0 && _lateLeft > 0 && !Classified(light.Ptr)) LateLight(light, pos, adjR);
            return false;
        }
        bool placeable = false;
        if (dbStatic >= 0)
        {
            if (dbStatic == 0) { CensusDynamic++; return false; }
        }
        else
        {
            bool st = IsStatic(light, area, out bool animated, out placeable, out bool byRun);
            if (animated) CensusAnimated++;
            if (byRun) CensusAnimRun++;
            if (!st) { CensusDynamic++; return false; }
            // CX-56: a reach the engine is still ramping is not the light to bake, and the ramp IS an animation
            // blend -- so ask the blend, not a walk counter (see Settled)
            if (!Settled(new Part(light.Ptr).Gob.Ptr)) { CensusUnsettled++; CensusDynamic++; return false; }
        }
        CensusStatic++;
        if (placeable) CensusPlaceable++; else CensusTile++;
        if (_stN == _stPosR.Length)
        {
            int n = _stN * 2;
            Array.Resize(ref _stPosR, n); Array.Resize(ref _stCol, n);
        }
        _seen[id] = _stN; _seenPtr[light.Ptr] = _stN;
        _stPosR[_stN] = new Vector4(pos, adjR); _stCol[_stN++] = color;
        return true;
    }

    /// <summary>CX-56: has every animation this light's gob is playing finished blending in, so the reach,
    /// colour and multiplier the part carries are the animation's own values and not a point on the ramp?
    /// <para>The ramp is the blend, proved end to end: <c>Gob::Animate</c> (Linux @0x49d8c0, Windows
    /// @0x14004dbd0) calls <c>AnimateHierarchy(root, anim->node, prev, time, run->blend)</c> with
    /// <see cref="EngineLayout.AnimRunBlend"/> as the weight; <c>AnimateHierarchy</c> @0x499990 hands it down
    /// the tree in xmm2 (the vtbl call at 0x4999d1 leaves xmm0-2 live) to
    /// <c>MdlNode::InternalControl</c> @0x4990c0, which forwards it to <c>NewController::Control</c> @0x498f20:
    /// <c>part[target] = w*value + (1-w)*part[target]</c>. At <c>w == 1</c> that IS the animation's value --
    /// the candles' 15 rather than a point on their 0.42 -> 15 ramp. <c>Gob::Animate</c> also drops every
    /// other run the moment one reaches 1, so "no run below 1" is exactly "the cross-fade is over".</para>
    /// <para>A gob-less light (a static tile's node, a Static placeable's own model light) animates nothing
    /// and is settled by construction. Hot: a bounded pointer walk, no allocation.</para>
    /// <para>Settled is NOT final: a multi-key track keeps moving at <c>w == 1</c> forever, which is what
    /// <see cref="AnimationDrives"/> refuses outright.</para></summary>
    private static bool Settled(IntPtr g)
    {
        if (g == IntPtr.Zero) return true;
        Gob gob = new(g);
        int n = gob.RunCount;
        if (n <= 0 || n > 64) return true;   // no list, or a count this side does not believe
        for (int i = 0; i < n; i++)
        {
            AnimRun run = gob.Run(i);
            if (run.IsNull) continue;
            uint flags = run.Flags;
            if ((flags & EngineLayout.AnimRunQueued) != 0) continue;   // not animated at all this frame
            if ((flags & EngineLayout.AnimRunFadingOut) != 0 || run.Blend < 1f) return false;
        }
        return true;
    }

    /// <summary>CX-56: the animation pairing, headless. A fake two-level part tree and an animation whose node
    /// tree names the same node numbers in a DIFFERENT sibling order (so the engine's <c>i % n</c> scan has to
    /// wrap) are laid out at the resolved <see cref="EngineLayout"/> rows, so the same check runs on either
    /// platform's offsets. Live-unverified beyond this: what the walk finds in a real torch is content.</summary>
    internal static bool LightsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.LightsSelfCheck");
        const int Blob = 0x200, Slots = 0x20;   // not-an-offset: sizes of the self-check's fake objects
        IntPtr root = (IntPtr)NativeMemory.AllocZeroed(Blob), mid = (IntPtr)NativeMemory.AllocZeroed(Blob), leaf = (IntPtr)NativeMemory.AllocZeroed(Blob);
        IntPtr model = (IntPtr)NativeMemory.AllocZeroed(Blob);
        IntPtr aRoot = (IntPtr)NativeMemory.AllocZeroed(Blob), aMid = (IntPtr)NativeMemory.AllocZeroed(Blob);
        IntPtr aLeaf = (IntPtr)NativeMemory.AllocZeroed(Blob), aOther = (IntPtr)NativeMemory.AllocZeroed(Blob);
        IntPtr gob = (IntPtr)NativeMemory.AllocZeroed(Blob * 8), run = (IntPtr)NativeMemory.AllocZeroed(Blob), anim = (IntPtr)NativeMemory.AllocZeroed(Blob);
        IntPtr* rootKids = (IntPtr*)NativeMemory.AllocZeroed(Slots), midKids = (IntPtr*)NativeMemory.AllocZeroed(Slots);
        IntPtr* aRootKids = (IntPtr*)NativeMemory.AllocZeroed(Slots), aMidKids = (IntPtr*)NativeMemory.AllocZeroed(Slots);
        IntPtr* runs = (IntPtr*)NativeMemory.AllocZeroed(Slots);
        IntPtr ctrl = (IntPtr)NativeMemory.AllocZeroed((nuint)EngineLayout.NewControllerStride * 2);
        IntPtr mCtrl = (IntPtr)NativeMemory.AllocZeroed((nuint)EngineLayout.NewControllerStride);
        try
        {
            const int MidId = 7, LeafId = 12, OtherId = 99;   // not-an-offset: the fake tree's MDL node numbers
            rootKids[0] = mid; midKids[0] = leaf;
            new Part(root).Children = (IntPtr)rootKids; new Part(root).ChildCount = 1;
            new Part(mid).Children = (IntPtr)midKids; new Part(mid).ChildCount = 1;
            new Part(mid).NodeId = MidId; new Part(mid).Parent = new Part(root);
            new Part(leaf).NodeId = LeafId; new Part(leaf).Parent = new Part(mid);
            new Part(leaf).Gob = new Gob(gob); new Part(leaf).Node = new MdlNode(model);
            // the animation names the same numbers in the other order: pairing that indexed by position fails here
            aRootKids[0] = aOther; aRootKids[1] = aMid; aMidKids[0] = aLeaf;
            new MdlNode(aRoot).Children = (IntPtr)aRootKids; new MdlNode(aRoot).ChildCount = 2;
            new MdlNode(aOther).NodeId = OtherId;
            new MdlNode(aMid).NodeId = MidId; new MdlNode(aMid).Children = (IntPtr)aMidKids; new MdlNode(aMid).ChildCount = 1;
            new MdlNode(aLeaf).NodeId = LeafId;
            new MdlNode(aLeaf).Controllers = ctrl; new MdlNode(aLeaf).ControllerCount = 1;
            NewController c = new(ctrl);
            c.Id = EngineLayout.LightRange; c.KeyCount = 3;
            runs[0] = run;
            new Gob(gob).AnimRuns = (IntPtr)runs; new Gob(gob).AnimRunCount = 1;
            new AnimRun(run).Animation = new Animation(anim);
            new Animation(anim).Node = new MdlNode(aRoot);

            check.Expect(AnimationDrives(leaf), "the running animation's multi-key radius track was not paired to the light");
            c.KeyCount = 1;
            check.Expect(!AnimationDrives(leaf), "a single-key track never moves after the blend: it is not animation");
            c.KeyCount = 3;
            c.Id = EngineLayout.LightRangeAlt;
            check.Expect(!AnimationDrives(leaf), "a shadowradius track drove the lit reach");
            c.Id = EngineLayout.LightIntensity;
            check.Expect(AnimationDrives(leaf), "a multiplier track is a moving reach too");
            new Part(leaf).AnimateGate = 1;
            check.Expect(!AnimationDrives(leaf), "a part AnimateHierarchy skips was still called animation-driven");
            new Part(leaf).AnimateGate = 0;
            new MdlNode(aLeaf).NodeId = LeafId + 1;
            check.Expect(!AnimationDrives(leaf), "an animation node the pairing cannot reach still drove the light");
            new MdlNode(aLeaf).NodeId = LeafId;
            new Gob(gob).AnimRunCount = 0;
            check.Expect(!AnimationDrives(leaf), "a gob playing nothing has nothing driving its lights");
            new Gob(gob).AnimRunCount = 1;
            new Part(leaf).Gob = new Gob(IntPtr.Zero);
            check.Expect(!AnimationDrives(leaf), "a gob-less light (a tile's own node) animates nothing");
            new Part(leaf).Gob = new Gob(gob);
            // ...and the model node's own track, the branch that was already there
            check.Expect(!Drives(new MdlNode(model)), "a light node with no controllers at all read as animated");
            new MdlNode(model).Controllers = mCtrl; new MdlNode(model).ControllerCount = 1;
            NewController m = new(mCtrl);
            m.Id = EngineLayout.LightColour; m.KeyCount = 2;
            check.Expect(Drives(new MdlNode(model)), "a multi-key colour track on the MODEL node is animation as well");
        }
        finally
        {
            IntPtr[] blobs = { root, mid, leaf, model, aRoot, aMid, aLeaf, aOther, gob, run, anim,
                               (IntPtr)rootKids, (IntPtr)midKids, (IntPtr)aRootKids, (IntPtr)aMidKids, (IntPtr)runs, ctrl, mCtrl };
            foreach (IntPtr p in blobs) NativeMemory.Free((void*)p);
        }
        if (check.Ok) log.Info("LightmapBake.LightsSelfCheck: passed (the animation pairing is AnimateHierarchy's own, node number for node number)");
        return check.Ok;
    }

    /// <summary>Within 1 u and 10% of reach of the set entry: the drift a static light is allowed after the close.</summary>
    private static bool Near(Vector4 was, Vector3 pos, float adjR)
        => (pos - new Vector3(was.X, was.Y, was.Z)).LengthSquared() < 1f && MathF.Abs(adjR - was.W) < 0.1f * was.W;

    /// <summary>One line per light the closed set refuses: the same object drifted (how far, by how much
    /// reach) or a new object (its node, gob and creature chain). Cold: allocates the strings.</summary>
    private static void LateLight(PartLight light, Vector3 pos, float adjR)
    {
        if (!_lateLogged.Add(light.Ptr)) return;
        _lateLeft--;
        Gob gob = new Part(light.Ptr).Gob;
        MdlNode node = new Part(light.Ptr).Node;
        string name = node.IsNull ? "?" : node.NameText ?? "?";
        if (_seenPtr.TryGetValue(light.Ptr, out int i))
        {
            Vector4 was = _stPosR[i];
            _log?.Info($"LightmapBake: late light '{name}' {light.Ptr:x} is set #{i} DRIFTED -- moved {(pos - new Vector3(was.X, was.Y, was.Z)).Length():F2}u, reach {was.W:F2} -> {adjR:F2}");
        }
        else
            _log?.Info($"LightmapBake: late light '{name}' {light.Ptr:x} is NEW after the close -- gob {gob.Ptr:x} tile {(gob.IsNull || !gob.TileData.IsNull)} creature {CreatureRooted(gob.Ptr)} at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) r {adjR:F2} flags {light.FlagA}/{light.FlagB} inten {light.Intensity:F2}");
    }

    /// <summary>The walk's tail: close the set on the first settled walk after <c>LoadArea</c>'s non-zero
    /// return, and every instance re-keys against it once. Closed, nothing here runs.</summary>
    internal static void EndWalk()
    {
        if (_closed) return;
        if (_clsArea == IntPtr.Zero) return;
        // the area's own message has not finished spawning it (or it failed): there is nothing to close over
        if (!_loadArmed) return;
        bool settled = CensusUnsettled == 0;
        if (!settled && _walks < BlendCeiling) return;
        // CX-120: and in DB mode, not before the charter has been handed something. A light's static verdict
        // asks Produces, Produces asks whether a chart has been OFFERED, and closing before the first offer
        // bins every light dynamic for the session (measured: "set closed at walk 1 ... dynamic=22,
        // static 0"). The offers come from the frame fold (MeshChart.DripDb), which runs while the area
        // loads, so this wait is short. Only while the charter can answer at all -- with no arena VAO and no
        // master knob there is nothing to wait for, which is also the off-device fixture's lightless area --
        // and under the same walk ceiling the settle wait uses.
        if (MeshChart.Armed && MeshChart.Offered == 0 && _walks < BlendCeiling) return;
        _closed = true;
        ulong acc = 0;
        for (int i = 0; i < _stN; i++) acc ^= SetTerm(_stPosR[i], _stCol[i]);
        ulong hash = _stN == 0 ? 0 : Mix(FnvOffset, (long)acc) | 1UL;   // 0 = nothing to bake: Enrol and Sweep stay refused
        // the runtime check the research asks for: the registry's count against the placeables the walk saw
        string how = $"walk {_walks} past LoadArea's return, {_stN} lights (tile {CensusTile}, placeable {CensusPlaceable} of {_regGobs.Count} the registry named), "
                   + $"{CensusAnimated} animated ({CensusAnimRun} of them by an animation their gob is running), "
                   + $"{CensusDynamic} dynamic{(_stN == 0 ? " -- no static light, nothing to bake" : "")}";
        if (settled) _log?.Info($"LightmapBake: static set closed on final values -- {how}");
        else _log?.Always($"LightmapBake: static set closed at the {BlendCeiling}-walk CEILING with {CensusUnsettled} lights still blending an animation in -- "
                          + $"the set is closed WITHOUT them and they light the area dynamically; {how}");
        _closeStatic = CensusStatic; _closeTile = CensusTile; _closePlaceable = CensusPlaceable; _closeAnimated = CensusAnimated; _closeAnimRun = CensusAnimRun; _closeDynamic = CensusDynamic;
        Arrived(WhyClose); StampClose();
        // CX-56: under an adopted area plan the close is a CHECK, not the edge that builds the set -- the tail
        // below would repack the whole area out from under the adoption on every first walk of an area.
        if (AreaConfirm(hash)) return;
        if (hash == SetHash && !_areaChanged) return;
        if (_setPosR.Length < _stN) { _setPosR = new Vector4[_stN]; _setCol = new Vector3[_stN]; }
        Array.Copy(_stPosR, _setPosR, _stN); Array.Copy(_stCol, _setCol, _stN);
        _setN = _stN;
        SetHash = hash;
        AbortBake();
        if (_areaChanged) { _areaChanged = false; _areaHalve = 0; _placedArea = 0; Repack(); }
        else if (hash == 0) Repack();
        else RekeyAll();
    }

    /// <summary>One light's term of <see cref="SetHash"/>: position and reach in 4 u bins, colour by hue in 0,1 steps.</summary>
    private static ulong SetTerm(Vector4 pr, Vector3 c)
    {
        float m = MathF.Max(MathF.Max(c.X, c.Y), MathF.Max(c.Z, 1e-3f));
        ulong h = FnvOffset;
        h = Mix(h, Q(pr.X, 4f)); h = Mix(h, Q(pr.Y, 4f)); h = Mix(h, Q(pr.Z, 4f)); h = Mix(h, Q(pr.W, 4f));
        h = Mix(h, Q(c.X / m, 0.1f)); h = Mix(h, Q(c.Y / m, 0.1f)); h = Mix(h, Q(c.Z / m, 0.1f));
        return h;
    }

    /// <summary>Why a saved set and the live one disagree, light by light: pairs within <see cref="Near"/> whose
    /// terms still differ (a bin edge or a colour), then the lights either side has alone -- the live ones named
    /// by node, gob and origin. Cold: once per refusal or drift.</summary>
    private static string SetDiff(Vector4[] planPosR, Vector3[] planCol, int planN)
    {
        var liveUsed = new bool[_stN];
        var byIdx = new IntPtr[_stN];
        foreach (var kv in _seenPtr) if (kv.Value < _stN) byIdx[kv.Value] = kv.Key;
        var planOnly = new List<int>();
        int same = 0, binned = 0;
        var sb = new System.Text.StringBuilder();
        for (int p = 0; p < planN; p++)
        {
            int hit = -1;
            for (int l = 0; l < _stN && hit < 0; l++)
                if (!liveUsed[l] && Near(planPosR[p], new Vector3(_stPosR[l].X, _stPosR[l].Y, _stPosR[l].Z), _stPosR[l].W)) hit = l;
            if (hit < 0) { planOnly.Add(p); continue; }
            liveUsed[hit] = true;
            if (SetTerm(planPosR[p], planCol[p]) == SetTerm(_stPosR[hit], _stCol[hit])) { same++; continue; }
            if (binned++ < 6) sb.Append($"; paired but binned apart: plan {Fmt(planPosR[p], planCol[p])} vs live {Fmt(_stPosR[hit], _stCol[hit])} {Who(byIdx[hit])}");
        }
        for (int i = 0; i < planOnly.Count && i < 6; i++) sb.Append($"; plan only: {Fmt(planPosR[planOnly[i]], planCol[planOnly[i]])}");
        int liveOnly = 0;
        for (int l = 0; l < _stN; l++)
            if (!liveUsed[l] && liveOnly++ < 6) sb.Append($"; live only: {Fmt(_stPosR[l], _stCol[l])} {Who(byIdx[l])}");
        return $"{same} lights agree, {binned} pair up but hash apart, {planOnly.Count} plan-only, {liveOnly} live-only" + sb;

        static string Fmt(Vector4 pr, Vector3 c) => $"({pr.X:F2},{pr.Y:F2},{pr.Z:F2}) r {pr.W:F2} col ({c.X:F2},{c.Y:F2},{c.Z:F2})";
        static string Who(IntPtr light)
        {
            if (light == IntPtr.Zero) return "'?'";
            if (Renderer.On) return $"db light {light:x}";   // a database light id, not a Part: nothing to dereference
            Gob gob = new Part(light).Gob;
            MdlNode node = new Part(light).Node;
            string origin = gob.IsNull ? "gob-less" : !gob.TileData.IsNull ? "tile" : _regGobs.Contains(gob.Ptr) ? "registry placeable" : "model light";
            return $"'{(node.IsNull ? "?" : node.NameText ?? "?")}' gob {gob.Ptr:x} {origin}";
        }
    }

    /// <summary>The per-instance half of the bake key (#666): an order-independent xor over the static lights
    /// whose sphere touches the instance's bounds -- position and range at 5 cm, full colour at 1/255.
    /// Zero when nothing touches (a bake of black, still a bake).</summary>
    private static ulong TouchingLights(Vector3 c, float r)
    {
        ulong acc = 0;
        for (int i = 0; i < _setN; i++)
        {
            if (!Touches(i, c, r)) continue;
            Vector4 pr = _setPosR[i];
            Vector3 col = _setCol[i];
            ulong h = FnvOffset;
            h = Mix(h, Q(pr.X, 0.05f)); h = Mix(h, Q(pr.Y, 0.05f)); h = Mix(h, Q(pr.Z, 0.05f)); h = Mix(h, Q(pr.W, 0.05f));
            h = Mix(h, Q(col.X, 1f / 255f)); h = Mix(h, Q(col.Y, 1f / 255f)); h = Mix(h, Q(col.Z, 1f / 255f));
            acc ^= h;
        }
        return acc;
    }

    /// <summary>Does static light <paramref name="l"/> of the settled set reach the sphere?</summary>
    private static bool Touches(int l, Vector3 c, float r)
    {
        Vector4 pr = _setPosR[l];
        float dx = pr.X - c.X, dy = pr.Y - c.Y, dz = pr.Z - c.Z, rr = pr.W + r;
        return dx * dx + dy * dy + dz * dz <= rr * rr;
    }

    private static ComputeStaticLightingFn _origCsl;

    /// <summary>The edges the static set is built on: <c>LoadArea</c> (CX-56, the one that CLOSES the set),
    /// <c>SetTile</c> (a tile re-spawns, the set is walked again), the AreaLoaded send (the hold) and the
    /// engine's own "the static lighting changed" signal (#664 §A.6, a script toggling a placeable light --
    /// voids the classification so the next walk re-reads every light). All cold hooks; none allocates.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _loadAreaSlot = hooks.ChainAreaLoadArea(&OnLoadArea, EdgePriority);
        if (_loadAreaSlot == null)
        {
            // CX-45: our own mechanism is missing, so the pass sits out -- it must not fall back to a walk
            // count nobody would see, and a set that never closes is a hold that never releases.
            log.Always("LightmapBake: REFUSED -- CNWCArea::LoadArea did not resolve, so nothing can close the static set; the bake and its loading hold are off for this session and every surface keeps the real-time loop");
            _noLoadEdge = true;
            return;
        }
        _origCsl = hooks.InstallComputeStaticLighting(&OnComputeStaticLighting);
        _loadedSlot = hooks.ChainSendAreaLoaded(&OnSendAreaLoaded, EdgePriority);
        _setTileSlot = hooks.ChainAreaSetTile(&OnSetTile, EdgePriority);
        if (_loadedSlot == null) log.Always("LightmapBake: the AreaLoaded send did not resolve -- the set still closes at LoadArea, but there is no loading hold and the player is placed over an unbaked area");
        else InstallSurface(log);
    }

    /// <summary>Observers, bar the hold (<c>LightmapBake.Hold.cs</c>): they read nothing and call through, so any priority is fine.
    /// On <c>LoadArea</c> it puts this INSIDE <c>TexStream</c>'s own chain link, which times the whole call.</summary>
    private const int EdgePriority = 0;
    private static SendAreaLoadedFn* _loadedSlot;
    private static AreaSetTileFn* _setTileSlot;
    private static AreaLoadAreaFn* _loadAreaSlot;

    /// <summary>The area's own message, start to finish. Entry: the area is changing under the walk, and a
    /// walk that runs inside this call (its <c>UpdateScreen</c> ticks) would see a half-spawned one -- so the
    /// close is disarmed and the edge raised. Return: non-zero arms the close, zero refuses it (the error
    /// paths destroy the tile array) and is said out loud. Cold; the strings only run on a refusal.</summary>
    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr area, IntPtr camera, IntPtr loadscreen)
    {
        _loadArmed = false; _loadRefused = false; _edgePending = true;
        Interlocked.Exchange(ref _areaKeyPending, 0);   // the previous area's key must not outlive its load
        int ok = (*_loadAreaSlot)(area, camera, loadscreen);
        _loadArmed = ok != 0;
        _loadRefused = ok == 0;
        if (ok == 0)
        {
            LoadRefusals++;
            _log?.Always($"LightmapBake: CNWCArea::LoadArea returned 0 for area {area:x} (refusal {LoadRefusals}) -- the tile array it was building is gone, so no static set is closed and this area is lit entirely in real time");
        }
        return ok;
    }

    [UnmanagedCallersOnly]
    private static void OnSetTile(IntPtr area, IntPtr tile, int id, int orientation, int height)
    {
        if (_setTileSlot != null) (*_setTileSlot)(area, tile, id, orientation, height);
        OnTileChanged();
    }

    [UnmanagedCallersOnly]
    private static void OnComputeStaticLighting(IntPtr scene)
    {
        if (_origCsl != null) _origCsl(scene);
        VoidClasses();
        _cslEdges++;
    }
}
