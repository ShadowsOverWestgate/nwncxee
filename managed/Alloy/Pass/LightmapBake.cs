using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#674: the lightmap bake and its read. Every charted static instance (#673) gets a rectangle on a
/// 4096x4096 page pair -- <b>A</b> irradiance (RG11B10F) and <b>B</b> dominant direction (RGBA8) -- baked on
/// the GPU page-major, light-major: per (page, light) the light's shadow is rendered from the STATIC set into
/// one scratch cube (<see cref="CasterPass.BakeStaticCube"/>), then every to-bake rect the light reaches is
/// drawn in UV space with the colour pass's own per-light body (<c>CxPointLight</c>) accumulating into two
/// RGBA16F scratch pages. A page's final pass normalises B, dilates two texels through the coverage alpha and
/// converts into the resident pair. The lit stage reads the per-slot bake record (<see cref="ChartBufDecl"/>)
/// and skips every static light for a baked surface (#668).
/// <para>Bake key = chart content + density + the placement (1 cm / 1e-3) + the touching static lights (5 cm,
/// 1/255) + the shadow look (#666); one deflated raw-bits file per key in <c>cx-charts/</c>
/// (<c>LightmapBake.Cache.cs</c>); a hit is a <c>glTexSubImage3D</c> at the packed offset and never queues.
/// Pacing: <see cref="HoldLightsPerFrame"/> (page, light) steps per frame under the loading-screen hold
/// (#675 sets <see cref="Hold"/>), <see cref="BackgroundLightsPerFrame"/> otherwise.</para>
/// <para>Refusals stand a part, a page or the area down with a logged reason and never a frame verdict: a
/// missing entry point or a failed link latches the pass off (every record stays unbaked, which IS the
/// real-time loop); a rect past the page cap stays unbaked and is counted.</para>
/// <para>ponytail: rects are never freed inside an area (Revoke rewinds the pages); an instance whose key
/// moves takes a NEW rect and the old one idles until the world edge. Page space is a session's, not a key's.</para></summary>
internal static unsafe partial class LightmapBake
{
    internal const int PageSize = 4096;
    internal const int MaxPages = 16;
    /// <summary>A rect's side is capped at a quarter page (#656): the density scales down past it.</summary>
    private const int MaxRect = PageSize / 2;
    /// <summary>Texels between packed rects: the dilation reaches two, so two rects never bleed.</summary>
    private const int Gutter = 2;
    private const int HoldLightsPerFrame = 32, BackgroundLightsPerFrame = 2;
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
    /// <summary>The CxChartBuf mirror: (page, u0, v0, s) per slot, -1 page = unbaked. Uploaded by dirty range.</summary>
    private static readonly float[] _rec = new float[AlloyRenderer.MaxOwned * 4];
    private static int _dirtyLo = int.MaxValue, _dirtyHi = -1;
    private static uint _bufChart;
    private static bool _bufFilled;

    private enum RState : byte { Packing, Probing, Queued, Landed, PastCap }

    /// <summary>One bake key's rectangle: where it sits, whose geometry draws it, what it touches.</summary>
    private struct Rect
    {
        internal ulong Key;
        internal int Slot, Vao, Item;
        internal int Page, X, Y, W, H;
        internal float S;
        internal Vector3 C; internal float R;
        internal RState State;
        internal ulong Content;
        internal int Halved;
    }
    private static Rect[] _rects = new Rect[1024];
    /// <summary>Per rect, the slots reading it -- what a landing writes records for. Small (a placement is
    /// usually one instance); a vacated slot is removed, so nothing stale is ever walked.</summary>
    private static List<int>?[] _owners = new List<int>?[1024];
    private static int _rectN;
    private static readonly Dictionary<ulong, int> _byKey = new();
    private static readonly List<int> _toPack = new();

    /// <summary>The shelf packer, per page: the open shelf's top, height and cursor.</summary>
    private static readonly int[] _shelfY = new int[MaxPages], _shelfH = new int[MaxPages], _shelfX = new int[MaxPages];
    private static int _packPage;
    private static int _layers;
    private static int _cap;

    private static uint _texA, _texB, _scrA, _scrB, _fbo, _cube, _pbo;
    private static int _cubeSize;
    private static uint _progBake, _progFinish;
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
    private static byte[] _readBuf = Array.Empty<byte>();
    private static long _pageT0;
    private static GpuQuery _qBake;

