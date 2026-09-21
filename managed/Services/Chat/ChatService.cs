using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>
/// Native-C# chatbox (map #13, architecture #17). One <see cref="IExtenderService"/>;
/// concerns split into pure Core classes (<c>Chat/Core/</c>, unit-testable, no native
/// refs) behind this thin shell of hooks/GL/SDL. State is static because the
/// <c>[UnmanagedCallersOnly]</c> handlers must be. Coexists with
/// <c>HotkeyInputService</c>; does not absorb it.
/// </summary>
public sealed unsafe class ChatService : IExtenderService
{
    private static ExtenderLog? _log;

    /// <summary>Shared arbitration flag: <c>HotkeyInputService</c> reads this to
    /// suppress gameplay hotkeys while the chat edit control owns the keyboard.
    /// Only ever set true with the chatbox up (<see cref="Enabled"/>).</summary>
    public static volatile bool IsTyping;

    /// <summary>The keyboard is wanted by chat typing OR an inline settings-field edit. HotkeyInputService
    /// routes keys/text into ChatService and suppresses gameplay hotkeys whenever this is true.</summary>
    public static bool WantsKey => IsTyping || _settings.Editing;

    /// <summary>The chat master, as it booted (<c>chat.enabled</c>, a Restart row -- ADR 0025). Off, the chatbox
    /// installs nothing: no tap, render, input capture or suppression, so the stock chat window runs untouched
    /// and HotkeyInputService observes the vanilla chat-focus hooks instead. The one pivot both services read.</summary>
    public static bool Enabled => ChatKnobs.Enabled.Boot;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();
        ChatWindow.SelfCheck(log);     // slice #21 drag/resize/tab/scroll proof (off-device)
        ChatLayout.SelfCheck(log);     // #37 Main-undeletable / always->=1-tab / add-close window proof (off-device)
        SettingsWindow.SelfCheck(log); // settings window open/toggle/close/ownership proof (off-device)
        TellTarget.SelfCheck(log);     // #45 tell tab send-decision proof (off-device)
        TellRouter.SelfCheck(log);     // #46 tell auto-open routing proof (off-device)
        WindowRenderer.SelfCheck(log); // #26 portrait-gutter emission proof (off-device)
        RowAlign.SelfCheck(log);       // shared row alignment/justify primitive proof (off-device)
        ChatAnim.SelfCheck(log);       // map #13 fade/slide/scroll math proof (off-device)
        ChatConfig.SelfCheck(log);     // per-window pair round-trip proof (off-device)
        TextRig.SelfCheck(log);   // #33 session-clear drops cached engine texture pointers (off-device)
        FontRegistry.SelfCheck(log);
        SettingsWindow.ColorResolver = WindowRenderer.CurrentColor;
        string[] names = FontRegistry.Names;   // "default" first: the window's own entry stands in for it
        var faces = new string[names.Length];
        faces[0] = SettingsWindow.FollowUi;
        Array.Copy(names, 1, faces, 1, names.Length - 1);
        SettingsWindow.Typefaces = faces;
        if (!Enabled)   // chat.enabled=0 at boot: install nothing -> the stock chat window runs
        {
            log.Info("ChatService: chat.enabled=0 -- standing down, vanilla chat left intact (/chat enabled 1 + relaunch)");
            return;
        }
        if (!SymbolMap.Require(log, "ChatService", RequiredSymbols)) return;   // #419: whole or nothing
        InitMessageCore(log, hooks);   // build slice #18
        InitRender(log, hooks);        // build slice #21: own SwapBuffers -> draw the window (bakes the atlas)
        InitSuppression(log, hooks);   // build slice #23: neutralize vanilla chat rendering
        InitEditControl(log, hooks);   // build slice #20
    }

    /// <summary>The chat unit (#419): every symbol the chatbox cannot run without -- ingest tap, vanilla
    /// suppression, openers, senders, name lookups, SDL text/clipboard, the glyph bake and the GL loader. One
    /// unresolved member stands the whole feature down (a half-live chatbox is worse than vanilla). Optional and
    /// NOT listed: AurTextureGet (portraits), GetModule (game-time stamps fall back to wall clock).</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.AppendToMsgBuffer, SymbolMap.AppendToChatBuffer, SymbolMap.ChatWindowSetFilter,
        SymbolMap.ChatWindowShow, SymbolMap.ChatPromptShow, SymbolMap.SetFocusChatPrompt, SymbolMap.ShowChatPrompt,
        SymbolMap.PrepChatPromptForTell, SymbolMap.ChatPromptSetFocus, SymbolMap.ChatPromptCtor, SymbolMap.GuiPanelDeactivate,
        SymbolMap.SendChatTalk, SymbolMap.SendChatShout, SymbolMap.SendChatWhisper, SymbolMap.SendChatParty,
        SymbolMap.SendChatDM, SymbolMap.SendChatTell, SymbolMap.SendChatTellId, SymbolMap.NotifyServerGuiEvent,
        SymbolMap.GetPlayerByPlayerName, SymbolMap.GetPlayerByCreatureName, SymbolMap.AppManager,
        SymbolMap.SwapBuffers,
        SymbolMap.SdlStartTextInput, SymbolMap.SdlGetClipboardText, SymbolMap.SdlSetClipboardText, SymbolMap.SdlFree,
        SymbolMap.SdlGlGetProcAddress, SymbolMap.StbttInitFont, SymbolMap.StbttScaleForPixelHeight,
        SymbolMap.StbttGetFontVMetrics, SymbolMap.StbttGetCodepointHMetrics, SymbolMap.StbttGetCodepointBitmapBox,
        SymbolMap.StbttMakeCodepointBitmap,
    };

    /// <summary>Backlog every ingested line lands in; tab views read from it (slice #19).</summary>
    private static readonly ChatStore _store = new();

    private static long _ingestCount;

    /// <summary>Log the first line and every Nth after, so a live run leaves visible tap-&gt;store proof.</summary>
    private const int LogEveryNth = 50;

    /// <summary>16-byte CResRef by value. Only r9 is free when it's passed, so SysV puts the whole
    /// struct on the stack (r9 unused) -- declaring it as a struct makes NativeAOT lower it that way.
    /// Passing it as two ulongs instead splits it r9+stack and corrupts the forwarded call.

    /// and the callee copies the 17th byte), so there the slot is an <c>IntPtr</c> forwarded untouched.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CResRef16 { public ulong Lo; public ulong Hi; }
#if WINDOWS
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void> _tapOrig;
#else
    // AppendToMsgBuffer(this, CExoString* text, uint channel, uint playerId, int hasPortrait, CResRef).
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void> _tapOrig;
#endif

    /// <summary>Slice #18: cold inbound tap on AppendToMsgBuffer -&gt; parse -&gt; ChatStore.Append.</summary>
    private static void InitMessageCore(ExtenderLog log, HookService hooks)
    {
        ColorRunParser.SelfCheck(log);
        ChatStore.SelfCheck(log);

#if WINDOWS
        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)&OnAppend);
#else
        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)&OnAppend);
#endif
        log.Info(_tapOrig != null
#if WINDOWS
            ? "ChatService: inbound tap installed (AppendToMsgBuffer, Win64 CResRef* ABI)"
#else
            ? "ChatService: inbound tap installed (AppendToMsgBuffer)"
#endif
            : "ChatService: inbound tap not installed (Install returned Zero: symbol unresolved or already hooked)");
    }

    [UnmanagedCallersOnly]
