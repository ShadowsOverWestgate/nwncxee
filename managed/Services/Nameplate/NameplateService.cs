using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Graphics;
using Sowx.Services.Chat;
using Sowx.Services.Chat.Core;

using Sowx.Ui;

namespace Sowx.Services.Nameplate;

/// <summary>
/// The overhead nameplate and chat bubble: one <see cref="Window"/> that draws every world-anchored plate
/// and bubble, four cold hooks that feed it, a <c>/nameplate</c> command and <c>sowx-nameplate.cfg</c>.
/// <para>Vanilla is suppressed by putting the engine in text-bubble mode 0 (the supported "Off" setting),
/// which stops it drawing either element while leaving every piece of state we poll intact -- including the
/// projected screen anchor, which under mode 0 is refreshed every tick for every bubble-owning object.</para>
/// <para>Nothing here reads the Alloy camera: the plate anchor is the engine's own projection, and the
/// distance fade reads the LIVE game camera off <c>CClientExoApp::GetModuleCamera()</c>.</para>
/// </summary>
public sealed unsafe class NameplateService : IExtenderService
{
    private static ExtenderLog? _log;
    private static NameplateConfig _cfg = new();
    private static readonly ConfigFile _file = new("sowx-nameplate.cfg");
    private static bool _configLoaded, _configDirty;
    private static bool _armed, _wasInModule, _suppressed;

    /// <summary>Whether the vanilla text-bubble mode-0 suppression is in force. While it is -- and only while
    /// it is -- the engine refreshes every bubble-owning object's projected screen anchor every tick
    /// (docs/re/nameplate.md §D), which is the anchor <c>CombatTextService</c> reads. It checks this before
    /// swallowing anything, so a stood-down nameplate feature stands the combat text down with it.</summary>
    public static bool Suppressing => _armed;

    private static readonly PlateTable _table = new();
    private static Window? _plates;
    private static Window? _panelWindow;
    private static NameplateSettings? _settings;

    // ---- trampolines -------------------------------------------------------------------------
    private static delegate* unmanaged<int, void> _origSetMode;
    private static delegate* unmanaged<IntPtr, void> _origCreaturePlate, _origPlaceablePlate, _origItemPlate;
    private static delegate* unmanaged<IntPtr, IntPtr, int, float, float, void> _origBubbleText;
    private static delegate* unmanaged<IntPtr, byte, void> _origChat;

    /// <summary>Nesting depth of a <c>ShowFeedbackBubbleText</c> in flight on this thread. The bubble-text
    /// sink is shared: non-zero means the string arriving there is the plate's own, not chat.</summary>
    private static int _inPlate;

    /// <summary>Channel byte of the chat line currently being handled, or -1. A sentinel rather than 0
    /// because <c>HandleServerToPlayerQuickChat</c> reaches the same sink without passing through the chat
    /// handler, and a stale byte would mis-style it.</summary>
    private static int _channel = -1;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Every pure piece self-checks BEFORE any stand-down return -- run-demo's only gate.
        NameplateConfig.SelfCheck(log);
        PlateLayout.SelfCheck(log);
        NameplateSettings.SelfCheck(log);
        NameplateNative.SelfCheck(log);
        PlateTable.SelfCheck(log);

        if (!GameGate.OwnsFramePresent)
        { log.Info("NameplateService: standing down (framework not owning frame present)"); return; }

