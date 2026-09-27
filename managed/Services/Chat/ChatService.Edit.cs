using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The keyboard side of the chatbox: the one EditBuffer, the skip-original vanilla openers, open / close /
/// submit, key + text routing, clipboard, and the keyboard-ownership handshake with the arbiter.</summary>
public sealed unsafe partial class ChatService
{
    /// <summary>Slice #20: EditBuffer + IsTyping + focus suppression. Filled by build slice 3.</summary>
    private static void InitEditControl(ExtenderLog log, HookService hooks)
    {
        EditBuffer.SelfCheck(log);     // off-device proof; surfaces in run-demo's cx.log
        ChatCommands.SelfCheck(log);   // slash-command grammar (#25)
        TypingSignal.SelfCheck(log);   // chat-bar focus/unfocus latch (off-device)
        RegisterCommands();            // #165: chat's own commands go on the shared registry like anyone's

        GetPlayerByPlayerNameFn getByName = EngineFn.GetPlayerByPlayerName;
        GetPlayerByCreatureNameFn getByCreature = EngineFn.GetPlayerByCreatureName;
        if (getByName != null || getByCreature != null)
        {
            _playerResolver = new NativePlayerResolver(getByName, getByCreature);
            log.Info("ChatService: player roster resolver installed (creature-name first, player-name fallback)");
        }

        // Own the three chat openers with skip-original handlers: install the hook but never call
        // through the trampoline, so the vanilla focus slot stays null and never grabs the keyboard.
        // Whichever opener the Enter path enters is patched at its entry, so all three flip IsTyping.
        _ = hooks.InstallSetFocusChatPrompt(&OnOpenFocus);
        _ = hooks.InstallShowChatPrompt(&OnOpenShow);
        _ = hooks.InstallPrepChatPromptForTell(&OnOpenTell);
        _ = hooks.InstallChatPromptSetFocus(&OnOpenSetFocus);
    }

    /// <summary>The one chat input line; the native shell drives it, all logic is in Core.</summary>
    public static readonly EditBuffer Edit = new();


    // SDL_Keymod bits (match HotkeyInputService): Ctrl/Shift both sides.
    private const int KmodShift = 0x0003;  // not-an-offset: SDL key/event code
    private const int KmodCtrl  = 0x00C0;  // not-an-offset: SDL key/event code

    // SDL keycodes. Printable ones are their codepoint; nav keys carry SDLK_SCANCODE_MASK (0x40000000).
    private const uint KeyReturn = 0x0D, KeyEsc = 0x1B, KeyBackspace = 0x08, KeyDelete = 0x7F;  // not-an-offset: SDL key/event code
    private const uint KeyA = 0x61, KeyC = 0x63, KeyV = 0x76, KeyX = 0x78;  // not-an-offset: SDL key/event code
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;  // not-an-offset: SDL key/event code
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D;  // not-an-offset: SDL key/event code

    /// <summary>SDL_TextInputEvent.text is char[32]; scan the null-terminated UTF-8 within it.</summary>
    private const int SdlTextMax = 31;

    // Skip-original openers: never call the trampoline (suppress vanilla focus), just take the keyboard.
    [UnmanagedCallersOnly] private static void OnOpenFocus(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenShow(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenTell(IntPtr self, IntPtr player)
    {
        if (player != IntPtr.Zero)
        {
            var plr = new CNWCPlayer(player);
            IntPtr buf = plr.CreatureName;
            uint len = plr.CreatureNameLen;
            if (buf != IntPtr.Zero && len > 0 && len <= 128)
            {
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)*(byte*)(buf + (int)k));
                string name = sb.ToString();
                uint ingestId = plr.PlayerId;
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
    /// focused window; falls back to Main if focused has no sendable channel (#41). Refuses if neither does, and
    /// out of game, where the chatbox is not drawn and a live line would eat every key unseen.</summary>
    private static void Open()
    {
        if (_layout == null || !GameGate.InGame) return;
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
        if (EngineFn.SdlStartTextInput != null) EngineFn.SdlStartTextInput();
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
        if (EngineFn.SdlSetClipboardText == null || !Edit.HasSelection) return;
        byte[] u = Encoding.UTF8.GetBytes(Edit.SelectedText);
        Span<byte> buf = stackalloc byte[u.Length + 1];
        u.CopyTo(buf);
        buf[u.Length] = 0;
        fixed (byte* b = buf) EngineFn.SdlSetClipboardText(b);
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
        if (EngineFn.SdlGetClipboardText == null) return;
        IntPtr p = EngineFn.SdlGetClipboardText();
        if (p == IntPtr.Zero) return;
        byte* s = (byte*)p;
        int n = 0;
        while (s[n] != 0) n++;
        Edit.InsertUtf8(new ReadOnlySpan<byte>(s, n));
        if (EngineFn.SdlFree != null) EngineFn.SdlFree(p);   // SDL_GetClipboardText returns SDL-malloc'd memory
        Rebuild();   // Ctrl+V early-returns from RouteKey before its trailing Rebuild -> redraw the footer here
    }

    /// <summary>Sync the engine keyboard-capture gate with a settings field edit: take the keyboard (so
    /// TEXTINPUT/keydown reach us and gameplay keys are gated) while a value box is being typed, release it
    /// when neither a field nor chat wants it. Called after any settings interaction that may start/end an edit.</summary>
    private static void SyncFieldKeyboard()
    {
        if (_settings.Editing) { KeyboardMode(true); if (EngineFn.SdlStartTextInput != null) EngineFn.SdlStartTextInput(); }
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
