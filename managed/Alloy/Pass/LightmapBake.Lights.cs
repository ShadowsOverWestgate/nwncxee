using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>The static-light side of the bake (#674): the three-branch rule (#654/#664/#659) classified once
/// per <c>PartLight</c> identity, the per-frame static snapshot the bake reads, the static-set fingerprint
/// that is the one re-key edge (#668), and the <c>CAurScene::ComputeStaticLighting</c> hook that voids the
/// classification. Every reader here runs on the light walk (the shadow bracket) and allocates nothing.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Classification table: open addressing on the PartLight pointer, MeshLod's flicker-table shape.
    /// 0 = unknown, 1 = static, 2 = dynamic. Voided whole at the world edge, the hook edge and a tile change.</summary>
    private const int ClsSlots = 2048;
    private const int ClsProbe = 8;
    private static readonly IntPtr[] _clsId = new IntPtr[ClsSlots];
    private static readonly byte[] _cls = new byte[ClsSlots];
    /// <summary>The static set is ONE walk: the first after the area-loaded edge (every tile spawned, both area
    /// lists read -- docs/research/area-light-list.md) that is <see cref="SettleWalks"/> walks past the edge
    /// and <see cref="QuietWalks"/> past the last light object to appear (the server's objects keep arriving
    /// for seconds after the edge; run 27: two candles 4 s in). In it a light is static only if its own place,
    /// reach and colour held for <see cref="SettleWalks"/> walks (run 26: the engine re-set the candles' reach
    /// 0.42 -> 15 and the torches' 15.89 -> 12 within 0.2 s of the edge; run 27: a whole-list hash never held
    /// in the harbour and the close fell back at 600). Closed, the set is final for the area: a light that
    /// appears later is dynamic, whatever its shape, and a server tile change reopens it
    /// (<see cref="OnTileChanged"/>).</summary>
    private static bool _closed, _edgeSeen, _reopen;
    private static int _walks, _edgeWalk, _lastNew;
    private const int FallbackWalks = 600, SettleWalks = 30, QuietWalks = 60;
    /// <summary>Per light object while open: its last reading and how many walks it has held.</summary>
    private static readonly Dictionary<IntPtr, (ulong key, int held)> _stab = new(512);
    private static readonly HashSet<IntPtr> _lateLogged = new();
    /// <summary>The area under the walk changed since the last close: the pages are the previous area's and
    /// rewind at this close (an in-module area change is no world edge -- run 30 kept 12 pages of the last
    /// area's rects resident under the next one and halved its density for them).</summary>
    private static bool _areaChanged;
    internal static int CensusUnstable;
    /// <summary>The first refused lights of an area, by node name, once: what the refused set IS (run 8: xtul's
    /// module marks every one of them Static, so the three-branch rule's premise is wrong somewhere).</summary>
    private static int _whyLeft;
    private static int _clsFull;
    private static int _regCountSeen = -1;
    /// <summary>Every gob the area's placeable-light registry has ever named this area. The registry is filled
    /// once from the area message and EMPTIES as each placeable adopts its light gob (CNWCPlaceable::LoadLight
    /// @0x8eb140 re-parents it and removes the record with bFree 0: docs/research/area-light-list.md), so
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
    /// <summary>The walk's census: static / animated (>1-key radius or colour track) / dynamic, per frame.</summary>
    internal static int CensusStatic, CensusAnimated, CensusDynamic, CensusTile, CensusPlaceable;
    /// <summary>The census of the closing walk: after the close the walk counts only static / dynamic.</summary>
    private static int _closeStatic, _closeTile, _closePlaceable, _closeAnimated, _closeDynamic;

    /// <summary>The closed static set: copied off the snapshot on the edge, read by the bake and the keys.</summary>
    private static Vector4[] _setPosR = new Vector4[256];
    private static Vector3[] _setCol = new Vector3[256];
    private static int _setN;
    internal static ulong SetHash { get; private set; }
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

    /// <summary>The AreaLoaded send (the loading screen's end): the next walk is a NEW area, whatever the
    /// area pointer says -- run 31's server transition freed one CNWCArea and malloc handed the next one the
    /// same address, so the pointer compare below saw nothing, the plaza was baked against the harbour's set
    /// and the harbour's pages and charts stayed resident under it.</summary>
    internal static void OnAreaLoaded() { _edgeSeen = true; _edgePending = true; }
    private static bool _edgePending;
    /// <summary><c>CNWCArea::SetTile</c>: one tile's model and lights re-spawn -- the set is walked again.</summary>
    internal static void OnTileChanged() => _reopen = true;

    /// <summary>Restart the per-frame snapshot. Called from the walk's top, beside <c>BeginAreaLights</c>.</summary>
    internal static void BeginWalk(IntPtr area)
    {
        CensusStatic = CensusAnimated = CensusDynamic = CensusTile = CensusPlaceable = CensusSkipped = CensusUnstable = 0;
        if (area != _clsArea || _edgePending) { _clsArea = area; _edgePending = false; _areaChanged = true; VoidClasses(); _regGobs.Clear(); _regCountSeen = -1; _whyLeft = 8; _lateLeft = 24; _lateLogged.Clear(); Reopen(); }
        if (_reopen) { _reopen = false; VoidClasses(); Reopen(); _edgeSeen = true; }
        if (_closed) return;
        // open, every walk is the whole set again: the lights the engine re-sets after the edge must not leave
        // their first reading in the set beside their real one
        _seen.Clear(); _seenPtr.Clear(); _stN = 0;
        _walks++;
        if (_edgeSeen && _edgeWalk < 0) _edgeWalk = _walks;
        int reg = area == IntPtr.Zero ? 0 : *(int*)(area + AreaPlaceableLightCount);
        if (reg != _regCountSeen)
        {
            _regCountSeen = reg;
            IntPtr regData = area == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(area + AreaPlaceableLights);
            if (regData != IntPtr.Zero && reg > 0 && reg < 65536)
                for (int i = 0; i < reg; i++)
                {
                    IntPtr rec = *((IntPtr*)regData + i);
                    if (rec != IntPtr.Zero) _regGobs.Add(*(IntPtr*)(rec + PlaceableLightGob));
                }
        }
    }

    private static void VoidClasses() { Array.Clear(_clsId); Array.Clear(_cls); }
    private static void Reopen() { _closed = false; _walks = _lastNew = 0; _edgeWalk = -1; _stab.Clear(); }

    /// <summary>The three-branch rule for one light: <c>gob == 0</c> (a static tile's node, or a Static
    /// placeable's own model light, both spawned gob-less) || the gob carries tile data (an animated tile
    /// model) || the gob is in the area's placeable-light registry (a <c>placeables.2da</c> light); and no
    /// radius/colour controller with more than one key. Ambient-class lights are never static (the caller
    /// excludes them). Cached per identity; a full table classifies without caching.</summary>
    internal static bool IsStatic(byte* p, IntPtr area, out bool animated, out bool placeable)
    {
        animated = placeable = false;
        int h = (int)((ulong)p >> 4) * -1640531527 & (ClsSlots - 1);
        int free = -1;
        for (int k = 0; k < ClsProbe; k++)
        {
            int j = (h + k) & (ClsSlots - 1);
            if (_clsId[j] == (IntPtr)p)
            {
                byte c = _cls[j];
                animated = (c & 4) != 0; placeable = (c & 8) != 0;
                return (c & 3) == 1;
            }
            if (free < 0 && _clsId[j] == IntPtr.Zero) free = j;
        }
        bool st = Classify(p, area, out animated, out placeable);
        if (!st && _whyLeft > 0 && _log != null)
        {
            _whyLeft--;
            IntPtr gob = *(IntPtr*)(p + PartGob), node = *(IntPtr*)(p + PartNodeLink);
            string name = node != IntPtr.Zero ? Marshal.PtrToStringAnsi(node + MdlNodeName) ?? "?" : "?";
            _log.Info($"LightmapBake: refused light -- node '{name}' gob {gob:x} tileData {(gob != IntPtr.Zero ? *(IntPtr*)(gob + GobTileData) : 0):x} animated {animated}");
        }
        if (free >= 0)
        {
            _clsId[free] = (IntPtr)p;
            _cls[free] = (byte)((st ? 1 : 2) | (animated ? 4 : 0) | (placeable ? 8 : 0));
        }
        else _clsFull++;
        return st;
    }

    private static bool Classify(byte* p, IntPtr area, out bool animated, out bool placeable)
    {
        animated = placeable = false;
        IntPtr gob = *(IntPtr*)(p + PartGob);
        bool tile = gob == IntPtr.Zero || *(IntPtr*)(gob + GobTileData) != IntPtr.Zero;
        if (!tile && gob != IntPtr.Zero) placeable = _regGobs.Contains(gob);
        IntPtr node = *(IntPtr*)(p + PartNodeLink);
        if (node != IntPtr.Zero)
        {
            int cnt = *(int*)(node + MdlNodeControllerCount);
            byte* recs = *(byte**)(node + MdlNodeControllers);
            if (recs != null && cnt > 0 && cnt < 256)
                for (int i = 0; i < cnt && !animated; i++)
                {
                    int id = *(int*)(recs + i * NewControllerStride);
                    short keys = *(short*)(recs + i * NewControllerStride + NewControllerKeyCount);
                    if ((id == LightRange || id == LightColour) && keys > 1) animated = true;
                }
        }
        // present at the closed edge and not animated = the area's light, whatever spawned it: the candles and
        // model lights on placeables the registry never named (run 24: 39 static of 97, 58 live on every lit
        // fragment, cutout 5 ms) -- only a light carried by a creature is a mover by construction
        return !animated && (tile || placeable || !CreatureRooted(gob));
    }

    /// <summary>AlloyRenderer.CreaturePart's chain walk on a light's gob: its own gob, or any gob up its
    /// attachment chain, is a creature (a torch in a hand is its own gob hanging off the body's).</summary>
    private static bool CreatureRooted(IntPtr g)
    {
        for (int hop = 0; hop < 8 && g != IntPtr.Zero; hop++)
        {
            if (SceneRay.IsCreature(g)) return true;
            IntPtr behavior = *(IntPtr*)(g + GobAttach);
            if (behavior == IntPtr.Zero) return false;
            g = *(IntPtr*)(behavior + BehaviorTarget);
        }
        return false;
    }

    /// <summary>One master-list light, classified and -- when static -- folded into the snapshot. Called for
    /// every point light the walk admits, BEFORE its fade and cull, with the lightmap on or off (GI keys its
    /// cache on the same fingerprint). Returns the static verdict for the SSBO bit.</summary>
    internal static bool Record(byte* p, IntPtr area, Vector3 pos, float adjR, Vector3 color, bool ambient)
    {
        ulong id = Mix(Mix(Mix(Mix(FnvOffset, Q(pos.X, 0.05f)), Q(pos.Y, 0.05f)), Q(pos.Z, 0.05f)), Q(adjR, 0.05f));
        if (ambient) { CensusDynamic++; return false; }
        // in the closed set = static, with the colour it was baked under (it is in every touching key);
        // anything else after the close is dynamic, whatever it is: a light that appears after load moved
        if (_seen.ContainsKey(id)) { CensusStatic++; return true; }
        if (_closed)
        {
            // the same object a hair off its close-time place (a bobbing lantern, a reach re-adjusted by the
            // engine) is still the light the bake holds; a far move is a reused address or a scripted move
            if (_seenPtr.TryGetValue((IntPtr)p, out int at) && Near(_stPosR[at], pos, adjR)) { CensusStatic++; return true; }
            CensusDynamic++;
            if (_lateLeft > 0) LateLight(p, pos, adjR);
            return false;
        }
        bool st = IsStatic(p, area, out bool animated, out bool placeable);
        if (animated) CensusAnimated++;
        if (!st) { CensusDynamic++; return false; }
        // this light's own reading, walk over walk: it joins the set once it has held SettleWalks (a reach
        // the engine is still ramping, a colour a script is still setting, is not the light to bake)
        ulong key = Mix(Mix(Mix(id, Q(color.X, 0.01f)), Q(color.Y, 0.01f)), Q(color.Z, 0.01f));
        ref var stab = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_stab, (IntPtr)p, out bool known);
        if (!known) _lastNew = _walks;
        if (stab.key == key) stab.held++; else stab = (key, 1);
        if (stab.held < SettleWalks) { CensusUnstable++; CensusDynamic++; return false; }
        CensusStatic++;
        if (placeable) CensusPlaceable++; else CensusTile++;
        if (_stN == _stPosR.Length)
        {
            int n = _stN * 2;
            Array.Resize(ref _stPosR, n); Array.Resize(ref _stCol, n);
        }
        _seen[id] = _stN; _seenPtr[(IntPtr)p] = _stN;
        _stPosR[_stN] = new Vector4(pos, adjR); _stCol[_stN++] = color;
        return true;
    }

    /// <summary>Within 1 u and 10% of reach of the set entry: the drift a static light is allowed after the close.</summary>
    private static bool Near(Vector4 was, Vector3 pos, float adjR)
        => (pos - new Vector3(was.X, was.Y, was.Z)).LengthSquared() < 1f && MathF.Abs(adjR - was.W) < 0.1f * was.W;

    /// <summary>One line per light the closed set refuses: the same object drifted (how far, by how much
    /// reach) or a new object (its node, gob and creature chain). Cold: allocates the strings.</summary>
    private static void LateLight(byte* p, Vector3 pos, float adjR)
    {
        if (!_lateLogged.Add((IntPtr)p)) return;
        _lateLeft--;
        IntPtr gob = *(IntPtr*)(p + PartGob), node = *(IntPtr*)(p + PartNodeLink);
        string name = node != IntPtr.Zero ? Marshal.PtrToStringAnsi(node + MdlNodeName) ?? "?" : "?";
        if (_seenPtr.TryGetValue((IntPtr)p, out int i))
        {
            Vector4 was = _stPosR[i];
            _log?.Info($"LightmapBake: late light '{name}' {(nint)p:x} is set #{i} DRIFTED -- moved {(pos - new Vector3(was.X, was.Y, was.Z)).Length():F2}u, reach {was.W:F2} -> {adjR:F2}");
        }
        else
            _log?.Info($"LightmapBake: late light '{name}' {(nint)p:x} is NEW after the close -- gob {gob:x} tile {(gob == IntPtr.Zero || *(IntPtr*)(gob + GobTileData) != IntPtr.Zero)} creature {CreatureRooted(gob)} at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) r {adjR:F2} flags {*(int*)(p + LightFlagA)}/{*(int*)(p + LightFlagB)} inten {*(float*)(p + LightIntensity):F2}");
    }

    /// <summary>The walk's tail: close the set on the first walk after the area-loaded edge (or the fallback),
    /// and every instance re-keys against it once. Closed, nothing here runs.</summary>
    internal static void EndWalk()
    {
        if (_closed) return;
        // a walk that saw no light at all is not the area (the edge fired a frame before the list existed:
        // run 23 closed on "0 lights" and baked the plaza black) -- the edge stays armed for the next walk
        if (_clsArea == IntPtr.Zero || (_stN == 0 && CensusDynamic == 0)) return;
        bool edge = _edgeWalk >= 0 && _walks - _edgeWalk >= SettleWalks && _walks - _lastNew >= QuietWalks;
        if (!edge && _walks < FallbackWalks) return;
        _edgeSeen = false; _closed = true;
        _stab.Clear();
        ulong acc = 0;
        for (int i = 0; i < _stN; i++)
        {
            Vector4 pr = _stPosR[i]; Vector3 c = _stCol[i];
            float m = MathF.Max(MathF.Max(c.X, c.Y), MathF.Max(c.Z, 1e-3f));
            ulong h = FnvOffset;
            h = Mix(h, Q(pr.X, 4f)); h = Mix(h, Q(pr.Y, 4f)); h = Mix(h, Q(pr.Z, 4f)); h = Mix(h, Q(pr.W, 4f));
            h = Mix(h, Q(c.X / m, 0.1f)); h = Mix(h, Q(c.Y / m, 0.1f)); h = Mix(h, Q(c.Z / m, 0.1f));
            acc ^= h;
        }
        ulong hash = Mix(FnvOffset, (long)acc) | 1UL;   // never 0: 0 means "no set yet"
        // the runtime check the research asks for: the registry's count against the placeables the walk saw
        _log?.Info($"LightmapBake: static set closed on the {(edge ? $"area-loaded edge (walk {_walks}, edge at {_edgeWalk}, last new light at {_lastNew})" : $"fallback ({FallbackWalks} walks, the edge never fired or lights kept arriving)")} -- "
                   + $"{_stN} lights (tile {CensusTile}, placeable {CensusPlaceable} of {_regGobs.Count} the registry named), {CensusAnimated} animated, {CensusDynamic} dynamic of which {CensusUnstable} still changing");
        _closeStatic = CensusStatic; _closeTile = CensusTile; _closePlaceable = CensusPlaceable; _closeAnimated = CensusAnimated; _closeDynamic = CensusDynamic;
        Arrived();
        if (hash == SetHash && !_areaChanged) return;
        if (_setPosR.Length < _stN) { _setPosR = new Vector4[_stN]; _setCol = new Vector3[_stN]; }
        Array.Copy(_stPosR, _setPosR, _stN); Array.Copy(_stCol, _setCol, _stN);
        _setN = _stN;
        SetHash = hash;
        AbortBake();
        if (_areaChanged) { _areaChanged = false; _areaHalve = 0; _placedArea = 0; Repack(); AlloyRenderer.SweepMeshTables(); }
        else RekeyAll();
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

    private static delegate* unmanaged<IntPtr, void> _origCsl;

    /// <summary>The engine's own "the static lighting changed" signal (#664 §A.6): called after a script
    /// toggles a placeable light on or off. Voids the classification so the next walk re-reads every light;
    /// the fingerprint then moves on its own if the set did. Cold hook: allocates nothing anyway.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _origCsl = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.ComputeStaticLighting, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnComputeStaticLighting);
        _loadedSlot = hooks.Chain(SymbolMap.SendAreaLoaded, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSendAreaLoaded, EdgePriority);
        _setTileSlot = hooks.Chain(SymbolMap.AreaSetTile, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, int, int, void>)&OnSetTile, EdgePriority);
        if (_loadedSlot == null) log.Info("LightmapBake: the AreaLoaded send did not resolve -- the static set closes on the walk fallback, no hold");
        else InstallSurface(log);
    }

    /// <summary>Observers, bar the hold (<c>LightmapBake.Hold.cs</c>): they read nothing and call through, so any priority is fine.</summary>
    private const int EdgePriority = 0;
    private static IntPtr* _loadedSlot, _setTileSlot;

    [UnmanagedCallersOnly]
    private static void OnSetTile(IntPtr area, IntPtr tile, int id, int orientation, int height)
    {
        if (_setTileSlot != null) ((delegate* unmanaged<IntPtr, IntPtr, int, int, int, void>)*_setTileSlot)(area, tile, id, orientation, height);
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
