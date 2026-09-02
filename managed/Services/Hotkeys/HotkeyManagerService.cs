using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// The client-native hotkey manager: a window drawn by the extender listing every armed key
/// with a per-key Unbind, a Revoke-all, and a passthrough to the server's binding UI. Unbinding
/// removes the key locally AND sends a Revoke, so the player owns their capture surface without
/// depending on the server.
/// </summary>
/// <remarks>
/// Draws by hooking the per-frame nk_sdl_render (which does nk_convert then nk_clear) and injecting
/// our window's immediate-mode calls just before it, reusing the client's live nuklear context.
/// Hot-path discipline (render/main thread every frame): the draw path allocates NOTHING -- every
/// string is pre-baked into unmanaged memory, and the key list is rebuilt only on HotkeyStore.Version
/// change (cold). See CLAUDE.md "hot hooks allocate nothing".
/// </remarks>
public sealed unsafe class HotkeyManagerService : IExtenderService
{
    private const int NkCurrentOff = 0x6738;                 // nk_context->current: non-null mid-build
    private const uint WinFlags = 1 /*BORDER*/ | 8 /*CLOSABLE*/ | 0x40 /*TITLE*/;
    private const uint NkTextLeft = 0x11;                    // NK_TEXT_ALIGN_LEFT | NK_TEXT_ALIGN_MIDDLE
    private const long ConfirmMs = 3000;                     // window to click Revoke-all a second time

    // nk_rect is passed BY VALUE (x,y in xmm0, w,h in xmm1). Layout + declaration-order delegate must
    // match the C ABI exactly, or the integer args shift and nuklear derefs garbage as the context.
    [StructLayout(LayoutKind.Sequential)]
    private struct NkRect { public float X, Y, W, H; }

    private static ExtenderLog? _log;
    private static IntPtr _nkGlobal;                         // &g_pNuklear (deref for the live context)
    private static IntPtr* _renderSlot;   // chain slot: the next nk_sdl_render subscriber
    private static delegate* unmanaged<IntPtr, byte*, NkRect, uint, int> _begin;
    private static delegate* unmanaged<IntPtr, void> _end;
    private static delegate* unmanaged<IntPtr, float, int, void> _row;
    private static delegate* unmanaged<IntPtr, byte*, uint, void> _label;
    private static delegate* unmanaged<IntPtr, byte*, int> _button;
    private static delegate* unmanaged<IntPtr, byte*, int> _isClosed;
    private static bool _ready;

    private static volatile bool _open;
    private static long _confirmDeadline;

