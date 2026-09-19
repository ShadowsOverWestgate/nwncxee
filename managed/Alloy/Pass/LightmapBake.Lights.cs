using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>The static-light side of the bake (#674): the three-branch rule (#654/#664/#659) classified once
/// per <c>PartLight</c> identity, the per-frame static snapshot the bake reads, the static-set fingerprint
/// that is the one re-key edge (#668), and the <c>CAurScene::ComputeStaticLighting</c> hook that voids the
/// classification. Every reader here runs on the light walk (the shadow bracket) and allocates nothing.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Classification table: open addressing on the PartLight pointer, MeshLod's flicker-table shape.
    /// 0 = unknown, 1 = static, 2 = dynamic. Voided whole at the world edge, the hook edge and a registry
    /// count change (the registry shrinks after load as dynamic placeables adopt their lights, #659).</summary>
    private const int ClsSlots = 2048;
    private const int ClsProbe = 8;
    private static readonly IntPtr[] _clsId = new IntPtr[ClsSlots];
    private static readonly byte[] _cls = new byte[ClsSlots];
    private static int _clsFull;
    private static int _regCountSeen = -1;
    private static IntPtr _clsArea;

    /// <summary>The area's static set over the UNCUT master list, accumulated across walks (#654: the
    /// fingerprint must not fold the culled set): position, LIT radius, the same colour the light SSBO carries.</summary>
    private static Vector4[] _stPosR = new Vector4[256];
    private static Vector3[] _stCol = new Vector3[256];
    private static int _stN;
    /// <summary>The walk's census: static / animated (>1-key radius or colour track) / dynamic, per frame.</summary>
    internal static int CensusStatic, CensusAnimated, CensusDynamic, CensusTile, CensusPlaceable;

    /// <summary>The fingerprint candidate and its hold (GI's <c>LightHash</c> shape: an order-independent xor
    /// of each static light's coarse position/range and chroma). <see cref="HoldFrames"/> frames of one
    /// value settle it; the settled value is the static set the bake works from.</summary>
    private const int HoldFrames = 120;   // ~2 s: an area's lights spawn over seconds after load, each spawn an edge otherwise
    private static ulong _candHash, _candFor;
    /// <summary>The settled static set: copied off the snapshot on the edge, read by the bake and the keys.</summary>
    private static Vector4[] _setPosR = new Vector4[256];
    private static Vector3[] _setCol = new Vector3[256];
    private static int _setN;
    internal static ulong SetHash { get; private set; }
    /// <summary><c>ComputeStaticLighting</c> hook edges this period (the period line prints them).</summary>
    private static int _cslEdges;

    private const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;
    private static ulong Mix(ulong h, long v) { h ^= (ulong)v; return h * FnvPrime; }
    private static long Q(float v, float step) => (long)MathF.Round(v / step);

    /// <summary>Restart the per-frame snapshot. Called from the walk's top, beside <c>BeginAreaLights</c>.</summary>
    /// <summary>The static lights seen in this area so far, by quantised position and reach: the engine's
    /// master list is distance-loaded (a tile's lights come and go with the player), and a bake over the
    /// area must not follow the camera. Only grows until the area changes; a light seen once is baked.</summary>
    private static readonly Dictionary<ulong, int> _seen = new(512);

    internal static void BeginWalk(IntPtr area)
    {
        CensusStatic = CensusAnimated = CensusDynamic = CensusTile = CensusPlaceable = 0;
        if (area != _clsArea) { _clsArea = area; VoidClasses(); _seen.Clear(); _stN = 0; }
        int reg = area == IntPtr.Zero ? 0 : *(int*)(area + AreaPlaceableLightCount);
        if (reg != _regCountSeen) { _regCountSeen = reg; VoidClasses(); }
    }

    private static void VoidClasses() { Array.Clear(_clsId); Array.Clear(_cls); }

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
        if (!tile && gob != IntPtr.Zero && area != IntPtr.Zero)
        {
            int regCount = *(int*)(area + AreaPlaceableLightCount);
            IntPtr regData = *(IntPtr*)(area + AreaPlaceableLights);
            if (regData != IntPtr.Zero && regCount > 0 && regCount < 65536)
                for (int i = 0; i < regCount && !placeable; i++)
                {
                    IntPtr rec = *((IntPtr*)regData + i);
                    if (rec != IntPtr.Zero && *(IntPtr*)(rec + PlaceableLightGob) == gob) placeable = true;
                }
        }
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
        return (tile || placeable) && !animated;
    }

    /// <summary>One master-list light, classified and -- when static -- folded into the snapshot. Called for
    /// every point light the walk admits, BEFORE its fade and cull, with the lightmap on or off (GI keys its
    /// cache on the same fingerprint). Returns the static verdict for the SSBO bit.</summary>
    internal static bool Record(byte* p, IntPtr area, Vector3 pos, float adjR, Vector3 color, bool ambient)
    {
        if (ambient) { CensusDynamic++; return false; }
        bool st = IsStatic(p, area, out bool animated, out bool placeable);
        if (animated) CensusAnimated++;
        if (!st) { CensusDynamic++; return false; }
        CensusStatic++;
        if (placeable) CensusPlaceable++; else CensusTile++;
        ulong id = Mix(Mix(Mix(Mix(FnvOffset, Q(pos.X, 0.05f)), Q(pos.Y, 0.05f)), Q(pos.Z, 0.05f)), Q(adjR, 0.05f));
        if (_seen.TryGetValue(id, out int at)) { _stCol[at] = color; return true; }
        if (_stN == _stPosR.Length)
        {
            int n = _stN * 2;
            Array.Resize(ref _stPosR, n); Array.Resize(ref _stCol, n);
        }
        _seen[id] = _stN;
        _stPosR[_stN] = new Vector4(pos, adjR); _stCol[_stN++] = color;
        return true;
    }

    /// <summary>The walk's tail: settle the fingerprint. A value held <see cref="HoldFrames"/> frames that
    /// differs from the working set becomes it, and every instance re-keys against it (#668: the one re-key
    /// edge -- load, a script toggle, nothing else).</summary>
    internal static void EndWalk()
    {
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
        // an area whose lights have not spawned yet is not a set of no static lights: it would bake black
        if (_stN == 0 && CensusDynamic == 0) { _candHash = 0; _candFor = 0; return; }
        if (hash != _candHash) { _candHash = hash; _candFor = 0; return; }
        if (++_candFor < (ulong)HoldFrames || hash == SetHash) return;
        if (_setPosR.Length < _stN) { _setPosR = new Vector4[_stN]; _setCol = new Vector3[_stN]; }
        Array.Copy(_stPosR, _setPosR, _stN); Array.Copy(_stCol, _setCol, _stN);
        _setN = _stN;
        SetHash = hash;
        AbortBake();
        RekeyAll();
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
    }

    [UnmanagedCallersOnly]
    private static void OnComputeStaticLighting(IntPtr scene)
    {
        if (_origCsl != null) _origCsl(scene);
        VoidClasses();
        _cslEdges++;
    }
}
