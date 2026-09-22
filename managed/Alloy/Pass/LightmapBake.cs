using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The lightmap bake and its read (#674). Every charted static instance gets a rectangle on a
/// 4096x4096 page pair -- <b>A</b> irradiance (RG11B10F) and <b>B</b> dominant direction (RGBA8) -- baked on
/// the GPU page-major, light-major: per (page, light) the light's shadow is rendered from the STATIC set into
/// one scratch cube (<see cref="CasterPass.BakeStaticCube"/>), then every to-bake rect the light reaches is
/// drawn in UV space with the colour pass's own per-light body (<c>CxPointLight</c>) accumulating into two
/// RGBA16F scratch pages. A page's final pass normalises B, dilates two texels through the coverage alpha and
/// converts into the resident pair. The lit stage reads the per-slot bake record (<see cref="ChartBufDecl"/>)
/// and skips every static light for a baked surface (#668).
/// <para>Bake key = chart content + density + the placement (1 cm / 1e-3) + the touching static lights (5 cm,
/// 1/255) + the shadow look; one deflated raw-bits file per key in <c>cx-charts/</c>
/// (<c>LightmapBake.Cache.cs</c>); a hit is a <c>glTexSubImage3D</c> at the packed offset and never queues.
/// Pacing: <see cref="HoldLightsPerFrame"/> (page, light) steps per frame under the loading-screen hold
/// (<see cref="Hold"/>, #675), <see cref="BackgroundLightsPerFrame"/> otherwise.</para>
/// <para>Refusals stand a part, a page or the area down with a logged reason and never a frame verdict: a
/// missing entry point or a failed link latches the pass off (every record stays unbaked, which IS the
/// real-time loop); a rect past the page cap stays unbaked and is counted.</para>
/// <para>Density tiers by distance from the player (<c>lightmap.near</c>, <c>lightmap.tiers</c>; #679). Every
/// instance enrols at the coarsest tier, the area-wide <b>baseline</b> that bakes first;
/// <see cref="RefineSweep"/> re-enrols the readers of a landed rect at a finer tier as the player nears it,
/// the baseline record live until the fine one lands. Baseline and fine rects pack on pages of their own
/// class; a fine page whose rects are all past the next tier's radius is evicted whole when the pack needs
/// the room, its readers back on their baseline rect (kept, keyed, never overwritten inside the area).</para>
/// <para>ponytail: baseline rects are never freed inside an area (Revoke rewinds the pages); an instance whose
/// key moves takes a NEW rect and the old one idles until the world edge. Page space is a session's, not a key's.</para></summary>
internal static unsafe partial class LightmapBake
{
    internal const int PageSize = 4096;
    internal const int MaxPages = 32;
    /// <summary>A rect's side is capped at a quarter page (#656): the density scales down past it.</summary>
    private const int MaxRect = PageSize / 2;
    /// <summary>Texels between packed rects: the dilation reaches two, so two rects never bleed.</summary>
    private const int Gutter = 2;
    /// <summary>Background: 4, not 2 -- a step is ~1 ms wall (the cube), and at 2 a 120-light page was 60 frames of
    /// pacing for 0.02 ms of GPU per step; the walk's refinement waves (#679) were paying that per wave.</summary>
    private const int HoldLightsPerFrame = 32, BackgroundLightsPerFrame = 4;
    /// <summary>Frames the queue must stand still before a page's bake opens: rects enrol as parts do, a
    /// few a frame, and a page finished per trickle is a finish pass and a readback per frame.</summary>
    private const int SettleFrames = 30, HoldSettleFrames = 4;
    internal const uint BindChart = 6;
    internal const uint UnitA = 20, UnitB = 21;
    private const int MaxRects = 1 << 16;
    private const int LogCap = 6;

    /// <summary>#675 flips this under the loading screen; the bake then takes the hold's pacing.</summary>
    internal static bool Hold;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("LightmapBake");
    internal static bool Enabled => Lightmap.G.Master is { V: true } && !_down.Failed;

    /// <summary>Per ownership slot: the rect it reads, -1 = never seen (enrol on sight), -2 = seen and refused.</summary>
    private static readonly int[] _slotRect = new int[AlloyRenderer.MaxOwned];
    /// <summary>The CxChartBuf mirror, one vec4 per slot: (page, u0, v0, s), -1 page = unbaked.</summary>
    private const int RecFloats = 4;
    private static readonly float[] _rec = new float[AlloyRenderer.MaxOwned * RecFloats];
    private static int _dirtyLo = int.MaxValue, _dirtyHi = -1;
    private static uint _bufChart;
    private static bool _bufFilled;

    private enum RState : byte { Packing, Probing, Queued, Landed, PastCap }

    /// <summary>One bake key's rectangle: where it sits, whose geometry draws it, what it touches.</summary>
    private struct Rect
    {
        /// <summary>The bake key, and the key before the halvings were mixed in (every re-halving derives from it).</summary>
        internal ulong Key, Key0;
        internal int Slot, Vao, Item;
        internal int Page, X, Y, W, H;
        internal float S;
        internal Vector3 C; internal float R;
        internal RState State;
        internal ulong Content;
        /// <summary>Density halvings baked in: <see cref="Tier"/> + the area-wide halving. Mixed into the key.</summary>
        internal int Halved;
        /// <summary>#679: the distance tier (0 = full density, <see cref="Baseline"/> = the coarsest, area-wide).</summary>
        internal int Tier;
        /// <summary>#679: a baseline rect whose readers moved to a finer one; it keeps its page space as their
        /// fallback (an evicted fine rect hands them back) and is no orphan to the repack scans.</summary>
        internal bool Refined;
    }
    private static Rect[] _rects = new Rect[1024];
    /// <summary>#679: entries of evicted rects, reused before the table grows (a walk evicts thousands).</summary>
    private static readonly Stack<int> _free = new();
    /// <summary>Per rect, the slots reading it -- what a landing writes records for. Small (a placement is
    /// usually one instance); a vacated slot is removed, so nothing stale is ever walked.</summary>
    private static List<int>?[] _owners = new List<int>?[1024];
    private static int _rectN;
    private static readonly Dictionary<ulong, int> _byKey = new();
    private static readonly List<int> _toPack = new();

    /// <summary>The shelf packer, per page: the open shelf's top, height and cursor.</summary>
    private static readonly int[] _shelfY = new int[MaxPages], _shelfH = new int[MaxPages], _shelfX = new int[MaxPages];
    private static int _packPage;
    /// <summary>#679: pages belong to one class -- 0 unopened, 1 baseline (the area, never evicted inside it),
    /// 2 fine (the walk: dropped whole when the pack needs the space). The fine cursor is its own.</summary>
    private static readonly byte[] _pageClass = new byte[MaxPages];
    private const byte ClassBase = 1, ClassFine = 2;
    private static int _finePage;
    /// <summary>#679: the refine sweep's cursor over the rects, and the frame it may run from (a refusal backs it off).</summary>
    private static int _refineAt, _refineCursor;
    /// <summary>Fine rects evicted this period.</summary>
    private static int _evicted;
    /// <summary>Rects the refine sweep examines per frame (a distance each: a 26k table is one wave in 13 frames --
    /// at 256 a wave was one slice's finds, 84-249 rects, each paying the settle and the page bake), and the frames
    /// it stands down after a fine rect found no page: with every fine page inside its margin nothing frees until
    /// the player moves on.</summary>
    private const int RefinePerFrame = 2048, RefuseBackoffFrames = 600;
    /// <summary>A refining rect's readers, copied out before <see cref="Enrol(int, int, int, int)"/> edits the owner list.</summary>
    private static readonly List<int> _readers = new();
    /// <summary>The coarsest tier: the area-wide baseline the load bakes first. 0 = no tiers (<c>lightmap.tiers</c> 1).</summary>
    private static int Baseline => Lightmap.Tiers.V - 1;
    internal const int MaxTiers = 4;
    /// <summary>Tier t covers <c>lightmap.near</c> x 2^t metres from the player; the baseline covers everything.</summary>
    private static float Radius(int tier) => Lightmap.Near.V * (1 << tier);
    /// <summary>The finest tier whose radius holds a rect at distance <paramref name="d"/>.</summary>
    private static int DesiredTier(float d)
    {
        int t = 0;
        while (t < Baseline && d > Radius(t)) t++;
        return t;
    }
    private static int _layers;
    private static int _cap;

    private static uint _texA, _texB, _scrA, _scrB, _scrZ, _fbo, _cube, _pbo;
    /// <summary>Depth steps the scratch depth holds: light l of a page writes (l + 1) / this under GREATER,
    /// so the pushed triangles (<see cref="BakeGs"/>) that overlap along a shared edge add a light ONCE -- the
    /// second fragment on the texel fails the test. The bake stays additive with no clear per light.</summary>
    private const int BakeDepthSteps = 1 << 16;
    private static int _cubeSize;
    private static uint _progBake, _progFinish;
    private static int _bDepth;
    private static int _bSlot, _bRect, _bLight, _bColor, _bMode, _bCubeTex, _bDynTex, _bCubeLight, _bCubePlane, _bCubeCount,
                       _bCubeSoft, _bCubeFloor, _bCubePcss, _bCubeDynPage, _bCubeFade, _bSoftWorld,
                       _bFalloff, _bWrap, _bAtten, _bHot, _bWrapN, _bSss, _bReach, _bGrid, _bTile, _bZ;
    private static int _fA, _fB;
    private static bool _built;

    /// <summary>The bake in flight: one page, its rect list and light list, a cursor over the lights.</summary>
    private static int _bakePage = -1, _bakeLight, _bakeSteps;
    private static readonly List<int> _bakeRects = new();
    private static readonly List<int> _bakeLights = new();
    private static readonly List<int> _pendingRead = new();
    /// <summary>The readback's page rectangle: the union of the rects that landed, which is what the finish
    /// pass touched and what the PBO holds (one ReadPixels pair, not one per rect).</summary>
    private static int _ux0, _uy0, _uw, _uh;
    private static IntPtr _fence;
    private static int _pboBytes;
    /// <summary>The readback's host copy, handed to the cache thread by slices; re-used once the thread has
    /// written every slice (<see cref="BakeCache.Outstanding"/>), replaced -- rarely -- while it has not.</summary>
    /// <summary>Union readback buffers, a small ring: one is handed to the cache thread per landing and comes
    /// back when its last file is written (<see cref="BakeCache.Busy"/>). A landing with no free buffer goes
    /// uncached rather than allocate: a fresh 128 MiB buffer per landing was 7.7 GB of garbage a period and a
    /// swap storm (run 6).</summary>
    private const int ReadBufs = 4;
    private static readonly byte[][] _readBufs = new byte[ReadBufs][];
    private static long _pageT0;
    private static GpuQuery _qBake;

    /// <summary>Period counters.</summary>
    private static int _landed, _landedHits, _pending, _refused, _pastCap, _bakeSkips, _uncached, _logLeft = LogCap;
    private static double _pageMs, _pageMsN;
    private static bool _rekeyPending;
    private static long _drewBaked, _drewNoRect, _drewRefused, _drewPending, _drewLandedUnbaked;
    private static int _frame, _queuedAt, _newAt;
    private static bool _repacked;
    /// <summary>Area-wide density halvings (0..<see cref="MaxAreaHalve"/>): an area whose rects would not fit the
    /// page cap at the knob's density is baked at a half, a quarter ... of it -- every rect, not the last ones
    /// packed (run 28: a 16k-part area landed 5.9k rects and refused 35k past the cap, viz 43 all magenta).
    /// Decided at Pack from the demand the settle window gathered; a later overflow bumps it and repacks.</summary>
    private static int _areaHalve;
    private const int MaxAreaHalve = 4;
    /// <summary>Shelf packing lands about this share of a page's texels.</summary>
    private const float PackShare = 0.8f;
    private const long PagePackTexels = (long)(PageSize * (long)PageSize * PackShare);
    /// <summary>Texel area (with gutters) of every rect placed so far this area.</summary>
    private static long _placedArea;
    /// <summary>The per-area "known differences" line (#666) went out with this area's first page.</summary>
    private static bool _knownLogged;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        Array.Fill(_slotRect, -1);
        Array.Fill(_rec, -1f);
        _cache.Attach(log);
    }

    /// <summary>The GLSL the lit stage composes for its read (#668): the record buffer by ownership slot, the
    /// two page arrays and the decode. <c>binding = 6</c> = <see cref="BindChart"/>; the sampler units are
    /// <see cref="UnitA"/>/<see cref="UnitB"/>. A content override that never declares the buffer simply never
    /// reads the lightmap.</summary>
    internal static string ChartBufDecl => ShaderSource.Get("cx_chartbuf.glsl");

    /// <summary>From the lit program bind: the record buffer, the pages and the two uniforms. Pages 0 when
    /// nothing is resident, so a record that somehow reads a page is ignored rather than sampled.</summary>
    internal static void BindForDraw(int locPages, int locMovers)
    {
        if (locPages < 0) return;
        bool live = Enabled && _layers > 0 && _bufChart != 0 && _bufFilled;
        if (live)
        {
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindChart, _bufChart);
            Gl.ActiveTexture(Gl.TEXTURE0 + UnitA); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texA);
            Gl.ActiveTexture(Gl.TEXTURE0 + UnitB); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texB);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        Gl.Uniform1i(locPages, live ? _layers : 0);
        if (locMovers >= 0) Gl.Uniform1i(locMovers, Lightmap.Movers.V ? 1 : 0);
    }

    /// <summary>The staging loop's one call per charted draw: a slot the bake has not seen enrols -- its key
    /// is computed against the settled static set and it either shares a landed rect, joins a rect's queue or
    /// takes a new one. Before the set settles nothing enrols (the slot is re-offered next frame).</summary>
    internal static void Enrol(int i, int vao, int item)
    {
        // the per-frame census of what viz 43 shows: a charted draw is baked, or unbaked for one of four reasons
        if (_rec[i * RecFloats] >= 0f) _drewBaked++;
        else if (_slotRect[i] == -1) _drewNoRect++;
        else if (_slotRect[i] == -2) _drewRefused++;
        else if (_rects[_slotRect[i]].State == RState.Landed) _drewLandedUnbaked++;
        else _drewPending++;
        if (_slotRect[i] != -1 || !Enabled || SetHash == 0) return;
        Enrol(i, vao, item, Baseline);
    }

    /// <summary>Enrol at a tier: the baseline from the sweep and the staging loop, a finer one from
    /// <see cref="RefineSweep"/>. The slot's record is left as it is -- a refining slot keeps reading its baseline
    /// rect until the fine one lands, and a slot falling back to its landed baseline takes it at once.</summary>
    private static void Enrol(int i, int vao, int item, int tier)
    {
        if (!MeshChart.Atlas(vao, item, out int w, out int h, out ulong content, out Vector3 bmin, out Vector3 bmax)) return;
        if (!AlloyRenderer.SlotPlacement(i, out _, out _, out Matrix4x4 model)) return;
        // the instance's world sphere off the CHART's bounds: the engine's part bbox is unpopulated on tile
        // parts (min == max), and a light reaches what the mesh actually spans
        float sx = new Vector3(model.M11, model.M12, model.M13).Length();
        Vector3 c = Vector3.Transform((bmin + bmax) * 0.5f, model);
        float r = (bmax - bmin).Length() * 0.5f * MathF.Max(sx, 1e-3f);
        int halved = tier + _areaHalve;
        float s = Lightmap.Density.V / MeshChart.AtlasTexelsPerUnit * MathF.Max(sx, 1e-3f) / (1 << halved);
        int rw = (int)MathF.Ceiling(w * s), rh = (int)MathF.Ceiling(h * s);
        if (rw > MaxRect || rh > MaxRect) { s *= (float)MaxRect / Math.Max(rw, rh); rw = (int)MathF.Ceiling(w * s); rh = (int)MathF.Ceiling(h * s); }
        if (rw <= 0 || rh <= 0) { _slotRect[i] = -2; return; }
        ulong key0 = BakeKey(content, model, c, r), key = Halve(key0, halved);
        LogKeySample(content, model, c, r, key);
        if (_byKey.TryGetValue(key, out int ri))
        {
            _slotRect[i] = ri;
            (_owners[ri] ??= new List<int>(2)).Add(i);
            if (_rects[ri].Slot < 0) _rects[ri].Slot = i;
            if (_rects[ri].State == RState.Landed) WriteRecord(i, ri);
            return;
        }
        if (_free.Count == 0 && _rectN == MaxRects) { _slotRect[i] = -2; _refused++; return; }
        ri = NewRect();
        _rects[ri] = new Rect { Key = key, Key0 = key0, Slot = i, Vao = vao, Item = item, Page = -1, W = rw, H = rh, S = s, C = c, R = r, State = RState.Packing, Content = content, Halved = halved, Tier = tier };
        (_owners[ri] ??= new List<int>(2)).Clear();
        _owners[ri]!.Add(i);
        _byKey[key] = ri;
        _slotRect[i] = ri;
        _toPack.Add(ri); _newAt = _frame; Arrived();
    }

    /// <summary>A slot was vacated (or the table wiped): its record reads unbaked from the next upload.</summary>
    internal static void Vacate(int i)
    {
        if ((uint)i >= (uint)_slotRect.Length) return;
        int ri = _slotRect[i];
        _slotRect[i] = -1;
        if (ri >= 0) Disown(ri, i);
        if (_rec[i * RecFloats] >= 0f) SetRecord(i, -1f, 0f, 0f, 0f);
    }

    /// <summary>Retire a rect that cannot be drawn: its readers forget it and enrol again next frame.</summary>
    private static void Drop(int ri, bool queued = true)
    {
        if (queued) _pending--; _refused++;
        var o = _owners[ri];
        if (o != null) { for (int k = 0; k < o.Count; k++) _slotRect[o[k]] = -1; o.Clear(); }
        Kill(ri);
    }

    /// <summary>A rect is dead: unkeyed, unowned, its entry free. The owners are the caller's to re-point.</summary>
    private static void Kill(int ri)
    {
        ref Rect r = ref _rects[ri];
        r.State = RState.PastCap; r.Slot = -1;
        _byKey.Remove(r.Key);
        _free.Push(ri);
    }

    private static void Disown(int ri, int i)
    {
        var o = _owners[ri];
        if (o != null) o.Remove(i);
        if (_rects[ri].Slot == i) _rects[ri].Slot = o != null && o.Count > 0 ? o[0] : -1;
    }

    /// <summary>A rect entry: the last freed one, unless its readback is still in flight (the cache file would
    /// take the newcomer's key over the old bits) -- then the table grows and the entry waits its turn.</summary>
    private static int NewRect()
    {
        if (_free.Count > 0 && !_pendingRead.Contains(_free.Peek())) return _free.Pop();
        if (_rectN == _rects.Length) { Array.Resize(ref _rects, _rectN * 2); Array.Resize(ref _owners, _rectN * 2); }
        return _rectN++;
    }

    /// <summary>#679: the walk. Landed rects coarser than their distance asks re-enrol their readers at the
    /// finer tier -- a new rect (cache hit or bake) while the baseline record stays live. Round-robin over
    /// the rects while nothing is pending (the baseline lands first, and one wave of fine rects lands before the
    /// next is asked; the wave gathers over the settle window, so it is every rect the pass found, not one
    /// frame's), and never inside a refusal's back-off. Not under the hold: the player is not placed yet.</summary>
    private static void RefineSweep()
    {
        if (Hold || Baseline == 0 || _rectN == 0 || _pending > 0 || _frame < _refineAt || SetHash == 0) return;
        Vector3 p = AlloyRenderer.PlayerPos;
        for (int k = 0; k < RefinePerFrame; k++)
        {
            int ri = _refineCursor; if (++_refineCursor >= _rectN) _refineCursor = 0;
            ref Rect r = ref _rects[ri];
            if (r.State != RState.Landed || r.Tier == 0) continue;
            var o = _owners[ri];
            if (o == null || o.Count == 0) continue;
            int want = DesiredTier((r.C - p).Length());
            if (want >= r.Tier) continue;
            r.Refined = true;
            _readers.Clear(); _readers.AddRange(o);
            for (int j = 0; j < _readers.Count; j++)
            {
                int i = _readers[j];
                if (!AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) continue;
                _slotRect[i] = -1; Disown(ri, i);
                Enrol(i, vao, item, want);
                if (_slotRect[i] == -1) { _slotRect[i] = ri; o.Add(i); }
            }
        }
    }

    /// <summary>#679: drop the fine page farthest past its tier -- every rect on it whose distance is beyond
    /// <paramref name="margin"/> x the next tier's radius (the hysteresis: a page in reach of a refine is never
    /// the one dropped) -- and re-open it. Its readers take their baseline rect at once (landed, keyed, its
    /// page space kept), or re-enrol. -1 = nothing to drop.</summary>
    private static int EvictFine(float margin)
    {
        Vector3 p = AlloyRenderer.PlayerPos;
        float* score = stackalloc float[MaxPages];
        for (int i = 0; i < MaxPages; i++) score[i] = _pageClass[i] == ClassFine && i != _bakePage ? float.MaxValue : -1f;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.Page < 0 || r.State == RState.PastCap || score[r.Page] < 0f) continue;
            float rel = (r.C - p).Length() / Radius(r.Tier + 1);
            if (rel < score[r.Page]) score[r.Page] = rel;
        }
        int best = -1, n = 0; float bs = 0f;
        for (int i = 0; i < MaxPages; i++) if (score[i] >= margin && (best < 0 || score[i] > bs)) { bs = score[i]; best = i; }
        if (best < 0) return -1;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.Page != best || r.State == RState.PastCap) continue;
            if (r.State != RState.Landed) _pending--;
            _placedArea -= Area(r); r.Page = -1;
            var o = _owners[ri];
            if (o != null)
            {
                for (int k = 0; k < o.Count; k++)
                {
                    int i = o[k];
                    _slotRect[i] = -1;
                    if (AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) Enrol(i, vao, item, Baseline);
                    if (_slotRect[i] < 0 || _rects[_slotRect[i]].State != RState.Landed) SetRecord(i, -1f, 0f, 0f, 0f);   // no landed baseline: the record named the dropped page
                }
                o.Clear();
            }
            Kill(ri); _evicted++; n++;
        }
        _shelfY[best] = _shelfH[best] = _shelfX[best] = 0; _pageClass[best] = 0;
        _finePage = best; _packPage = Math.Min(_packPage, best);   // either cursor must reach the re-opened page
        if (_logLeft > 0) _log?.Info($"LightmapBake: page {best} evicted -- {n} rects, {bs:F1}x past their tier");
        return best;
    }

    /// <summary>The one re-key edge (#668): the static-set fingerprint moved, or the density knob. Every seen
    /// slot forgets its rect and enrols again; a key that comes out the same keeps its rect, page and record
    /// (no flip to the live loop), a changed one reads unbaked at once and never a stale page. The orphans a
    /// re-key leaves are reclaimed by one <see cref="Repack"/> when a page first refuses. Deferred to the frame.</summary>
    internal static void RekeyAll() => _rekeyPending = true;

    private static void Rekey()
    {
        _rekeyPending = false;
        AbortBake();   // a page in flight is baking rects its readers are about to leave
        for (int ri = 0; ri < _rectN; ri++) _rects[ri].Refined = false;   // every key moves: nothing is anyone's fallback
        int changed = 0;
        for (int i = 0; i < _slotRect.Length; i++)
        {
            if (_slotRect[i] < 0) { if (_slotRect[i] == -2) _slotRect[i] = -1; continue; }
            int ri = _slotRect[i];
            _slotRect[i] = -1;
            Disown(ri, i);
            if (!AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) continue;
            Enrol(i, vao, item, Math.Min(_rects[ri].Tier, Baseline));   // at its tier: a static light toggled is no reason to drop to the baseline
            // an unchanged key keeps its page and its record; a changed one keeps the OLD record until the
            // new rect lands (a rect's page space is never reused before Repack) -- clearing it flashed every
            // touched surface through the live loop, the "rebuild" of run 21 as the camera zoomed out
            if (_slotRect[i] != ri) changed++;
        }
        _log?.Info($"LightmapBake: re-keyed (static set {SetHash:x16}) -- {changed} instances re-bake");
    }

    /// <summary>Forget every rect and the packing; the resident pages stay and are overwritten as rects land.</summary>
    private static void Repack()
    {
        Array.Fill(_slotRect, -1);
        for (int i = 0; i < _rec.Length; i += RecFloats) if (_rec[i] >= 0f) { _rec[i] = -1f; _dirtyLo = 0; _dirtyHi = _slotRect.Length - 1; }
        _rectN = 0; _byKey.Clear(); _toPack.Clear(); _free.Clear(); _pending = 0; _placedArea = 0;
        Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX); Array.Clear(_pageClass); _packPage = 0; _finePage = 0; _refineCursor = 0;
        AbortBake();
        if (Gl.DeleteTextures != null) DropPages();
        if (_fence != IntPtr.Zero) { _uncached += _pendingRead.Count; if (Gl.DeleteSync != null) Gl.DeleteSync(_fence); _fence = IntPtr.Zero; }
        _pendingRead.Clear();
        _cache.Drain();
    }

    /// <summary>The bake key (#666): the chart, the density, the placement at 1 cm / 1e-3, the touching
    /// static lights and the shadow look. The placement is the record's matrix AND the world centre: a tile
    /// part's vertices sit in world space under an identity matrix (#673 rebases the CONTENT, so every
    /// placement of one tile model shares a chart), and without the centre every placement would share a
    /// bake too. Two placements of one model in one lighting still share a file.</summary>
    private static ulong BakeKey(ulong content, in Matrix4x4 m, Vector3 c, float r)
    {
        ulong h = Mix(FnvOffset, (long)content);
        h = Mix(h, Lightmap.Density.V);
        h = Mix(h, Q(c.X, 0.01f)); h = Mix(h, Q(c.Y, 0.01f)); h = Mix(h, Q(c.Z, 0.01f));
        h = Mix(h, Q(m.M41, 0.01f)); h = Mix(h, Q(m.M42, 0.01f)); h = Mix(h, Q(m.M43, 0.01f));
        h = Mix(h, Q(m.M11, 1e-3f)); h = Mix(h, Q(m.M12, 1e-3f)); h = Mix(h, Q(m.M13, 1e-3f));
        h = Mix(h, Q(m.M21, 1e-3f)); h = Mix(h, Q(m.M22, 1e-3f)); h = Mix(h, Q(m.M23, 1e-3f));
        h = Mix(h, Q(m.M31, 1e-3f)); h = Mix(h, Q(m.M32, 1e-3f)); h = Mix(h, Q(m.M33, 1e-3f));
        h = Mix(h, (long)TouchingLights(c, r));
        return Mix(h, (long)ShadowLook());
    }

    /// <summary>A different size is a different file: the halvings mixed into the key, none at zero.</summary>
    private static ulong Halve(ulong key0, int halved) => halved > 0 ? Mix(key0, halved) : key0;
    /// <summary>Texel area a rect takes on a page, gutters in.</summary>
    private static long Area(in Rect r) => (long)(r.W + Gutter) * (r.H + Gutter);

    /// <summary>Every dial the bake stage reads: a change to any of them is a different look, so a different key.</summary>
    private static ulong ShadowLook()
    {
        ulong h = FnvOffset;
        h = Mix(h, CasterPass.CubeFaceSize);
        // the floor KNOB, not AlloyLights.ShadowFloor: that is the knob scaled by the area ambient, which
        // drifts -- keyed at 1e-3 it moved every session's keys off the last one's files (0 of 7426 hit, run 7)
        h = Mix(h, Q(AlloyLights.ShadowSoft, 0.01f)); h = Mix(h, Q(Shadows.Floor.V, 1e-3f));
        h = Mix(h, Q(AlloyLights.ShadowNearWorld, 0.01f));
        Vector2 at = AlloyLights.Atten;
        h = Mix(h, Q(at.X, 1e-3f)); h = Mix(h, Q(at.Y, 1e-3f));
        h = Mix(h, Q(Lighting.Gain.V, 1e-3f)); h = Mix(h, Q(Lighting.Radius.V, 1e-3f)); h = Mix(h, Q(Lighting.Falloff.V, 1e-3f));
        h = Mix(h, Q(Lighting.Hot.V, 1e-3f)); h = Mix(h, Q(Lighting.HotSize.V, 1e-3f));
        h = Mix(h, Q(Lighting.Wrap.V, 1e-3f)); h = Mix(h, Q(Lighting.WrapNdl.V, 1e-3f)); h = Mix(h, Q(Lighting.Sss.V, 1e-3f));
        h = Mix(h, Q(AlloyLights.ReachUniform, 1e-3f));
        return h;
    }

    private static void SetRecord(int i, float page, float x, float y, float s)
    {
        int o = i * RecFloats;
        _rec[o] = page; _rec[o + 1] = x; _rec[o + 2] = y; _rec[o + 3] = s;
        if (i < _dirtyLo) _dirtyLo = i;
        if (i > _dirtyHi) _dirtyHi = i;
    }

    private static void WriteRecord(int i, int ri)
    {
        ref Rect r = ref _rects[ri];
        SetRecord(i, r.Page, r.X, r.Y, r.S);
    }

    /// <summary>A rect landed: every slot reading it takes the page.</summary>
    private static void Land(int ri, bool hit)
    {
        _rects[ri].State = RState.Landed;
        _landed++; if (hit) _landedHits++;
        if (Held && _firstLandT == 0) _firstLandT = Stopwatch.GetTimestamp();
        var o = _owners[ri];
        if (o != null) for (int k = 0; k < o.Count; k++) WriteRecord(o[k], ri);
    }

    private static void UploadRecords()
    {
        if (Gl.GenBuffers == null || Gl.BufferData == null || Gl.BufferSubData == null) return;
        if (_bufChart == 0)
        {
            uint b; Gl.GenBuffers(1, &b); if (b == 0) return;
            _bufChart = b; _bufFilled = false;
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufChart);
        fixed (float* p = _rec)
        {
            if (!_bufFilled) { Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)_rec.Length * 4), p, Gl.DYNAMIC_DRAW); _bufFilled = true; }
            else if (_dirtyHi >= _dirtyLo)
                Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)_dirtyLo * RecFloats * 4), (IntPtr)((long)(_dirtyHi - _dirtyLo + 1) * RecFloats * 4), p + (long)_dirtyLo * RecFloats);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _dirtyLo = int.MaxValue; _dirtyHi = -1;
    }

    /// <summary>The page cap: <c>lightmap.pages</c>, or 0 = a quarter of the card's memory in 128 MiB pages
    /// (8 GiB = 16, 16 GiB = 32), 1..<see cref="MaxPages"/>; 2 when the device answers no memory query (#668).
    /// Pages are allocated as rects need them, so the cap costs nothing an area does not use.</summary>
    private static int PageCap()
    {
        int k = Lightmap.Pages.V;
        if (k > 0) return Math.Min(k, MaxPages);
        long mib = AlloyRenderer.VramTotalMib > 0 ? AlloyRenderer.VramTotalMib : AlloyRenderer.VramFreeMib;
        if (mib <= 0) return 2;
        return (int)Math.Clamp(mib / 4 / 128, 1, MaxPages);
    }

    /// <summary>Shelf-pack the new rects, tallest first (#668: so the cap drops the smallest last), once no
    /// newcomer has arrived for the settle window: an enrolment drains over many frames, and a shelf opened
    /// per frame's trickle packed an area's 3.7 pages of rects into 8.</summary>
    private static void Pack()
    {
        if (_toPack.Count == 0 || _frame - _newAt < (Hold ? HoldSettleFrames : SettleFrames)) return;
        if (!FitDensity(out long demand)) return;
        // one allocation for the pages this pack needs, not one per page: growing a layer at a time copied
        // 1+2+..+12 layers of pages (10 GB of GPU copy, a transient double of the pages on the card)
        int need = (int)Math.Min(_cap, (_placedArea + demand + PagePackTexels - 1) / PagePackTexels);
        if (need > _layers && !Grow(need)) return;
        _toPack.Sort((a, b) => _rects[b].H.CompareTo(_rects[a].H));
        for (int k = 0; k < _toPack.Count; k++)
        {
            int ri = _toPack[k];
            ref Rect r = ref _rects[ri];
            if (!Place(ref r))
            {
                if (EvictFine(r.Tier < Baseline ? 1f : 0f) >= 0 && Place(ref r)) goto placed;
                if (r.Tier < Baseline) { Drop(ri, queued: false); _refineAt = _frame + RefuseBackoffFrames; continue; }
                // the pages are full of re-key orphans (a shelf cannot free): repack everything from nothing
                // once, and only then refuse. The slots re-enrol next frame and adopt their files from the cache.
                if (_rectN > _toPack.Count && !_repacked) { _repacked = true; Repack(); return; }
                // past the cap: half the density rather than no bake, down to a quarter (its own key: the file
                // is a different size). ponytail: the LAST rects packed halve, not the largest
                if (r.S > 0f && r.W > 4 && r.H > 4 && r.Halved - r.Tier < 2)
                {
                    _byKey.Remove(r.Key);
                    r.Halved++; r.S *= 0.5f; r.W = (r.W + 1) / 2; r.H = (r.H + 1) / 2; r.Key = Halve(r.Key0, r.Halved);
                    _byKey[r.Key] = ri;
                    if (Place(ref r)) goto placed;
                }
                r.State = RState.PastCap; _pastCap++;
                continue;
            }
            placed:
            _repacked = false;
            _placedArea += Area(r);
            r.State = RState.Probing; _pending++;
            _cache.Probe(ri, r.Key, r.W, r.H);
        }
        _toPack.Clear();
    }

    /// <summary>The newcomers' texel demand against the pages left: over it, the area's density halves until it
    /// fits (to <see cref="MaxAreaHalve"/>). Only the baseline's demand counts (#679). Nothing placed yet = the newcomers shrink in place; else everything
    /// repacks at the new size. False = this Pack is over (the slots re-enrol next frame).</summary>
    private static bool FitDensity(out long demand)
    {
        demand = 0; long baseDemand = 0;
        for (int k = 0; k < _toPack.Count; k++)
        {
            ref Rect r = ref _rects[_toPack[k]];
            demand += Area(r);
            if (r.Tier >= Baseline) baseDemand += Area(r);
        }
        long cap = (long)(_cap * (long)PageSize * PageSize * PackShare);
        if (_placedArea + baseDemand <= cap) return true;   // only the baseline's overflow halves the area: a fine rect evicts or drops
        demand = baseDemand;
        // the pages hold the last area's orphans (an in-module area change is no world edge, so nothing
        // rewound them): repack the live rects once before shrinking anyone's texels
        if (!_repacked)
        {
            long orphan = 0;
            for (int ri = 0; ri < _rectN; ri++)
                if (_rects[ri].Page >= 0 && _rects[ri].State != RState.PastCap && !_rects[ri].Refined && (_owners[ri] == null || _owners[ri]!.Count == 0))
                    orphan += Area(_rects[ri]);
            if (orphan > 0) { _repacked = true; _placedArea = 0; Repack(); return false; }
        }
        int halve = 0;
        while (_placedArea + (demand >> (2 * halve)) > cap && _areaHalve + halve < MaxAreaHalve) halve++;
        if (halve == 0) return true;
        _areaHalve += halve;
        _log?.Info($"LightmapBake: {(_placedArea + demand) / 1e6:F1} Mtexels asked of {cap / 1e6:F0} across {_cap} pages -- the area bakes at 1/{1 << _areaHalve} of lightmap.density");
        if (_placedArea > 0) { _placedArea = 0; Repack(); return false; }
        for (int k = 0; k < _toPack.Count; k++)
        {
            ref Rect r = ref _rects[_toPack[k]];
            _byKey.Remove(r.Key);
            r.Halved = r.Tier + _areaHalve; r.S /= 1 << halve; r.W = Math.Max((r.W + (1 << halve) - 1) >> halve, 1); r.H = Math.Max((r.H + (1 << halve) - 1) >> halve, 1);
            r.Key = Halve(r.Key0, r.Halved);
            _byKey[r.Key] = _toPack[k];
        }
        return true;
    }

    private static bool Place(ref Rect r)
    {
        byte cls = r.Tier < Baseline ? ClassFine : ClassBase;
        ref int cursor = ref (cls == ClassFine ? ref _finePage : ref _packPage);
        while (true)
        {
            if (cursor >= _cap) return false;
            if (cursor >= _layers && !AddLayer()) return false;
            int p = cursor;
            if (_pageClass[p] != 0 && _pageClass[p] != cls) { cursor++; continue; }
            _pageClass[p] = cls;
            if (_shelfX[p] + r.W + Gutter <= PageSize && r.H <= _shelfH[p])
            {
                r.Page = p; r.X = _shelfX[p] + Gutter; r.Y = _shelfY[p] + Gutter;
                _shelfX[p] += r.W + Gutter;
                return true;
            }
            int ny = _shelfY[p] + _shelfH[p] + (_shelfH[p] > 0 ? Gutter : 0);
            if (ny + r.H + Gutter * 2 <= PageSize && r.W + Gutter * 2 <= PageSize)
            {
                _shelfY[p] = ny; _shelfH[p] = r.H + Gutter; _shelfX[p] = 0;
                continue;
            }
            cursor++;
        }
    }

    /// <summary>Once a frame, at the frame slot after the probe captures: adopt cache hits, pack newcomers,
    /// advance the bake by its pacing, collect readbacks and upload the records. Inside the colour bracket,
    /// with the shadow bracket's gather staged; the caller restores its target after.</summary>
    /// <summary>Bytes this thread allocated inside <see cref="Run"/> this period, for the period line: the
    /// frame line's GC total said 4-7 GB a period and nothing said whose.</summary>
    private static long _allocRun;
    internal static void Run(bool capturing, int frame)
    {
        if (!Enabled || capturing) return;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        try { RunInner(frame); }
        finally { _allocRun += GC.GetAllocatedBytesForCurrentThread() - a0; }
    }

    private static void RunInner(int frame)
    {
        if (!Ensure()) return;
        _frame = frame;
        if (_rekeyPending) Rekey();
        Sweep();
        RefineSweep();
        _cap = PageCap();
        Adopt();
        Pack();
        CollectReadback();
        int steps = Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame;
        for (int k = 0; k < steps; k++) if (!Step()) break;
        UploadRecords();
    }

    /// <summary>Every owned slot the bake has not seen is offered from the frame, in view or not: the bake is
    /// over the area, not over what the camera happened to show, so a part seen for the first time is already
    /// baked. Round-robin: a chart arrives from the worker whenever it does.</summary>
    private const int SweepPerFrame = 1024;
    private static int _sweepAt;
    private static void Sweep()
    {
        if (SetHash == 0) return;
        int per = Hold ? HoldSweepPerFrame : SweepPerFrame;
        for (int k = 0; k < per; k++)
        {
            int i = _sweepAt; if (++_sweepAt >= _slotRect.Length) _sweepAt = 0;
            if (_slotRect[i] != -1 || !AlloyRenderer.SlotPlacement(i, out int vao, out int item, out Matrix4x4 m)) continue;
            if (m.M44 == 0f) continue;   // never staged yet: the record's matrix is unwritten, the staging loop enrols it
            Enrol(i, vao, item);
        }
    }

    /// <summary>The static set moved under a page in flight: its light indices name other lights now. The
    /// page's rects stay queued and start over against the new set (or re-key away from it).</summary>
    private static void AbortBake()
    {
        _bakePage = -1; _bakeRects.Clear(); _bakeLights.Clear();
    }

    /// <summary>One (page, light) step of the bake, or the page's finish when its lights are done. False =
    /// nothing to do this frame (no queued rect, or the scratch cube could not be rendered).</summary>
    private static bool Step()
    {
        if (_bakePage < 0 && !BeginPage()) return false;
        if (_bakeLight < _bakeLights.Count)
        {
            int l = _bakeLights[_bakeLight];
            Vector4 pr = _setPosR[l];
            var pos = new Vector3(pr.X, pr.Y, pr.Z);
            // the cube starts at the self-shadow skip radius: a brazier's bowl sits right under its light
            // node and blocked every steep ray, a dark disc round each one that only grazing rays escaped
            if (!CasterPass.BakeStaticCube(_cube, _cubeSize, pos, BakeNear, pr.W)) { _bakeSkips++; return false; }
            BakeLight(l, pos, pr.W);
            _bakeLight++; _bakeSteps++;
            return true;
        }
        FinishPage();
        return true;
    }

    /// <summary>Open the lowest page holding queued rects: its rect list, the static lights reaching any of
    /// them, the scratch cleared and every rect's coverage laid down.</summary>
    private static bool BeginPage()
    {
        if (_frame - _queuedAt < (Hold ? HoldSettleFrames : SettleFrames)) return false;
        int page = int.MaxValue;
        for (int ri = 0; ri < _rectN; ri++)
            if (_rects[ri].State == RState.Queued && _rects[ri].Page < page) page = _rects[ri].Page;
        if (page == int.MaxValue) return false;
        _bakeRects.Clear(); _bakeLights.Clear();
        for (int ri = 0; ri < _rectN; ri++)
        {
            if (_rects[ri].State != RState.Queued || _rects[ri].Page != page) continue;
            // no live owner, or the chart range moved out from under it (a VDO slot re-pointed): the rect
            // cannot draw, so it is dropped and its readers re-enrol into a fresh one rather than land black
            ref Rect r = ref _rects[ri];
            if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out _, out _, out _, out _)) { Drop(ri); continue; }
            _bakeRects.Add(ri);
        }
        if (_bakeRects.Count == 0) return false;
        for (int l = 0; l < _setN; l++)
            for (int k = 0; k < _bakeRects.Count; k++)
            {
                ref Rect r = ref _rects[_bakeRects[k]];
                if (Touches(l, r.C, r.R)) { _bakeLights.Add(l); break; }
            }
        _bakePage = page; _bakeLight = 0;
        _pageT0 = Stopwatch.GetTimestamp();
        // the scratch pair: A' = sum rgb + coverage alpha, B' = sum luminance * direction
        BindScratch();
        float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
        Gl.ClearBufferfv(Gl.COLOR, 0, zero); Gl.ClearBufferfv(Gl.COLOR, 1, zero);
        Gl.DepthMask(1); Gl.ClearBufferfv(Gl.DEPTH, 0, zero); Gl.DepthMask(0);
        Gl.UseProgram(_progBake);
        Gl.Uniform1i(_bMode, 0);
        Gl.Uniform1f(_bDepth, -1f);
        Gl.BindVertexArray(MeshChart.Vao);
        for (int k = 0; k < _bakeRects.Count; k++) DrawRect(_bakeRects[k]);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (_logLeft > 0) LogNearest(page);
        if (_logLeft > 0) _log?.Info($"LightmapBake: page {page} bake begins -- {_bakeRects.Count} rects, {_bakeLights.Count} static lights"
                                     + (_knownLogged ? "" : "; known differences on baked surfaces: no static specular beyond the one directional lobe, non-Static placeables cast no shadow from static lights, a fixed-disc penumbra where the live loop runs PCSS"));
        _knownLogged = true;
        return true;
    }

    /// <summary>Diag for a surface baked wrong: the page's rect nearest the camera, its sphere, and the
    /// static lights that reach it (with the nearest one's distance against its reach when none does).</summary>
    private static void LogNearest(int page)
    {
        Vector3 cam = FrameInputs.CameraPos;
        int best = -1; float bd = float.MaxValue;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            float d = (_rects[_bakeRects[k]].C - cam).LengthSquared();
            if (d < bd) { bd = d; best = _bakeRects[k]; }
        }
        if (best < 0) return;
        ref Rect r = ref _rects[best];
        int n = 0, nl = -1; float nd = float.MaxValue;
        for (int l = 0; l < _setN; l++)
        {
            if (Touches(l, r.C, r.R)) n++;
            float d = (new Vector3(_setPosR[l].X, _setPosR[l].Y, _setPosR[l].Z) - r.C).Length();
            if (d < nd) { nd = d; nl = l; }
        }
        _log?.Info($"LightmapBake: [diag] page {page} nearest rect {r.W}x{r.H} at ({r.C.X:F1},{r.C.Y:F1},{r.C.Z:F1}) r={r.R:F1} slot {r.Slot}: "
                   + $"{n} static lights reach it" + (nl >= 0 ? $", nearest light {nd:F1}u away with reach {_setPosR[nl].W:F1}" : "")
                   + $", {(cam - r.C).Length():F1}u from the camera");
    }

    private static void BindScratch()
    {
        AttachPair(-1);
        new PassState(depth: false, depthWrite: false, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
    }

    /// <summary>The FBO over a pair of pages as its two colour outputs: the scratch pair (-1) or one resident layer.</summary>
    private static void AttachPair(int layer)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        if (layer < 0)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _scrA, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _scrB, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _scrZ, 0);
        }
        else
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _texA, 0, layer);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, _texB, 0, layer);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        }
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        Gl.Viewport(0, 0, PageSize, PageSize);
    }

    private static void DrawRect(int ri)
    {
        ref Rect r = ref _rects[ri];
        if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out int baseVertex, out nint off, out int count, out _)) return;
        Gl.Uniform1i(_bSlot, r.Slot);
        Gl.Uniform4f(_bRect, r.X, r.Y, r.S, 0f);
        Gl.Uniform1f(_bSoftWorld, 1f / (MeshChart.AtlasTexelsPerUnit * r.S));
        Gl.DrawElementsBaseVertex(Gl.TRIANGLES, count, CasterPass.GlUnsignedShort, (void*)off, baseVertex);
    }

    /// <summary>One light over the page: the bake program takes the light and its scratch cube exactly the
    /// way the lit stage takes a cube slot (slot 0 of a one-cube array), and every rect it reaches adds.</summary>
    /// <summary>The scratch cube's near plane: the live receiver's self-shadow skip radius (<c>shadow.near</c>
    /// x <c>light.radius</c>), so the geometry a light node sits in casts nothing in the bake.</summary>
    private static float BakeNear => AlloyLights.ShadowNearWorld;

    private static void BakeLight(int l, Vector3 pos, float far)
    {
        bool q = _qBake.Begin(Gl.TIME_ELAPSED);
        BindScratch();
        new PassState(depth: true, depthWrite: true, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
        Gl.DepthFunc(Gl.GREATER);
        Gl.UseProgram(_progBake);
        Gl.Uniform1i(_bMode, 1);
        Gl.Uniform1f(_bDepth, (float)(_bakeLight + 1) / BakeDepthSteps * 2f - 1f);
        Vector3 c = _setCol[l];
        Gl.Uniform4f(_bLight, pos.X, pos.Y, pos.Z, far);
        Gl.Uniform3f(_bColor, c.X, c.Y, c.Z);
        int* vp = stackalloc int[4] { 0, 0, PageSize, PageSize };
        AlloyLights.SetDrawUniforms(_bGrid, _bTile, _bZ, _bFalloff, _bWrap, _bAtten, _bHot, _bWrapN, _bSss, vp, 1f, 100f, _bReach);
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, _cube);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_bCubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(_bDynTex, (int)AlloyRenderer.CubeDynUnit);
        Gl.Uniform4f(_bCubeLight, pos.X, pos.Y, pos.Z, far);
        Gl.Uniform2f(_bCubePlane, MathF.Max(BakeNear, CasterPass.CubeNearPlane), 1f / _cubeSize);
        Gl.Uniform1i(_bCubeCount, 1);
        // the disc everywhere (no screen to stand it down against), the near skip, no cap; PCSS off:
        // the bake has no raw view of its cube -- a fixed disc at the softness knob. ponytail: bind the
        // scratch cube through the raw sampler too if the bake's penumbra ever needs to match the live one.
        Gl.Uniform4f(_bCubeSoft, AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, 0f, 0f);
        Gl.Uniform1f(_bCubeFloor, AlloyLights.ShadowFloor);
        Gl.Uniform4f(_bCubePcss, 0f, 0f, Shadows.Taps.V, 0f);
        float one = 1f, zero = 0f;
        Gl.Uniform1fv(_bCubeDynPage, 1, &zero);
        Gl.Uniform1fv(_bCubeFade, 1, &one);
        Gl.BindVertexArray(MeshChart.Vao);
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            if (Touches(l, r.C, r.R)) DrawRect(_bakeRects[k]);
        }
        Gl.BindVertexArray(0);
        Gl.DepthFunc(Gl.LEQUAL);   // every later pass assumes LEQUAL (#364)
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // the frame's own cube array back on the unit the lit programs expect it on
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (q) _qBake.End(Gl.TIME_ELAPSED);
    }

    /// <summary>The page's final pass: normalise B, dilate two texels through the coverage alpha, convert
    /// into the resident layer -- only where the scratch says a bake happened, so cache-hit rects already on
    /// the page (scratch alpha 0) are left alone. Then the readback of what landed, and the records.</summary>
    private static void FinishPage()
    {
        int page = _bakePage;
        AttachPair(page);
        // the finish and the readback cover the union of what landed (+ the dilation margin), never the page
        int x0 = PageSize, y0 = PageSize, x1 = 0, y1 = 0;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            x0 = Math.Min(x0, r.X - Gutter); y0 = Math.Min(y0, r.Y - Gutter);
            x1 = Math.Max(x1, r.X + r.W + Gutter); y1 = Math.Max(y1, r.Y + r.H + Gutter);
        }
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, PageSize); y1 = Math.Min(y1, PageSize);
        new PassState(depth: false, depthWrite: false, cull: false, scissor: true, srgb: false).Apply();
        Gl.Scissor(x0, y0, x1 - x0, y1 - y0);
        Gl.UseProgram(_progFinish);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, _scrA);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _scrB);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_fA, 0); Gl.Uniform1i(_fB, 1);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        // the readback: the union's raw bits into one PBO, fenced; CollectReadback writes the files. A previous
        // page's fence still open is polled, never waited on: the render thread does not stall for a cache
        // file, and a page landing that close behind another goes uncached (a miss next session, nothing else).
        if (_fence != IntPtr.Zero) CollectReadback();
        if (_fence == IntPtr.Zero && Gl.FenceSync != null && Gl.MapBufferRange != null) QueueReadback(x0, y0, x1 - x0, y1 - y0);
        else _uncached += _bakeRects.Count;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        for (int k = 0; k < _bakeRects.Count; k++) { Land(_bakeRects[k], false); _pending--; }
        double ms = (Stopwatch.GetTimestamp() - _pageT0) * 1000.0 / Stopwatch.Frequency;
        _pageMs += ms; _pageMsN++;
        if (_logLeft-- > 0) _log?.Info($"LightmapBake: page {page} landed -- {_bakeRects.Count} rects over {_bakeLights.Count} lights in {ms:F0} ms wall" + (_qBake.Measured ? $", {_qBake.Avg / 1e6:F2} ms GPU per light step" : ""));
        AbortBake();
    }

    private static void QueueReadback(int x0, int y0, int w, int h)
    {
        int bytes = w * h * 8;
        if (bytes <= 0 || _bakeRects.Count == 0) return;
        if (_pbo == 0) { uint b; Gl.GenBuffers(1, &b); _pbo = b; if (b == 0) return; }
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo);
        if (bytes > _pboBytes) { Gl.BufferData(Gl.PIXEL_PACK_BUFFER, (IntPtr)bytes, null, Gl.STREAM_READ); _pboBytes = bytes; }
        Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
        Gl.ReadPixels(x0, y0, w, h, Gl.RGB, Gl.UNSIGNED_INT_10F_11F_11F_REV, null);
        Gl.ReadBuffer(Gl.COLOR_ATTACHMENT1);
        Gl.ReadPixels(x0, y0, w, h, Gl.RGBA, Gl.UNSIGNED_BYTE, (void*)((long)w * h * 4));
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        _ux0 = x0; _uy0 = y0; _uw = w; _uh = h;
        _pendingRead.Clear(); _pendingRead.AddRange(_bakeRects);
        _fence = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
    }

    /// <summary>A fenced readback that has completed is mapped once, sliced per rect and handed to the cache
    /// thread. Polled, never waited on.</summary>
    private static void CollectReadback()
    {
        if (_fence == IntPtr.Zero) return;
        uint st = Gl.ClientWaitSync(_fence, 0, 0);
        if (st != Gl.ALREADY_SIGNALED && st != Gl.CONDITION_SATISFIED) return;
        Gl.DeleteSync(_fence); _fence = IntPtr.Zero;
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo);
        long total = (long)_uw * _uh * 8;
        byte* p = (byte*)Gl.MapBufferRange(Gl.PIXEL_PACK_BUFFER, IntPtr.Zero, (IntPtr)total, Gl.MAP_READ_BIT);
        if (p != null)
        {
            int b = -1;
            for (int i = 0; i < ReadBufs && b < 0; i++) if (_cache.Busy[i] == 0) b = i;
            if (b < 0) { _uncached += _pendingRead.Count; Gl.UnmapBuffer(Gl.PIXEL_PACK_BUFFER); }
            else
            {
                if (_readBufs[b] == null || _readBufs[b]!.Length < total) _readBufs[b] = new byte[Math.Max(total, 1 << 20)];
                fixed (byte* d = _readBufs[b]) Buffer.MemoryCopy(p, d, total, total);
                Gl.UnmapBuffer(Gl.PIXEL_PACK_BUFFER);
                var table = new BakeCache.Entry[_pendingRead.Count];   // cold: one per page bake
                for (int k = 0; k < _pendingRead.Count; k++)
                {
                    ref Rect r = ref _rects[_pendingRead[k]];
                    table[k] = new BakeCache.Entry { Key = r.Key, X = r.X - _ux0, Y = r.Y - _uy0, W = r.W, H = r.H };
                }
                _cache.Save(_readBufs[b]!, b, _uw, _uh, table);
            }
        }
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        _pendingRead.Clear();
    }

    /// <summary>Once per area, on its first rect: every term of the key, so two sessions' logs say which one
    /// moved when the second misses the first's files (0 of 7426 hit, run 6, with 18640 v6 files on disk).</summary>
    private static bool _keyLogged;
    private static void LogKeySample(ulong content, in Matrix4x4 m, Vector3 c, float r, ulong key)
    {
        if (_keyLogged) return;
        _keyLogged = true;
        int n = 0; for (int i = 0; i < _setN; i++) if (Touches(i, c, r)) n++;
        Vector2 at = AlloyLights.Atten;
        _log?.Info($"LightmapBake: key sample -- content {content:x16} at ({c.X:F2},{c.Y:F2},{c.Z:F2}) r {r:F2} m41 ({m.M41:F2},{m.M42:F2},{m.M43:F2}) density {Lightmap.Density.V}"
                   + $" touching {n} of {_setN} static lights = {TouchingLights(c, r):x16}, look {ShadowLook():x16} (cube {CasterPass.CubeFaceSize}, soft {AlloyLights.ShadowSoft:F3}, floor {AlloyLights.ShadowFloor:F4}, near {AlloyLights.ShadowNearWorld:F3}, atten {at.X:F4}/{at.Y:F4}, reach {AlloyLights.ReachUniform:F3})"
                   + $" -> key {key:x16}");
    }

    /// <summary>Cache answers: a hit is uploaded straight out of its blob's union (row-strided, no copy) into
    /// the resident pair at the packed offset and lands; a miss queues the rect for the bake.
    /// Hits uploaded per frame, in bytes: 1282 re-keyed rects answered in one frame was a 795 ms frame of
    /// TexSubImage3D (run 21). Under the hold the backdrop hides the hitch, so a page lands in four frames.</summary>
    private const long AdoptBytesPerFrame = 8L << 20, HoldAdoptBytesPerFrame = 32L << 20;
    private static void Adopt()
    {
        long uploaded = 0, budget = Hold ? HoldAdoptBytesPerFrame : AdoptBytesPerFrame;
        bool strided = false;
        while (uploaded < budget && _cache.TryTake(out int ri, out ulong key, out byte[]? data, out int x, out int y, out int uw, out int uh))
        {
            // the key guards a stale answer: a Revoke between the probe and the answer re-numbers the rects
            if (ri >= _rectN || _rects[ri].State != RState.Probing || _rects[ri].Key != key) continue;
            ref Rect r = ref _rects[ri];
            if (data == null) { r.State = RState.Queued; _queuedAt = _frame; continue; }
            uploaded += (long)r.W * r.H * 8;
            if (!strided) { strided = true; Gl.ActiveTexture(Gl.TEXTURE0); }
            Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, uw);
            fixed (byte* p = data)
            {
                Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texA);
                Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, r.X, r.Y, r.Page, r.W, r.H, 1, Gl.RGB, Gl.UNSIGNED_INT_10F_11F_11F_REV, p + UnionOffset(0, x, y, uw, uh));
                Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texB);
                Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, r.X, r.Y, r.Page, r.W, r.H, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, p + UnionOffset(1, x, y, uw, uh));
            }
            Land(ri, true); _pending--;
        }
        if (strided) { Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, 0); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0); }
    }

    /// <summary>Grow the resident pair by one layer: a new array, the old layers copied across, the old
    /// names deleted. Cold: at most <see cref="MaxPages"/> times a session.</summary>
    private static bool AddLayer() => Grow(_layers + 1);

    private static bool Grow(int n)
    {
        uint a = PageArray(Gl.R11F_G11F_B10F, n), b = PageArray(Gl.RGBA8, n);
        if (a == 0 || b == 0)
        {
            if (a != 0) Gl.DeleteTextures(1, &a);
            if (b != 0) Gl.DeleteTextures(1, &b);
            return _down.Refuse(_log, $"page {n} could not be allocated (out of video memory?)", "the rest of the area stays unbaked");
        }
        if (_layers > 0 && Gl.CopyImageSubData != null)
        {
            Gl.CopyImageSubData(_texA, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, a, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, PageSize, PageSize, _layers);
            Gl.CopyImageSubData(_texB, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, b, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, 0, PageSize, PageSize, _layers);
        }
        if (_texA != 0) { uint t = _texA; Gl.DeleteTextures(1, &t); }
        if (_texB != 0) { uint t = _texB; Gl.DeleteTextures(1, &t); }
        int from = _layers;
        _texA = a; _texB = b; _layers = n;
        // a fresh layer is black + straight-up: an unbaked texel that somehow gets sampled reads as no light
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        float* black = stackalloc float[4] { 0f, 0f, 0f, 1f };
        float* up = stackalloc float[4] { 0.5f, 0.5f, 1f, 1f };
        for (int l = from; l < n; l++)
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _texA, 0, l);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, _texB, 0, l);
            Gl.ClearBufferfv(Gl.COLOR, 0, black); Gl.ClearBufferfv(Gl.COLOR, 1, up);
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _log?.Info($"LightmapBake: pages {from} -> {n}/{_cap} resident ({n * 128} MiB)");
        return true;
    }

    /// <summary>Free the pages: after a <see cref="Repack"/> every texel on them is dead (each rect lands again),
    /// so keeping them only cost the card the memory and <see cref="Grow"/> the copy of that dead content.
    /// The next Pack allocates what the area needs in one go.</summary>
    private static void DropPages()
    {
        if (_texA != 0) { uint t = _texA; Gl.DeleteTextures(1, &t); }
        if (_texB != 0) { uint t = _texB; Gl.DeleteTextures(1, &t); }
        if (_layers > 0) _log?.Info($"LightmapBake: {_layers} pages freed ({_layers * 128} MiB)");
        _texA = _texB = 0; _layers = 0;
    }

    private static uint PageArray(uint format, int layers)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, t);
        Gl.DrainErrors();
        Gl.TexStorage3D(Gl.TEXTURE_2D_ARRAY, 1, format, PageSize, PageSize, layers);
        if (Gl.GetError() != 0) { Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0); Gl.DeleteTextures(1, &t); return 0; }
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        return t;
    }

    private static uint Scratch()
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.DrainErrors();
        Gl.TexStorage2D(Gl.TEXTURE_2D, 1, Gl.RGBA16F, PageSize, PageSize);
        if (Gl.GetError() != 0) { Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.DeleteTextures(1, &t); return 0; }
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        return t;
    }

    private static uint ScratchDepth()
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.DrainErrors();
        Gl.TexStorage2D(Gl.TEXTURE_2D, 1, Gl.DEPTH_COMPONENT16, PageSize, PageSize);
        if (Gl.GetError() != 0) { Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.DeleteTextures(1, &t); return 0; }
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        return t;
    }

    private static uint ScratchCube(int size)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, t);
        Gl.DrainErrors();
        Gl.TexStorage3D(Gl.TEXTURE_CUBE_MAP_ARRAY, 1, Gl.DEPTH_COMPONENT24, size, size, 6);
        if (Gl.GetError() != 0) { Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, 0); Gl.DeleteTextures(1, &t); return 0; }
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP_ARRAY, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, 0);
        return t;
    }

    /// <summary>Build once per context: entry points, texture units, the programs, the scratch pair, the FBO
    /// and the cube. Any refusal latches the pass off for the context with its reason.</summary>
    private static bool Ensure()
    {
        if (_down.Failed) return false;
        if (_built) return true;
        _built = true;
        if (Gl.TexStorage3D == null || Gl.TexSubImage3D == null || Gl.DrawElementsBaseVertex == null || Gl.DrawBuffers == null
            || Gl.ClearBufferfv == null || Gl.FramebufferTextureLayer == null || Gl.GenFramebuffers == null || Gl.ReadPixels == null)
            return _down.Fail(_log, "a GL entry point the bake needs is missing", "every surface keeps the real-time loop");
        int units = 0; Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
        if (units <= (int)UnitB)
            return _down.Fail(_log, $"{units} fragment texture units, the pages need {UnitB + 1}", "every surface keeps the real-time loop");
        _progBake = GlProgram.Graphics(BakeVs, BakeGs, BakeFs, _log, "LightmapBake bake program");
        _progFinish = GlProgram.Graphics(PostChain.FullscreenVs, FinishFs, _log, "LightmapBake finish program");
        if (_progBake == 0 || _progFinish == 0) return _down.Fail(_log, "the bake or finish program failed to link", "every surface keeps the real-time loop");
        _bSlot = GlProgram.Loc(_progBake, "uSlot"); _bRect = GlProgram.Loc(_progBake, "uRect"); _bDepth = GlProgram.Loc(_progBake, "uBakeDepth");
        _bLight = GlProgram.Loc(_progBake, "uBakeLight"); _bColor = GlProgram.Loc(_progBake, "uBakeColor"); _bMode = GlProgram.Loc(_progBake, "uBakeMode");
        _bCubeTex = GlProgram.Loc(_progBake, "uLocalShadow"); _bDynTex = GlProgram.Loc(_progBake, "uLocalDynShadow");
        _bCubeLight = GlProgram.Loc(_progBake, "uLocalLight"); _bCubePlane = GlProgram.Loc(_progBake, "uLocalPlane");
        _bCubeCount = GlProgram.Loc(_progBake, "uLocalCubes"); _bCubeSoft = GlProgram.Loc(_progBake, "uLocalSoft");
        _bCubeFloor = GlProgram.Loc(_progBake, "uLocalFloor"); _bCubePcss = GlProgram.Loc(_progBake, "uLocalPcss");
        _bCubeDynPage = GlProgram.Loc(_progBake, "uLocalDynPage"); _bCubeFade = GlProgram.Loc(_progBake, "uLocalFade"); _bSoftWorld = GlProgram.Loc(_progBake, "uLocalSoftWorld");
        _bFalloff = GlProgram.Loc(_progBake, "uFalloff"); _bWrap = GlProgram.Loc(_progBake, "uWrap"); _bAtten = GlProgram.Loc(_progBake, "uAtten");
        _bHot = GlProgram.Loc(_progBake, "uHot"); _bWrapN = GlProgram.Loc(_progBake, "uWrapN"); _bSss = GlProgram.Loc(_progBake, "uSss");
        _bReach = GlProgram.Loc(_progBake, "uReach"); _bGrid = GlProgram.Loc(_progBake, "uGrid"); _bTile = GlProgram.Loc(_progBake, "uTile"); _bZ = GlProgram.Loc(_progBake, "uZ");
        _fA = GlProgram.Loc(_progFinish, "uScrA"); _fB = GlProgram.Loc(_progFinish, "uScrB");
        uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        _scrA = Scratch(); _scrB = Scratch(); _scrZ = ScratchDepth();
        _cubeSize = Math.Max(CasterPass.CubeFaceSize, 64);
        _cube = ScratchCube(_cubeSize);
        if (_fbo == 0 || _scrA == 0 || _scrB == 0 || _scrZ == 0 || _cube == 0)
            return _down.Fail(_log, "the scratch pages or cube could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        _log?.Info($"LightmapBake: armed -- page cap {PageCap()}, scratch cube {_cubeSize}, {(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame)} light steps per frame");
        return true;
    }

    /// <summary>The world edge: every rect, record and page assignment describes an area that is gone. The
    /// GL names stay (the pages are re-packed from zero), the cache thread's queue is drained.</summary>
    internal static void Revoke()
    {
        DropHold();
        _areaHalve = 0; _refineAt = 0;
        Repack();
        _landed = _landedHits = _refused = _pastCap = _bakeSkips = _uncached = 0; _pageMs = 0; _pageMsN = 0;
        _logLeft = LogCap; _knownLogged = false; _keyLogged = false;
        SetHash = 0; _setN = 0; _stN = 0; _seen.Clear(); _seenPtr.Clear(); Reopen(); _edgeSeen = false; _rekeyPending = false;
        VoidClasses();
    }

    /// <summary>The context is gone: forget every name; the next frame rebuilds and re-packs.</summary>
    internal static void ContextLost()
    {
        _texA = _texB = _scrA = _scrB = _scrZ = _fbo = _cube = _pbo = _progBake = _progFinish = _bufChart = 0;
        _layers = 0; _pboBytes = 0; _built = false; _bufFilled = false; _fence = IntPtr.Zero; _qBake = default;
        _down.Forget();
        Revoke();
    }

    internal static string Describe()
        => !Lightmap.G.Master!.V ? "" : _down.Failed ? $"bake FAILED: {_down.Reason}"
         : $"bake: pages {_layers}/{_cap}, {_landed} rects landed ({_landedHits} from cache), {_pending} pending, {_pastCap} past the cap; "
           + $"static lights {CensusStatic} (tile {CensusTile}, placeable {CensusPlaceable}) animated {CensusAnimated} dynamic {CensusDynamic}";

    /// <summary>One clause of the Alloy period line.</summary>
    internal static string Stats(int frames)
    {
        if (!Enabled || (_rectN == 0 && CensusStatic == 0)) return "";
        frames = Math.Max(frames, 1);
        string s = $"lightmap pages {_layers}/{_cap}, bakes {_landed} landed ({_landedHits} cached) / {_pending} pending / {_refused + _pastCap} refused"
                   + (_pastCap > 0 ? $" ({_pastCap} past the cap)" : "") + $", static {CensusStatic} of {CensusStatic + CensusDynamic} lights skipped on baked surfaces"
                   + (_pageMsN > 0 ? $", {_pageMs / _pageMsN:F0} ms wall per page" : "")
                   + (_qBake.Measured ? $", {_qBake.Avg / 1e6:F2} ms GPU per light step (~{_qBake.Avg / 1e6 * _bakeSteps / Math.Max(_pageMsN, 1):F0} ms per page)" : "")
                   + (_bakeSkips > 0 ? $", {_bakeSkips} steps skipped (no gather)" : "")
                   + (_cache.Stale > 0 ? $", {_cache.Stale} stale bake blobs dropped" : "")
                   + (_uncached > 0 ? $", {_uncached} landed uncached (readback busy)" : "")
                   + TierStats()
                   + $", charted draws/frame {_drewBaked / frames} baked / {_drewNoRect / frames} no rect / {_drewPending / frames} pending / {_drewRefused / frames} refused"
                   + (_drewLandedUnbaked > 0 ? $" / {_drewLandedUnbaked / frames} LANDED BUT UNBAKED" : "")
                   + $", census static {CensusStatic} (tile {CensusTile}, placeable {CensusPlaceable}) animated {CensusAnimated} dynamic {CensusDynamic} skipped {CensusSkipped}"
                   + $", alloc bake {_allocRun >> 20} MiB / cache thread {_cache.Allocated >> 20} MiB" 
                   + (_clsFull > 0 ? $", {_clsFull} lights past the class table" : "")
                   + (_cslEdges > 0 ? $", {_cslEdges} ComputeStaticLighting edges" : "");
        _drewBaked = _drewNoRect = _drewRefused = _drewPending = _drewLandedUnbaked = 0;
        _qBake.Reset(); _bakeSkips = 0; _allocRun = 0; _evicted = 0; _cache.Allocated = 0; _cslEdges = 0; _bakeSteps = 0; _pageMs = 0; _pageMsN = 0;
        return s;
    }

    /// <summary>#679: landed rects per tier and the pages of each class. Cold: once a period.</summary>
    private static string TierStats()
    {
        if (Baseline == 0) return "";
        Span<int> per = stackalloc int[MaxTiers]; per.Clear();
        for (int ri = 0; ri < _rectN; ri++) if (_rects[ri].State == RState.Landed) per[_rects[ri].Tier]++;
        int fine = 0, basePages = 0;
        for (int p = 0; p < _layers; p++) { if (_pageClass[p] == ClassFine) fine++; else if (_pageClass[p] == ClassBase) basePages++; }
        var sb = new System.Text.StringBuilder(", tiers");
        for (int t = 0; t <= Baseline; t++) sb.Append(t == 0 ? " " : " / ").Append(per[t]);
        sb.Append($" rects landed, pages {basePages} baseline + {fine} fine");
        if (_evicted > 0) sb.Append($", {_evicted} evicted");
        return sb.ToString();
    }

    /// <summary>Headless: the packer keeps rects apart by the gutter and inside the page, refuses past the
    /// cap, and the bake key moves with what it must (placement, density, a touching light) and only that.
    /// #679: the tiers double out to the baseline, the two classes never share a page, an evicted fine page
    /// is the next fine rect's, and a re-halved key is the key a fresh enrol computes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(ChartBufDecl.Contains($"binding = {BindChart}") && ChartBufDecl.Contains($"binding = {UnitA}") && ChartBufDecl.Contains($"binding = {UnitB}"),
          "the GLSL bindings drifted from BindChart/UnitA/UnitB");
        check.Expect(ChartBufDecl.Contains($"CX_LM_PAGE = {PageSize}.0") && BakeVs.Contains($"CX_LM_PAGE = {PageSize}.0") && BakeGs.Contains($"CX_LM_PAGE = {PageSize}.0"), "the GLSL page size drifted from PageSize");
        check.Expect(BakeFs.Contains("CxPointLight(") && BakeFs.Contains("CxPointDiffuse(") && BakeFs.Contains("CxLocalShadow("),
          "the bake stage does not run the lit stage's own per-light body");
        // the packer, on a scratch page state
        int cap = _cap, layers = _layers, page = _packPage;
        var sy = (int[])_shelfY.Clone(); var sh = (int[])_shelfH.Clone(); var sx = (int[])_shelfX.Clone();
        _cap = 1; _layers = 1; _packPage = 0; Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX);
        var a = new Rect { W = 100, H = 50 }; var b = new Rect { W = 100, H = 40 }; var c = new Rect { W = 4000, H = 60 };
        check.Expect(Place(ref a) && Place(ref b), "two rects fit an empty page");
        check.Expect(a.Page == 0 && b.Page == 0 && b.X >= a.X + a.W + Gutter && a.Y == Gutter, "the second rect sits past the first by the gutter, on the first shelf");
        check.Expect(Place(ref c) && c.Y >= a.Y + a.H + Gutter, "a rect too wide for the shelf opens a new shelf below it");
        var big = new Rect { W = 4000, H = 4000 };
        check.Expect(!Place(ref big), "a rect past the last page under the cap is refused, not placed");
        float near = Lightmap.Near.V;
        check.Expect(DesiredTier(0f) == 0 && DesiredTier(near * 100f) == Baseline && (Baseline < 1 || DesiredTier(near * 1.5f) == 1), "the tier doubles out from lightmap.near to the baseline");
        if (Baseline > 0 && _rectN == 0)
        {
            _cap = 2; _layers = 2; _packPage = 0; _finePage = 0; Array.Clear(_pageClass);
            int rb = NewRect(), rf = NewRect();
            _rects[rb] = new Rect { Key = 1, W = 100, H = 50, Tier = Baseline, Page = -1 }; _rects[rf] = new Rect { Key = 2, W = 100, H = 50, Tier = 0, Page = -1, C = new Vector3(near * 1.5f, 0f, 0f) };
            _byKey[1] = rb; _byKey[2] = rf;
            check.Expect(Place(ref _rects[rb]) && Place(ref _rects[rf]) && _rects[rb].Page != _rects[rf].Page, "a baseline rect and a fine rect open pages of their own");
            _rects[rf].State = RState.Landed;
            var fill = new Rect { W = 4000, H = 4080, Tier = 0 };
            check.Expect(!Place(ref fill), "a fine rect past the cap is refused before eviction");
            int fp = _rects[rf].Page;
            check.Expect(EvictFine(1f) < 0, "a page inside the next tier's radius is never evicted (the hysteresis)");
            _rects[rf].C = new Vector3(near * 8f, 0f, 0f);
            check.Expect(EvictFine(1f) == fp && Place(ref fill) && fill.Page == fp, "the fine page past its tier is evicted and re-opened for the next fine rect");
            check.Expect(_rects[rf].State == RState.PastCap && !_byKey.ContainsKey(2) && _free.Count == 1, "the evicted rect is dead, unkeyed and its entry free for reuse");
            check.Expect(Halve(Halve(7, 0), 2) == Halve(7, 2) && Halve(7, 2) != Halve(7, 3) && Halve(7, 0) == 7, "a re-halved key is the key a fresh enrol computes");
            _rectN = 0; _byKey.Clear(); _free.Clear(); Array.Clear(_pageClass); _finePage = 0;
        }
        _cap = cap; _layers = layers; _packPage = page; Array.Copy(sy, _shelfY, sy.Length); Array.Copy(sh, _shelfH, sh.Length); Array.Copy(sx, _shelfX, sx.Length);
        // the key
        int setN = _setN;
        if (_setPosR.Length < 1) { _setPosR = new Vector4[1]; _setCol = new Vector3[1]; }
        _setPosR[0] = new Vector4(0f, 0f, 0f, 10f); _setCol[0] = Vector3.One; _setN = 1;
        ulong k0 = BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f);
        check.Expect(k0 == BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "the key is deterministic");
        check.Expect(k0 != BakeKey(2, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "a different chart is a different key");
        check.Expect(k0 != BakeKey(1, Matrix4x4.CreateTranslation(0.5f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f), "a moved placement is a different key");
        check.Expect(k0 == BakeKey(1, Matrix4x4.CreateTranslation(0.001f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f), "a millimetre of placement is the same key (1 cm quantum)");
        check.Expect(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(50f, 0f, 0f), 1f), "out of the light's reach is a different key");
        _setCol[0] = new Vector3(0.5f, 1f, 1f);
        check.Expect(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "a touching light's colour is in the key");
        _setN = setN;
        log.Info(check.Ok ? "LightmapBake.SelfCheck: passed" : "LightmapBake.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