    // Baked labels (unmanaged, NUL-terminated UTF8).
    private static byte* _title, _unbind, _revokeAll, _revokeConfirm, _bindMore, _empty, _hint;
    private static int _count;
    private static byte** _keyLabels;                       // _count entries
    private static int* _keyCodes;                          // _count entries
    private static int _bakedVersion = -1;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _nkGlobal = Native.Sym(SymbolMap.Nuklear);
        _begin    = (delegate* unmanaged<IntPtr, byte*, NkRect, uint, int>)Native.Sym(SymbolMap.NkBegin);
        _end      = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.NkEnd);
        _row      = (delegate* unmanaged<IntPtr, float, int, void>)Native.Sym(SymbolMap.NkLayoutRowDynamic);
        _label    = (delegate* unmanaged<IntPtr, byte*, uint, void>)Native.Sym(SymbolMap.NkLabel);
        _button   = (delegate* unmanaged<IntPtr, byte*, int>)Native.Sym(SymbolMap.NkButtonLabel);
        _isClosed = (delegate* unmanaged<IntPtr, byte*, int>)Native.Sym(SymbolMap.NkWindowIsClosed);

        // Chained, not Installed: UiHostService draws the framework's own windows on this symbol too (#88).
        // Lower priority = inner, so its raw GL and state resync are done before we queue a nuklear window.
        _renderSlot = hooks.Chain(SymbolMap.NkSdlRender, (IntPtr)(delegate* unmanaged<float, void>)&OnNkRender, 10);

        _ready = _nkGlobal != IntPtr.Zero && _begin != null && _end != null && _row != null
              && _label != null && _button != null && _isClosed != null && _renderSlot != null;
        if (!_ready) { _log?.Info("HotkeyManager: nuklear unavailable on this client -- manager disabled"); return; }

        _title         = Bake("SoW Hotkeys");
        _unbind        = Bake("Unbind");
        _revokeAll     = Bake("Revoke all");
        _revokeConfirm = Bake("Click again to confirm");
        _bindMore      = Bake("Bind more (open server UI)");
        _empty         = Bake("No keys are armed.");
        _hint          = Bake("Keys this client may capture:");
    }

    public static bool IsReady => _ready;

    /// <summary>
    /// Toggle from the Options "Hotkeys" button. Returns false if the manager can't run (caller then
    /// falls back to opening the server UI so the button is never dead).
    /// </summary>
    public static bool Toggle()
    {
        if (!_ready) return false;
        _open = !_open;
        _confirmDeadline = 0;
        return true;
    }

    /// <summary>Close on login: a manager left open across a disconnect must not linger into a fresh session.</summary>
    public static void Close() { _open = false; _confirmDeadline = 0; }

    [UnmanagedCallersOnly]
    private static void OnNkRender(float aa)
    {
        if (_open) Draw();
        ((delegate* unmanaged<float, void>)(*_renderSlot))(aa);
    }

    private static void Draw()
    {
        IntPtr ctx = *(IntPtr*)_nkGlobal;
        if (ctx == IntPtr.Zero) return;
        // Trap: nk_begin returns 0 WITHOUT pushing a window if another is mid-build (ctx->current set)
        // -- an unconditional nk_end would then close someone else's window. Skip the frame; once past
        // this, nk_begin is guaranteed to push, so nk_end is safe.
        if (*(IntPtr*)(ctx + NkCurrentOff) != IntPtr.Zero) return;

        EnsureBaked();

        // Deferred: never mutate the armed set (which rebakes the buffers we're reading) mid-window.
        // Record the click, act after nk_end.
        int unbindCode = -1;
        bool clickRevokeAll = false, clickBindMore = false;

        var rect = new NkRect { X = 60, Y = 60, W = 380, H = 440 };
        if (_begin(ctx, _title, rect, WinFlags) != 0)
        {
            _row(ctx, 22, 1);
            _label(ctx, _count == 0 ? _empty : _hint, NkTextLeft);

            for (int i = 0; i < _count; i++)
            {
                _row(ctx, 26, 2);
                _label(ctx, _keyLabels[i], NkTextLeft);
                if (_button(ctx, _unbind) != 0) unbindCode = _keyCodes[i];
            }

            bool confirming = _confirmDeadline != 0 && Environment.TickCount64 < _confirmDeadline;
            _row(ctx, 30, 1);
            if (_count > 0 && _button(ctx, confirming ? _revokeConfirm : _revokeAll) != 0) clickRevokeAll = true;

            _row(ctx, 30, 1);
            if (_button(ctx, _bindMore) != 0) clickBindMore = true;
        }
        _end(ctx);

        // Window's own [x] closes it. nk_window_is_closed returns 1 for "not found" too, so this MUST
        // stay after nk_begin pushed the window this frame (the pre-guard guarantees it) -- moving it
        // earlier reads "closed" and self-closes the manager every frame.
        if (_isClosed(ctx, _title) != 0) { Close(); return; }

        // Act now (cold: at most one per click).
        if (unbindCode != -1) UnbindOne(unbindCode);
        else if (clickRevokeAll) RevokeAll();
        else if (clickBindMore) BindMore();
    }

    private static void UnbindOne(int code)
    {
        var next = new HashSet<int>(HotkeyStore.Armed);
        if (!next.Remove(code)) return;
        HotkeyStore.Set(next, _log!);                        // bumps Version -> rebake next frame
        HotkeyConsent.Revoke(new[] { code });                // server forgets the binding
        _confirmDeadline = 0;
        _log?.Info($"HotkeyManager: unbound {HotkeyConsent.KeyName(code)}");
    }

    private static void RevokeAll()
    {
        // First click arms the confirm; a second within the window commits. No second modal.
        long now = Environment.TickCount64;
        if (_confirmDeadline == 0 || now >= _confirmDeadline) { _confirmDeadline = now + ConfirmMs; return; }

        var all = HotkeyStore.Armed;
        if (all.Count > 0)
        {
            HotkeyConsent.Revoke(all);                       // one Revoke carrying every key
            HotkeyStore.Set(new HashSet<int>(), _log!);
            _log?.Info($"HotkeyManager: revoked all {all.Count} armed key(s)");
        }
        _confirmDeadline = 0;
    }

    /// <summary>Passthrough to the server's binding NUI (the same OpenUi the Hotkeys button used to send).</summary>
    private static void BindMore() =>
        ClientApi.Send(HotkeyProtocol.OpenUiMajor, HotkeyProtocol.OpenUiMinor, null, 0);

    /// <summary>Rebuild the baked key list only when the armed set changed. Cold path -- allocation is fine.</summary>
    private static void EnsureBaked()
    {
        int v = HotkeyStore.Version;
        if (v == _bakedVersion) return;
        FreeKeys();

        var armed = HotkeyStore.Armed;                       // immutable snapshot
        _count = armed.Count;
        if (_count > 0)
        {
            _keyCodes = (int*)NativeMemory.Alloc((nuint)_count, sizeof(int));
            _keyLabels = (byte**)NativeMemory.Alloc((nuint)_count, (nuint)sizeof(void*));
            int i = 0;
            foreach (int code in armed)
            {
                _keyCodes[i] = code;
                _keyLabels[i] = Bake(HotkeyConsent.KeyName(code));
                i++;
            }
        }
        _bakedVersion = v;
    }

    private static void FreeKeys()
    {
        if (_keyLabels != null)
        {
            for (int i = 0; i < _count; i++) NativeMemory.Free(_keyLabels[i]);
            NativeMemory.Free(_keyLabels);
            _keyLabels = null;
        }
        if (_keyCodes != null) { NativeMemory.Free(_keyCodes); _keyCodes = null; }
        _count = 0;
    }

    private static byte* Bake(string s)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s);
        byte* p = (byte*)NativeMemory.Alloc((nuint)(utf8.Length + 1));
        for (int i = 0; i < utf8.Length; i++) p[i] = utf8[i];
        p[utf8.Length] = 0;
        return p;
    }
}