        // ORDER: the two critical symbols first, so nothing else installs if either refuses.
        _origSetMode = (delegate* unmanaged<int, void>)hooks.Install(
            SymbolMap.SetChatBubbleMode, (IntPtr)(delegate* unmanaged<int, void>)&OnSetChatBubbleMode);
        _origBubbleText = (delegate* unmanaged<IntPtr, IntPtr, int, float, float, void>)hooks.Install(
            SymbolMap.SetPopUpBubbleText,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, float, float, void>)&OnSetPopUpBubbleText);
        if (_origBubbleText == null || _origSetMode == null)
        {
            log.Info("NameplateService: standing down (mode="
                   + (_origSetMode != null ? "hooked" : "unresolved") + " text="
                   + (_origBubbleText != null ? "hooked" : "unresolved")
                   + ") -- suppression never arms, vanilla plates and bubbles draw as they always did");
            return;
        }

        _origCreaturePlate = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.CreatureShowFeedbackBubbleText, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnCreaturePlate);
        _origPlaceablePlate = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PlaceableShowFeedbackBubbleText, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPlaceablePlate);
        _origItemPlate = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.ItemShowFeedbackBubbleText, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnItemPlate);
        _origChat = (delegate* unmanaged<IntPtr, byte, void>)hooks.Install(
            SymbolMap.HandleServerToPlayerChat, (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnServerChat);

        if (_origChat == null)
        { _cfg.ChannelStyle = false; log.Info("NameplateService: channel styling disabled (chat handler unhooked)"); }

        _settings = new NameplateSettings(() => _cfg, () => _configDirty = true);

        // No HitTestOverride: a plate must never answer UiHost.HitTest -- see OnPlateClick.
        _plates = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = static () => { long t = FrameZones.Now; Draw(); FrameZones.Add(FrameZones.Zone.Plates, t); },
            OnMouseButton = OnPlateClick,
        };
        UiHost.SendToBack(_plates);   // plates paint UNDER the chat box and the dialog panel

        _panelWindow = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = DrawPanel,
            OnMouseButton = OnPanelButton,
            OnMouseMotion = OnPanelMotion,
            OnKey = OnPanelKey,
            HitTestOverride = (px, py) => _settings is { IsOpen: true } s && s.Contains(px, py),
        };

        CommandRegistry.Global.Register("/nameplate", "report or tune the overhead nameplates", Cmd, "/np");
        _armed = true;
        log.Info("NameplateService: armed (vanilla bubbles suppressed via text-bubble mode 0)");
    }

    // ---- hooks -------------------------------------------------------------------------------

    /// <summary>Hook 1. <c>CGuiInGamePopUpBubble::SetChatBubbleMode(int)</c> is static, and it is BOTH our
    /// suppression and the vanilla <c>ui.text-bubble-mode</c> parity knob: whatever the options screen (or
    /// the debug console) asks for becomes OUR display mode, and the engine is always left at 0. That is
    /// what makes mode 0 the only value the engine ever sees, so nothing of vanilla's ever draws.
    /// <para>Two rules. Unarmed, the value passes through untouched -- a stand-down must leave vanilla exactly
    /// as it found it, and this handler is live from <c>Commit</c> whether or not the rest of the service armed.
    /// And the FIRST call is skipped: that is <c>DefineClientConfiguration</c>'s startup bind replaying
    /// <c>ui.text-bubble-mode</c> from the engine's ini (docs/re/nameplate.md, <c>gh 0x9cb5b0</c>), which would
    /// otherwise overwrite our own stored mode and rewrite the cfg on the first frame of every session.</para>
    /// <para>Cold (config/options thread), so the mode is handed to the render thread through
    /// <see cref="_pendingMode"/> rather than written into <see cref="_cfg"/>, which <see cref="LoadConfig"/>
    /// replaces wholesale.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnSetChatBubbleMode(int mode)
    {
        if (!_armed) { if (_origSetMode != null) _origSetMode(mode); return; }
        _log?.Info($"[nameplate] text-bubble-mode {mode} requested (bound={_modeBound})");
        if (mode >= NameplateConfig.ModeMin && mode <= NameplateConfig.ModeMax)
        {
            if (_modeBound) Interlocked.Exchange(ref _pendingMode, mode);
            else _modeBound = true;
        }
        if (_origSetMode != null) _origSetMode(0);
    }

    /// <summary>Set once the engine's startup config bind has been seen; later calls are real user changes.</summary>
    private static bool _modeBound;

    /// <summary>A user-requested display mode waiting for the render thread, or -1. The hook publishes,
    /// <see cref="Draw"/> takes it with an exchange -- the one cross-thread field this service has.</summary>
    private static int _pendingMode = -1;

    /// <summary>Hook 2 (x3). Flag the plate's own text so the shared sink below can tell it from chat, then
    /// let the original run -- it sets the per-class "plate is up" flag we poll every frame and drives the
    /// vanilla state machine. Nothing is read off <c>this</c> here on purpose: <c>CNWCItem</c> is multiply
    /// inherited, so only the sink's <c>CNWCObject*</c> is layout-safe for every class.</summary>
    [UnmanagedCallersOnly]
    private static void OnCreaturePlate(IntPtr self)
    { _inPlate++; if (_origCreaturePlate != null) _origCreaturePlate(self); _inPlate--; }

    [UnmanagedCallersOnly]
    private static void OnPlaceablePlate(IntPtr self)
    { _inPlate++; if (_origPlaceablePlate != null) _origPlaceablePlate(self); _inPlate--; }

    [UnmanagedCallersOnly]
    private static void OnItemPlate(IntPtr self)
    { _inPlate++; if (_origItemPlate != null) _origItemPlate(self); _inPlate--; }

    /// <summary>Hook 3. The one sink both elements pass through, and the only place the object is a clean
    /// <c>CNWCObject*</c>. After the original runs: a plate's own text is recorded as a plate candidate (we
    /// build our own string), anything else is a chat line and is queued with the engine's decay.
    /// <para>The <c>+0x14c</c> reset at the tail is load-bearing. Under mode 0 nothing else ever clears that
    /// latch, and <c>MouseOver</c> refuses to build a plate while it is 1 -- without the reset a creature's
    /// plate dies permanently the first time it speaks. We enforce chat-wins ourselves instead.</para>
    /// <para>Switched off, the suppression still stands -- the engine stays at mode 0 and the latch still has to
    /// be cleared -- but nothing is banked, so neither the hook ring nor the table fills with candidates no frame
    /// will ever draw.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnSetPopUpBubbleText(IntPtr self, IntPtr exo, int forceShow, float perChar, float fixedSec)
    {
        if (_origBubbleText != null) _origBubbleText(self, exo, forceShow, perChar, fixedSec);
        if (!_armed || self == IntPtr.Zero) return;

        NameplateConfig cfg = _cfg;   // one read: LoadConfig replaces the reference on the render thread
        if (cfg.On && cfg.Mode != 0)
        {
            byte type = NameplateNative.TypeOf(self);
            if (_inPlate > 0)
            {
                _table.Note(self, type);
            }
            else
            {
                string text = NameplateNative.ReadExo(exo);
                if (text.Length != 0)
                    _table.NoteBubble(self, type, text, NameplateNative.Decay(text.Length, perChar, fixedSec),
                                      _channel, Environment.TickCount64);
            }
        }
        NameplateNative.ClearLiveTextLatch(self);
    }

    /// <summary>Hook 4 (optional). Latch the channel byte for the <c>SetPopUpBubbleText</c> that runs on the
    /// same stack, and drop it again on return so a quick-chat line cannot inherit it.</summary>
    [UnmanagedCallersOnly]
    private static void OnServerChat(IntPtr self, byte channel)
    {
        int prev = _channel;
        _channel = channel;
        if (_origChat != null) _origChat(self, channel);
        _channel = prev;
    }

    // ---- config ------------------------------------------------------------------------------

    /// <summary>Stays retryable until <see cref="ConfigFile"/>'s deferral gate opens (our loader runs
    /// pre-main), so the first-frame caller just keeps asking.</summary>
    private static void LoadConfig()
    {
        if (!_file.Load(_log, t => _cfg = NameplateConfig.Parse(t))) return;
        _configLoaded = true;
        _file.Baseline(_cfg.ToText());
        if (_settings != null) { _settings.Layout(); _panelDirty = true; }
    }

    private static void SaveConfig() { _file.Save(_cfg.ToText(), _log); _configDirty = false; }

    // ---- the per-frame draw ------------------------------------------------------------------

    /// <summary>Plate/bubble geometry, reused. Doubling is a guard, not a path.</summary>
    private static float[] _verts = new float[8192];

    private static readonly float[] _boxX = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxY = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxW = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxH = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxA = new float[PlateTable.MaxEntries];
    /// <summary>Where <c>Place</c> put each box, before separation and before the slide residual. Kept so the
    /// push can be measured on its own and absorbed into the residual rather than teleporting the box.</summary>
    private static readonly float[] _boxBaseY = new float[PlateTable.MaxEntries];
    private static readonly long[] _boxKey = new long[PlateTable.MaxEntries];
    private static readonly uint[] _boxId = new uint[PlateTable.MaxEntries];
    private static readonly bool[] _boxIn = new bool[PlateTable.MaxEntries];
    private static readonly int[] _boxOrder = new int[PlateTable.MaxEntries];

    /// <summary>One clickable plate, published for the input thread.</summary>
    private struct Hit { public float X, Y, W, H; public string Name; }

    /// <summary>Double-buffered so the render thread can publish without allocating and the input thread
    /// never reads a half-written array. A torn read across the flip costs one stale rect, nothing worse.</summary>
    private static readonly Hit[][] _hits = { new Hit[PlateTable.MaxEntries], new Hit[PlateTable.MaxEntries] };
    private static readonly int[] _hitCount = new int[2];
    private static volatile int _hitCur;

    private const int MaxPortraitsPerFrame = 32;
    private static readonly string[] _portraitRef = new string[MaxPortraitsPerFrame];
    private static readonly float[] _portraitRect = new float[MaxPortraitsPerFrame * 5];   // x,y,w,h,alpha

    /// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs -- the chat renderer's.</summary>
    private static readonly float[] OutlineOffsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };

    private static readonly (float r, float g, float b, float a) PlateBg = (0.03f, 0.04f, 0.06f, 0.78f);
    private static readonly (float r, float g, float b, float a) BubbleBg = (0.04f, 0.05f, 0.08f, 0.80f);
    private static readonly (float r, float g, float b, float a) BarTrack = (0.10f, 0.11f, 0.14f, 0.9f);

    /// <summary>Chrome padding inside a bubble box, design px.</summary>
    private const float BubblePadPx = 5f;
    /// <summary>Vertical gap between a bubble box and the one queued above it, design px.</summary>
    private const float BubbleGapPx = 2f;
    /// <summary>Vertical clearance the separation pass leaves between two overlapping boxes, design px.</summary>
    private const float SeparationGapPx = 2f;
    /// <summary>Speech tail under a bubble box, design px. Sized off the design grid, not the atlas, so it
    /// scales with <c>Metrics.Scale</c> exactly as the padding does.</summary>
    private const float TailHeightPx = 9f, TailWidthPx = 12f;
    /// <summary>How far below its resting place a new plate/bubble starts, design px. It eases up from there
    /// while the entry fades in; the same residual absorbs a box that grows under a newer line.</summary>
    private const float SlideUpPx = 14f;

    /// <summary>Monotonic clock the animation deltas come off -- <c>Environment.TickCount64</c>, the only
    /// dt source the framework has. Primed on the first drawn frame so a long load is not a 30s step.</summary>
    private static long _animClockMs;
    private static bool _animPrimed;

    /// <summary>Set by <c>/nameplate probe</c>; the next drawn frame dumps one line per tracked entry and
    /// clears it. Cold by construction -- one invocation, one dump.</summary>
    private static volatile bool _probe;

    private static void Draw()
    {
        if (!_configLoaded) LoadConfig();

        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) _table.Clear();
        _wasInModule = inModule;

        if (!_armed) return;
        if (!_suppressed && _origSetMode != null) { _suppressed = true; _origSetMode(0); }
        ApplyPendingMode();
        // A settings slider fires onChanged at drag rate, so a dirty flag is only banked while the pointer is
        // down; the release path (and the command) is what actually writes.
        if (_configDirty && _configLoaded && !_panelPress) SaveConfig();

        _table.Drain(_cfg.BubbleQueue, _cfg.AnimMs);   // BEFORE every early return below: the hook ring is fixed-size

        _hitCount[_hitCur ^ 1] = 0;
        if (!GameGate.InGame) { Publish(0); return; }             // acceptance §11: GUI hidden -> nothing
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || !_cfg.On || _cfg.Mode == 0) { Publish(0); return; }   // acceptance §2: mode 0 = nothing

        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        long now = Environment.TickCount64;
        long dt = _animPrimed ? now - _animClockMs : 0L;
        _animClockMs = now; _animPrimed = true;
        _table.Refresh(now, dt, _cfg.AnimMs, _cfg.MaxDistance, _cfg.FadeDistance, _cfg.BubbleQueue, vpW, vpH);
        if (_probe) { _probe = false; Probe(vpW, vpH); }

        int count = _table.VisibleCount;
        if (count == 0) { Publish(0); return; }

        // ponytail: plate text cannot be sized independently of the chat font -- the atlas is baked once at the
        // effective px. The upgrade path is a glyph emit of our own that scales the quad AND the advance.
        float scale = Metrics.Scale;
        float lineH = atlas.LineHeight;
        bool portraits = _cfg.Portraits && _cfg.Mode == 2;
        bool background = _cfg.Background && _cfg.Mode == 2;

        // Pass 1: measure and place every box at its anchor. The slide residual is NOT folded in yet -- it is
        // applied after separation, so a box easing into place cannot perturb the overlap tests.
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            float w, h;
            if (e.ShowBubble) MeasureBubble(e, atlas, scale, lineH, out w, out h);
            else MeasurePlate(e, atlas, scale, lineH, portraits, out w, out h);
            PlateLayout.Place(e.AnchorX, e.AnchorY, w, h, vpW, vpH, out _boxX[i], out _boxBaseY[i]);
            // A newcomer starts a slide below its place; afterwards, GROWTH of the box (a newer line queued
            // beneath the older ones) pushes the top edge up, and feeding that delta back into the residual
            // makes the older lines EASE to the new offset instead of jumping to it. Shrink is deliberately
            // NOT absorbed: the box's bottom is pinned to the anchor, so when the oldest line expires every
            // surviving line stays exactly where it was and there is no displacement to animate.
            if (e.AnimNew) { e.AnimNew = false; e.YOff = SlideUpPx * scale; }
            else if (e.LastH > 0f && h > e.LastH) e.YOff += h - e.LastH;
            e.LastH = h;
            _boxY[i] = _boxBaseY[i];
            _boxW[i] = w; _boxH[i] = h;
            _boxKey[i] = e.SortKey;
            _boxId[i] = e.Id;
            _boxIn[i] = !e.FadingOut;
            // The bubble's own fade is now PER LINE (PlateTable.ExpireLines), applied inside EmitBubble; all the
            // box alpha still owes it is the "no line left to draw" cut.
            _boxA[i] = e.DistanceFade * e.Alpha * (e.ShowBubble && e.TopLine < 0 ? 0f : 1f);
        }

        int sepCount = PlateLayout.OrderByRecency(_boxOrder, _boxKey, _boxId, _boxIn, count);
        PlateLayout.Separate(_boxX, _boxY, _boxW, _boxH, _boxOrder, sepCount, SeparationGapPx * scale, vpH);

        // Absorb whatever the push changed by into the slide residual, then draw at placed + push + residual.
        // A box that took no part keeps the push it last had, so it fades where it stands rather than sliding
        // back down to its bare anchor as the stack it belonged to dissolves around it.
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            if (_boxIn[i]) PlateLayout.AbsorbPush(ref e.YOff, ref e.LastPush, _boxY[i] - _boxBaseY[i]);
            _boxY[i] = _boxBaseY[i] + e.LastPush + e.YOff;
            if (vpH <= 0f) continue;
            float maxY = vpH - _boxH[i];
            if (_boxY[i] > maxY) _boxY[i] = maxY;
            if (_boxY[i] < 0f) _boxY[i] = 0f;
        }

        // Pass 2: emit. Order of emission IS draw order (no depth), so chrome before text.
        int n = 0, pn = 0;
        float su = atlas.SolidU, sv = atlas.SolidV;
        int hitN = 0;
        Hit[] hits = _hits[_hitCur ^ 1];
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            float a = _boxA[i];
            if (a <= 0f) continue;
            if (!e.ShowBubble && e.Name.Length == 0 && !(_cfg.HealthBand && e.Damage >= 0)) continue;
            if (e.ShowBubble)
                EmitBubble(ref n, su, sv, atlas, e, _boxX[i], _boxY[i], _boxW[i], _boxH[i], a, scale, lineH, background);
            else
                EmitPlate(ref n, ref pn, su, sv, atlas, e, _boxX[i], _boxY[i], a, scale, lineH, portraits,
                          background, hits, ref hitN);
        }

        if (n > 0) TextRig.DrawBatch(_verts, n / VertexBatch.Floats);
        if (pn > 0) DrawPortraits(pn);
        Publish(hitN);
    }

    /// <summary>COLD, one shot per <c>/nameplate probe</c>: every term of the anchor conversion, so a residual
    /// offset can be settled from a single log rather than another disassembly session. Logs the raw
    /// <c>+0x154</c>/<c>+0x158</c> floats, what they converted to, both viewports, the GUI scale, and the
    /// behind-camera dot product for every tracked slot -- including the ones the frame gates rejected.</summary>
    private static void Probe(float vpW, float vpH)
    {
        ExtenderLog? log = _log;
        if (log == null) return;

        bool hasRect = NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh);
        bool hasCamRect = NameplateNative.CameraViewportRect(out float mx, out float my, out float mw, out float mh);
        bool hasGl = NameplateNative.GlViewportRect(out float gx, out float gy, out float gw, out float gh);
        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        float gui = Metrics.GameUiScale;

        log.Info($"[nameplate probe] draw={vpW}x{vpH} used={(hasRect ? "" : "MISSING ")}({rx},{ry},{rw},{rh}) "
               + $"cam+0x4d4={(hasCamRect ? "" : "MISSING ")}({mx},{my},{mw},{mh}) "
               + $"glvp={(hasGl ? "" : "MISSING ")}({gx},{gy},{gw},{gh}) "
               + $"guiscale={gui.ToString("F3", CultureInfo.InvariantCulture)} "
               + $"campos={(hasCam ? $"({cx},{cy},{cz})" : "MISSING")} "
               + $"camfwd={(hasFwd ? $"({fx},{fy},{fz})" : "MISSING")} visible={_table.VisibleCount}");

        PlateTable.Entry[] all = _table.Entries;
        for (int i = 0; i < all.Length; i++)
        {
            PlateTable.Entry e = all[i];
            if (!e.Used) continue;
            IntPtr obj = NameplateNative.GameObject(e.Id);
            if (obj == IntPtr.Zero) { log.Info($"[nameplate probe] id=0x{e.Id:x} <no live object>"); continue; }
            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay))
            { log.Info($"[nameplate probe] id=0x{e.Id:x} <no bubble>"); continue; }

            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out float dx2, out float dy2);
            NameplateNative.Position(obj, out float ox, out float oy, out float oz);
            float dot = hasCam && hasFwd ? (ox - cx) * fx + (oy - cy) * fy + (oz - cz) * fz : float.NaN;
            log.Info($"[nameplate probe] id=0x{e.Id:x} type={e.Type} name='{e.Name}' "
                   + $"raw=({ax},{ay}) draw=({dx2},{dy2}) world=({ox},{oy},{oz}) "
                   + $"fwddot={dot.ToString("F2", CultureInfo.InvariantCulture)} "
                   + $"dist={e.Distance.ToString("F1", CultureInfo.InvariantCulture)} "
                   + $"alpha={e.Alpha.ToString("F2", CultureInfo.InvariantCulture)} "
                   + $"yoff={e.YOff.ToString("F1", CultureInfo.InvariantCulture)} "
                   + $"plated={e.Plated} bubble={e.ShowBubble} alive={e.Alive} "
                   + $"ovmode={e.OverrideMode} ovparas={e.ParaCount} ov='{e.OverrideText.Replace("\n", "\\n")}'");
        }
    }

    private static void Publish(int count)
    {
        int back = _hitCur ^ 1;
        _hitCount[back] = count;
        _hitCur = back;
    }

    // ---- measurement -------------------------------------------------------------------------

    private static float MeasureRunes(GlyphAtlas atlas, ReadOnlySpan<char> s)
    {
        float w = 0f;
        foreach (Rune r in s.EnumerateRunes()) w += atlas.AdvanceOf(r);
        return w;
    }

    /// <summary>Width of one wrapped name line, the ellipsis included on the last line of a name the wrap had
    /// to cut. Hot, but the ellipsis measure only happens on a cut name.</summary>
    private static float NameLineW(PlateTable.Entry e, GlyphAtlas atlas, int i, int lines)
    {
        float w = MeasureRunes(atlas, e.Name.AsSpan(e.NameStart[i], e.NameLen[i]));
        if (e.NameTruncated && i == lines - 1) w += MeasureRunes(atlas, PlateTable.Ellipsis);
        return w;
    }

    /// <summary>Widest of the entry's name lines, and how many there are. The single funnel through which the
    /// name acquires its wrap: <c>EnsureNameWrap</c> is cold-on-change (keyed on width + atlas epoch + scale),
    /// so a steady frame is three compares and the measure walks preallocated spans, allocating nothing.</summary>
    private static float MeasureName(PlateTable.Entry e, GlyphAtlas atlas, float maxW, out int lines)
    {
        PlateTable.EnsureNameWrap(e, atlas.AdvanceOfCached, maxW, TextRig.AtlasEpoch);
        lines = e.NameLineCount;
        float w = 0f;
        for (int i = 0; i < lines; i++)
        {
            float lw = NameLineW(e, atlas, i, lines);
            if (lw > w) w = lw;
        }
        return w;
    }

    /// <summary>The max width a plate name column and a bubble box share -- one knob, <c>bubblewidth</c>.</summary>
    private static float TextMaxW(float scale) => _cfg.BubbleWidth * scale;

    private static void MeasurePlate(PlateTable.Entry e, GlyphAtlas atlas, float scale, float lineH,
                                     bool portraits, out float w, out float h)
    {
        bool band = _cfg.HealthBand && e.Damage >= 0;
        float nameW = MeasureName(e, atlas, TextMaxW(scale), out int nameLines);
        float bandW = band ? MeasureRunes(atlas, PlateLayout.BandText(e.Damage)) : 0f;
        bool portrait = portraits && e.PortraitResref.Length != 0;
        OverrideLayout ov = LayoutOverride(e, atlas, scale, nameLines, band, bandW);
        PlateMetrics m = PlateLayout.Measure(ov.ShowName ? nameW : 0f, ov.ShowName ? nameLines : 0,
                                             bandW, portrait, ov.ShowBand,
                                             _cfg.HealthBar && e.Damage >= 0, lineH, scale,
                                             ov.PreLines, ov.PostLines, ov.ExtraW);
        w = m.W; h = m.H;
    }

    private static void MeasureBubble(PlateTable.Entry e, GlyphAtlas atlas, float scale, float lineH,
                                      out float w, out float h)
    {
        float pad = BubblePadPx * scale, gap = BubbleGapPx * scale;
        float maxW = TextMaxW(scale);
        w = 0f; h = 0f;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            PlateTable.EnsureWrap(ln, atlas, maxW, TextRig.AtlasEpoch, _cfg.BubbleLines);
            float bw = ln.WrapPixelW + pad * 2f;
            if (bw > w) w = bw;
            h += ln.SpanCount * lineH + pad * 2f + (i > 0 ? gap : 0f);
        }
        if (w <= 0f) w = pad * 2f;
        if (h <= 0f) h = lineH + pad * 2f;
        h += TailH(scale);   // the band the tail hangs into; the box's own bottom edge stops above it
    }

    /// <summary>The chrome the speech tail needs, or 0 when there is no box for it to hang off (text-only mode,
    /// or the background turned off). A plate never gets one -- it is a label, not speech.</summary>
    private static float TailH(float scale) => _cfg.Background && _cfg.Mode == 2 ? TailHeightPx * scale : 0f;

    /// <summary>Adopt a display mode the options screen asked for while we were suppressing the engine. Render
    /// thread only, so <see cref="_cfg"/> is never written from under <see cref="LoadConfig"/>.</summary>
    private static void ApplyPendingMode()
    {
        int mode = Interlocked.Exchange(ref _pendingMode, -1);
        if (mode < 0 || mode == _cfg.Mode) return;
        _cfg.Mode = mode;
        _configDirty = true;
        _settings?.Layout();
        _panelDirty = true;
    }

    // ---- emit --------------------------------------------------------------------------------

    private static void Grow(int need)
    {
        while (need > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
    }

    private static void Vtx(ref int n, float x, float y, float u, float v,
                            float r, float g, float b, float a)
    { float[] q = _verts; q[n++] = x; q[n++] = y; q[n++] = u; q[n++] = v; q[n++] = r; q[n++] = g; q[n++] = b; q[n++] = a; }

    private static void EmitRect(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                                 float r, float g, float b, float a)
    {
        Grow(n + 6 * VertexBatch.Floats);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a); Vtx(ref n, x0, y1, su, sv, r, g, b, a);
    }

    /// <summary>Solid-colour triangle, for the speech tail. The batch is submitted as <c>GL_TRIANGLES</c> with
    /// culling off, so three vertices in any winding is a whole primitive.</summary>
    private static void EmitTri(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                                float x2, float y2, float r, float g, float b, float a)
    {
        Grow(n + 3 * VertexBatch.Floats);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a); Vtx(ref n, x2, y2, su, sv, r, g, b, a);
    }

    private static void EmitGlyph(ref int n, in Glyph gl, float penX, float baseline,
                                  float r, float g, float b, float a, float shear)
    {
        if (gl.W <= 0) return;
        Grow(n + 6 * VertexBatch.Floats);
        float x0 = penX + gl.XOff, y0 = baseline + gl.YOff, x1 = x0 + gl.W, y1 = y0 + gl.H;
        float dx = shear * gl.H;
        Vtx(ref n, x0 + dx, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1 + dx, y0, gl.U1, gl.V0, r, g, b, a);
        Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0 + dx, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0, y1, gl.U0, gl.V1, r, g, b, a);
    }

    /// <summary>One string at one colour, outlined when the config asks. The halo is the chat renderer's
    /// 8-way ±1px black offset -- 9x the quads, which is nothing at plate text volumes and is the whole
    /// readability answer over a bright world.</summary>
    private static void EmitString(ref int n, GlyphAtlas atlas, ReadOnlySpan<char> s, float penX, float baseline,
                                   float r, float g, float b, float a, float shear)
    {
        foreach (Rune ru in s.EnumerateRunes())
        {
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            if (_cfg.Outline)
                for (int o = 0; o < OutlineOffsets.Length; o += 2)
                    EmitGlyph(ref n, gl, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1], 0f, 0f, 0f, a, shear);
            EmitGlyph(ref n, gl, penX, baseline, r, g, b, a, shear);
            penX += gl.Advance;
        }
    }

    /// <summary>One line of a plate NAME. Same glyphs as <see cref="EmitString"/>, except that a char the
    /// name's own <c>&lt;cRGB&gt;</c> tags coloured wins over the reaction colour -- the fallback rule
    /// <see cref="EmitRuns"/> already applies to an override's untagged runs, now applied to the name too.
    /// <paramref name="colours"/> is indexed by char of <c>Entry.Name</c>, so the line's own char offset is
    /// passed in rather than assumed to be zero.</summary>
    private static void EmitNameLine(ref int n, GlyphAtlas atlas, ReadOnlySpan<char> s, uint[]? colours,
                                     int offset, float penX, float baseline,
                                     float r, float g, float b, float a)
    {
        if (colours == null) { EmitString(ref n, atlas, s, penX, baseline, r, g, b, a, 0f); return; }
        int ci = 0;
        while (ci < s.Length)
        {
            Rune.DecodeFromUtf16(s.Slice(ci), out Rune ru, out int used);
            int at = offset + ci;
            uint c = at < colours.Length ? colours[at] : PlateLayout.Plain;
            float cr = r, cg = g, cb = b;
            if (c != PlateLayout.Plain)
            { cr = ((c >> 16) & 0xFF) / 255f; cg = ((c >> 8) & 0xFF) / 255f; cb = (c & 0xFF) / 255f; }
            ci += used;
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            if (_cfg.Outline)
                for (int o = 0; o < OutlineOffsets.Length; o += 2)
                    EmitGlyph(ref n, gl, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1],
                              0f, 0f, 0f, a, 0f);
            EmitGlyph(ref n, gl, penX, baseline, cr, cg, cb, a, 0f);
            penX += gl.Advance;
        }
    }

    /// <summary>Emit one run of already-parsed, per-rune-coloured text and return the pen. The one place
    /// colour runs become glyphs: the bubble's wrapped spans and the plate's override lines both come through
    /// here, so an override's <c>&lt;cRGB&gt;</c> tags render exactly as a chat line's do.
    /// <para><paramref name="fallbackRgb"/> re-colours only runes the parser left at plain white, which is how
    /// a channel style tints untagged text without overriding the author's own colours.</para></summary>
    private static float EmitRuns(ref int n, GlyphAtlas atlas, List<Rune> runes, uint[] colours,
                                  int from, int to, float penX, float baseline, float a,
                                  uint fallbackRgb, float shear, out uint tailRgb)
    {
        tailRgb = 0xffffffu;
        for (int k = from; k < to && k < runes.Count; k++)
        {
            Rune ru = runes[k];
            uint c = k < colours.Length ? colours[k] : 0xffffffu;
            tailRgb = c;
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            if (c == 0xffffffu && fallbackRgb != 0u) c = fallbackRgb;
            float r = ((c >> 16) & 0xFF) / 255f, g = ((c >> 8) & 0xFF) / 255f, b = (c & 0xFF) / 255f;
            if (_cfg.Outline)
                for (int o = 0; o < OutlineOffsets.Length; o += 2)
                    EmitGlyph(ref n, gl, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1],
                              0f, 0f, 0f, a, shear);
            EmitGlyph(ref n, gl, penX, baseline, r, g, b, a, shear);
            penX += gl.Advance;
        }
        return penX;
    }

    /// <summary>Width of one wrapped span of a colour-run line. Hot but tiny -- override lines are status
    /// labels, and a plate carries at most <see cref="PlateTable.MaxParas"/> of them.</summary>
    private static float SpanW(GlyphAtlas atlas, PlateTable.BubbleLine ln, int s)
    {
        TextWrapper.LineSpan sp = ln.Spans[s];
        float w = 0f;
        for (int k = sp.Start; k < sp.End && k < ln.Runes.Count; k++) w += atlas.AdvanceOf(ln.Runes[k]);
        return w;
    }

    /// <summary>Where an entry's <c>ObjectTextBubbleOverride</c> lands on the plate, resolved once and used by
    /// both the measure and the emit. The engine composes ONE string, <c>name\nband</c>, and glues the
    /// override onto its head or tail (docs/re/nameplate.md §8.5) -- so the segment at the joint continues an
    /// existing line and the rest become lines of their own.</summary>
    private struct OverrideLayout
    {
        public bool ShowName, ShowBand;
        public PlateSplice Target;
        /// <summary>Para spliced onto <see cref="Target"/>'s line, or -1.</summary>
        public int SpliceIdx;
        public int PreFrom, PreTo, PostFrom, PostTo;
        public int PreLines, PostLines;
        public float SpliceW, ExtraW;
    }

    /// <summary>Wrap the override's lines around the placement <see cref="PlateLayout.PlaceOverride"/> chose.
    /// The splice keeps to a SINGLE line, wrapped into whatever its anchor leaves of the column: a status
    /// suffix is short, and letting it wrap would put half of it under the band and half beside it.
    /// <para>ponytail: raise that cap here if a server ever glues a paragraph onto the injury line.</para></summary>
    private static OverrideLayout LayoutOverride(PlateTable.Entry e, GlyphAtlas atlas, float scale,
                                                 int nameLines, bool band, float bandW)
    {
        OverrideLayout o = default;
        o.Target = PlateLayout.PlaceOverride(e.OverrideMode, e.ParaCount, nameLines, band,
                                             out o.ShowName, out o.ShowBand, out o.SpliceIdx,
                                             out o.PreFrom, out o.PreTo, out o.PostFrom, out o.PostTo);
        if (e.OverrideMode == 0 || e.ParaCount == 0) return o;

        float maxW = TextMaxW(scale);
        if (o.SpliceIdx >= 0)
        {
            float anchorW = o.Target switch
            {
                PlateSplice.BandFirst or PlateSplice.BandLast => bandW,
                PlateSplice.NameFirst => NameLineW(e, atlas, 0, nameLines),
                PlateSplice.NameLast => NameLineW(e, atlas, nameLines - 1, nameLines),
                _ => 0f,
            };
            PlateTable.BubbleLine sp = e.Paras[o.SpliceIdx];
            if (sp.Runes.Count != 0)
            {
                float room = maxW - anchorW;
                float floorW = maxW * SpliceMinFraction;
                if (room < floorW) room = floorW;
                PlateTable.EnsureWrap(sp, atlas, room, TextRig.AtlasEpoch, 1);
                o.SpliceW = sp.SpanCount > 0 ? SpanW(atlas, sp, 0) : 0f;
                if (sp.Truncated) o.SpliceW += MeasureRunes(atlas, PlateTable.Ellipsis);
            }
            o.ExtraW = anchorW + o.SpliceW;
        }

        o.PreLines = WrapParas(e, atlas, maxW, o.PreFrom, o.PreTo, ref o.ExtraW);
        o.PostLines = WrapParas(e, atlas, maxW, o.PostFrom, o.PostTo, ref o.ExtraW);
        return o;
    }

    /// <summary>Least of the text column a spliced segment is allowed, however wide the line it joins.</summary>
    private const float SpliceMinFraction = 0.25f;

    private static int WrapParas(PlateTable.Entry e, GlyphAtlas atlas, float maxW, int from, int to,
                                 ref float extraW)
    {
        int lines = 0;
        for (int i = from; i < to; i++)
        {
            PlateTable.BubbleLine ln = e.Paras[i];
            if (ln.Runes.Count == 0) continue;   // the empty segment that says "nothing glues here"
            PlateTable.EnsureWrap(ln, atlas, maxW, TextRig.AtlasEpoch, _cfg.BubbleLines);
            lines += ln.SpanCount;
            if (ln.WrapPixelW > extraW) extraW = ln.WrapPixelW;
        }
        return lines;
    }

    /// <summary>Draw the spliced override segment -- its single wrapped line -- at the pen the plate line it
    /// continues left off at.</summary>
    private static void EmitSplice(ref int n, GlyphAtlas atlas, PlateTable.Entry e, in OverrideLayout ov,
                                   float penX, float baseline, float a)
    {
        PlateTable.BubbleLine sp = e.Paras[ov.SpliceIdx];
        // An EMPTY splice segment ("\nSneaking") keeps whatever spans the pooled line last held; it is never
        // re-wrapped, so the run count is the only honest liveness test.
        if (sp.Runes.Count == 0 || sp.SpanCount == 0) return;
        float end = EmitRuns(ref n, atlas, sp.Runes, sp.Colours, sp.Spans[0].Start, sp.Spans[0].End,
                             penX, baseline, a, 0u, 0f, out uint tail);
        if (!sp.Truncated) return;
        EmitString(ref n, atlas, PlateTable.Ellipsis, end, baseline,
                   ((tail >> 16) & 0xFF) / 255f, ((tail >> 8) & 0xFF) / 255f, (tail & 0xFF) / 255f, a, 0f);
    }

    /// <summary>Draw a range of standalone override lines down from <paramref name="top"/>, and return the y
    /// past the last one.</summary>
    private static float EmitParas(ref int n, GlyphAtlas atlas, PlateTable.Entry e, int from, int to,
                                   float colX, float textW, float top, float a, float lineH)
    {
        for (int i = from; i < to; i++)
        {
            PlateTable.BubbleLine ln = e.Paras[i];
            if (ln.Runes.Count == 0) continue;
            for (int s = 0; s < ln.SpanCount; s++)
            {
                float lw = SpanW(atlas, ln, s);
                float lx = PlateLayout.LineX(colX, textW, lw);
                EmitRuns(ref n, atlas, ln.Runes, ln.Colours, ln.Spans[s].Start, ln.Spans[s].End,
                         lx, top + atlas.Ascent, a, 0u, 0f, out _);
                top += lineH;
            }
        }
        return top;
    }

    private static void EmitPlate(ref int n, ref int pn, float su, float sv, GlyphAtlas atlas,
                                  PlateTable.Entry e, float x, float y, float a, float scale, float lineH,
                                  bool portraits, bool background, Hit[] hits, ref int hitN)
    {
        bool band = _cfg.HealthBand && e.Damage >= 0;
        bool bar = _cfg.HealthBar && e.Damage >= 0;
        float nameW = MeasureName(e, atlas, TextMaxW(scale), out int nameLines);
        string bandText = band ? PlateLayout.BandText(e.Damage) : "";
        float bandW = band ? MeasureRunes(atlas, bandText) : 0f;
        bool portrait = portraits && e.PortraitResref.Length != 0;
        OverrideLayout ov = LayoutOverride(e, atlas, scale, nameLines, band, bandW);
        band = ov.ShowBand;
        if (!ov.ShowName) nameLines = 0;
        PlateMetrics m = PlateLayout.Measure(ov.ShowName ? nameW : 0f, nameLines, bandW, portrait, band, bar,
                                             lineH, scale, ov.PreLines, ov.PostLines, ov.ExtraW);

        if (background) EmitRect(ref n, su, sv, x, y, x + m.W, y + m.H, PlateBg.r, PlateBg.g, PlateBg.b, PlateBg.a * a);

        (float r, float g, float b) nc = PlateLayout.ReactionColor(e.Reaction);
        float colX = x + m.NameX;

        // Override lines that sit above the name (prepend mode) or replace the plate outright (mode 1).
        EmitParas(ref n, atlas, e, ov.PreFrom, ov.PreTo, colX, m.TextW, y + m.PreY, a, lineH);

        for (int i = 0; i < nameLines; i++)
        {
            ReadOnlySpan<char> line = e.Name.AsSpan(e.NameStart[i], e.NameLen[i]);
            float lw = NameLineW(e, atlas, i, nameLines);
            bool prefixed = ov.Target == PlateSplice.NameFirst && i == 0;
            bool suffixed = ov.Target == PlateSplice.NameLast && i == nameLines - 1;
            float lx = PlateLayout.LineX(colX, m.TextW, lw + (prefixed || suffixed ? ov.SpliceW : 0f));
            float by = y + m.NameY + i * lineH + atlas.Ascent;
            if (prefixed) { EmitSplice(ref n, atlas, e, ov, lx, by, a); lx += ov.SpliceW; }
            EmitNameLine(ref n, atlas, line, e.NameColours, e.NameStart[i], lx, by, nc.r, nc.g, nc.b, a);
            float penX = lx + lw;
            if (e.NameTruncated && i == nameLines - 1)
                EmitString(ref n, atlas, PlateTable.Ellipsis,
                           lx + lw - MeasureRunes(atlas, PlateTable.Ellipsis), by, nc.r, nc.g, nc.b, a, 0f);
            if (suffixed) EmitSplice(ref n, atlas, e, ov, penX, by, a);
        }

        // The name glyphs only, never the plate box or the override -- see OnPlateClick. Every line centres in
        // the text column, so the column IS the rect: a narrower one would miss the name it just moved.
        if (_cfg.ClickToTell && ov.ShowName && e.Name.Length != 0 && nameW > 0f && hitN < hits.Length)
            hits[hitN++] = new Hit { X = colX, Y = y + m.NameY, W = m.TextW,
                                     H = lineH * nameLines, Name = e.Name };

        if (band)
        {
            (float r, float g, float b) bc = PlateLayout.BandColor(e.Damage);
            float by = y + m.BandY + atlas.Ascent;
            bool spliced = ov.Target is PlateSplice.BandFirst or PlateSplice.BandLast;
            float bx = PlateLayout.LineX(x + m.BandX, m.TextW, bandW + (spliced ? ov.SpliceW : 0f));
            // A prepend that has no name line to run into runs into the band instead -- in FRONT of it.
            if (ov.Target == PlateSplice.BandFirst) { EmitSplice(ref n, atlas, e, ov, bx, by, a); bx += ov.SpliceW; }
            EmitString(ref n, atlas, bandText, bx, by, bc.r, bc.g, bc.b, a, 0f);
            if (ov.Target == PlateSplice.BandLast) EmitSplice(ref n, atlas, e, ov, bx + bandW, by, a);
        }

        // ... and the ones below it (append mode).
        EmitParas(ref n, atlas, e, ov.PostFrom, ov.PostTo, colX, m.TextW, y + m.PostY, a, lineH);

        if (bar && m.BarW > 0f)
        {
            (float r, float g, float b) bc = PlateLayout.BandColor(e.Damage);
            float frac = 1f - e.Damage / (float)(PlateLayout.BandCount - 1);
            EmitRect(ref n, su, sv, x + m.BarX, y + m.BarY, x + m.BarX + m.BarW, y + m.BarY + m.BarH,
                     BarTrack.r, BarTrack.g, BarTrack.b, BarTrack.a * a);
            EmitRect(ref n, su, sv, x + m.BarX, y + m.BarY, x + m.BarX + m.BarW * frac, y + m.BarY + m.BarH,
                     bc.r, bc.g, bc.b, a);
        }

        if (portrait && pn < MaxPortraitsPerFrame)
        {
            float ph = m.PortraitSize, pw = ph * TextRig.PortraitAspect;
            _portraitRef[pn] = e.PortraitResref;
            int o = pn * 5;
            _portraitRect[o] = x + m.PortraitX; _portraitRect[o + 1] = y + m.PortraitY;
            _portraitRect[o + 2] = pw; _portraitRect[o + 3] = ph; _portraitRect[o + 4] = a;
            pn++;
        }
    }

    private static void EmitBubble(ref int n, float su, float sv, GlyphAtlas atlas, PlateTable.Entry e,
                                   float x, float y, float w, float h, float a, float scale, float lineH,
                                   bool background)
    {
        float pad = BubblePadPx * scale, gap = BubbleGapPx * scale;
        float cy = y;
        // The tail hangs off the NEWEST line's box -- the bottom-most one -- so it is the last one's geometry
        // and alpha the tail is drawn from.
        float tailX = x, tailW = w, tailY = y, tailA = 0f;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            float bh = ln.SpanCount * lineH + pad * 2f;
            float bw = ln.WrapPixelW + pad * 2f;
            float bx = x + (w - bw) * 0.5f;
            // Oldest at the top, so it is also the first to fade out from under the newer lines. Each line
            // carries its OWN fade (PlateTable.ExpireLines), which is what stops a dying line from popping.
            float la = a * ln.Alpha * (i == e.TopLine ? 1f : 0.65f);
            tailX = bx; tailW = bw; tailY = cy + bh; tailA = la;
            ChannelStyle st = StyleOf(ln.Channel);
            if (background)
                EmitRect(ref n, su, sv, bx, cy, bx + bw, cy + bh,
                         BubbleBg.r, BubbleBg.g, BubbleBg.b, BubbleBg.a * la);

            for (int s = 0; s < ln.SpanCount; s++)
            {
                float penX = bx + pad;
                float baseline = cy + pad + s * lineH + atlas.Ascent;
                TextWrapper.LineSpan sp = ln.Spans[s];
                penX = EmitRuns(ref n, atlas, ln.Runes, ln.Colours, sp.Start, sp.End, penX, baseline,
                                la * st.Alpha, st.Rgb, st.Shear, out uint tail);
                // The wrap cut the message: say so on the last kept line rather than stopping mid-word.
                if (ln.Truncated && s == ln.SpanCount - 1)
                {
                    if (tail == 0xffffffu && st.Rgb != 0u) tail = st.Rgb;
                    EmitString(ref n, atlas, PlateTable.Ellipsis, penX, baseline,
                               ((tail >> 16) & 0xFF) / 255f, ((tail >> 8) & 0xFF) / 255f, (tail & 0xFF) / 255f,
                               la * st.Alpha, st.Shear);
                }
            }
            cy += bh + gap;
        }

        float th = TailH(scale);
        if (background && th > 0f && tailA > 0f)
        {
            PlateLayout.TailPoints(e.AnchorX, tailX, tailW, TailWidthPx * scale, pad,
                                   out float bl, out float br, out float tipX);
            if (br > bl)
                EmitTri(ref n, su, sv, bl, tailY, br, tailY, tipX, tailY + th,
                        BubbleBg.r, BubbleBg.g, BubbleBg.b, BubbleBg.a * tailA);
        }
    }

    // ---- channel styling ---------------------------------------------------------------------

    /// <summary>Per-channel emphasis: a tint applied only to text the server did not colour itself, an
    /// alpha multiplier and a synthetic-italic shear.</summary>
    private readonly struct ChannelStyle
    {
        public readonly uint Rgb; public readonly float Alpha, Shear;
        public ChannelStyle(uint rgb, float alpha, float shear) { Rgb = rgb; Alpha = alpha; Shear = shear; }
    }

    private static readonly ChannelStyle Neutral = new(0u, 1f, 0f);

    /// <summary>Channel values are the standard NWN message channels read out of
    /// <c>HandleServerToPlayerChat</c>'s switch: talk 1, shout 2, whisper 3, tell 4, party 6, DM 7, and the
    /// DM variants 8..0x0e / 0x11..0x1e of the same. Off (or unlatched) is one neutral style.</summary>
    private static ChannelStyle StyleOf(int channel)
    {
        if (!_cfg.ChannelStyle || channel < 0) return Neutral;
        return channel switch
        {
            0x02 or 0x09 or 0x12 => new ChannelStyle(0xffd28cu, 1f, 0f),      // shout
            0x03 or 0x0a or 0x13 => new ChannelStyle(0xa8b0bcu, 0.75f, 0.2f),// whisper: dim + italic
            0x04 or 0x14 => new ChannelStyle(0xe8a8ffu, 1f, 0f),             // tell
            0x06 or 0x16 => new ChannelStyle(0x9ce0a0u, 1f, 0f),             // party
            0x07 or 0x0e or 0x1e => new ChannelStyle(0xffb060u, 1f, 0f),     // DM
            _ => Neutral,
        };
    }

    // ---- portraits ---------------------------------------------------------------------------

    /// <summary>Second pass, the chat renderer's shape: resolve EVERY texture id and build ALL the geometry
    /// first, because <c>AurTextureGet</c>'s create path can touch GL and rebind <c>ARRAY_BUFFER</c> -- it
    /// must never run between our BindBuffer and BufferData. Then one upload and one bind+draw per plate.
    /// A texture id of 0 means the engine has not uploaded it yet: skip that plate this frame.</summary>
    /// <summary>One line per NEW portrait texture, the first eight: level-0 size, internal format, level-1
    /// size, mip range, filter, and a hash of levels 0 and 1 as the driver holds them. Read off the ENGINE's
    /// texture, because that is what the plate samples -- so two machines disagreeing on the hash of the
    /// same portrait is an upload/decode divergence, and agreeing while the plate looks different is a
    /// draw one. Cold: once per portrait name; the readback is a sync, spent eight times a session.</summary>
    private static void PortraitDiag(int tid, string resref)
    {
        if (_diagN >= _diagTids.Length || Gl.GetTexLevelParameteriv == null || Gl.GetTexImage == null
            || Gl.GetTexParameteriv == null || Gl.PixelStorei == null) return;
        for (int i = 0; i < _diagN; i++) if (_diagTids[i] == tid) return;
        _diagTids[_diagN++] = tid;
        int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        int w = 0, h = 0, fmt = 0, w1 = 0, h1 = 0, maxLevel = -1, minFilter = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 1, Gl.TEXTURE_WIDTH, &w1);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 1, Gl.TEXTURE_HEIGHT, &h1);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, &minFilter);
        ulong hash0 = 0, hash1 = 0;
        if (w > 0 && h > 0 && w <= 1024 && h <= 1024)
        {
            byte[] px = new byte[w * h * 4];   // cold: once per portrait name
            int align; Gl.GetIntegerv(Gl.PACK_ALIGNMENT, &align);
            Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
            fixed (byte* p = px)
            {
                Gl.GetTexImage(Gl.TEXTURE_2D, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                hash0 = Fnv(p, w * h * 4);
                if (w1 > 0 && h1 > 0)
                {
                    Gl.GetTexImage(Gl.TEXTURE_2D, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                    hash1 = Fnv(p, w1 * h1 * 4);
                }
            }
            Gl.PixelStorei(Gl.PACK_ALIGNMENT, align);
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);
        _log?.Info($"[nameplate] portrait {resref} tid {tid}: {w}x{h} fmt 0x{fmt:x} level1 {w1}x{h1} " +
                   $"maxlevel {maxLevel} minfilter 0x{minFilter:x} hash0 {hash0:x16} hash1 {hash1:x16} glerr 0x{Gl.GetError():x}");
    }

    private static readonly int[] _diagTids = new int[8];
    private static int _diagN;

    private static ulong Fnv(byte* p, int n)
    {
        ulong x = 14695981039346656037UL;
        for (int i = 0; i < n; i++) { x ^= p[i]; x *= 1099511628211UL; }
        return x;
    }

    private static void DrawPortraits(int count)
    {
        if (count <= 0 || !AurTex.Ready || !TextRig.EnsurePortraitProgram()) return;
        if (!TextRig._pvaoUp) TextRig.SetupPortraitVao();

        int* tids = stackalloc int[MaxPortraitsPerFrame];
        float* verts = stackalloc float[VertexBatch.Floats * 6 * MaxPortraitsPerFrame];
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 5;
            int tid = AurTex.TidByName(_portraitRef[i]);
            if (tid == 0) continue;
            PortraitDiag(tid, _portraitRef[i]);
            tids[drawn] = tid;
            TextRig.FillQuad(verts + drawn * VertexBatch.Floats * 6,
                             new Sowx.Services.Chat.Core.Model.PortraitQuad(
                                 _portraitRect[o], _portraitRect[o + 1], _portraitRect[o + 2], _portraitRect[o + 3],
                                 _portraitRef[i], default),
                             0f, _portraitRect[o + 4]);
            drawn++;
        }
        if (drawn == 0) return;

        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        float* m = stackalloc float[16];
        TextRig.Ortho(m, vp[2], vp[3]);

        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.UseProgram(TextRig._progRgb);
        Gl.UniformMatrix4fv(TextRig._uProjRgb, 1, (byte)Gl.FALSE, m);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(TextRig._uTexRgb, 0);
        Gl.BindVertexArray(TextRig._pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, TextRig._pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(VertexBatch.Floats * 6 * drawn * sizeof(float)), verts, Gl.DYNAMIC_DRAW);
        for (int i = 0; i < drawn; i++)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)tids[i]);
            Gl.DrawArrays(Gl.TRIANGLES, i * 6, 6);
        }
    }

    // ---- settings panel ----------------------------------------------------------------------

    private static VertexBatch? _panelBatch;
    private static float[] _panelVerts = System.Array.Empty<float>();
    private static int _panelCount;
    /// <summary>The panel's geometry is rebuilt only on a real change (open, press, drag, relayout): the
    /// batch's <c>ToArray</c> is a fresh array, so rebuilding it every frame would allocate on the render
    /// thread for as long as the panel is open.</summary>
    private static bool _panelDirty = true;

    private static void DrawPanel()
    {
        NameplateSettings? s = _settings;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (s == null || atlas == null || !s.IsOpen || !GameGate.InGame) return;
        if (_panelDirty || _panelEpoch != TextRig.AtlasEpoch)
        {
            _panelDirty = false; _panelEpoch = TextRig.AtlasEpoch;
            VertexBatch b = _panelBatch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
            b.Reset(atlas.SolidU, atlas.SolidV);
            s.Panel.Emit(b, atlas, atlas.LineHeight, new SettingsPanel.Palette
            {
                Body = (0.02f, 0.02f, 0.03f, 1f),
                TitleBar = (0.10f, 0.12f, 0.16f, 1f),
                Active = (0.20f, 0.34f, 0.52f, 0.95f),
                TextRgb = 0xd8dee9u,
            });
            _panelVerts = b.ToArray();
            _panelCount = b.VertexCount;
        }
        if (_panelCount > 0) TextRig.DrawBatch(_panelVerts, _panelCount);
    }

    private static int _panelEpoch = -1;

    private static bool _panelPress;

    private static bool OnPanelButton(bool down, int button, int x, int y)
    {
        NameplateSettings? s = _settings;
        if (s == null || button != 1) return false;
        if (down)
        {
            _panelPress = false;
            if (!s.IsOpen) return false;
            _panelPress = true;
            s.MouseDown(x, y);
            _panelDirty = true;
            SyncPanelModal();     // a click-off or the close button may have just shut it
            return true;
        }
        if (!_panelPress) return false;
        _panelPress = false;
        s.MouseUp();
        _panelDirty = true;
        if (_configDirty) SaveConfig();
        SyncPanelModal();
        return true;
    }

    /// <summary>Mirror the panel's open state onto the window's modal flag. Driven from the input thread at every
    /// edge that can open or close it -- a render-thread sync would lag by a frame, and one frame is one keypress
    /// leaking to a gameplay quickslot.</summary>
    private static void SyncPanelModal()
    {
        if (_panelWindow != null) _panelWindow.SuppressChords = _settings is { IsOpen: true };
    }

    /// <summary>Keyboard while the settings panel is open, the shape <see cref="Sowx.Services.Dialog"/>'s panel
    /// uses: an in-progress value-box edit takes digits / dot / minus / backspace, Return commits and persists,
    /// Esc cancels the edit and then closes the panel. Every key is consumed while the panel is up -- without
    /// that, typing <c>1.5</c> into a slider's box also fires quickslots 1 and 5.</summary>
    private static bool OnPanelKey(uint sdlKey, int mods)
    {
        NameplateSettings? s = _settings;
        if (s is not { IsOpen: true }) return false;
        if (s.Editing)
        {
            sdlKey = NormalizeKeypad(sdlKey);
            switch (sdlKey)
            {
                case KeyReturn: s.CommitEdit(); if (_configDirty) SaveConfig(); break;
                case KeyEsc: s.CancelEdit(); break;
                case KeyBackspace: s.EditBackspace(); break;
                default:
                    if ((sdlKey >= KeyDigit0 && sdlKey <= KeyDigit9) || sdlKey == KeyDot || sdlKey == KeyMinus)
                        s.EditInsert(((char)sdlKey).ToString());
                    break;
            }
            _panelDirty = true;
            return true;
        }
        if (sdlKey == KeyEsc) { s.Close(); _panelDirty = true; SyncPanelModal(); }
        return true;
    }

    private const uint KeyBackspace = 0x08, KeyReturn = 0x0D, KeyEsc = 0x1B,
                       KeyMinus = 0x2D, KeyDot = 0x2E, KeyDigit0 = 0x30, KeyDigit9 = 0x39;

    /// <summary>Numpad SDL keycodes (SDLK_KP_*, scancode-mask bit set) to their ASCII equivalents, so keypad
    /// digits drive the value box exactly like the main row. Same table as the dialog panel's.</summary>
    private static uint NormalizeKeypad(uint k) => k switch
    {
        0x40000058 => KeyReturn,                  // KP_ENTER
        0x40000063 => KeyDot,                     // KP_PERIOD
        0x40000056 => KeyMinus,                   // KP_MINUS
        0x40000062 => KeyDigit0,                  // KP_0
        >= 0x40000059 and <= 0x40000061 => k - 0x40000059 + KeyDigit1,   // KP_1..KP_9
        _ => k,
    };

    private const uint KeyDigit1 = 0x31;

    private static bool OnPanelMotion(int x, int y)
    {
        NameplateSettings? s = _settings;
        if (s is not { IsOpen: true } || !s.MouseMove(x, y)) return false;
        _panelDirty = true;
        return true;
    }

    // ---- input: click a plate -> /tell -------------------------------------------------------

    /// <summary>Set while a press landed on a plate name, so the matching release is consumed too and the engine
    /// never sees a button-up with no button-down.</summary>
    private static bool _plateGrab;

    /// <summary>Click a plate's NAME to start a tell to it. Reuses the chat's own Tell entry path
    /// (<see cref="ChatService.StartTell"/>) -- the same one the chat portrait click uses.
    /// <para>This window publishes NO hit-test override, so the pointer over a plate is not framework UI: the
    /// cursor stays the engine's and every click outside the narrow name box falls straight through to the game
    /// world. Opt-in (<see cref="NameplateConfig.ClickToTell"/> ships off) because even the name box is world
    /// the player would otherwise have clicked.</para></summary>
    /// <summary>A plate name as one line again, for the chat entry: the name carries the LFs
    /// <c>PlateLayout.BreakName</c> put there, and an embedded newline in a <c>/tell</c> prefill is a bug.
    /// Cold (a click), so the copy costs nothing that matters; a break-free name is returned as-is.</summary>
    private static string Flatten(string name) => name.IndexOf('\n') < 0 ? name : name.Replace('\n', ' ');

    private static bool OnPlateClick(bool down, int button, int x, int y)
    {
        if (button != 1) return false;
        if (!down) { bool had = _plateGrab; _plateGrab = false; return had; }
        if (!_cfg.ClickToTell) return false;
        int cur = _hitCur;
        Hit[] h = _hits[cur];
        int n = _hitCount[cur];
        for (int i = 0; i < n && i < h.Length; i++)
            if (x >= h[i].X && x < h[i].X + h[i].W && y >= h[i].Y && y < h[i].Y + h[i].H)
            { ChatService.StartTell(Flatten(h[i].Name)); _plateGrab = true; return true; }
        return false;
    }

    // ---- /nameplate --------------------------------------------------------------------------

    private static string? Cmd(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0)
        {
            int word = NameplateNative.FeedbackWord();
            return $"[nameplate] on={_cfg.On} mode={_cfg.Mode} outline={_cfg.Outline} bg={_cfg.Background} "
                 + $"portraits={_cfg.Portraits} band={_cfg.HealthBand} bar={_cfg.HealthBar} "
                 + $"maxdist={_cfg.MaxDistance.ToString("F0", CultureInfo.InvariantCulture)} "
                 + $"fadedist={_cfg.FadeDistance.ToString("F0", CultureInfo.InvariantCulture)} "
                 + $"lines={_cfg.BubbleLines} queue={_cfg.BubbleQueue} "
                 + $"width={_cfg.BubbleWidth.ToString("F0", CultureInfo.InvariantCulture)} "
                 + $"channelstyle={_cfg.ChannelStyle} clicktotell={_cfg.ClickToTell} "
                 + $"anim={_cfg.AnimMs.ToString("F0", CultureInfo.InvariantCulture)}ms "
                 + $"| tracked={_table.VisibleCount} overflow={_table.Overflow} feedbackword={word}";
        }

        switch (a[0])
        {
            case "settings":
                _settings?.OpenAt(TextRig.ViewportW, TextRig.ViewportH);
                _panelDirty = true; SyncPanelModal();
                return "[nameplate] settings open";
            case "on": _cfg.On = Flag(a); break;
            case "mode": _cfg.Mode = Clamp(Num(a, _cfg.Mode), NameplateConfig.ModeMin, NameplateConfig.ModeMax); break;
            case "outline": _cfg.Outline = Flag(a); break;
            case "bg": case "background": _cfg.Background = Flag(a); break;
            case "portraits": _cfg.Portraits = Flag(a); break;
            case "band": _cfg.HealthBand = Flag(a); break;
            case "bar": _cfg.HealthBar = Flag(a); break;
            case "maxdist": _cfg.MaxDistance = ClampF(NumF(a, _cfg.MaxDistance), 0f, NameplateConfig.DistMax); break;
            case "fadedist": _cfg.FadeDistance = ClampF(NumF(a, _cfg.FadeDistance), 0f, NameplateConfig.DistMax); break;
            case "lines": _cfg.BubbleLines = Clamp(Num(a, _cfg.BubbleLines), NameplateConfig.LinesMin, NameplateConfig.LinesMax); break;
            case "queue": _cfg.BubbleQueue = Clamp(Num(a, _cfg.BubbleQueue), NameplateConfig.QueueMin, NameplateConfig.QueueMax); break;
            case "width": _cfg.BubbleWidth = ClampF(NumF(a, _cfg.BubbleWidth), NameplateConfig.WidthMin, NameplateConfig.WidthMax); break;
            case "channelstyle": _cfg.ChannelStyle = Flag(a); break;
            case "clicktotell": _cfg.ClickToTell = Flag(a); break;
            case "anim": _cfg.AnimMs = ClampF(NumF(a, _cfg.AnimMs), NameplateConfig.AnimMin, NameplateConfig.AnimMax); break;
            case "probe":
                _probe = true;
                return "[nameplate] probe armed -- the next drawn frame dumps the anchor conversion to the log "
                     + "(needs to be in-game with the HUD up)";
            default:
                return "[nameplate] usage: /nameplate | /nameplate settings | /nameplate <on|mode|outline|bg|"
                     + "portraits|band|bar|maxdist|fadedist|lines|queue|width|channelstyle|clicktotell|anim> <value>"
                     + " | /nameplate probe"
                     + " (width = the max text width for BOTH a bubble box and a plate name, design px)"
                     + " (values clamp: mode 0-2, dist 0-100, width 120-480, anim 0-1000, queue 1-"
                     + NameplateConfig.QueueMax + ", lines 1-" + NameplateConfig.LinesMax + ")";
        }
        SaveConfig();
        _settings?.Layout();
        _panelDirty = true;
        return $"[nameplate] {a[0]} -> ok";
    }

    private static bool Flag(string[] a) => a.Length < 2 || a[1] != "0";
    private static int Num(string[] a, int fallback)
        => a.Length > 1 && int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    private static float NumF(string[] a, float fallback)
        => a.Length > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
