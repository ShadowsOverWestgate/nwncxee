using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Ui;

namespace CX.Alloy;

/// <summary>#675: the loading-screen hold. The engine's <c>SendPlayerToServerArea_AreaLoaded</c> is what places
/// the player (the server's <c>MovePlayerToArea</c> runs on nothing else, and no server clock runs while it
/// waits -- CX-132); the hook swallows it, an opaque CX window covers the frames
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
    /// <summary>CX-56: slots the enrol sweep visits per frame under the hold -- the whole table in ONE frame, so
    /// "nothing left to enrol" is a one-frame silence rather than a ten-frame one. A slot that is enrolled or
    /// empty costs the sweep two array reads (<c>_slotRect[i]</c>, then <c>AlloyRenderer.SlotPlacement</c>'s
    /// <c>_oTri[i]</c>), so the empty majority of the 163840 is a linear scan; the few that do enrol do the same
    /// work they did spread over ten frames.</summary>
    private const int HoldSweepPerFrame = AlloyRenderer.MaxOwned;
    /// <summary>Frames of silence (no close, no enrol, no chart landing) before the queue counts as drained.</summary>
    private static int QuietFrames => AlloyRenderer.MaxOwned / HoldSweepPerFrame + HoldSettleFrames + 2;
    /// <summary>The frame rate <see cref="QuietFrames"/> is sized against. Below it the window is spent in
    /// seconds instead (<see cref="QuietSeconds"/>): the count means "the sweep visited the whole table",
    /// which is frames, but a frame stretched by texture builds or a slow disk must cost the hold that wall
    /// clock rather than multiply it -- CX-54 measured the hold growing 2,2 -&gt; 3,7 s on frames that had
    /// grown by 12 ms of texture building each.</summary>
    private const float QuietFps = 30f;
    /// <summary>The quiet window's wall-clock ceiling: <see cref="QuietFrames"/> at <see cref="QuietFps"/>.
    /// Reached first, the hold releases with the sweep part-way -- a slot it had not visited enrols after
    /// placement and draws unbaked for a few frames, which is the trade this ceiling makes.</summary>
    private static float QuietSeconds => QuietFrames / QuietFps;
    private const uint KeyEscape = 27;   // SDLK_ESCAPE

    private static IntPtr _heldMsg;
    private static long _holdT0;
    private static bool _skipOpen;
    /// <summary>The last frame something arrived that the bake still has to turn into a landed rect.</summary>
    private static int _quietFrom = int.MinValue;
    /// <summary>...and its timestamp, for the window's wall-clock arm (<see cref="QuietSeconds"/>).</summary>
    private static long _quietT;
    private static long _firstLandT;
    private static Window? _surface;
    private static int _captionN = -1, _captionM = -1, _captionPhase = -1, _chartsSeen = -1;
    /// <summary>The caption <see cref="LoadScreen"/> shows for us; rebuilt only when one of its numbers moves.</summary>
    private static string _caption = "";

    /// <summary>CX-100: can a bake hold follow the engine's load? Then the load screen's bar is shared (<see cref="LoadScreen"/>).</summary>
    internal static bool HoldHooked => _loadedSlot != null && Enabled;

    /// <summary>The hold is up: the send is ours to make.</summary>
    internal static bool Held => _heldMsg != IntPtr.Zero;

    /// <summary>The frames the hold covers: the engine's send arrives here and stays until <see cref="Release"/>.
    /// No hold (master off, the pass stood down, <c>lightmap.hold</c> 0) = the send goes straight through.</summary>
    [UnmanagedCallersOnly]
    private static void OnSendAreaLoaded(IntPtr msg)
    {
        OnAreaLoaded();
        StampsReset();
        if (!Enabled || Lightmap.HoldSeconds.V <= 0 || _surface == null)
        {
            _log?.Info(Enabled ? "LightmapBake: AreaLoaded send passed through -- hold disabled (lightmap.hold 0)" : "LightmapBake: AreaLoaded send passed through -- no bake");
            LoadScreen.End();   // CX-100: no bake phase follows, so the engine's load owns the rest of the shared bar
            Forward(msg); return;
        }
        bool silent = AreaHitReady(out int planned);
        if (Held) _log?.Info("LightmapBake: the AreaLoaded send fired under a hold -- restarting it");
        _heldMsg = msg; _holdT0 = Stopwatch.GetTimestamp(); _holdFrame = _frame; _holdPresents = 0; _skipOpen = false; _firstLandT = 0;
        _quietFrom = int.MaxValue; _quietT = _holdT0; _sharedEnrols = 0; _captionPhase = -1;
        Hold = true; _surface.SuppressChords = true; _silentHold = silent;
        // CX-56: a full hit draws no backdrop -- up for a fifth of a second it is a flicker
        _log?.Info(silent ? $"LightmapBake: holding the AreaLoaded send silently -- area bake cache hit ({planned} rects), released once the plan's rects are claimed"
                          : "LightmapBake: holding the AreaLoaded send for the baseline bake");
    }

    private static void Forward(IntPtr msg)
    {
        if (_loadedSlot != null) (*_loadedSlot)(msg);
    }

    /// <summary>The set closed, a rect was minted, a chart landed: the drain clock restarts, on both arms.
    /// <paramref name="why"/> is carried for the release line's quiet term only (CX-56 step 0).</summary>
    private static void Arrived(byte why) { if (Held) { _quietFrom = _frame; _quietT = Stopwatch.GetTimestamp(); _stWhy = why; } }

    /// <summary>CX-54: an enrolment's verdict for the hold. Only a rect the bake has yet to produce is work
    /// the hold waits on. A slot that joined a rect already on file is not -- and that is what a streaming
    /// texture's re-enrol always produces (<c>TexStream.Landed</c> -&gt; re-stage -&gt; <see cref="Enrol(int,int,int)"/>),
    /// the bake key carrying no texture term at all -- so counting one would restart the quiet clock on
    /// every texture that landed and the hold could never go quiet while textures streamed.</summary>
    private static void Enrolled(bool minted) { if (minted) { _stMints++; Arrived(WhyMint); } else _sharedEnrols++; }
    /// <summary>Enrolments that found their rect already on file, since the hold began.</summary>
    private static long _sharedEnrols;

    /// <summary>Nothing left to enrol, pack or bake, for a quiet window past the last arrival -- or a set
    /// that closed with no static light (SetHash 0): nothing to bake, nothing to wait for. CX-56: a
    /// <c>LoadArea</c> the engine refused closes no set at all, and that is the whole of the wait too. A lit set
    /// with no rect minted is a world the sweep has not seen yet, not a drained one (a warm entry went quiet on the
    /// close alone and released before its first chart).</summary>
    private static bool BaselineIn
        => _loadRefused
        || (_closed || _areaAdopted) && !_edgePending && (SetHash == 0 || _rectN > 0 && AreaUploadsIn && MeshChart.InFlight == 0 && AlloyRenderer.ChartDeferred == 0 && _toPack.Count == 0 && _pending == 0 && _bakePage < 0
           && ClaimsIn && Drained(_frame, _quietFrom, QuietFrames, QuietSince, QuietSeconds));

    /// <summary>Wall clock since the last arrival; 0 until one happens under this hold.</summary>
    private static float QuietSince => (Stopwatch.GetTimestamp() - _quietT) / (float)Stopwatch.Frequency;

    /// <summary>CX-54: the quiet verdict, frames OR wall clock, whichever lands first. The frame count is
    /// what the window MEANS -- <see cref="HoldSweepPerFrame"/> makes "nothing left to enrol" a whole-table
    /// sweep, and a sweep is frames -- and the seconds are its ceiling, so sixteen 100 ms frames cost the
    /// hold <see cref="QuietSeconds"/> rather than 1,6 s. A clock never touched (<see cref="int.MaxValue"/>)
    /// is not drained on either arm: nothing has arrived yet.</summary>
    internal static bool Drained(int frame, int quietFrom, int windowFrames, float since, float windowSeconds)
        => quietFrom != int.MaxValue && (frame - quietFrom >= windowFrames || since >= windowSeconds);

    /// <summary>The idle exit's wall-clock arm, and now its ceiling: the login send fires before the world scene
    /// exists (nothing renders until the player is placed -- 2026-09-21's log held 23 s over an empty frame), and
    /// a bake that never ticks has nothing to wait for either. Kept for a client presenting too slowly for
    /// <see cref="IdlePresents"/> to mean anything.</summary>
    private const float IdleSeconds = 2f;
    /// <summary>CX-56: the idle exit's frame arm -- presents under the hold with no Alloy pass behind them.
    /// <see cref="DrawSurface"/> runs at every present while <see cref="_frame"/> moves only inside the pass, so
    /// this counts what the clock could not tell apart. Measured 2026-09-27: a real area reaches the frame anchor
    /// 2,7 ms after the send -- inside the first present, area edge consumed and light walk run -- while a
    /// scene-less one never reaches it at all and spent ~260 presents (~129 fps) waiting out
    /// <see cref="IdleSeconds"/>. Eight is several times the live latency, under a ceiling either way.</summary>
    private const int IdlePresents = 8;
    /// <summary>Presents drawn under this hold, reset with it.</summary>
    private static int _holdPresents;
    /// <summary>The bake frame at the hold's start: unchanged = the pass never ran under it.</summary>
    private static int _holdFrame;
    private static bool IdleUnderHold => _frame == _holdFrame || _edgePending;
    /// <summary>The idle exit's verdict: presents with no pass behind them OR the clock, the two arms
    /// <see cref="Drained"/> carries and for the same reason -- the count is what the exit MEANS, and the
    /// seconds are its ceiling so a stretched frame costs the hold wall clock rather than multiplying it.</summary>
    private static bool IdleOut(int presents, float since) => presents >= IdlePresents || since >= IdleSeconds;

    /// <summary>Send the held message and log the hold: its length, the exit, the walk the set closed on and the
    /// census after settle (the residual check on the animated branch #659 could not exercise).</summary>
    private static void Release(string why)
    {
        IntPtr msg = _heldMsg;
        float s = HeldSeconds;
        string stages = StampsClause();   // CX-56 step 0
        bool complete = BaselineIn;   // CX-56: the release's own verdict, and what decides whether the area plan is written
        End();
        float first = _firstLandT == 0 ? -1f : (_firstLandT - _holdT0) / (float)Stopwatch.Frequency;
        _log?.Info($"LightmapBake: hold released after {s:F1} s ({why}) -- first bake at {(first < 0 ? "never" : $"{first:F1} s")}, "
                 + $"{_landed} rects landed ({_landedHits} cached) / {_pending} pending, pages {_layers}, set {(_closed ? $"closed at walk {_walks}" : "OPEN")} "
                 + $"SetHash {SetHash:x16}, "
                 + $"charts {MeshChart.Done}/{MeshChart.Offered}, {_sharedEnrols} enrols shared a standing rect; census at the close static={_closeStatic} (tile {_closeTile}, placeable {_closePlaceable}) animated={_closeAnimated} (anim-driven {_closeAnimRun}) dynamic={_closeDynamic}, now static {CensusStatic} dynamic {CensusDynamic}"
                 + stages + AreaStats());
        // CX-54 step 0: the key sample is chosen over the whole area, so it is emitted here, not on the first rect.
        FlushKeySample();
        Forward(msg);
        AreaWrite(complete);
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
    internal static void EndAreaEdge() { _areaEdge = false; _edgePresent = GlContextService.Presents; OnAreaLoaded(); StampEdge(); TakeAreaKey(); }

    /// <summary>The window is the input side only (Escape, the clicks it eats); the draw is <see cref="LoadScreen"/>'s,
    /// at the present, after the GL bracket hands the engine's context back -- the window roll-call sits under the
    /// engine's loadscreen (the NUI chain), and the first live run showed the stock screen, not this one.</summary>
    private static void End()
    {
        _silentHold = false; _heldMsg = IntPtr.Zero; Hold = false; _skipOpen = false; _surface!.SuppressChords = false;
        LoadScreen.End();   // CX-100: the hold was the last phase of the one screen, so the screen is over too
    }

    private static void InstallSurface(ExtenderLog log)
    {
        _surface = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            OnKey = (key, _) => { if (!Held) return false; if (key == KeyEscape && _skipOpen) Release("skipped"); return true; },
            OnMouseButton = (down, button, x, y) =>
            {
                if (!Held) return false;
                if (down && button == 1 && _skipOpen && LoadScreen.HitSkip(x, y)) Release("skipped");
                return true;
            },
        };
        FrameRegistry.Global.Register(FramePhase.PrePresent, 20, DrawSurface, "LightmapBake.hold", log);
    }

    private static float HeldSeconds => (Stopwatch.GetTimestamp() - _holdT0) / (float)Stopwatch.Frequency;

    /// <summary>Per presented frame. Ticks the exits, then feeds <see cref="LoadScreen"/> -- the ONE loading
    /// screen the engine's own load phase already drew (CX-100) -- and draws it over everything the engine drew.
    /// ponytail: the caption is a string built whenever its number moves (most frames of the chart phase) --
    /// under an opaque backdrop a gen0 pause shows nowhere; a char buffer if it ever does.</summary>
    private static void DrawSurface()
    {
        if (!Held) return;
        _holdPresents++;
        if (MeshChart.Done != _chartsSeen) { _chartsSeen = MeshChart.Done; Arrived(WhyChart); }
        StampTick();
        float s = HeldSeconds;
        if (BaselineIn) { Release(_loadRefused ? "the engine refused the area load" : SetHash == 0 ? "no static light -- nothing to bake" : _silentHold ? $"baseline in, silent -- {_areaClaimed} of {_areaBase} baseline rects claimed" : "baseline in"); return; }
        if (IdleUnderHold && IdleOut(_holdPresents, s)) { Release($"Alloy idle under the hold -- no scene yet after {_holdPresents} presents"); return; }
        if (s >= CapSeconds) { Release("cap"); return; }
        if (!Enabled) { Release("bake stood down"); return; }
        if (_silentHold) { if (s >= SilentCapSeconds) Release($"silent hold ceiling -- {_areaClaimed} of {_areaBase} baseline rects claimed"); return; }
        _skipOpen = s >= Lightmap.HoldSeconds.V;
        int phase = _closed && MeshChart.InFlight == 0 && AlloyRenderer.ChartDeferred == 0 ? 1 : 0;
        int n = phase == 0 ? MeshChart.Done : PagesDone(), m = phase == 0 ? MeshChart.Offered : _layers;
        if (n != _captionN || m != _captionM || phase != _captionPhase)
        {
            _captionN = n; _captionM = m; _captionPhase = phase;
            _caption = phase == 0 ? $"Building light charts {n}/{m}" : $"Baking the shadows... {n}/{m} pages";
        }
        LoadScreen.BakeStep(BakeFraction(phase, n, m), _caption, _skipOpen);
        LoadScreen.Draw();
    }

    /// <summary>The hold's two sub-phases as one 0..1 term: charts fill the first half, pages the second. Without
    /// the split the bar would stall at the charts' full mark while the pages counted up from zero.</summary>
    internal static float BakeFraction(int phase, int n, int m)
        => (Math.Clamp(phase, 0, 1) + (m > 0 ? Math.Clamp(n / (float)m, 0f, 1f) : 0f)) * 0.5f;

    /// <summary>The present "Compiling shaders" last went up in: once per present, the screen keeps it until the next.</summary>
    private static int _noticeAt = -1;
    /// <summary>The present the last area edge ran in: nothing of that area is on screen until it ends. SoW's login
    /// runs its edge ~125 ms after the AreaLoaded send, past the hold's idle exit (2026-09-29: the module's whole
    /// program set compiled on that frame, 9,6 s, with no hold up).</summary>
    private static int _edgePresent = -1;

    /// <summary>A shader compile is about to block the render thread on a loading screen -- under the hold, or on the
    /// frame an area edge ran in: put the backdrop up with "Compiling shaders…" NOW, as its own present. Every
    /// other draw comes at the frame's present, after the compile, so without this the player watches a frozen
    /// frame for the seconds a cold lit variant takes. Mid-play first-use compiles get no notice: a backdrop
    /// flashing over the world is worse than the hitch. Only from inside the 4.6 bracket (every Alloy compile is),
    /// which it leaves for the engine's context and re-enters.</summary>
    internal static void CompileNotice()
    {
        int now = GlContextService.Presents;
        if (!Held && _edgePresent != now || _noticeAt == now || !GlContextService.Inside || TextRig.Atlas == null) return;
        // CX-100: under the engine's load screen the frozen screen is the notice; a CX backdrop would be a second one.
        if (LoadScreen.PanelUp) return;
        int w = TextRig.ViewportW, h = TextRig.ViewportH;
        if (w <= 0 || h <= 0) return;
        _noticeAt = now; _captionPhase = -1;   // the screen's batches are shared: the hold's caption rebakes at its next present
        GlContextService.Leave();
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.Viewport(0, 0, w, h);
        LoadScreen.Notice("Compiling shaders…", "Only once: the result is saved for next time");
        GlContextService.SwapNow();
        UiHostService.HandBack();
        Gl.Viewport(vp[0], vp[1], vp[2], vp[3]);   // GLRender::SetViewPort caches no GL state, so the raw value is all it had
        if (GlContextService.Enter()) _log?.Info($"LightmapBake: a shader compile {(Held ? "under the hold" : "on an area's first frame")} -- \"Compiling shaders\" is on screen until it finishes");
        else _log?.Always("LightmapBake: the 4.6 context REFUSED re-entry after the compile notice -- this frame's Alloy GL runs on the engine's context");
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

    /// <summary>Headless: the drain clock and the exits, no GL, no window.</summary>
    internal static bool HoldSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.HoldSelfCheck");
        check.Expect(CapSeconds > Lightmap.HoldSeconds.Max, "the cap must sit past the knob's range or the skip line never shows");
        check.Expect(QuietFrames < 60, "the drain clock is a second or more: a second visit would not hold ~0 s");
        check.Expect(AlloyRenderer.MaxOwned / HoldSweepPerFrame == 1, "the hold's sweep no longer covers the whole table in one frame, so QuietFrames' sweep term is wrong");
        check.Expect(BakeFraction(0, 0, 0) == 0f, "an unopened chart phase starts the bake term at zero");
        check.Expect(BakeFraction(0, 4, 4) == 0.5f, "a finished chart phase hands over at the halfway mark");
        check.Expect(BakeFraction(1, 0, 4) == 0.5f, "...and the page phase picks it up there, so the bar never stalls");
        check.Expect(BakeFraction(1, 4, 4) == 1f, "a finished bake fills the bake term");
        check.Expect(BakeFraction(0, 9, 0) == 0f, "a zero denominator is a zero term, not a NaN");
        check.Expect(IdleSeconds < CapSeconds && IdleSeconds <= Lightmap.HoldSeconds.Max, "the idle exit must come before the cap and the skip");
        // CX-56: the idle exit counts presents -- the draw slot runs at every present while the bake's frame
        // moves only inside the Alloy pass -- with the seconds kept as the ceiling, Drained's two-arm shape.
        check.Expect(!IdleOut(IdlePresents - 1, IdleSeconds - 0.01f), "the idle exit fires before either arm is reached");
        check.Expect(IdleOut(IdlePresents, 0f), "the present arm does not release a hold no Alloy pass ran under");
        check.Expect(IdleOut(0, IdleSeconds), "the clock ceiling no longer releases a hold that barely presented");
        check.Expect(IdlePresents / QuietFps < IdleSeconds, "the present arm cannot fire before the ceiling: the idle exit is still a two-second wait");
        // CX-54: the quiet verdict, both arms and the ceiling that stops a stretched frame multiplying
        const int w = 16; const float ws = 0.5f;
        check.Expect(!Drained(100, int.MaxValue, w, 99f, ws), "an untouched drain clock releases on the clock arm");
        check.Expect(!Drained(100, 90, w, 0.1f, ws), "a window neither arm has reached counts as drained");
        check.Expect(Drained(106, 90, w, 0.1f, ws), "the frame arm does not release after QuietFrames of silence");
        check.Expect(Drained(95, 90, w, 0.6f, ws), "the clock arm does not release when the frames stretched past the window's seconds");
        check.Expect(Drained(106, 90, w, 0.6f, ws) && !Drained(99, 90, w, 0.0f, ws), "the verdict is monotone in both frames and seconds");
        check.Expect(QuietSeconds > 0f && QuietSeconds < CapSeconds, "the quiet ceiling must sit inside the hold's own cap");
        // CX-54: only a MINTED rect is an arrival. A shared one is what a landing texture's re-enrol always
        // produces, and counting it is what kept the hold running while textures streamed.
        IntPtr msg = _heldMsg; int qf = _quietFrom; long qt = _quietT;
        _heldMsg = (IntPtr)1; _frame = 100; _quietFrom = 50; _quietT = 0;
        Enrolled(false);
        check.Expect(_quietFrom == 50, "a shared enrol restarted the quiet clock");
        Enrolled(true);
        check.Expect(_quietFrom == 100, "a minted rect did not restart the quiet clock");
        _heldMsg = msg; _quietFrom = qf; _quietT = qt; _sharedEnrols = 0;
        _quietFrom = int.MaxValue; _frame = 100;
        check.Expect(!BaselineIn, "an untouched drain clock counts as drained");
        // CX-56: the close is LoadArea's return and the blends, not a walk count. A lightless area sees no
        // light at all -- the set still closes on the first armed walk, the bake stays un-armed (SetHash 0)
        // and the hold has nothing to wait for.
        // CX-56: a silent hold waits for half the plan's baseline rects to be claimed; a loud one never does
        _silentHold = true; _areaBase = 10; _areaClaimed = 4;
        check.Expect(!ClaimsIn, "a silent hold released with fewer than half of the plan's rects claimed");
        _areaClaimed = 5;
        check.Expect(ClaimsIn, "a silent hold with half the plan claimed still waits");
        _silentHold = false; _areaClaimed = 0;
        check.Expect(ClaimsIn, "a loud hold waits on claims");
        _areaBase = 0;
        ExtenderLog? live = _log; _log = null;   // the close logs as evidence: a headless one must not open every cx.log
        _clsArea = 1; Reopen(); _edgePending = false; _stN = 0; CensusDynamic = CensusUnsettled = 0; SetHash = 0xBEEF;
        _loadArmed = false; _walks = 1; EndWalk();
        check.Expect(!_closed, "the set closed with LoadArea still unarmed -- a half-spawned area can be baked");
        _loadArmed = true; EndWalk();
        check.Expect(_closed, "a lightless area never closes its static set on the first walk past LoadArea");
        check.Expect(SetHash == 0 && _setN == 0, "a lightless close armed the bake");
        check.Expect(BaselineIn, "a closed lightless set does not release the hold");
        // ...and a light still blending its animation in holds the close open, up to the ceiling
        Reopen(); _stN = 0; CensusUnsettled = 1; _walks = 1; EndWalk();
        check.Expect(!_closed, "a static light still blending did not hold the close open");
        _walks = BlendCeiling; EndWalk();
        check.Expect(_closed, "the blend wait has no ceiling: one stuck blend would hold the loading screen to its cap");
        _log = live;
        check.Expect(Settled(IntPtr.Zero), "a gob-less light (a tile's own node) animates nothing and must count as settled");
        _clsArea = IntPtr.Zero; Reopen(); _loadArmed = false; CensusUnsettled = 0; SetHash = 0; _quietFrom = int.MinValue;
        if (check.Ok) log.Info($"LightmapBake.HoldSelfCheck: passed (cap {CapSeconds:F0} s, drain {QuietFrames} frames or {QuietSeconds:F2} s, idle {IdlePresents} presents or {IdleSeconds:F0} s)");
        return check.Ok;
    }
}