#if WINDOWS
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, IntPtr portraitPtr)
    {
        CResRef16 portrait = portraitPtr != IntPtr.Zero ? *(CResRef16*)portraitPtr : default;
        string raw = ReadExo(text);   // BEFORE the forward: the Win64 callee destroys its by-value CExoString
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portraitPtr); // vanilla chat still needs it
#else
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, CResRef16 portrait)
    {
        string raw = ReadExo(text);
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portrait); // vanilla chat still needs it
#endif
        try
        {
            string portraitRef = hasPortrait != 0 ? ResRef(portrait) : "";
            // Outbound Tell echoes: the server sends back a plain tell with speaker = self and no recipient
            // anywhere in it (nwserver SendServerToPlayerChatMessage case 4), so the name comes from what we sent.
            string tellRecip = (channel & 0x002) != 0 && _playerResolver?.LocalIngestId() == playerId
                ? TellTarget.TakeEcho(raw) : "";
            // ponytail: senderName "" -- the tap doesn't carry it (inline in raw). SenderName component is the ceiling.
            var line = new ChatLine(channel, "", playerId, portraitRef,
                ColorRunParser.Parse(raw), DateTime.UtcNow, raw, GameNow(), tellRecip);
            // Tells are rare + this path is cold: log every one unconditionally (throttle covers the rest).
            if ((channel & 0x002) != 0)
                _log?.Info($"[chat] tell ingest: pid={playerId} local={_playerResolver?.LocalIngestId()} recip=\"{tellRecip}\" raw={raw}");
            _store.Append(line);

            long n = ++_ingestCount;
            // One-shot thread-model probe (#33 follow-up): is ingest really a separate thread from the frame
            // present? If ingest tid == swap tid the "cold network thread" comments are wrong and the store
            // races don't exist. Compare the two [chatthread] lines in a live cx.log.
            if (n == 1)
            {
                int itid = Environment.CurrentManagedThreadId;
                _log?.Info($"[chatthread] ingest tid={itid} swap tid={_swapTid} same={(itid == _swapTid)}");
            }
            if (n == 1 || n % LogEveryNth == 0)
                _log?.Info($"[chat] ingest #{n} ch=0x{channel:x} pid={playerId} portrait={portraitRef} " +
                           $"runs={line.ColorRuns.Length} text={raw}");
        }
        // An escaped exception aborts the NativeAOT process (the live client), so swallow ingest
        // faults -- but log the first, or a parse/append bug is permanently silent (no ingest ever).
        catch (Exception e) { if (_faults++ == 0) _log?.Info($"[chat] ingest fault (swallowed): {e}"); }
    }

    private static long _faults;

    /// <summary>Decode a CExoString ({char* buf; uint len}) to a string (Latin1 keeps color bytes intact).</summary>
    private static string ReadExo(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return "";
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        if (buf == null || len == 0 || len > 4096) return "";
        var sb = new StringBuilder((int)len);
        for (uint k = 0; k < len; k++) sb.Append((char)buf[k]);
        return sb.ToString();
    }

    /// <summary>Decode a 16-byte CResRef (null-padded ASCII) to its resref string.</summary>
    private static string ResRef(CResRef16 r)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, r.Lo);
        BitConverter.TryWriteBytes(b.Slice(8), r.Hi);
        int n = 0;
        while (n < 16 && b[n] != 0) n++;
        return Encoding.ASCII.GetString(b.Slice(0, n));
    }

    // ---- slice #21: window + owned render -----------------------------------------------------

    /// <summary>The chat window layout (#37/#41): the ordered window list ChatService drives for draw,
    /// input, and persistence. Index 0 is the undeletable Main; <see cref="ChatLayout.FocusedIndex"/>
    /// tracks which window owns the keyboard + input strip.</summary>
    private static ChatLayout? _layout;

    /// <summary>The focused chat window (owns keyboard + input strip, #41). Falls back to Main.</summary>
    private static ChatWindow? _focused => _layout?.Focused;

    /// <summary>The chat settings window (opened from the gear icon), replacing the old inline gear dropdown so
    /// the option list can grow. Edits <see cref="ChatWindow.Toggles"/>; holds no option state of its own.</summary>
    private static readonly SettingsWindow _settings = new();

    /// <summary>Name→live PlayerId resolver for Tell-pinned tabs (#45). Null until the shell wires a real
    /// roster-backed implementation; pinned tabs silently drop sends while null (safe degradation).</summary>
    private static IPlayerResolver? _playerResolver;

    /// <summary>Native player roster resolver: CClientExoApp::GetPlayerByCreatureName first (pinned tab
    /// names are character names), then GetPlayerByPlayerName; reads the player ID at CNWCPlayer+0x8.
    /// Every name this hands out is the CHARACTER name: pins from the player list, nameplates, portraits
    /// and incoming tells must all live in one name space or a reply opens a second tab. Cold.</summary>
    // ponytail: exact-case match (engine strcmp); own roster walk (+0x38/+0x40) with case-folding if players trip
    private sealed class NativePlayerResolver : IPlayerResolver
    {
        private readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> _getByName;
        private readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> _getByCreatureName;

        // CNWCPlayer field offsets (verified via Ghidra decompilation).
        // +0x8  = the player ID: what vanilla ParseChatString reads (-1 = invalid) and passes to
        //         SendPlayerToServerChat_Tell, and what AppendToMsgBuffer passes as playerId.
        // +0x40/+0x48 = character name (the one GetPlayerByCreatureName compares); +0x10/+0x18 is the
        // player/account name, which is NOT what the player list, nameplates or the roster show.
        private static readonly int IngestPlayerIdOff = EngineLayout.PlayerId;
        private static readonly int PlayerNameOff = EngineLayout.PlayerCreatureName;
        private static readonly int PlayerNameLenOff = EngineLayout.PlayerCreatureNameLen;

        public NativePlayerResolver(delegate* unmanaged<IntPtr, IntPtr, IntPtr> getByName,
                                    delegate* unmanaged<IntPtr, IntPtr, IntPtr> getByCreatureName)
        { _getByName = getByName; _getByCreatureName = getByCreatureName; }

        private static IntPtr App() => PlayerIdentity.App();

        public uint? Resolve(string name)
        {
            IntPtr app = App();
            if (app == IntPtr.Zero) return null;
            IntPtr exo = Native.NewExoString(name);
            try
            {
                IntPtr player = IntPtr.Zero;
                if (_getByCreatureName != null) player = _getByCreatureName(app, exo);
                if (player == IntPtr.Zero && _getByName != null) player = _getByName(app, exo);
                if (player == IntPtr.Zero) return null;
                uint id = *(uint*)(player + IngestPlayerIdOff);
                return id == 0xffffffff ? null : id;
            }
            finally { Native.FreeExoString(exo); }
        }

        private static readonly int InternalOff = EngineLayout.ExoAppInternal;
        private static readonly int ListPtrOff = EngineLayout.InternalPlayers;
        private static readonly int ListCountOff = EngineLayout.InternalPlayerCount;

        /// <summary>Read the character name for an ingest PlayerId (+0x8), or null if the player is gone.</summary>
        public string? NameOf(uint playerId)
        {
            IntPtr app = App();
            if (app == IntPtr.Zero) return null;
            IntPtr intern = *(IntPtr*)(app + InternalOff);
            if (intern == IntPtr.Zero) return null;
            IntPtr listBase = *(IntPtr*)(intern + ListPtrOff);
            int count = *(int*)(intern + ListCountOff);
            if (listBase == IntPtr.Zero || count <= 0 || count > 64) return null;
            for (int i = 0; i < count; i++)
            {
                IntPtr player = *(IntPtr*)(listBase + i * 8);
                if (player == IntPtr.Zero) continue;
                if (*(uint*)(player + IngestPlayerIdOff) != playerId) continue;
                IntPtr buf = *(IntPtr*)(player + PlayerNameOff);
                if (buf == IntPtr.Zero) return null;
                uint len = *(uint*)(player + PlayerNameLenOff);
                if (len == 0 || len > 128) return null;
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)*(byte*)(buf + (int)k));
                return sb.ToString();
            }
            return null;
        }

        /// <summary>The local player's ingest-space playerId, from the shared roster walk. Cold (Tell ingest only).</summary>
        public uint? LocalIngestId()
        {
            IntPtr player = PlayerIdentity.LocalPlayer();
            return player == IntPtr.Zero ? null : *(uint*)(player + IngestPlayerIdOff);
        }
    }
    /// <summary>Always-closed stub passed to non-focused window builds so EmitSettings doesn't duplicate.</summary>
    private static readonly SettingsWindow _closedSettings = new();

    private static volatile int _lastMx, _lastMy;   // latest pointer pos, for wheel hit-testing
    private static bool _mdiag;                      // one-shot: log first click coords + rect
    private static int _hoverIndex = -1;             // window under the pointer (-1 = none), for grip visibility
    private static bool _anyTabDrag;                 // true while any window has an active tab drag (#42)

    // *g_pAppManager, for the game clock (below) and the Tell player resolver. The in-game gate that used to
    // walk this chain now lives in UiHostService (#62); chat reads GameGate.InGame / GameGate.InModule instead.
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    // In-game game clock (chat game-time timestamps). The server syncs ONLY the game HOUR to the client, into
    // CNWCModule+0x16c (verified: the free-running CWorldTimer is never anchored to module time, which is why
    // reading it showed a wrong hour). CClientExoApp::GetModule(app) fetches the module off the same
    // *g_pAppManager chain the gate walks. minute/second aren't on the wire, so we anchor real-time to each
    // hour edge (PollGameClock, per frame) and interpolate a sub-hour minute at ingest.
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _getModule =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
    private static readonly int ModuleHourOff = EngineLayout.ModuleHour;
    private static readonly int ModuleMinutesPerHourOff = EngineLayout.ModuleMinutesPerHour;

    private static int _gmHour = -1;    // last polled server game hour (0..23); -1 = none seen -> local fallback
    private static long _gmHourMs;      // TickCount64 when _gmHour last changed (sub-hour interpolation anchor)
    private static int _gmMph = 2;      // module MinutesPerHour (real minutes per game hour); 2 = engine default

    /// <summary>The client CNWCModule (server-synced state), or Zero off a module (main menu / run-demo). All
    /// guarded pointer reads off *g_pAppManager, then CClientExoApp::GetModule(app).</summary>
    private static IntPtr GameModule()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return IntPtr.Zero;
        return _getModule(app);
    }

    /// <summary>Track the server-synced game hour each frame so ingest can interpolate a sub-hour minute. Hot,
    /// zero-alloc: pointer reads + one call. Resets to "unseen" off a module. An out-of-range hour (e.g. a
    /// wrong offset, or the field still zero pre-first-update reading as a valid midnight) is treated as unseen.</summary>
    private static void PollGameClock()
    {
        IntPtr module = GameModule();
        if (module == IntPtr.Zero) { _gmHour = -1; return; }
        int hour = *(int*)(module + ModuleHourOff);
        if ((uint)hour > 23u) { _gmHour = -1; return; }   // guards a bad offset -> local fallback
        int mph = *(byte*)(module + ModuleMinutesPerHourOff);
        _gmMph = mph > 0 ? mph : 2;
        if (hour != _gmHour) { _gmHour = hour; _gmHourMs = Environment.TickCount64; }   // hour edge -> re-anchor
    }

    /// <summary>Current in-game time, or an invalid <see cref="GameStamp"/> (-> the caller uses local) before
    /// any server hour is seen. The HOUR is authoritative (server-synced); the minute/second are extrapolated
    /// from real time since the hour edge and self-correct at each rollover. Cold (ingest). ponytail: the wire
    /// carries only the hour, so minute/second are an estimate -- they drift/pin under pause/timestop (the
    /// engine's own lighting interpolator has the same limit); HH-only is the fallback if this reads wrong live.</summary>
    private static GameStamp GameNow()
    {
        int hour = _gmHour;
        if (hour < 0) return default;
        long hourLenMs = (long)_gmMph * 60000L;
        if (hourLenMs <= 0) return new GameStamp(hour, 0, 0);
        double frac = (Environment.TickCount64 - _gmHourMs) / (double)hourLenMs;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;   // clamp into the hour (pause / just-past-rollover slop)
        int minute = (int)(frac * 60); if (minute > 59) minute = 59;
        int second = (int)(frac * 3600) % 60;
        return new GameStamp(hour, minute, second);
    }

    /// <summary>Restore the per-window state from the <c>chat</c> group's Hidden bag (bound from <c>cx.cfg</c>
    /// pre-main, so this runs at Initialize) and apply it to every window (rect/toggles/input side/send
    /// channel/tabs). Creates additional windows from the config up to the cap.</summary>
    private static void LoadConfig()
    {
        if (_layout == null) return;
        ChatConfig config = ChatConfig.From(ChatKnobs.Windows.Pairs);
        for (int i = 0; i < config.Windows.Count && i < ChatLayout.MaxWindows; i++)
        {
            var wc = config.Windows[i];
            ChatWindow win;
            if (i < _layout.Count)
            {
                win = _layout.Windows[i];
            }
            else
            {
                // Only create additional windows for config entries with a real rect (skip gap-filled defaults).
                if (wc.W < ChatWindow.MinW || wc.H < ChatWindow.MinH) continue;
                win = new ChatWindow(wc.X, wc.Y,
                    System.Math.Max(wc.W, ChatWindow.MinW), System.Math.Max(wc.H, ChatWindow.MinH),
                    new[] { new Tab("All", 0xffffffff) });
                if (_layout.AddWindow(win) == null) break;
            }
            if (wc.W >= ChatWindow.MinW && wc.H >= ChatWindow.MinH)
            { win.X = wc.X; win.Y = wc.Y; win.W = wc.W; win.H = wc.H; }
            win.Toggles = wc.Toggles;
            win.InputOnTop = wc.InputOnTop;
            if (wc.SendChannel >= 0 && wc.SendChannel <= 5) win.SendChannel = (Channel)wc.SendChannel;
            if (wc.AnimEnabled is bool ae) win.AnimEnabled = ae;
            if (wc.HoldSec is float hs) win.HoldSec = hs;
            if (wc.FadeSec is float fs) win.FadeSec = fs;
            if (wc.TauMs is float tm) win.TauMs = tm;
            if (wc.Outline is bool ol) win.Outline = ol;
            if (wc.FontPx is int fp && fp >= TextRig.MinFontPx && fp <= TextRig.MaxFontPx) win.FontPx = fp;
            if (wc.Typeface is { Length: > 0 } tfn) win.Typeface = tfn;
            if (wc.TimeFormat is int tf) win.TimeFormat = (TsFormat)tf;
            if (wc.TimeSource is int ts) win.TimeSource = (TsSource)ts;
            if (wc.Align is int al && al >= 0 && al <= 3) win.Align = (TextAlign)al;
            if (wc.BgMode is int bgm && bgm >= 0 && bgm <= 3) win.BgMode = (BgMode)bgm;
            if (wc.BgAngle is float ba) win.BgAngle = Math.Clamp(ba, 0f, 360f);
            if (wc.BgStart is float bs) win.BgStart = Math.Clamp(bs, 0f, 1f);
            if (wc.BgEnd is float be) win.BgEnd = Math.Clamp(be, 0f, 1f);
            if (wc.AutoHideHeader is bool ahh) win.AutoHideHeader = ahh;
            if (wc.AutoHideFrame is bool ahf) win.AutoHideFrame = ahf;
            if (wc.Locked is bool lk) win.Locked = lk;
            if (wc.TellsHere is bool th) win.TellsHere = th;
            foreach (var kv in wc.Colors) win.Colors[kv.Key] = kv.Value;
            if (wc.Tabs.Count > 0)
            {
                var tabs = new Tab[wc.Tabs.Count];
                for (int j = 0; j < wc.Tabs.Count; j++)
                    tabs[j] = new Tab(wc.Tabs[j].Label, wc.Tabs[j].Mask) { PinnedName = wc.Tabs[j].Pin, Kept = wc.Tabs[j].Kept };
                win.SetTabs(tabs);
            }
        }
    }

    /// <summary>Sync every window-owned field into the <c>chat</c> group's Hidden bag (a whole replace, so a
    /// closed window's keys leave the file) and arm the <c>cx.cfg</c> writer. AC4: ephemeral (auto-created,
    /// unkept) tabs are excluded; a non-Main window whose tabs are ALL ephemeral is dropped entirely.</summary>
    private static void SaveConfig()
    {
        if (_layout == null) return;
        var config = new ChatConfig();
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
        {
            ChatWindow win = wins[i];
            // AC4: skip non-Main windows where every tab is ephemeral (no kept content).
            if (i > 0)
            {
                bool allEph = true;
                var tabs = win.Tabs;
                for (int j = 0; j < tabs.Count; j++) if (!tabs[j].Ephemeral) { allEph = false; break; }
                if (allEph) continue;
            }
            var wc = new WindowConfig();
            wc.X = win.X; wc.Y = win.Y; wc.W = win.W; wc.H = win.H;
            wc.Toggles = win.Toggles; wc.InputOnTop = win.InputOnTop;
            wc.SendChannel = (int)win.SendChannel;
            wc.AnimEnabled = win.AnimEnabled; wc.HoldSec = win.HoldSec;
            wc.FadeSec = win.FadeSec; wc.TauMs = win.TauMs;
            wc.Outline = win.Outline;
            wc.FontPx = win.FontPx; wc.Typeface = win.Typeface;
            wc.TimeFormat = (int)win.TimeFormat; wc.TimeSource = (int)win.TimeSource;
            wc.Align = (int)win.Align;
            wc.BgMode = (int)win.BgMode; wc.BgAngle = win.BgAngle; wc.BgStart = win.BgStart; wc.BgEnd = win.BgEnd;
            wc.AutoHideHeader = win.AutoHideHeader; wc.AutoHideFrame = win.AutoHideFrame;
            wc.Locked = win.Locked;
            wc.TellsHere = win.TellsHere;
            foreach (var kv in win.Colors) wc.Colors[kv.Key] = kv.Value;
            for (int j = 0; j < win.Tabs.Count; j++)
            {
                if (win.Tabs[j].Ephemeral) continue; // AC4: unkept tabs excluded
                wc.Tabs.Add((win.Tabs[j].Label, win.Tabs[j].ChannelMask, win.Tabs[j].PinnedName, win.Tabs[j].Kept));
            }
            config.Windows.Add(wc);
        }
        ChatKnobs.Windows.Replace(config.Pairs());
    }

    /// <summary>Slice #21: build the windows from the persisted state, observe ingest, and register the framework
    /// Window whose content-draw is the whole chatbox. The atlas is the frame host's (<c>UiHostService</c> bakes
    /// it at <c>ui.font</c>), so the first content draw is the first real build.</summary>
    private static void InitRender(ExtenderLog log, HookService hooks)
    {
        // Tabs are pure receive-filters now (#25); the send channel is the dropdown (window-level).
        // Default is a single All tab; the player adds per-channel tabs via the settings window (#39).
        _layout = new ChatLayout(new ChatWindow(60, 40, 520, 300,
        [
            new Tab("All",  0xffffffff),
        ]));
        LoadConfig();   // the bag bound pre-main: rects, toggles, tabs are there at Initialize
        _store.AddObserver(OnIngest);  // new line -> slide-in/hold + rebuild (cold network thread)
        // #216: the chatbox is the extender's one text surface, so it is where any service's on-screen
        // notice lands -- an Alloy dropout above all. Set here rather than published as a chat API: Notice
        // is the seam, and nothing outside this file needs to know a chatbox drew the line.
        Notice.Sink = EchoLocal;

        // Register the framework Window whose content IS the whole chatbox (#62/#64). UiHostService drives
        // DrawContent once per frame in z-order; the framework input layer (UiInputService) routes captured
        // input through the window's delegates. The chatbox owns many sub-windows internally (ChatLayout), so it
        // stays one framework Window with a hit-test override (its UI extends outside any single rect -- popped-out
        // strips, dropdowns) and self-manages focus; WantsKeyboard is driven via KeyboardMode -> RequestKeyboard.
        _uiWindow = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = OnFrame,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = OnChatKey,
            OnText = InsertUtf8,
            HitTestOverride = (px, py) => MouseOverUi((int)px, (int)py),
        };
        UiHost.AddChordConsumer(new SlashChord());   // "/" opens a command line
        log.Info("ChatService: registered as a framework Window (content-draw + routed input), \"/\" opens a command line");
    }

    private static Window? _uiWindow;
    private static bool _booted;                  // first-frame viewport relayout done
    private static int _swapTid;                  // managed thread id of the frame-present thread (probe, #33)
    private static bool _wasInModule;             // last frame's GameGate.InModule, for the module-unload edge
    private static float _appliedScale = float.NaN;   // Metrics.Scale we last relaid at (font / GUI-scale edge); NaN = never
    private static int _appliedEpoch = -1;        // TextRig.AtlasEpoch we last relaid at (re-bake edge: typeface, a new face)
    private static float _panelPx, _panelSmallPx; // the settings panel's face heights at the last open

    /// <summary>The chord-consumer face of chat: an unmodified "/" that has fallen past every window opens the
    /// command line. Priority 1, so a hotkey bound to the same key (Priority 2) wins it.</summary>
    private sealed class SlashChord : IChordConsumer
    {
        /// <summary>SDL keycode for <c>/</c> (SDLK_SLASH), and the raw SDL modifier masks
        /// (KMOD_SHIFT / KMOD_CTRL) that mean the player meant something else.</summary>
        private const int KeySlash = 47, KmodShift = 0x0003, KmodCtrl = 0x00C0;

        public int Priority => 1;

        public bool WantsKey(int key) => key == KeySlash;

        public void Fire(uint key, int sdlMods)
        {
            // Any modifier means the player meant something else (shift+/ = "?", ctrl+/ = a bind).
            if (key != KeySlash || (sdlMods & (KmodShift | KmodCtrl)) != 0) return;
            OpenWith('/');
        }

        public void FireMouse(int button, int sdlMods) { }
    }

    /// <summary>Pull every chat window back inside the live viewport. The rect is restored from
    /// <c>cx.cfg</c> in absolute pixels, so a client that comes up smaller than the one that wrote it --
    /// windowed after fullscreen, a different monitor, a Proton prefix -- lands the box past the edge, where it
    /// draws every frame and nobody can see or drag it back. Same <see cref="Window.ClampAxis"/> Dialog uses.
    /// Viewport is whatever the last draw measured, so this is a no-op on the very first frame.</summary>
    private static void KeepOnScreen()
    {
        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        if (_layout == null || vpW <= 0f || vpH <= 0f) return;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
        {
            ChatWindow w = wins[i];
            float x = Window.ClampAxis(w.X, w.W, vpW), y = Window.ClampAxis(w.Y, w.H, vpH);
            if (x == w.X && y == w.Y) continue;
            _log?.Info($"[chat] window {i} was off-screen at ({w.X},{w.Y}) in {vpW}x{vpH} -- pulled to ({x},{y})");
            w.X = x; w.Y = y;
            DirtyAll();
        }
    }

    /// <summary>The chat window's per-frame content draw (invoked by UiHostService via <see cref="UiHost.DrawFrame"/>).
    /// Lays out for the live viewport once, reacts to the host-published gate + scale edges, then draws the box
    /// in-game (or the attribution overlay on the main menu). Hot + zero-alloc in the steady state: the bootstrap,
    /// session reset and scale relayout are all edge-gated (rare). Draws through ChatRenderer, which keeps chat's
    /// live-verified cold->hot frame hand-off internally.</summary>
    private static void OnFrame()
    {
        if (!_booted)
        {
            _booted = true;
            _swapTid = Environment.CurrentManagedThreadId;
            _log?.Info($"ChatService: first content draw [chatthread] swap tid={_swapTid}");
            _log?.Info($"ChatService: cfg dir = {Native.UserDirectory() ?? "(engine dir unresolved -> ~/.config fallback)"}");
            Rebuild();   // re-layout for the live viewport width
        }
        // Module-unload edge (was inside the old InGame() gate): drop cached engine ptrs + stale state (#33).
        // It now fires once per frame on the render thread instead of possibly on the input thread; ResetSession
        // is documented idempotent and zero-alloc, so the move is safe.
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) ResetSession();
        _wasInModule = inModule;

        if (GameGate.InGame)
        {
            // Scale edge: the frame host re-baked the atlas (ui.font or the game's UI scale moved) -> relay the chrome.
            if (Metrics.Scale != _appliedScale)
            {
                _appliedScale = Metrics.Scale;
                if (_layout != null)
                {
                    ChatWindow[] wins = _layout.Windows;
                    for (int i = 0; i < wins.Length; i++) { wins[i].ClampToMin(); EnsureFace(wins[i]); }   // window faces follow the GUI scale; the first in-game frame bakes the loaded ones
                }
                DirtyAll();
                Rebuild();   // re-lay the chrome at the new scale
            }
            // Re-bake edge: the sheet was re-baked (ui.typeface, a per-window face) -> every cached face is stale.
            if (TextRig.AtlasEpoch != _appliedEpoch)
            {
                _appliedEpoch = TextRig.AtlasEpoch;
                if (_settings.Open)
                {
                    GlyphAtlas? body = TextRig.BakedFace(_panelPx) ?? TextRig.Atlas;
                    _settings.SetFaces(body, TextRig.BakedFace(_panelSmallPx) ?? body);
                }
                DirtyAll();
                Rebuild();
            }
            PollGameClock();
            KeepOnScreen();
            ChatRenderer.Draw();
            DrawSettings();
            // Dialog and the quickbar draw themselves: each registers its own framework Window and UiHostService's
            // z-order roll-call invokes their content-draw after ours (#63). No cross-call from here.
        }
        else if (!inModule) ChatRenderer.DrawOverlay();   // main menu (no module): the attribution overlay
    }

    /// <summary>Slice #23: neutralize vanilla chat so only our box draws. Two SkipOriginal groups:
    /// (1) TEXT -- AppendToChatBuffer (new lines) + SetFilter (backlog re-render / reply buttons); the shared
    /// ring buffer is filled by AppendToMsgBuffer BEFORE these run, and that caller also frees the by-value
    /// CExoString temporary (both verified in the disasm), so skipping loses no data and leaks nothing.
    /// (2) CHROME -- CGuiInGameChatWindow::Show + CGuiInGameChatPrompt::Show, so the docked panels and the
    /// input prompt never activate into the render tree (Show pairs with Activate, Hide with Deactivate).
    /// We do NOT call Hide: it clears the window's IsVisible flag (win+0xc68), and rather than mirror that
    /// flag <see cref="InGame"/> now gates on module-presence, so an un-shown vanilla window doesn't blank
    /// our box. Input-side openers are skipped in <see cref="InitEditControl"/>. Never reached with the
    /// master off.</summary>
    private static void InitSuppression(ExtenderLog log, HookService hooks)
    {
        var skip = (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnSuppressed;
        _ = hooks.Install(SymbolMap.AppendToChatBuffer, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, long>)&OnSuppressedExo);   // text: new lines
        if (OperatingSystem.IsWindows())
        {
            // MSVC inlined CGuiInGameChatPrompt::Show into the prompt's ctor (#419), so the skip on Show alone
            // leaves the "Talk:" prompt activated from birth: undo it right after construction (= vanilla Hide).
            _promptCtorOrig = (delegate* unmanaged<IntPtr, IntPtr>)hooks.Install(SymbolMap.ChatPromptCtor,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&OnPromptCtor);
            _panelDeactivate = (delegate* unmanaged<IntPtr, int, void>)Native.Sym(SymbolMap.GuiPanelDeactivate);
            // ponytail: CGuiInGameChatWindow::DoSplitScreen (0x879560) also inlines Show, so toggling the
            // split-chat option activates the docked window on Windows; hook it the same way if anyone trips it.
        }
        _ = hooks.Install(SymbolMap.ChatWindowSetFilter, skip);   // text: backlog re-render + reply buttons
        _ = hooks.Install(SymbolMap.ChatWindowShow,      skip);   // chrome: docked message panels
        _ = hooks.Install(SymbolMap.ChatPromptShow,      skip);   // chrome: the "Talk:" input prompt
        log.Info("ChatService: vanilla chat suppressed (text: AppendToChatBuffer+SetFilter; chrome: Window/Prompt Show)");
    }

    // Skip-original: install the hook, never call the trampoline, so the vanilla path is a no-op. (IntPtr
    // self) ignores any further args -- safe under SysV caller-cleanup since we forward nothing. Return 0:
    // the Itanium mangling hides each real return type, so leave rax = 0 (reads as false/null/nothing-done)
    // in case a caller consumes it. One handler serves every single-`this` suppression target.
    [UnmanagedCallersOnly] private static long OnSuppressed(IntPtr self) => 0;

    /// <summary>Skip for a target taking a by-value CExoString. Win64 hands the callee a copy IT must destroy
    /// (decomp: every such body ends in delete[] + zero), so a skipped body would leak one buffer per line;
    /// SysV's caller destroys, so there the arg is left alone.</summary>
    [UnmanagedCallersOnly] private static long OnSuppressedExo(IntPtr self, IntPtr exo)
    {
        if (OperatingSystem.IsWindows()) Native.DestroyExoString(exo);
        return 0;
    }

    private static delegate* unmanaged<IntPtr, IntPtr> _promptCtorOrig;
    private static delegate* unmanaged<IntPtr, int, void> _panelDeactivate;
    private static readonly int WinPromptShownOff = EngineLayout.ChatPromptShown;

    [UnmanagedCallersOnly]
    private static IntPtr OnPromptCtor(IntPtr self)
    {
        IntPtr r = _promptCtorOrig != null ? _promptCtorOrig(self) : self;
        if (_panelDeactivate != null && *(int*)(self + WinPromptShownOff) != 0)
        {
            _panelDeactivate(self, 0);
            *(int*)(self + WinPromptShownOff) = 0;
        }
        return r;
    }

    /// <summary>New line ingested: auto-open Tell routing (#46), then compensate every window whose active
    /// tab matches (scroll hold / slide-in), then publish ONCE. Cold (network thread).</summary>
    private static void OnIngest(ChatLine line)
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (_layout == null || atlas == null) { Rebuild(); return; }

        // #46: route incoming Tells before the scroll-compensation loop so a new tab is visible on rebuild.
        // ponytail: races the input thread's AddTab/AddWindow; atomic array swap keeps it memory-safe.
        if ((line.Channel & 0x002) != 0)
            RouteIncomingTell(line);

        ChatWindow[] wins = _layout.Windows;   // snapshot: concurrent add/close swaps the array
        int fi = _layout.FocusedIndex;
        for (int i = 0; i < wins.Length; i++)
        {
            Tab active = wins[i].Active;
            if (!active.Matches(line)) continue;
            wins[i].Dirty = true;
            float grow = WindowRenderer.MeasureBlock(wins[i], line, WindowAtlas(wins[i], atlas)) + WindowRenderer.MsgGap;
            if (active.ScrollPx == 0f && i == fi && wins[i].AnimEnabled) ChatAnim.AddResidual(grow);
            else if (active.ScrollPx != 0f) active.ScrollPx += grow;

            // Mark non-active ephemeral tabs that match this line as unread.
            var tabs = wins[i].Tabs;
            int at = wins[i].ActiveTab;
            for (int ti = 0; ti < tabs.Count; ti++)
                if (ti != at && tabs[ti].Ephemeral && tabs[ti].Matches(line))
                    tabs[ti].Unread = true;
        }
        Rebuild();
    }

    /// <summary>Auto-open routing for an incoming Tell (#46). Creates a tab or window per the config;
    /// never steals keyboard focus (no FocusedIndex change on ingest).</summary>
    private static void RouteIncomingTell(ChatLine line)
    {
        if (_layout == null) return;
        // Any line carrying the local player's ingest ID is our own outbound tell echo -- skip routing so it
        // never spawns a phantom tab. Gating on the "[To ]" echo format instead would misfire on servers/locales
        // whose echo lacks that prefix; a genuine self-tell not auto-opening a tab is a non-loss.
        if (_playerResolver?.LocalIngestId() == line.PlayerId)
        { _log?.Info($"[chat] tell echo skipped pid={line.PlayerId}"); return; }
        string? name = _playerResolver?.NameOf(line.PlayerId);
        if (name == null)
        { _log?.Info($"[chat] tell route: unresolvable pid=0x{line.PlayerId:x}"); return; }

        TellAutoOpen mode = ChatKnobs.TellMode;
        var decision = TellRouter.Route(mode, _layout.Windows, _layout.FocusedIndex, name);
        _log?.Info($"[chat] tell route: name=\"{name}\" mode={mode} -> {decision.Action} w={decision.WindowIndex} t={decision.TabIndex}");
        switch (decision.Action)
        {
            case TellRoute.Exists:
            {
                // ponytail: refresh the stale connection-scoped PlayerId so the filter stays live after relog.
                ChatWindow[] wins = _layout.Windows;
                if ((uint)decision.WindowIndex < (uint)wins.Length)
                {
                    var tabs = wins[decision.WindowIndex].Tabs;
                    if ((uint)decision.TabIndex < (uint)tabs.Count)
                        tabs[decision.TabIndex].PlayerId = line.PlayerId;
                }
                break;
            }
            case TellRoute.NewTab:
            {
                ChatWindow[] wins = _layout.Windows;
                if ((uint)decision.WindowIndex < (uint)wins.Length)
                {
                    var tab = new Tab($"T:{name}", 0x002, line.PlayerId) { PinnedName = name };
                    wins[decision.WindowIndex].AddTab(tab);
                    wins[decision.WindowIndex].Dirty = true;
                    _log?.Info($"[chat] tell auto-open: new tab '{name}' (pid={line.PlayerId}) in window {decision.WindowIndex}");
                }
                break;
            }
            case TellRoute.NewWindow:
            {
                ChatWindow main = _layout.Main;
                float offset = _layout.Count * 30f;
                var tab = new Tab($"T:{name}", 0x002, line.PlayerId) { PinnedName = name };
                var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H, new[] { tab });
                if (_layout.AddWindow(win) != null)
                    _log?.Info($"[chat] tell auto-open: new window for '{name}' (pid={line.PlayerId})");
                else
                {
                    ChatWindow[] wins = _layout.Windows;
                    int fi = _layout.FocusedIndex;
                    if ((uint)fi < (uint)wins.Length) { wins[fi].AddTab(tab); wins[fi].Dirty = true; }
                    _log?.Info($"[chat] tell auto-open: window cap, tab fallback for '{name}'");
                }
                break;
            }
        }
    }

    /// <summary>Trigger a rebuild from outside ChatService (e.g. QuickbarService after a chrome change).
    /// Safe to call from the swap thread (same threading model as <see cref="OnFrame"/>).</summary>
    internal static void RequestRebuild() => Rebuild();

    /// <summary>Collects the screen rects of the other live extender windows for window-to-window snapping.
    /// Called once at drag-start on the input thread; only one window drags at a time (one mouse), so a single
    /// snapshot is sufficient. <paramref name="excludeChat"/> drops the dragged chat window by identity.</summary>
    public static (float X, float Y, float W, float H)[] GatherSnapRects(ChatWindow? excludeChat, bool includeDialog)
    {
        var list = new System.Collections.Generic.List<(float, float, float, float)>(6);
        ChatWindow[] wins = _layout?.Windows ?? System.Array.Empty<ChatWindow>();
        foreach (ChatWindow w in wins)
            if (!ReferenceEquals(w, excludeChat)) list.Add((w.X, w.Y, w.W, w.H));
        if (Quickbar.QuickbarService.SnapRectOut(out var q)) list.Add(q);
        if (includeDialog && Dialog.DialogService.SnapRectOut(out var d)) list.Add(d);
        return list.ToArray();
    }

    /// <summary>Per-window last-built frame cache. Keyed on the window reference so an add/close can't
    /// serve a stale frame for a different window.</summary>
    // ponytail: torn two-ref tuple read is a one-frame cosmetic (documented tolerance); volatile struct is the ceiling
    private static (ChatWindow? win, RenderFrame? frame)[] _lastBuilt =
        new (ChatWindow?, RenderFrame?)[ChatLayout.MaxWindows];

    private static VertexBatch? _panelBatch;

    /// <summary>Per frame (render thread, hot): the settings panel over every window, from its live state --
    /// a drag or a hover shows next frame with no rebuild (as the main panel draws; CX-683). Allocates nothing
    /// while open and idle: the batch is reused and <see cref="KnobPanel.Emit"/> is zero-alloc.</summary>
    private static void DrawSettings()
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (!_settings.Open || atlas == null) return;
        VertexBatch b = _panelBatch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        _settings.Panel.Emit(b);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    /// <summary>Mark every window dirty (config change, UI-scale change, session reset).</summary>
    private static void DirtyAll()
    {
        if (_layout == null) return;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++) wins[i].Dirty = true;
    }

    /// <summary>Rebuild vertex batches for all windows and publish them. Only the focused window gets the
    /// input strip, settings panel, and slide residual (#41). Non-focused windows whose
    /// <see cref="ChatWindow.Dirty"/> flag is false reuse their last-built frame, skipping the build +
    /// LOH scratch alloc (#53). The focused window is always rebuilt (it owns drag chrome, grip, strip).</summary>
    private static void Rebuild()
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (_layout == null || atlas == null) return;
        ChatWindow[] wins = _layout.Windows;   // snapshot
        int n = wins.Length;
        // Ghost tab: find the source window (if any) to append a floating ghost frame (#42).
        int dragSrc = -1;
        for (int i = 0; i < n; i++) if (wins[i].TabDragActive) { dragSrc = i; break; }
        var frames = new RenderFrame[n + (dragSrc >= 0 ? 1 : 0)];
        int fi = _layout.FocusedIndex;
        if ((uint)fi >= (uint)n) fi = 0;
        // Build non-focused windows first, focused last, so the focused window draws on top.
        int slot = 0;
        for (int i = 0; i < n; i++)
        {
            if (i == fi) continue;
            // Clean + cached: reuse the last-built frame.
            if (!wins[i].Dirty && i < _lastBuilt.Length
                && _lastBuilt[i].win == wins[i] && _lastBuilt[i].frame != null)
            {
                frames[slot++] = _lastBuilt[i].frame!;
            }
            else
            {
                wins[i].Dirty = false;
                var rf = BuildOneWindow(wins[i], false, atlas, i);
                if (i < _lastBuilt.Length) _lastBuilt[i] = (wins[i], rf);
                frames[slot++] = rf;
            }
        }
        {
            wins[fi].Dirty = false;
            var rf = BuildOneWindow(wins[fi], true, atlas, fi);
            if (fi < _lastBuilt.Length) _lastBuilt[fi] = (wins[fi], rf);
            frames[slot++] = rf;
        }
        if (dragSrc >= 0)
        {
            ChatWindow src = wins[dragSrc];
            float gw = src.TabW;
            frames[slot] = WindowRenderer.BuildGhostTab(atlas, src,
                _lastMx - gw * 0.5f, _lastMy - ChatWindow.TabBarH * 0.5f, gw, src.Active.Label);
        }
        // Quickbar chrome: band/borders/cog drawn first (under chat), settings panel drawn last (on top).
        RenderFrame[] qbExtra = Quickbar.QuickbarService.ExtraFrames;
        int chatCount = slot + (dragSrc >= 0 ? 1 : 0);
        if (qbExtra.Length > 0)
        {
            var all = new RenderFrame[1 + chatCount + (qbExtra.Length > 1 ? 1 : 0)];
            int ai = 0;
            all[ai++] = qbExtra[0];
            for (int i = 0; i < chatCount; i++) all[ai++] = frames[i];
            if (qbExtra.Length > 1) all[ai++] = qbExtra[1];
            ChatRenderer.Publish(all);
        }
        else
        {
            ChatRenderer.Publish(frames);
        }
    }

    /// <summary>The window's own face (CX-683): its typeface / text size at the GUI scale, else the UI atlas.
    /// Hot-safe (a baked-face lookup, never a bake); <see cref="EnsureFace"/> baked it cold when it was picked or
    /// loaded, so a miss here is the bake having refused -- the UI face stands in.</summary>
    private static GlyphAtlas WindowAtlas(ChatWindow win, GlyphAtlas atlas)
    {
        if (win.FontPx <= 0 && win.Typeface.Length == 0) return atlas;
        return TextRig.BakedFace(win.Typeface.Length > 0 ? win.Typeface : FontRegistry.DefaultName, WindowFacePx(win)) ?? atlas;
    }

    private static float WindowFacePx(ChatWindow win) => TextRig.FacePx(win.FontPx);

    /// <summary>Bake a window's own face if it has one. COLD (a miss re-bakes the sheet): a pick on the gear, and
    /// the scale edge (where the frame host re-bakes the UI atlas anyway). A refused bake logs; the window then
    /// draws with the UI face. ponytail: every (typeface, px) ever picked stays in the sheet for the session; when
    /// it fills, later picks fall back to the UI face. Upgrade: release the window's previous face when no other
    /// window shares it.</summary>
    private static void EnsureFace(ChatWindow win)
    {
        if (!TextRig.Ready || (win.FontPx <= 0 && win.Typeface.Length == 0)) return;
        string face = win.Typeface.Length > 0 ? win.Typeface : FontRegistry.DefaultName;
        if (TextRig.Face(face, WindowFacePx(win)) == null)
            _log?.Info($"[chat] window face {face} @ {WindowFacePx(win)}px refused; drawing with the UI face");
    }

    private static RenderFrame BuildOneWindow(ChatWindow win, bool focused, GlyphAtlas atlas, int winIndex)
    {
        bool footer = focused && IsTyping && win.HasSendable;
        win.FooterVisible = footer;
        bool grip = (winIndex == _hoverIndex) || win.Dragging || _anyTabDrag;
        var frame = WindowRenderer.Build(win, focused ? _settings : _closedSettings, _store, WindowAtlas(win, atlas),
            footer, footer ? Edit.Text : "", footer ? Edit.Cursor : 0, grip,
            footer ? Edit.Selection : null, chrome: atlas);
        frame.ApplyResidual = focused && win.AnimEnabled;
        frame.AnimEnabled = win.AnimEnabled;
        frame.HoldSec = win.HoldSec;
        frame.FadeSec = win.FadeSec;
        frame.TauMs = win.TauMs;
        return frame;
    }

    /// <summary>Reset all per-session state at a module boundary (#33), fanned in from the gate's
    /// window-drop-to-zero edge (which fires on clean exit and disconnect alike). Crash-critical clear first:
    /// the engine frees its portrait textures when a module unloads, so a cached <c>CAurTexture*</c> would
    /// dangle into the next session and crash when bound (confirmed via core dump). Then the cosmetic clears
    /// so the new session starts visually clean -- no prior lines, no half-open edit/scroll/panel state.
    /// Idempotent and zero-alloc, so it's safe to fire from the (hot) gate read on the transition frame.</summary>
    private static void ResetSession()
    {
        ChatRenderer.ClearSessionState();   // crash-critical: freed engine textures must not carry over
        Dialog.DialogService.Close();       // drop any dialog left open by a mid-conversation disconnect
        _gmHour = -1;                        // drop the game clock: the next module re-anchors it
        // ponytail: racing a late-arriving ingest (disconnect teardown) can leave <=1 stale line; indices
        // stay in-bounds so it's memory-safe, just cosmetic. Add a lock only if the [chatthread] log proves
        // ingest really runs on a separate thread.
        _store.Clear();                     // cosmetic: no prior session's lines leak in
        Edit.Clear();
        IsTyping = false;
        TypingSignal.Reset();               // session edge: drop the latch, never send (the connection is gone)
        KeyboardMode(false);                // drop any keyboard capture left from the old session
        _settings.Reset();                  // close + drop any half-owned press so no stray up gets eaten next session
        if (_layout != null)
        {
            ChatWindow[] wins = _layout.Windows;
            for (int wi = 0; wi < wins.Length; wi++)
            {
                ChatWindow w = wins[wi];
                w.SendChannel = Channel.Talk;
                w.ClampSendChannel();
                w.ChannelListOpen = false;
                w.Dirty = true;
                w.LastScroll = System.DateTime.MinValue;
                var wt = w.Tabs; for (int ti = 0; ti < wt.Count; ti++) wt[ti].ScrollPx = 0f;
            }
        }
    }

    /// <summary>Pointer press/release. Returns true when it lands on any chat window (the WM shell then
    /// consumes it, so it doesn't fall through to the 3D world). Focused window is checked first (it owns
    /// the popped-out strip/dropdown outside its frame rect); then other windows in reverse draw order (#41).
    /// Wired as this service's framework Window mouse-button delegate (#64).</summary>
    public static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (_layout == null || !GameGate.InGame || button != 1) return false;
        ChatWindow? fwin = _focused;
        if (fwin == null) return false;
        if (down && !_mdiag)
        {
            _mdiag = true;
            _log?.Info($"[chat] first click ({x},{y}) windows={_layout.Count}");
        }
        if (down)
        {
            // Settings window is modal and owned by the focused window.
            if (_settings.Open)
            {
                bool cs = _settings.MouseDown(x, y, fwin);
                if (_settings.CopyWindowRequested)
                {
                    _settings.CopyWindowRequested = false;
                    _settings.Reset();
                    CreateWindow(copyFrom: fwin);
                }
                if (_settings.CloseWindowRequested)
                {
                    int idx = _layout.FocusedIndex;
                    _settings.Reset();
                    if (IsTyping) CloseChat(false);
                    if (_layout.CloseWindow(idx)) { SaveConfig(); _log?.Info($"[chat] window {idx} closed (settings)"); }
                    Rebuild(); return true;
                }
                if (_settings.CopyRequested >= 0)
                {
                    int src = _settings.CopyRequested;
                    _settings.CopyRequested = -1;
                    ChatWindow[] wins = _layout.Windows;
                    if ((uint)src < (uint)wins.Length)
                    {
                        fwin.CopySettingsFrom(wins[src]);
                        _settings.Layout(fwin);
                    }
                }
                // The shared knob and a window font pick apply here, on the input thread (the face bakes cold).
                if (_settings.TellOpenPicked >= 0) { ChatKnobs.TellOpen.Set(_settings.TellOpenPicked); _settings.TellOpenPicked = -1; }
                if (_settings.FontChanged) { _settings.FontChanged = false; EnsureFace(fwin); }
                // #46: TellsHere exclusivity — clear all other windows when one turns it on.
                if (_settings.TellsHereChanged)
                {
                    _settings.TellsHereChanged = false;
                    ChatWindow[] wins = _layout.Windows;
                    for (int i = 0; i < wins.Length; i++)
                        if (wins[i] != fwin) wins[i].TellsHere = false;
                }
                DirtyAll(); SyncFieldKeyboard();
                if (IsTyping && !fwin.HasSendable) CloseChat(false);
                Rebuild(); return cs;
            }
            // Check focused first (owns the strip/dropdown that extends outside the frame).
            if (DownOnWindow(_layout.FocusedIndex, x, y)) return true;
            // Then check other windows in reverse draw order (last drawn = topmost).
            for (int i = _layout.Count - 1; i >= 0; i--)
            {
                if (i == _layout.FocusedIndex) continue;
                if (_layout.Windows[i].Contains(x, y))
                {
                    _layout.FocusedIndex = i;
                    if (DownOnWindow(i, x, y)) return true;
                    Rebuild();
                    return true;
                }
            }
            return false;
        }
        // Up: settings first, then tab-drag cross-window drop (#42), then generic grabs.
        if (_settings.MouseUp()) { SaveConfig(); Rebuild(); return true; }
        // Cross-window tab drop: resolve before MouseUp clears the grab state.
        for (int i = 0; i < _layout.Count; i++)
        {
            ChatWindow src = _layout.Windows[i];
            if (!src.TabDragActive) continue;
            // Scan other windows in reverse draw order for a drop target.
            for (int j = _layout.Count - 1; j >= 0; j--)
            {
                if (j == i) continue;
                int dp = _layout.Windows[j].TabBarDropIndex(x, y);
                if (dp < 0) continue;
                if (_layout.TransferTab(i, src.ActiveTab, j, dp))
                {
                    _layout.FocusedIndex = j;
                    _layout.Windows[j].ActiveTab = dp;
                    _layout.Windows[j].ClampSendChannel();
                    if (IsTyping && !_layout.Windows[j].HasSendable) CloseChat(false);
                }
                break;
            }
            src.MouseUp(x, y);
            src.Dirty = true;
            SaveConfig(); Rebuild();
            return true;
        }
        for (int i = 0; i < _layout.Count; i++)
        {
            bool cu = _layout.Windows[i].MouseUp(x, y);
            if (cu) { SaveConfig(); Rebuild(); return true; }
        }
        return false;
    }

    /// <summary>Press dispatch on a single window: the [⚙] then ChatWindow.MouseDown. Focuses and returns true if consumed.</summary>
    private static bool DownOnWindow(int index, int x, int y)
    {
        ChatWindow win = _layout!.Windows[index];
        if (win.OverGear(x, y))
        {
            _layout.FocusedIndex = index;
            win.ChannelListOpen = false;
            float scale = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
            _panelPx = MathF.Round(KnobPanel.FacePx * scale); _panelSmallPx = MathF.Round(KnobPanel.SmallPx * scale);
            GlyphAtlas? body = TextRig.Face(_panelPx) ?? TextRig.Atlas;   // cold: a miss bakes, on the input thread
            _settings.Panel.Title = _layout.Count > 1 ? $"Chat window {index + 1}" : "Chat window";
            _settings.OpenAt(win, TextRig.ViewportW, TextRig.ViewportH, _panelPx, body, TextRig.Face(_panelSmallPx) ?? body,
                             winIndex: index, winCount: _layout.Count);
            Rebuild(); return true;
        }
        // Portrait click: intercept before ChatWindow.MouseDown (which would consume it as a body no-op).
        if (TryPortraitClick(win, index, x, y)) return true;
        bool cd = win.MouseDown(x, y);
        if (cd)
        {
            _layout.FocusedIndex = index;
            if (IsTyping && !win.HasSendable) CloseChat(false);
            Rebuild();
        }
        return cd;
    }

    /// <summary>Portrait click → tell flow: resolve the portrait's PlayerId to a player name, then
    /// prefill <c>/t "Name" </c> into the chat input (same as vanilla's PrepChatPromptForTellToPlayer).
    /// Returns true if the click landed on a portrait and was consumed.</summary>
    private static bool TryPortraitClick(ChatWindow win, int winIndex, int x, int y)
    {
        PortraitHit[] ph = win.PortraitHits;
        for (int i = 0; i < ph.Length; i++)
        {
            var h = ph[i];
            if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H)
            {
                string? name = _playerResolver?.NameOf(h.PlayerId);
                if (name != null)
                {
                    _layout!.FocusedIndex = winIndex;
                    StartTellTo(name, h.PlayerId);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>The one public door onto the Tell entry path, for a second consumer that has a character name
    /// and no player id (a nameplate click, #click-to-tell). Same flow as the portrait click: the pinned Tell
    /// tab when routing is on, the <c>/t "Name" </c> prefill when it is off. Cold (input thread).</summary>
    public static void StartTell(string name) { if (!string.IsNullOrEmpty(name)) StartTellTo(name, _playerResolver?.Resolve(name)); }

    /// <summary>Open the chat input targeting <paramref name="name"/>. When Tell routing is Off, falls back
    /// to pre-filling <c>/t "name" </c>. Otherwise navigates to the existing pinned tab or creates one (#46).
    /// <paramref name="playerId"/> filters the tab to that player's messages.</summary>
    private static void StartTellTo(string name, uint? playerId)
    {
        if (_layout == null) return;
        // One pin name space: whatever the caller typed or read, a live player pins under the roster's
        // character name -- the same bytes incoming-tell routing produces, or a reply opens a second tab.
        if (playerId is uint p) name = _playerResolver?.NameOf(p) ?? name;
        TellAutoOpen mode = ChatKnobs.TellMode;
        if (mode == TellAutoOpen.Off)
        {
            Open();
            string cmd = $"/t \"{name}\" ";
            // name was read Latin1 (char == source byte); Latin1.GetBytes recovers the original UTF-8
            // bytes. UTF8.GetBytes would double-encode a non-ASCII name into the command.
            byte[] utf8 = Encoding.Latin1.GetBytes(cmd);
            Edit.InsertUtf8(utf8);
            Rebuild();
            return;
        }
        // Route: find or create a pinned tab, switch to it, set channel to Tell, open input.
        var decision = TellRouter.Route(mode, _layout.Windows, _layout.FocusedIndex, name);
        switch (decision.Action)
        {
            case TellRoute.Exists:
            {
                ChatWindow win = _layout.Windows[decision.WindowIndex];
                var tabs = win.Tabs;
                for (int j = 0; j < tabs.Count; j++)
                    if (tabs[j].PinnedName == name) { win.ActiveTab = j; break; }
                _layout.FocusedIndex = decision.WindowIndex;
                win.SendChannel = Channel.Tell;
                break;
            }
            case TellRoute.NewTab:
            {
                ChatWindow win = _layout.Windows[decision.WindowIndex];
                win.AddTab(new Tab($"T:{name}", 0x002, playerId) { PinnedName = name });
                win.ActiveTab = win.Tabs.Count - 1;
                _layout.FocusedIndex = decision.WindowIndex;
                win.SendChannel = Channel.Tell;
                break;
            }
            case TellRoute.NewWindow:
            {
                ChatWindow main = _layout.Main;
                float offset = _layout.Count * 30f;
                var tab = new Tab($"T:{name}", 0x002, playerId) { PinnedName = name };
                var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H, new[] { tab });
                if (_layout.AddWindow(win) != null)
                {
                    _layout.FocusedIndex = _layout.Count - 1;
                    win.SendChannel = Channel.Tell;
                }
                break;
            }
            default:
                return;
        }
        Open();
        SaveConfig();
        Rebuild();
    }

    /// <summary>Create a new chat window, offset from Main, and focus it.</summary>
    /// <summary>Open a new window: <paramref name="copyFrom"/>'s look (the panel's "Copy this window", CX-683) or
    /// the defaults (<c>/newwin</c>), one All tab, offset from Main.</summary>
    private static void CreateWindow(ChatWindow? copyFrom = null)
    {
        if (_layout == null) return;
        ChatWindow main = _layout.Main;
        float offset = _layout.Count * 30f;
        var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H,
            new[] { new Tab("All", 0xffffffff) });
        if (copyFrom != null) { win.CopySettingsFrom(copyFrom); EnsureFace(win); }
        if (_layout.AddWindow(win) == null)
        {
            _log?.Info("[chat] new window: cap reached");
            return;
        }
        _layout.FocusedIndex = _layout.Count - 1;
        SaveConfig();
        _log?.Info($"[chat] window {_layout.Count - 1} created");
        Rebuild();
    }

    /// <summary>Pointer motion. Consumed (true) only while dragging/resizing; also tracks hover grips.
    /// Only rebuilds when a drag is active or the hover target changes (avoids a full re-wrap on every
    /// mouse-poll event).</summary>
    public static bool OnMouseMotion(int x, int y)
    {
        _lastMx = x; _lastMy = y;
        if (_layout == null || !GameGate.InGame) return false;
        if (_settings.Open && _focused != null)
        {
            // The panel draws itself from live state every frame (no rebuild for a move or a hover); only a
            // slider drag changes the window's look, which the frames carry.
            if (_settings.MouseMove(x, y, _focused) && _settings.Panel.SliderDragging) { DirtyAll(); Rebuild(); }
            return true;
        }
        ChatWindow[] wins = _layout.Windows;
        bool consumed = false;
        for (int i = 0; i < wins.Length; i++)
        {
            if (wins[i].MouseMove(x, y)) consumed = true;
            if (wins[i].ScrollDragging) wins[i].LastScroll = System.DateTime.UtcNow;
        }
        // Topmost window under the pointer (reverse draw order = focused last = checked first).
        // During a tab drag, don't let the source's Dragging pin the hover -- the pointer may be over
        // a target window whose header must stay visible for the drop (#42).
        int newHover = -1;
        int fi = _layout.FocusedIndex;
        bool tabDrag = false;
        for (int i = 0; i < wins.Length; i++) if (wins[i].TabDragActive) { tabDrag = true; break; }
        _anyTabDrag = tabDrag;
        bool fiDrag = (uint)fi < (uint)wins.Length && wins[fi].Dragging && !wins[fi].TabDragActive;
        if ((uint)fi < (uint)wins.Length && (wins[fi].Contains(x, y) || fiDrag)) newHover = fi;
        else for (int i = wins.Length - 1; i >= 0; i--)
            if (wins[i].Contains(x, y) || (wins[i].Dragging && !wins[i].TabDragActive)) { newHover = i; break; }
        // Portrait hover: hit-test the topmost window's portrait rects for a highlight.
        int oldPortraitHover = -1;
        int newPortraitHover = -1;
        if (newHover >= 0 && (uint)newHover < (uint)wins.Length)
        {
            oldPortraitHover = wins[newHover].HoveredPortrait;
            PortraitHit[] ph = wins[newHover].PortraitHits;
            for (int i = 0; i < ph.Length; i++)
            {
                var h = ph[i];
                if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) { newPortraitHover = i; break; }
            }
            wins[newHover].HoveredPortrait = newPortraitHover;
        }
        // Clear hover on the window we left (if different).
        if (_hoverIndex >= 0 && _hoverIndex != newHover && (uint)_hoverIndex < (uint)wins.Length)
            wins[_hoverIndex].HoveredPortrait = -1;
        bool hoverChanged = newHover != _hoverIndex || newPortraitHover != oldPortraitHover;
        System.DateTime hnow = System.DateTime.UtcNow;
        for (int i = 0; i < wins.Length; i++)
            if (wins[i].SetHoverTab(i == newHover ? x : float.NaN, y, hnow)) { wins[i].Dirty = true; hoverChanged = true; }
        if (consumed || hoverChanged)
        {
            // Mark the windows that actually changed dirty.
            if (consumed) wins[fi].Dirty = true;   // the dragging window IS focused (press focuses before grab)
            if (hoverChanged)
            {
                if (newHover >= 0 && (uint)newHover < (uint)wins.Length) wins[newHover].Dirty = true;
                if (_hoverIndex >= 0 && (uint)_hoverIndex < (uint)wins.Length) wins[_hoverIndex].Dirty = true;
            }
            if (tabDrag) DirtyAll();   // tab drag needs all headers for drop targets
            _hoverIndex = newHover;
            Rebuild();
        }
        return consumed;
    }

    /// <summary>Lines a wheel notch scrolls.</summary>
    private const float WheelLines = 3f;

    /// <summary>Wheel notch. Consumed (and scrolls) only when the pointer is over a chat window. Routes
    /// to the hovered window (not necessarily focused). Scrolling on the focused window eases the residual;
    /// on others it snaps.</summary>
    public static bool OnMouseWheel(int dy)
    {
        if (_layout == null || !GameGate.InGame) return false;
        if (_settings.Open) { _settings.MouseWheel(dy); return true; }   // the panel / its open list scrolls; it draws itself
        // Find the topmost window under the pointer (focused draws last = checked first).
        ChatWindow[] wins = _layout.Windows;
        ChatWindow? hit = null; int hitIdx = -1;
        int fi = _layout.FocusedIndex;
        if ((uint)fi < (uint)wins.Length && wins[fi].Contains(_lastMx, _lastMy)) { hit = wins[fi]; hitIdx = fi; }
        else for (int i = wins.Length - 1; i >= 0; i--)
            if (wins[i].Contains(_lastMx, _lastMy)) { hit = wins[i]; hitIdx = i; break; }
        if (hit == null) return false;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null) return true;
        float before = hit.Active.ScrollPx;
        hit.ScrollByPx(dy * WheelLines * WindowAtlas(hit, atlas).LineHeight);
        hit.Dirty = true;
        Rebuild();
        float applied = hit.Active.ScrollPx - before;
        hit.LastScroll = System.DateTime.UtcNow;
        if (applied != 0f && hitIdx == _layout.FocusedIndex) ChatAnim.AddResidual(-applied);
        return true;
    }

    /// <summary>True when (<paramref name="x"/>, <paramref name="y"/>) is over any chat UI element:
    /// window frames, input strips, open channel dropdowns, or the settings panel.</summary>
    public static bool MouseOverUi(int x, int y)
    {
        if (_layout == null || !GameGate.InGame) return false;
        if (_settings.Open && _settings.Contains(x, y)) return true;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
            if (wins[i].HitTestFull(x, y)) return true;
        return false;
    }


    /// <summary>Slice #20: EditBuffer + IsTyping + focus suppression. Filled by build slice 3.</summary>
    private static void InitEditControl(ExtenderLog log, HookService hooks)
    {
        EditBuffer.SelfCheck(log);     // off-device proof; surfaces in run-demo's cx.log
        ChatCommands.SelfCheck(log);   // slash-command grammar (#25)
        TypingSignal.SelfCheck(log);   // chat-bar focus/unfocus latch (off-device)
        RegisterCommands();            // #165: chat's own commands go on the shared registry like anyone's

        _startText = (delegate* unmanaged<void>)Native.Sym(SymbolMap.SdlStartTextInput);
        _getClip   = (delegate* unmanaged<IntPtr>)Native.Sym(SymbolMap.SdlGetClipboardText);
        _setClip   = (delegate* unmanaged<byte*, int>)Native.Sym(SymbolMap.SdlSetClipboardText);
        _sdlFree   = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.SdlFree);

        // Outbound send parity (#22). Each CExoString arg is a non-trivial C++ class -> passed by
        // invisible reference, so the ABI slot is a pointer; the callee copies and we own+free it (#14).
        _sendTalk    = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatTalk);
        _sendShout   = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatShout);
        _sendWhisper = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatWhisper);
        _sendParty   = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatParty);
        _sendDM      = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatDM);
        _sendTell    = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatTell);
        _sendTellId  = (delegate* unmanaged<IntPtr, uint, IntPtr, void>)Native.Sym(SymbolMap.SendChatTellId);

        // Chat-bar focus/unfocus parity: our window skip-originals every vanilla site that emitted it.
        _notifyGui = (delegate* unmanaged<IntPtr, int, int, uint, IntPtr, void>)Native.Sym(SymbolMap.NotifyServerGuiEvent);

        var getByName = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Native.Sym(SymbolMap.GetPlayerByPlayerName);
        var getByCreature = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Native.Sym(SymbolMap.GetPlayerByCreatureName);
        if (getByName != null || getByCreature != null)
        {
            _playerResolver = new NativePlayerResolver(getByName, getByCreature);
            log.Info("ChatService: player roster resolver installed (creature-name first, player-name fallback)");
        }

        // Own the three chat openers with skip-original handlers: install the hook but never call
        // through the trampoline, so the vanilla focus slot stays null and never grabs the keyboard.
        // Whichever opener the Enter path enters is patched at its entry, so all three flip IsTyping.
        _ = hooks.Install(SymbolMap.SetFocusChatPrompt, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnOpenFocus);
        _ = hooks.Install(SymbolMap.ShowChatPrompt,     (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnOpenShow);
        _ = hooks.Install(SymbolMap.PrepChatPromptForTell, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnOpenTell);
        _ = hooks.Install(SymbolMap.ChatPromptSetFocus, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnOpenSetFocus);
    }

    /// <summary>The one chat input line; the native shell drives it, all logic is in Core.</summary>
    public static readonly EditBuffer Edit = new();

    private static delegate* unmanaged<IntPtr, void> _sdlFree;
    private static delegate* unmanaged<void> _startText;
    private static delegate* unmanaged<IntPtr> _getClip;
    private static delegate* unmanaged<byte*, int> _setClip;

    // Chat senders (#22). Talk/Shout/Whisper/Party/DM: (this CNWCMessage*, CExoString*). Tell also
    // takes the target playerId: (this, uint playerId, CExoString*).
    private static delegate* unmanaged<IntPtr, IntPtr, void> _sendTalk, _sendShout, _sendWhisper, _sendParty, _sendDM;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> _sendTell;
    private static delegate* unmanaged<IntPtr, uint, IntPtr, void> _sendTellId;

    // CGuiInGame::NotifyServerAboutGuiEvent(this, type, integer, oid, Vector const&). `this` is dead in
    // the callee (rdi is clobbered before any read) and the not-in-a-module guard is inside it.
    private static delegate* unmanaged<IntPtr, int, int, uint, IntPtr, void> _notifyGui;

    /// <summary>Tell the server the chat bar took or gave back the keyboard, the way vanilla's chat
    /// prompt does (ChatBarFocus / ChatBarUnFocus; servers draw a "typing" indicator from it). The
    /// latch suppresses repeats; the engine call drops the packet when we are not in a module.
    /// Cold -- only reached from the input thread's open/close.</summary>
    private static void NotifyChatBar(int eventType, Channel ch)
    {
        if (_notifyGui == null) return;
        float* pos = stackalloc float[3];   // Vector const& -- vanilla passes {0,0,0} at every chat-bar site
        pos[0] = 0f; pos[1] = 0f; pos[2] = 0f;
        _notifyGui(IntPtr.Zero, eventType, TypingSignal.ModeOf(ch), TypingSignal.ObjectInvalid, (IntPtr)pos);
    }

    // SDL_Keymod bits (match HotkeyInputService): Ctrl/Shift both sides.
    private const int KmodShift = 0x0003;
    private const int KmodCtrl  = 0x00C0;

    // SDL keycodes. Printable ones are their codepoint; nav keys carry SDLK_SCANCODE_MASK (0x40000000).
    private const uint KeyReturn = 0x0D, KeyEsc = 0x1B, KeyBackspace = 0x08, KeyDelete = 0x7F;
    private const uint KeyA = 0x61, KeyC = 0x63, KeyV = 0x76, KeyX = 0x78;
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D;

    /// <summary>SDL_TextInputEvent.text is char[32]; scan the null-terminated UTF-8 within it.</summary>
    private const int SdlTextMax = 31;

    // Skip-original openers: never call the trampoline (suppress vanilla focus), just take the keyboard.
    [UnmanagedCallersOnly] private static void OnOpenFocus(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenShow(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenTell(IntPtr self, IntPtr player)
    {
        if (player != IntPtr.Zero)
        {
            IntPtr buf = *(IntPtr*)(player + EngineLayout.PlayerCreatureName);
            uint len = *(uint*)(player + EngineLayout.PlayerCreatureNameLen);
            if (buf != IntPtr.Zero && len > 0 && len <= 128)
            {
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)*(byte*)(buf + (int)k));
                string name = sb.ToString();
                uint ingestId = *(uint*)(player + EngineLayout.PlayerId);
                StartTellTo(name, ingestId);
                return;
            }
        }
        Open();
    }

    /// <summary>Skip-original CGuiInGameChatPrompt::SetFocus(CExoString): the player-list "Tell Player"
    /// button (and friends) open the vanilla prompt with a prefill. '/t "Name" ' / '/tp "Name" ' route
    /// into the Tell-tab flow; any other prefill opens the box with the text inserted.</summary>
    [UnmanagedCallersOnly] private static void OnOpenSetFocus(IntPtr self, IntPtr exo)
    {
        string text = "";
        if (exo != IntPtr.Zero)
        {
            IntPtr buf = *(IntPtr*)exo;
            uint len = *(uint*)(exo + 0x8);
            if (buf != IntPtr.Zero && len > 0 && len <= 256)
            {
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)*(byte*)(buf + (int)k));
                text = sb.ToString();
            }
            if (OperatingSystem.IsWindows()) Native.DestroyExoString(exo);   // Win64: the callee owns the copy
        }
        var (ch, tellTarget, _) = ChatCommands.Parse(text.TrimEnd());
        if (ch == Channel.Tell && !string.IsNullOrEmpty(tellTarget))
        {
            StartTellTo(tellTarget!, _playerResolver?.Resolve(tellTarget!));
            return;
        }
        Open();
        if (text.Length > 0 && IsTyping)
        {
            // text was read Latin1 (char == source byte), so Latin1.GetBytes recovers the original
            // UTF-8 bytes; UTF8.GetBytes here would re-encode and double-encode any multibyte name.
            Edit.InsertUtf8(Encoding.Latin1.GetBytes(text));
            Rebuild();
        }
    }

    /// <summary>One-shot: swallow the SDL_TEXTINPUT that the very keypress which OPENED chat still delivers.
    /// <para>Starting text input mid-keypress does not retract the character -- SDL hands it to the freshly
    /// opened line as well, so inserting the prefix ourselves produced "//". We keep the explicit insert (the
    /// prefix is then guaranteed regardless of whether SDL delivers) and drop the echo here.</para>
    /// <para>ponytail: cleared by the first text event either way, so at worst a prefix SDL never echoed costs
    /// the next identical character. Track the opening keypress properly if that is ever observed.</para></summary>
    private static byte _eatEcho;

    /// <summary>Open chat with <paramref name="prefix"/> already typed. Used by the "/" chord to start a
    /// command line. No-op when chat is already typing, or when no window has a sendable channel.</summary>
    internal static void OpenWith(char prefix)
    {
        if (IsTyping) return;
        Open();
        if (!IsTyping) return;             // Open() refuses when no window has a sendable channel
        Edit.InsertUtf8(new[] { (byte)prefix });
        _eatEcho = (byte)prefix;
        Rebuild();
    }

    /// <summary>Take the keyboard: reset the line, start SDL text input, gate gameplay keys. Targets the
    /// focused window; falls back to Main if focused has no sendable channel (#41). Refuses if neither does.</summary>
    private static void Open()
    {
        if (_layout == null) return;
        ChatWindow target = _layout.Focused;
        if (!target.HasSendable)
        {
            target = _layout.Main;
            _layout.FocusedIndex = 0;
            if (_settings.Open) _settings.Reset();
        }
        if (!target.HasSendable) return;
        IsTyping = true;
        if (TypingSignal.Focus()) NotifyChatBar(TypingSignal.EventFocus, target.SendChannel);
        Edit.Clear();
        if (_startText != null) _startText();
        KeyboardMode(true);
        Rebuild();
    }

    /// <summary>
    /// Release the keyboard. <paramref name="submit"/> recalls the line into history; either way the
    /// line clears and gameplay input resumes.
    /// </summary>
    public static void CloseChat(bool submit)
    {
        _eatEcho = 0;
        if (submit)
        {
            string text = Edit.Submit();
            if (!string.IsNullOrEmpty(text) && CommandRegistry.Global.TryDispatch(text, out string? reply))
            {
                // Handled by whichever service registered it -- don't send. The chat service's only remaining
                // job is putting the handler's answer, if any, in front of the player.
                if (reply != null) EchoLocal(reply);
            }
            else if (_layout?.Focused is ChatWindow win && !string.IsNullOrEmpty(text))
            {
                var (switchTo, tellTarget, rest) = ChatCommands.Parse(text);
                Channel prev = win.SendChannel;
                if (switchTo is Channel nc) win.SendChannel = nc;
                if (!string.IsNullOrEmpty(rest))
                {
                    string? tellName = null;
                    if (win.SendChannel == Channel.Tell)
                    {
                        tellName = TellTarget.ResolveName(win.Active, tellTarget, _playerResolver);
                        _log?.Info($"[chat] tell resolve: target=\"{tellTarget}\" pin=\"{win.Active.PinnedName}\" name={tellName ?? "(null)"}");
                    }
                    _log?.Info($"[chat] submit: ch={win.SendChannel} fi={_layout!.FocusedIndex} tab={win.ActiveTab} pin=\"{win.Active.PinnedName}\" tell={tellName ?? "-"}");
                    Send(win.SendChannel, tellName, rest);
                }
                // Tell is ephemeral on unpinned tabs (each send names its target), so a /t switch never
                // sticks there; pinned Tell tabs are dedicated to one target and keep it.
                if (win.SendChannel == Channel.Tell && win.Active.PinnedName.Length == 0)
                    win.SendChannel = prev == Channel.Tell ? Channel.Talk : prev;
            }
        }
        else Edit.Clear();
        IsTyping = false;
        // After the send, like vanilla's HandleEnter (ParseChatString, then the unfocus event). Cancel
        // paths (Esc, focus loss, a window losing its sendable channel) all funnel here too, so the
        // server never keeps a stuck "typing" state -- the same three cases vanilla covers.
        if (TypingSignal.Unfocus())
            NotifyChatBar(TypingSignal.EventUnfocus, _layout?.Focused.SendChannel ?? Channel.Talk);
        // Do NOT SDL_StopTextInput: the engine leaves text input globally on (it never toggles it) and the
        // debug console + other text fields depend on TEXTINPUT events firing. KeyboardModeOff below is the
        // real gate that stops chars reaching us. Stopping SDL text input here breaks the console.
        KeyboardMode(false);
        Rebuild();
    }

    /// <summary>Register the chat service's own window-management commands (#41) into the shared registry
    /// (#165). Every other command on the surface belongs to the service that implements it.</summary>
    private static void RegisterCommands()
    {
        var r = CommandRegistry.Global;
        r.Register("/newwin", "open a new chat window", _ => { CreateWindow(); return null; });
        r.Register("/closewin", "close the focused chat window", _ =>
        {
            if (_layout == null) return null;
            int idx = _layout.FocusedIndex;
            if (_layout.CloseWindow(idx))
            {
                SaveConfig();
                _log?.Info($"[chat] /closewin: window {idx} closed");
            }
            return null;
        });
    }

    /// <summary>Put a line in the local chat store without touching the wire -- command feedback the player
    /// can read while standing in the scene they are tuning. Talk channel so it lands in the default tab.</summary>
    private static void EchoLocal(string text)
    {
        _store.Append(new ChatLine(0x001, "", 0, "", ColorRunParser.Parse(text), DateTime.UtcNow, text, GameNow()));
    }

    /// <summary>Route submitted text to the server on <paramref name="ch"/> via the vanilla
    /// SendPlayerToServerChat_* senders (major 0x09, wire unchanged -> the extender stays optional).
    /// Cold (input thread) so allocation is fine. The callee copies the CExoString; we own + free it.</summary>
    // ponytail: no content is logged (repo's no-keylog stance) -- the send echoes back through the
    // inbound tap, which logs the delivered line, so a live run still proves each channel end-to-end.
    private static void Send(Channel ch, string? tellName, string text)
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { _log?.Info($"[chat] send dropped (ch={ch}): no live CNWCMessage yet"); return; }
        IntPtr exo = Native.NewExoString(text);
        string wire = Encoding.Latin1.GetString(Native.EngineBytes(text));   // what the tell echo comes back as
        try
        {
            switch (ch)
            {
                case Channel.Talk    when _sendTalk    != null: _sendTalk(msg, exo); break;
                case Channel.Shout   when _sendShout   != null: _sendShout(msg, exo); break;
                case Channel.Whisper when _sendWhisper != null: _sendWhisper(msg, exo); break;
                case Channel.Party   when _sendParty   != null: _sendParty(msg, exo); break;
                case Channel.DM      when _sendDM      != null: _sendDM(msg, exo); break;
                case Channel.Tell:
                    if (tellName == null) { _log?.Info("[chat] tell dropped: no target name"); break; }
                    // Vanilla /t path: resolve client-side to the playerId (CNWCPlayer+0x8) and send the
                    // id-based form. The name-based TellName form is resolved server-side by FIRST name
                    // only, so full character names always miss -- keep it only as a fallback.
                    if (_playerResolver?.Resolve(tellName) is uint pid && _sendTellId != null)
                    {
                        _log?.Info($"[chat] tell SENDING via id: name=\"{tellName}\" pid={pid}");
                        TellTarget.Sent(_playerResolver.NameOf(pid) ?? tellName, wire);   // canonical pin name
                        _sendTellId(msg, pid, exo);
                    }
                    else if (_sendTell != null)
                    {
                        IntPtr nameExo = Native.NewExoString(tellName);
                        _log?.Info($"[chat] tell SENDING via TellName fallback: name=\"{tellName}\"");
                        TellTarget.Sent(tellName, wire);
                        try { _sendTell(msg, nameExo, exo); }
                        finally { Native.FreeExoString(nameExo); }
                    }
                    else _log?.Info($"[chat] tell dropped: sender symbols unresolved");
                    break;
                // A slash line that reached here missed dispatch: log its first token (never the rest -- /tell carries content).
                default: _log?.Info($"[chat] send dropped (ch={ch}): sender symbol unresolved" +
                                    (text.StartsWith('/') ? $" -- undispatched command token '{text.Split(' ', 2)[0]}' len={text.Length}" : "")); break;
            }
        }
        finally { Native.FreeExoString(exo); }
    }

    /// <summary>Insert finished UTF-8 codepoints (SDL_TEXTINPUT payload) at the caret.</summary>
    public static void InsertUtf8(IntPtr text)
    {
        if (text == IntPtr.Zero) return;
        byte* p = (byte*)text;
        int n = 0;
        while (n < SdlTextMax && p[n] != 0) n++;
        if (_eatEcho != 0)
        {
            byte echo = _eatEcho;
            _eatEcho = 0;
            if (n == 1 && p[0] == echo) return;   // the opening keypress's own character, already inserted
        }
        if (_settings.Editing)   // typed chars go to the focused settings value box, not the chat line
        { _settings.EditInsert(Encoding.UTF8.GetString(p, n)); return; }
        Edit.InsertUtf8(new ReadOnlySpan<byte>(p, n));
        Rebuild();   // reflect the new text in the footer
    }

    /// <summary>The framework key delegate (#64): route the key to the edit line only while chat owns the
    /// keyboard (typing or editing a settings field), consuming it; otherwise decline so it falls to gameplay.
    /// The arbiter routes here first via the keyboard-owner path when <see cref="WantsKey"/> is set.</summary>
    private static bool OnChatKey(uint key, int mods)
    {
        if (!WantsKey) return false;
        RouteKey(key, mods);
        return true;
    }

    /// <summary>Dispatch a control/nav key to the edit line while the box owns the keyboard.</summary>
    public static void RouteKey(uint key, int mods)
    {
        // A settings value box owns the keyboard first (before the chat ctrl block, or Ctrl+V would hit chat).
        if (_settings.Editing)
        {
            switch (key)
            {
                case KeyReturn:    _settings.CommitEdit(_focused); break;
                case KeyEsc:       _settings.CancelEdit(); break;
                case KeyBackspace: _settings.EditBackspace(); break;
                // other keys (nav/ctrl) are ignored while typing a number
            }
            if (_settings.FontChanged && _focused != null) { _settings.FontChanged = false; EnsureFace(_focused); }   // Enter on the size / typeface list
            DirtyAll();
            SaveConfig();   // a keyboard commit has no paired mouse-up to persist on
            SyncFieldKeyboard(); Rebuild();
            return;
        }
        bool shift = (mods & KmodShift) != 0;
        bool ctrl  = (mods & KmodCtrl) != 0;
        if (ctrl)
        {
            if (key == KeyA) { Edit.SelectAll(); Rebuild(); return; }
            if (key == KeyC) { Copy(); return; }
            if (key == KeyV) { Paste(); return; }
            if (key == KeyX) { Cut(); return; }
        }
        switch (key)
        {
            case KeyReturn:    CloseChat(true); break;
            case KeyEsc:       CloseChat(false); break;
            case KeyBackspace: Edit.Backspace(); break;
            case KeyDelete:    Edit.Delete(); break;
            case KeyLeft:      if (ctrl) Edit.MoveWordLeft(shift); else Edit.MoveLeft(shift); break;
            case KeyRight:     if (ctrl) Edit.MoveWordRight(shift); else Edit.MoveRight(shift); break;
            case KeyHome:      Edit.Home(shift); break;
            case KeyEnd:       Edit.End(shift); break;
            case KeyUp:        Edit.HistoryPrev(); break;
            case KeyDown:      Edit.HistoryNext(); break;
        }
        Rebuild();   // caret move / edit / history recall all change the footer
    }

    private static void Copy()
    {
        if (_setClip == null || !Edit.HasSelection) return;
        byte[] u = Encoding.UTF8.GetBytes(Edit.SelectedText);
        Span<byte> buf = stackalloc byte[u.Length + 1];
        u.CopyTo(buf);
        buf[u.Length] = 0;
        fixed (byte* b = buf) _setClip(b);
    }

    /// <summary>Ctrl+X: copy the selection to the clipboard, then delete it (Backspace removes a selection).</summary>
    private static void Cut()
    {
        if (!Edit.HasSelection) return;
        Copy();
        Edit.Backspace();
        Rebuild();
    }

    private static void Paste()
    {
        if (_getClip == null) return;
        IntPtr p = _getClip();
        if (p == IntPtr.Zero) return;
        byte* s = (byte*)p;
        int n = 0;
        while (s[n] != 0) n++;
        Edit.InsertUtf8(new ReadOnlySpan<byte>(s, n));
        if (_sdlFree != null) _sdlFree(p);   // SDL_GetClipboardText returns SDL-malloc'd memory
        Rebuild();   // Ctrl+V early-returns from RouteKey before its trailing Rebuild -> redraw the footer here
    }

    /// <summary>Sync the engine keyboard-capture gate with a settings field edit: take the keyboard (so
    /// TEXTINPUT/keydown reach us and gameplay keys are gated) while a value box is being typed, release it
    /// when neither a field nor chat wants it. Called after any settings interaction that may start/end an edit.</summary>
    private static void SyncFieldKeyboard()
    {
        if (_settings.Editing) { KeyboardMode(true); if (_startText != null) _startText(); }
        else if (!IsTyping) KeyboardMode(false);
    }

    /// <summary>Take/release the keyboard by asserting this window's keyboard ownership on the arbiter, which
    /// drives the centralized engine gate (KeyboardModeOn/Off + the #34 drive-latch reset) once for all
    /// consumers (#64). The gate logic itself moved to <c>UiInputService</c>; chat now only declares intent.</summary>
    private static void KeyboardMode(bool on)
    {
        if (_uiWindow != null) UiHost.RequestKeyboard(_uiWindow, on);
    }
}