    /// <summary>Period counters.</summary>
    private static int _landed, _landedHits, _pending, _refused, _pastCap, _bakeSkips, _uncached, _logLeft = LogCap;
    private static double _pageMs, _pageMsN;
    private static bool _rekeyPending;
    private static int _frame, _queuedAt, _newAt;
    private static bool _repacked;
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
    internal const string ChartBufDecl = """
        // #674: the bake record per ownership slot -- (page, u0, v0, s): page layer (-1 = unbaked), the rect's
        // origin in page texels, page texels per atlas texel. Written on landing/enrol/vacate edges, never per frame.
        layout(std430, binding = 6) readonly buffer CxChartBuf { vec4 uChart[]; };
        layout(binding = 20) uniform sampler2DArray uLmA;   // irradiance, RG11B10F
        layout(binding = 21) uniform sampler2DArray uLmB;   // dominant direction, RGBA8 as xyz * 0.5 + 0.5
        uniform int uLmPages;                                // resident layers; 0 = no lightmap this frame
        const float CX_LM_PAGE = 4096.0;
        // The read: false = unbaked (dynamic instance, refused mesh, bake in flight, past the page cap).
        bool CxLightmap(vec4 rec, vec2 chart, out vec3 irr, out vec3 dir)
        {
            irr = vec3(0.0); dir = vec3(0.0, 0.0, 1.0);
            if (rec.x < 0.0 || uLmPages <= 0) return false;
            vec3 uv = vec3((rec.yz + chart * rec.w) / CX_LM_PAGE, rec.x);
            irr = texture(uLmA, uv).rgb;
            vec3 b = texture(uLmB, uv).xyz * 2.0 - 1.0;
            float l = length(b);
            if (l > 1e-4) dir = b / l;
            return true;
        }
        // Unity's directional decode: the irradiance was baked against the VERTEX normal Ng; the shading
        // normal N (a normal map) rebalances it, so bump survives under static light (#666).
        vec3 CxLightmapLit(vec3 irr, vec3 dir, vec3 N, vec3 Ng)
        {
            return irr * max(dot(N, dir), 0.0) / max(dot(Ng, dir), 0.1);
        }

        """;

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
        if (_slotRect[i] != -1 || !Enabled || SetHash == 0) return;
        if (!MeshChart.Atlas(vao, item, out int w, out int h, out ulong content, out Vector3 bmin, out Vector3 bmax)) return;
        if (!AlloyRenderer.SlotPlacement(i, out _, out _, out Matrix4x4 model)) return;
        // the instance's world sphere off the CHART's bounds: the engine's part bbox is unpopulated on tile
        // parts (min == max), and a light reaches what the mesh actually spans
        float sx = new Vector3(model.M11, model.M12, model.M13).Length();
        Vector3 c = Vector3.Transform((bmin + bmax) * 0.5f, model);
        float r = (bmax - bmin).Length() * 0.5f * MathF.Max(sx, 1e-3f);
        float s = Lightmap.Density.V / MeshChart.AtlasTexelsPerUnit * MathF.Max(sx, 1e-3f);
        int rw = (int)MathF.Ceiling(w * s), rh = (int)MathF.Ceiling(h * s);
        if (rw > MaxRect || rh > MaxRect) { s *= (float)MaxRect / Math.Max(rw, rh); rw = (int)MathF.Ceiling(w * s); rh = (int)MathF.Ceiling(h * s); }
        if (rw <= 0 || rh <= 0) { _slotRect[i] = -2; return; }
        ulong key = BakeKey(content, model, c, r);
        if (_byKey.TryGetValue(key, out int ri))
        {
            _slotRect[i] = ri;
            (_owners[ri] ??= new List<int>(2)).Add(i);
            if (_rects[ri].Slot < 0) _rects[ri].Slot = i;
            if (_rects[ri].State == RState.Landed) WriteRecord(i, ri);
            return;
        }
        if (_rectN == MaxRects) { _slotRect[i] = -2; _refused++; return; }
        if (_rectN == _rects.Length) { Array.Resize(ref _rects, _rectN * 2); Array.Resize(ref _owners, _rectN * 2); }
        ri = _rectN++;
        _rects[ri] = new Rect { Key = key, Slot = i, Vao = vao, Item = item, Page = -1, W = rw, H = rh, S = s, C = c, R = r, State = RState.Packing, Content = content };
        (_owners[ri] ??= new List<int>(2)).Clear();
        _owners[ri]!.Add(i);
        _byKey[key] = ri;
        _slotRect[i] = ri;
        _toPack.Add(ri); _newAt = _frame;
    }

    /// <summary>A slot was vacated (or the table wiped): its record reads unbaked from the next upload.</summary>
    internal static void Vacate(int i)
    {
        if ((uint)i >= (uint)_slotRect.Length) return;
        int ri = _slotRect[i];
        _slotRect[i] = -1;
        if (ri >= 0) Disown(ri, i);
        if (_rec[i * 4] >= 0f) SetRecord(i, -1f, 0f, 0f, 0f);
    }

    /// <summary>Retire a rect that cannot be drawn: its readers forget it and enrol again next frame.</summary>
    private static void Drop(int ri)
    {
        ref Rect r = ref _rects[ri];
        r.State = RState.PastCap; _pending--; _refused++;
        _byKey.Remove(r.Key);
        var o = _owners[ri];
        if (o != null) { for (int k = 0; k < o.Count; k++) _slotRect[o[k]] = -1; o.Clear(); }
        r.Slot = -1;
    }

    private static void Disown(int ri, int i)
    {
        var o = _owners[ri];
        if (o != null) o.Remove(i);
        if (_rects[ri].Slot == i) _rects[ri].Slot = o != null && o.Count > 0 ? o[0] : -1;
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
        int changed = 0;
        for (int i = 0; i < _slotRect.Length; i++)
        {
            if (_slotRect[i] < 0) { if (_slotRect[i] == -2) _slotRect[i] = -1; continue; }
            int ri = _slotRect[i];
            _slotRect[i] = -1;
            Disown(ri, i);
            if (!AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) continue;
            Enrol(i, vao, item);
            // an unchanged key keeps its page and its record: the surface never flips to the live loop
            if (_slotRect[i] != ri) { changed++; if (_rec[i * 4] >= 0f) SetRecord(i, -1f, 0f, 0f, 0f); }
        }
        if (_logLeft > 0) _log?.Info($"LightmapBake: static set re-keyed (fingerprint {SetHash:x16}) -- {changed} instances re-bake");
    }

    /// <summary>Forget every rect and the packing; the resident pages stay and are overwritten as rects land.</summary>
    private static void Repack()
    {
        Array.Fill(_slotRect, -1);
        for (int i = 0; i < _rec.Length; i += 4) if (_rec[i] >= 0f) { _rec[i] = -1f; _dirtyLo = 0; _dirtyHi = _slotRect.Length - 1; }
        _rectN = 0; _byKey.Clear(); _toPack.Clear(); _pending = 0;
        Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX); _packPage = 0;
        AbortBake();
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

    /// <summary>Every dial the bake stage reads: a change to any of them is a different look, so a different key.</summary>
    private static ulong ShadowLook()
    {
        ulong h = FnvOffset;
        h = Mix(h, CasterPass.CubeFaceSize);
        h = Mix(h, Q(AlloyLights.ShadowSoft, 0.01f)); h = Mix(h, Q(AlloyLights.ShadowFloor, 1e-3f));
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
        int o = i * 4;
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
                Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)_dirtyLo * 16), (IntPtr)((long)(_dirtyHi - _dirtyLo + 1) * 16), p + (long)_dirtyLo * 4);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _dirtyLo = int.MaxValue; _dirtyHi = -1;
    }

    /// <summary>The page cap: <c>lightmap.pages</c>, or 0 = an eighth of the card's memory in 128 MiB pages,
    /// 1..8; 2 when the device answers no memory query (#668).</summary>
    private static int PageCap()
    {
        int k = Lightmap.Pages.V;
        if (k > 0) return Math.Min(k, MaxPages);
        long mib = AlloyRenderer.VramTotalMib > 0 ? AlloyRenderer.VramTotalMib : AlloyRenderer.VramFreeMib;
        if (mib <= 0) return 2;
        return (int)Math.Clamp(mib / 8 / 128, 1, MaxPages);
    }

    /// <summary>Shelf-pack the new rects, tallest first (#668: so the cap drops the smallest last), once no
    /// newcomer has arrived for the settle window: an enrolment drains over many frames, and a shelf opened
    /// per frame's trickle packed an area's 3.7 pages of rects into 8.</summary>
    private static void Pack()
    {
        if (_toPack.Count == 0 || _frame - _newAt < (Hold ? HoldSettleFrames : SettleFrames)) return;
        _toPack.Sort((a, b) => _rects[b].H.CompareTo(_rects[a].H));
        for (int k = 0; k < _toPack.Count; k++)
        {
            int ri = _toPack[k];
            ref Rect r = ref _rects[ri];
            if (!Place(ref r))
            {
                // the pages are full of re-key orphans (a shelf cannot free): repack everything from nothing
                // once, and only then refuse. The slots re-enrol next frame and adopt their files from the cache.
                if (_rectN > _toPack.Count && !_repacked) { _repacked = true; Repack(); return; }
                // past the cap: half the density rather than no bake, down to a quarter (its own key: the file
                // is a different size). ponytail: the LAST rects packed halve, not the largest
                if (r.S > 0f && r.W > 4 && r.H > 4 && r.Halved < 2)
                {
                    _byKey.Remove(r.Key);
                    r.Halved++; r.S *= 0.5f; r.W = (r.W + 1) / 2; r.H = (r.H + 1) / 2; r.Key = Mix(r.Key, r.Halved);
                    _byKey[r.Key] = ri;
                    if (Place(ref r)) goto placed;
                }
                r.State = RState.PastCap; _pastCap++;
                continue;
            }
            placed:
            _repacked = false;
            r.State = RState.Probing; _pending++;
            _cache.Probe(ri, r.Key, r.W, r.H);
        }
        _toPack.Clear();
    }

    private static bool Place(ref Rect r)
    {
        while (true)
        {
            if (_packPage >= _cap) return false;
            if (_packPage >= _layers && !AddLayer()) return false;
            int p = _packPage;
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
            _packPage++;
        }
    }

    /// <summary>Once a frame, at the frame slot after the probe captures: adopt cache hits, pack newcomers,
    /// advance the bake by its pacing, collect readbacks and upload the records. Inside the colour bracket,
    /// with the shadow bracket's gather staged; the caller restores its target after.</summary>
    internal static void Run(bool capturing, int frame)
    {
        if (!Enabled || capturing) return;
        if (!Ensure()) return;
        _frame = frame;
        if (_rekeyPending) Rekey();
        Sweep();
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
        for (int k = 0; k < SweepPerFrame; k++)
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
            if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out _, out _, out _)) { Drop(ri); continue; }
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
        Gl.UseProgram(_progBake);
        Gl.Uniform1i(_bMode, 0);
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
        }
        else
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _texA, 0, layer);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, _texB, 0, layer);
        }
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        Gl.Viewport(0, 0, PageSize, PageSize);
    }

    private static void DrawRect(int ri)
    {
        ref Rect r = ref _rects[ri];
        if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out int baseVertex, out nint off, out int count)) return;
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
        Gl.UseProgram(_progBake);
        Gl.Uniform1i(_bMode, 1);
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
            // one copy of the union; the cache thread slices each rect out of it. A buffer that thread is
            // still reading from is left to it and a fresh one taken (rare: a page landing a frame apart)
            if (_readBuf.Length < total || _cache.Outstanding > 0) _readBuf = new byte[Math.Max(total, 1 << 20)];
            fixed (byte* d = _readBuf) Buffer.MemoryCopy(p, d, total, total);
            Gl.UnmapBuffer(Gl.PIXEL_PACK_BUFFER);
            for (int k = 0; k < _pendingRead.Count; k++)
            {
                ref Rect r = ref _rects[_pendingRead[k]];
                _cache.Save(r.Key, r.W, r.H, _readBuf, _ux0, _uy0, _uw, _uh, r.X, r.Y);
            }
        }
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        _pendingRead.Clear();
    }

    /// <summary>Cache answers: a hit is uploaded straight into the resident pair at the packed offset and
    /// lands; a miss queues the rect for the bake.</summary>
    private static void Adopt()
    {
        while (_cache.TryTake(out int ri, out ulong key, out byte[]? data))
        {
            // the key guards a stale answer: a Revoke between the probe and the answer re-numbers the rects
            if (ri >= _rectN || _rects[ri].State != RState.Probing || _rects[ri].Key != key) continue;
            ref Rect r = ref _rects[ri];
            if (data == null) { r.State = RState.Queued; _queuedAt = _frame; continue; }
            fixed (byte* p = data)
            {
                Gl.ActiveTexture(Gl.TEXTURE0);
                Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texA);
                Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, r.X, r.Y, r.Page, r.W, r.H, 1, Gl.RGB, Gl.UNSIGNED_INT_10F_11F_11F_REV, p);
                Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _texB);
                Gl.TexSubImage3D(Gl.TEXTURE_2D_ARRAY, 0, r.X, r.Y, r.Page, r.W, r.H, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, p + r.W * r.H * 4);
                Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
            }
            Land(ri, true); _pending--;
        }
    }

    /// <summary>Grow the resident pair by one layer: a new array, the old layers copied across, the old
    /// names deleted. Cold: at most <see cref="MaxPages"/> times a session.</summary>
    private static bool AddLayer()
    {
        int n = _layers + 1;
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
        _texA = a; _texB = b; _layers = n;
        // a fresh layer is black + straight-up: an unbaked texel that somehow gets sampled reads as no light
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _texA, 0, n - 1);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, _texB, 0, n - 1);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        float* black = stackalloc float[4] { 0f, 0f, 0f, 1f };
        float* up = stackalloc float[4] { 0.5f, 0.5f, 1f, 1f };
        Gl.ClearBufferfv(Gl.COLOR, 0, black); Gl.ClearBufferfv(Gl.COLOR, 1, up);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _log?.Info($"LightmapBake: page {n}/{_cap} resident ({n * 128} MiB)");
        return true;
    }

    private static uint PageArray(uint format, int layers)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, t);
        while (Gl.GetError() != 0) { }
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
        while (Gl.GetError() != 0) { }
        Gl.TexStorage2D(Gl.TEXTURE_2D, 1, Gl.RGBA16F, PageSize, PageSize);
        if (Gl.GetError() != 0) { Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.DeleteTextures(1, &t); return 0; }
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        return t;
    }

    private static uint ScratchCube(int size)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, t);
        while (Gl.GetError() != 0) { }
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
        _progBake = GlProgram.Graphics(BakeVs, BakeFs, _log, "LightmapBake bake program");
        _progFinish = GlProgram.Graphics(PostChain.FullscreenVs, FinishFs, _log, "LightmapBake finish program");
        if (_progBake == 0 || _progFinish == 0) return _down.Fail(_log, "the bake or finish program failed to link", "every surface keeps the real-time loop");
        _bSlot = GlProgram.Loc(_progBake, "uSlot"); _bRect = GlProgram.Loc(_progBake, "uRect");
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
        _scrA = Scratch(); _scrB = Scratch();
        _cubeSize = Math.Max(CasterPass.CubeFaceSize, 64);
        _cube = ScratchCube(_cubeSize);
        if (_fbo == 0 || _scrA == 0 || _scrB == 0 || _cube == 0)
            return _down.Fail(_log, "the scratch pages or cube could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        _log?.Info($"LightmapBake: armed -- page cap {PageCap()}, scratch cube {_cubeSize}, {(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame)} light steps per frame");
        return true;
    }

    /// <summary>The world edge: every rect, record and page assignment describes an area that is gone. The
    /// GL names stay (the pages are re-packed from zero), the cache thread's queue is drained.</summary>
    internal static void Revoke()
    {
        Repack();
        _landed = _landedHits = _refused = _pastCap = _bakeSkips = _uncached = 0; _pageMs = 0; _pageMsN = 0;
        _logLeft = LogCap; _knownLogged = false;
        SetHash = 0; _candHash = 0; _candFor = 0; _setN = 0; _stN = 0; _seen.Clear(); _rekeyPending = false;
        VoidClasses();
    }

    /// <summary>The context is gone: forget every name; the next frame rebuilds and re-packs.</summary>
    internal static void ContextLost()
    {
        _texA = _texB = _scrA = _scrB = _fbo = _cube = _pbo = _progBake = _progFinish = _bufChart = 0;
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
        string s = $"lightmap pages {_layers}/{_cap}, bakes {_landed} landed ({_landedHits} cached) / {_pending} pending / {_refused + _pastCap} refused"
                   + (_pastCap > 0 ? $" ({_pastCap} past the cap)" : "") + $", static {CensusStatic} of {CensusStatic + CensusDynamic} lights skipped on baked surfaces"
                   + (_pageMsN > 0 ? $", {_pageMs / _pageMsN:F0} ms wall per page" : "")
                   + (_qBake.Measured ? $", {_qBake.Avg / 1e6:F2} ms GPU per light step (~{_qBake.Avg / 1e6 * _bakeSteps / Math.Max(_pageMsN, 1):F0} ms per page)" : "")
                   + (_bakeSkips > 0 ? $", {_bakeSkips} steps skipped (no gather)" : "")
                   + (_cache.Stale > 0 ? $", {_cache.Stale} stale bake files replaced" : "")
                   + (_uncached > 0 ? $", {_uncached} landed uncached (readback busy)" : "")
                   + (_clsFull > 0 ? $", {_clsFull} lights past the class table" : "")
                   + (_cslEdges > 0 ? $", {_cslEdges} ComputeStaticLighting edges" : "");
        _qBake.Reset(); _bakeSkips = 0; _cslEdges = 0; _bakeSteps = 0; _pageMs = 0; _pageMsN = 0;
        return s;
    }

    /// <summary>Headless: the packer keeps rects apart by the gutter and inside the page, refuses past the
    /// cap, and the bake key moves with what it must (placement, density, a touching light) and only that.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"LightmapBake.SelfCheck: FAILED -- {w}"); } }
        E(ChartBufDecl.Contains($"binding = {BindChart}") && ChartBufDecl.Contains($"binding = {UnitA}") && ChartBufDecl.Contains($"binding = {UnitB}"),
          "the GLSL bindings drifted from BindChart/UnitA/UnitB");
        E(ChartBufDecl.Contains($"CX_LM_PAGE = {PageSize}.0") && BakeVs.Contains($"CX_LM_PAGE = {PageSize}.0"), "the GLSL page size drifted from PageSize");
        E(BakeFs.Contains("CxPointLight(") && BakeFs.Contains("CxPointDiffuse(") && BakeFs.Contains("CxLocalShadow("),
          "the bake stage does not run the lit stage's own per-light body");
        // the packer, on a scratch page state
        int cap = _cap, layers = _layers, page = _packPage;
        var sy = (int[])_shelfY.Clone(); var sh = (int[])_shelfH.Clone(); var sx = (int[])_shelfX.Clone();
        _cap = 1; _layers = 1; _packPage = 0; Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX);
        var a = new Rect { W = 100, H = 50 }; var b = new Rect { W = 100, H = 40 }; var c = new Rect { W = 4000, H = 60 };
        E(Place(ref a) && Place(ref b), "two rects fit an empty page");
        E(a.Page == 0 && b.Page == 0 && b.X >= a.X + a.W + Gutter && a.Y == Gutter, "the second rect sits past the first by the gutter, on the first shelf");
        E(Place(ref c) && c.Y >= a.Y + a.H + Gutter, "a rect too wide for the shelf opens a new shelf below it");
        var big = new Rect { W = 4000, H = 4000 };
        E(!Place(ref big), "a rect past the last page under the cap is refused, not placed");
        _cap = cap; _layers = layers; _packPage = page; Array.Copy(sy, _shelfY, sy.Length); Array.Copy(sh, _shelfH, sh.Length); Array.Copy(sx, _shelfX, sx.Length);
        // the key
        int setN = _setN;
        if (_setPosR.Length < 1) { _setPosR = new Vector4[1]; _setCol = new Vector3[1]; }
        _setPosR[0] = new Vector4(0f, 0f, 0f, 10f); _setCol[0] = Vector3.One; _setN = 1;
        ulong k0 = BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f);
        E(k0 == BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "the key is deterministic");
        E(k0 != BakeKey(2, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "a different chart is a different key");
        E(k0 != BakeKey(1, Matrix4x4.CreateTranslation(0.5f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f), "a moved placement is a different key");
        E(k0 == BakeKey(1, Matrix4x4.CreateTranslation(0.001f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f), "a millimetre of placement is the same key (1 cm quantum)");
        E(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(50f, 0f, 0f), 1f), "out of the light's reach is a different key");
        _setCol[0] = new Vector3(0.5f, 1f, 1f);
        E(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f), "a touching light's colour is in the key");
        _setN = setN;
        log.Info(ok ? "LightmapBake.SelfCheck: passed" : "LightmapBake.SelfCheck: FAILED -- see above");
        return ok;
    }
}
