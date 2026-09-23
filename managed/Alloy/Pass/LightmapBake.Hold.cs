using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Ui;

namespace CX.Alloy;

/// <summary>#675: the loading-screen hold. The engine's <c>SendPlayerToServerArea_AreaLoaded</c> is what places
/// the player (the server's <c>MovePlayerToArea</c> runs on nothing else, and no server clock runs while it
/// waits -- docs/research/area-loaded-hold.md); the hook swallows it, an opaque CX window covers the frames
/// until the area's baseline bakes are in, then the send goes out from here. Three exits: the bake is in
/// (<see cref="BaselineIn"/>), the player skips (Escape, offered after <c>lightmap.hold</c> s), or
/// <see cref="CapSeconds"/>. On a skip or the cap the bake finishes in the background at its own pacing.
/// <para>The server keeps streaming the destination area's objects to a client in this state (its per-player
/// update falls back to the creature's desired area: <c>UpdateClientGameObjectsForPlayer</c>), so the static
/// set closes under the hold with the placeables in it. Windows: the send symbol is anchored (build 1/4).</para></summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The hold's ceiling, whatever the knob says: SoW's PlayerReadiness gives a character up 60 s after activation.</summary>
    private const float CapSeconds = 45f;
    /// <summary>Slots the enrol sweep visits per frame under the hold: the whole table in ten frames, so "nothing
    /// left to enrol" is a ten-frame silence, not a two-second one.</summary>
    private const int HoldSweepPerFrame = 16384;
    /// <summary>Frames of silence (no close, no enrol, no chart landing) before the queue counts as drained.</summary>
    private static int QuietFrames => AlloyRenderer.MaxOwned / HoldSweepPerFrame + HoldSettleFrames + 2;
    private const uint KeyEscape = 27;   // SDLK_ESCAPE

    private static IntPtr _heldMsg;
    private static long _holdT0;
    private static bool _skipOpen;
    /// <summary>The last frame something arrived that the bake still has to turn into a landed rect.</summary>
    private static int _quietFrom = int.MinValue;
    private static long _firstLandT;
    private static Window? _surface;
    private static readonly VertexBatch _batch = new(0f, 0f);
    private static int _captionN = -1, _captionM = -1, _captionPhase = -1, _chartsSeen = -1;
    private static bool _captionSkip;
    /// <summary>The skip line's rect (px), the click target beside Escape.</summary>
    private static float _skipX0, _skipX1, _skipY0, _skipY1;
    /// <summary>Caption and skip-line colours (rgb).</summary>
    private const uint CaptionRgb = 0xE8E4DA, SkipRgb = 0x9A968C;   // not-an-offset: colours

    /// <summary>The hold is up: the send is ours to make.</summary>
    internal static bool Held => _heldMsg != IntPtr.Zero;

    /// <summary>The frames the hold covers: the engine's send arrives here and stays until <see cref="Release"/>.
    /// No hold (master off, the pass stood down, <c>lightmap.hold</c> 0) = the send goes straight through.</summary>
    [UnmanagedCallersOnly]
    private static void OnSendAreaLoaded(IntPtr msg)
    {
        OnAreaLoaded();
        if (!Enabled || Lightmap.HoldSeconds.V <= 0 || _surface == null)
        {
            _log?.Info(Enabled ? "LightmapBake: AreaLoaded send passed through -- hold disabled (lightmap.hold 0)" : "LightmapBake: AreaLoaded send passed through -- no bake");
            Forward(msg); return;
        }
        if (Held) _log?.Info("LightmapBake: the AreaLoaded send fired under a hold -- restarting it");
        _heldMsg = msg; _holdT0 = Stopwatch.GetTimestamp(); _holdFrame = _frame; _skipOpen = false; _firstLandT = 0;
        _quietFrom = int.MaxValue; _captionPhase = -1;
        Hold = true; _surface.SuppressChords = true;
        _log?.Info("LightmapBake: holding the AreaLoaded send for the baseline bake");
    }

    private static void Forward(IntPtr msg)
    {
        if (_loadedSlot != null) ((delegate* unmanaged<IntPtr, void>)*_loadedSlot)(msg);
    }

    /// <summary>The set closed, a slot enrolled, a chart landed: the drain clock restarts.</summary>
    private static void Arrived() { if (Held) _quietFrom = _frame; }

    /// <summary>Nothing left to enrol, pack or bake, for <see cref="QuietFrames"/> frames past the last arrival --
    /// or a set that closed with no static light (SetHash 0): nothing to bake, nothing to wait for.</summary>
    private static bool BaselineIn
        => _closed && !_edgePending && (SetHash == 0 || MeshChart.InFlight == 0 && AlloyRenderer.ChartDeferred == 0 && _toPack.Count == 0 && _pending == 0 && _bakePage < 0
           && _quietFrom != int.MaxValue && _frame - _quietFrom >= QuietFrames);

    /// <summary>Seconds of no Alloy pass and no light walk after the send before the hold gives up: the login
    /// send fires before the world scene exists (nothing renders until the player is placed -- 2026-09-21's
    /// log held 23 s over an empty frame), and a bake that never ticks has nothing to wait for either.</summary>
    private const float IdleSeconds = 2f;
    /// <summary>The bake frame at the hold's start: unchanged = the pass never ran under it.</summary>
    private static int _holdFrame;
    private static bool IdleUnderHold => _frame == _holdFrame || _edgePending;

    /// <summary>Send the held message and log the hold: its length, the exit, the walk the set closed on and the
    /// census after settle (the residual check on the animated branch #659 could not exercise).</summary>
    private static void Release(string why)
    {
        IntPtr msg = _heldMsg;
        float s = HeldSeconds;
        End();
        float first = _firstLandT == 0 ? -1f : (_firstLandT - _holdT0) / (float)Stopwatch.Frequency;
        _log?.Info($"LightmapBake: hold released after {s:F1} s ({why}) -- first bake at {(first < 0 ? "never" : $"{first:F1} s")}, "
                 + $"{_landed} rects landed ({_landedHits} cached) / {_pending} pending, pages {_layers}, set {(_closed ? $"closed at walk {_walks}" : "OPEN")}, "
                 + $"charts {MeshChart.Done}/{MeshChart.Offered}; census at the close static={_closeStatic} (tile {_closeTile}, placeable {_closePlaceable}) animated={_closeAnimated} dynamic={_closeDynamic}, now static {CensusStatic} dynamic {CensusDynamic}");
        Forward(msg);
    }

    /// <summary>The world edge under a hold (a part-table wipe is one, and an area transition's debris can trip
    /// it): the send still goes out -- a swallowed send is a player the server never places. The one exception
    /// is CX-19's area edge, which IS the edge this hold was raised for.</summary>
    private static void DropHold() { if (Held && !_areaEdge) Release("world edge"); }

    /// <summary>CX-19: <c>AlloyRenderer.AreaEdge</c> runs <see cref="Revoke"/> as part of the one area-edge
    /// revoke list. Bracketed by these two, that revoke neither forwards the held send nor loses the edge
    /// marks the static set closes against -- <see cref="EndAreaEdge"/> re-arms them, so the bake restarts on
    /// the new area's inputs rather than falling back to the 600-walk close.</summary>
    private static bool _areaEdge;
    internal static void BeginAreaEdge() => _areaEdge = true;
    internal static void EndAreaEdge() { _areaEdge = false; OnAreaLoaded(); }

    /// <summary>The window is the input side only (Escape, the clicks it eats); the draw is at the present,
    /// after the GL bracket hands the engine's context back -- the window roll-call sits under the engine's
    /// loadscreen (the NUI chain), and the first live run showed the stock screen, not this one.</summary>
    private static void End() { _heldMsg = IntPtr.Zero; Hold = false; _skipOpen = false; _surface!.SuppressChords = false; }

    private static void InstallSurface(ExtenderLog log)
    {
        _surface = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            OnKey = (key, _) => { if (!Held) return false; if (key == KeyEscape && _skipOpen) Release("skipped"); return true; },
            OnMouseButton = (down, button, x, y) =>
            {
                if (!Held) return false;
                if (down && button == 1 && _skipOpen && x >= _skipX0 && x < _skipX1 && y >= _skipY0 && y < _skipY1) Release("skipped");
                return true;
            },
        };
        FrameRegistry.Global.Register(FramePhase.PrePresent, 20, DrawSurface, "LightmapBake.hold", log);
    }

    private static float HeldSeconds => (Stopwatch.GetTimestamp() - _holdT0) / (float)Stopwatch.Frequency;

    /// <summary>Per presented frame. Ticks the exits, then draws the backdrop, the bar and the captions over
    /// everything the engine drew. ponytail: the caption is a string built whenever its number moves (most
    /// frames of the chart phase) -- under an opaque backdrop a gen0 pause shows nowhere; a char buffer if it ever does.</summary>
    private static void DrawSurface()
    {
        if (!Held) return;
        if (MeshChart.Done != _chartsSeen) { _chartsSeen = MeshChart.Done; Arrived(); }
        float s = HeldSeconds;
        if (BaselineIn) { Release(SetHash == 0 ? "no static light -- nothing to bake" : "baseline in"); return; }
        if (s >= IdleSeconds && IdleUnderHold) { Release("Alloy idle under the hold -- no scene yet"); return; }
        if (s >= CapSeconds) { Release("cap"); return; }
        if (!Enabled) { Release("bake stood down"); return; }
        _skipOpen = s >= Lightmap.HoldSeconds.V;
        float w = TextRig.ViewportW, h = TextRig.ViewportH;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (w <= 0 || atlas == null) return;
        int phase = _closed && MeshChart.InFlight == 0 && AlloyRenderer.ChartDeferred == 0 ? 1 : 0;
        int n = phase == 0 ? MeshChart.Done : PagesDone(), m = phase == 0 ? MeshChart.Offered : _layers;
        if (n != _captionN || m != _captionM || phase != _captionPhase || _skipOpen != _captionSkip) Bake(atlas, w, h, phase, n, m);
        TextRig.DrawBatch(_batch.AsSpan(), _batch.VertexCount);
    }

    /// <summary>Pages with no rect still queued, probing or packing.</summary>
    private static int PagesDone()
    {
        Span<bool> busy = stackalloc bool[MaxPages]; busy.Clear();
        for (int ri = 0; ri < _rectN; ri++)
        {
            RState st = _rects[ri].State;
            if ((st == RState.Queued || st == RState.Probing) && (uint)_rects[ri].Page < MaxPages) busy[_rects[ri].Page] = true;
        }
        int done = 0;
        for (int p = 0; p < _layers; p++) if (!busy[p]) done++;
        return done;
    }

    /// <summary>Backdrop, one bar (the phase's N/M), the caption and the skip line. Cold: runs on a change.</summary>
    private static void Bake(GlyphAtlas atlas, float w, float h, int phase, int n, int m)
    {
        _captionN = n; _captionM = m; _captionPhase = phase; _captionSkip = _skipOpen;
        _batch.Reset(atlas.SolidU, atlas.SolidV);
        _batch.Rect(0, 0, w, h, 0.04f, 0.04f, 0.05f, 1f);
        float barW = MathF.Min(w * 0.5f, 640f * Metrics.Scale), barH = 10f * Metrics.Scale;
        float x0 = (w - barW) * 0.5f, y0 = h * 0.72f;
        _batch.Rect(x0, y0, x0 + barW, y0 + barH, 0.16f, 0.16f, 0.18f, 1f);
        float f = m > 0 ? Math.Clamp(n / (float)m, 0f, 1f) : 0f;
        _batch.Rect(x0, y0, x0 + barW * f, y0 + barH, 0.78f, 0.66f, 0.36f, 1f);
        string caption = phase == 0 ? $"Building light charts {n}/{m}" : $"Baking the shadows... {n}/{m} pages";
        float tw = UiText.Measure(atlas, caption);
        UiText.Draw(_batch, atlas, caption, (w - tw) * 0.5f, y0 - atlas.LineHeight * 0.6f, CaptionRgb, w);
        if (_skipOpen)
        {
            const string skip = "Press Escape to skip";
            float sw = UiText.Measure(atlas, skip), sx = (w - sw) * 0.5f, sy = y0 + barH + atlas.LineHeight * 1.4f;
            UiText.Draw(_batch, atlas, skip, sx, sy, SkipRgb, w);
            _skipX0 = sx - 8f; _skipX1 = sx + sw + 8f; _skipY0 = sy - atlas.Ascent - 4f; _skipY1 = sy + atlas.LineHeight * 0.5f + 4f;
        }
    }

    /// <summary>Headless: the drain clock and the exits, no GL, no window.</summary>
    internal static bool HoldSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.HoldSelfCheck");
        check.Expect(CapSeconds > Lightmap.HoldSeconds.Max, "the cap must sit past the knob's range or the skip line never shows");
        check.Expect(QuietFrames < 60, "the drain clock is a second or more: a second visit would not hold ~0 s");
        check.Expect(IdleSeconds < CapSeconds && IdleSeconds <= Lightmap.HoldSeconds.Max, "the idle exit must come before the cap and the skip");
        _quietFrom = int.MaxValue; _frame = 100;
        check.Expect(!BaselineIn, "an untouched drain clock counts as drained");
        // a lightless area: the walks past the edge see no light at all -- the set still closes, the bake stays
        // un-armed (SetHash 0) and the hold has nothing to wait for
        _clsArea = 1; Reopen(); _edgeSeen = true; _edgePending = false; _stN = 0; CensusDynamic = 0; SetHash = 0xBEEF;
        ExtenderLog? live = _log; _log = null;   // the close logs as evidence: a headless one must not open every cx.log
        for (int i = 0; i < SettleWalks + QuietWalks; i++) { _walks++; if (_edgeWalk < 0) _edgeWalk = _walks; EndWalk(); }
        _log = live;
        check.Expect(_closed, "a lightless area never closes its static set");
        check.Expect(SetHash == 0 && _setN == 0, "a lightless close armed the bake");
        check.Expect(BaselineIn, "a closed lightless set does not release the hold");
        _clsArea = IntPtr.Zero; Reopen(); _edgeSeen = false; SetHash = 0; _quietFrom = int.MinValue;
        if (check.Ok) log.Info($"LightmapBake.HoldSelfCheck: passed (cap {CapSeconds:F0} s, drain {QuietFrames} frames)");
        return check.Ok;
    }
}
