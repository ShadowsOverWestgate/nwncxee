using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Ui;

/// <summary>The framework's frame owner (ADR 0004, spec #62; ADR 0014 amendment, #170). Owns the single
/// <c>SwapBuffers</c> present, computes the in-game gate and syncs the game UI scale once per frame, then drives
/// two registries and nothing else: the <see cref="UiHost"/> window roll-call in z-order, and the
/// <see cref="FrameRegistry"/> phase walks around it. It names no service. Each service supplies its window's <see cref="Window.DrawContent"/>;
/// the host never touches any service's rendering. Was Chat's <c>OnSwap</c> roll-call. Raw input capture is the
/// sibling <c>UiInputService</c>'s job (#64); this service owns only the frame present.</summary>
public sealed unsafe class UiHostService : IExtenderService
{
    private static ExtenderLog? _log;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiHost.SelfCheck(log);   // #61 arbiter + #62 draw roll-call proof (off-device)
        FrameZones.SelfCheck(log);   // #238 frame-split instrument (off-device)
        SettingsPanel.SelfCheck(log);   // #166 shared settings panel: hit-test, drag, edit buffer (off-device)
        if (GameGate.Locked)   // lockchat: install nothing -> vanilla rendering intact
        {
            log.Info("UiHostService: lockchat set -- framework stood down, vanilla rendering left intact");
            return;
        }
        _getGuiScale = (delegate* unmanaged<float>)Native.Sym(SymbolMap.GetGuiScale);
        _resetStates = (delegate* unmanaged<void>)Native.Sym(SymbolMap.RiResetRenderStates);
        _bindBuffer = (delegate* unmanaged<uint, uint, void>)Native.Sym(SymbolMap.RiBindBuffer);
        _swapOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwap);
        log.Info(_swapOrig != null
            ? "UiHostService: frame present owned (SwapBuffers) -- driving the window registry"
            : "UiHostService: frame present not installed (Install returned Zero: symbol unresolved or already hooked)");

        // The roll-call's draw site. nk_sdl_render is nuklear's GL flush: run the windows just before it and
        // they land under every NUI window and over the vanilla GUI panels. Chained, not Installed --
        // HotkeyManagerService builds a nuklear window on the same symbol (#88). Higher priority = outer, so
        // our raw GL and its resync are done before the manager adds its window to the queue.
        _nkSlot = hooks.Chain(SymbolMap.NkSdlRender, (IntPtr)(delegate* unmanaged<float, void>)&OnNkRender, 20);
        log.Info(_nkSlot != null
            ? "UiHostService: window roll-call runs below NUI (chained on nk_sdl_render)"
            : "UiHostService: nk_sdl_render unavailable -- window roll-call stays at the present (above NUI)");
    }

    private static delegate* unmanaged<IntPtr, void> _swapOrig;
    private static IntPtr* _nkSlot;   // chain slot: the next nk_sdl_render subscriber (null = not chained)
    private static delegate* unmanaged<float> _getGuiScale;   // CAurora::GetGUIScale -> live GUI-scale multiplier
    private static delegate* unmanaged<void> _resetStates;    // RenderInterface::ResetRenderStates -- the hand-back resync
    private static delegate* unmanaged<uint, uint, void> _bindBuffer;   // ...and the one binding it does not cover
    private static bool _announced;
    private const uint GlArrayBuffer = 0x8892;

    /// <summary>#456: presents left to sample. Costs a pipeline stall each, so it is a knob that runs out.</summary>
    private static int _presentProbe;

    private static uint PresentPixel()
    {
        if (Gl.ReadPixels == null) return 0;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        if (vp[2] <= 16 || vp[3] <= 16) return 0;
        uint px = 0;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.ReadPixels(vp[0] + vp[2] / 2, vp[1] + vp[3] / 2, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &px);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        for (int i = 0; i < 4 && Gl.GetError != null && Gl.GetError() != 0; i++) { }
        return px;
    }

    /// <summary>The frame present: announce once, run the frame phases, then present. The window roll-call
    /// itself happens earlier, at <see cref="OnNkRender"/>, so the windows land below NUI; it falls back to
    /// here only when that chain never installed. Hot (every frame) and zero-alloc.</summary>
    [UnmanagedCallersOnly]
    private static void OnSwap(IntPtr window)
    {
        if (!_announced)
        {
            _announced = true;
            _log?.Info("UiHostService: first frame presented -- hook is LIVE");
            _presentProbe = Settings.Int("presentprobe", 0, 0, 200);
            int pin = Settings.Int("pin", -1, -1, 1023);
            if (pin >= 0) _log?.Info($"UiHostService: main thread pin={pin} -> {(Affinity.PinThis(pin) ? "pinned" : "REFUSED")} (was on cpu {Affinity.CurrentCpu})");
        }
        FrameRegistry.Global.Run(FramePhase.Start);
        if (_nkSlot == null) DrawWindows();       // fallback: no nk_sdl_render chain -> draw here, above NUI
        FrameRegistry.Global.Run(FramePhase.PrePresent);
        // #210 (G1): the hand-back resync. Window content draws raw GL in the ENGINE's 3.3 context, past
        // GLRender's shadow cache, which then early-outs on a stale match and skips real GL calls for the rest
        // of the frame. One engine-side full resync closes that instead of each drawer mirroring the state it
        // touched. Must be the LAST thing before the present -- after the window roll-call and after every
        // PrePresent participant (GlContextService's 4.6 bracket switches back to this context in there).
        if (_resetStates != null) _resetStates();
        // ResetRenderStates rewrites every shadow-cache global EXCEPT the two buffer bindings, and
        // GLRender::BindBuffer early-outs on a cache match -- so a window that left its own VBO bound in real
        // GL would have the engine's next streamed vertex upload land in it. Routed through the engine
        // wrapper, not glBindBuffer, precisely so the cache learns about it.
        if (_bindBuffer != null) _bindBuffer(GlArrayBuffer, 0);
        // #238: the present is its own zone. It is the discriminator the other brackets cannot supply --
        // a frame that spends its time HERE is waiting on the GPU or on vsync, which is a different problem
        // from every zone that measures our own CPU work.
        // #456: the black-frame discriminator, and the only one that is free of the two-context question --
        // this read is in the ENGINE's context, at the last instruction before the present, so what it sees
        // IS what the swap is about to show. A bright value on a frame that displays black means the swap
        // presented a buffer nobody drew; alternating values mean the frame was lost before it got here.
        // Only in a module: the main menu presents for a minute before a save is loaded, and a budget spent
        // there is 60 samples of a static background -- which is what the first run of this probe measured.
        if (_presentProbe > 0 && GameGate.InModule)
        {
            _presentProbe--;
            _log?.Always($"[presentprobe] fb0 centre before swap: 0x{PresentPixel():x8}"
                       + $"  into={CX.Alloy.PostChain.LastInto} resolves={CX.Alloy.PostChain.Resolves}");
        }
        long tSwap = FrameZones.Now;
        if (_swapOrig != null) _swapOrig(window); // present
        FrameZones.Add(FrameZones.Zone.Present, tSwap);
        FrameStats();
        FrameRegistry.Global.Run(FramePhase.PostPresent);
    }

    /// <summary>The window roll-call's draw site (below NUI): draw, resync, then let the rest of the chain --
    /// and finally nuklear's own flush -- run. Hot, and on the same thread as the present.</summary>
    [UnmanagedCallersOnly]
    private static void OnNkRender(float aa)
    {
        DrawWindows();
        ((delegate* unmanaged<float, void>)(*_nkSlot))(aa);
    }

    /// <summary>Refresh the session gate + UI scale, run the z-order roll-call, then hand the GL state back.
    /// The hand-back is #210 (G1) one call earlier: our raw GL desyncs GLRender's shadow cache, and what
    /// renders next is NUI, not the present.</summary>
    private static void DrawWindows()
    {
        RefreshGate();
        if (GameGate.InGame) SyncUiScale();
        long tUi = FrameZones.Now;
        UiHost.DrawFrame();                       // draw every registered window in z-order (content-owned)
        FrameZones.Add(FrameZones.Zone.Ui, tUi);
        if (_resetStates != null) _resetStates();
        if (_bindBuffer != null) _bindBuffer(GlArrayBuffer, 0);
    }

    private static long _fsLast, _fsSum, _fsMax;
    private static int _fsN, _fsOver7, _fsOver17;
    /// <summary>GC counts and bytes at the last period line: a gen0 count near the hitch count is a per-frame allocator.</summary>
    private static int _gc0, _gc1, _gc2;
    private static long _alloc;

    /// <summary>Present-to-present frame stats, logged once per ~900 frames: avg/max ms plus counts over the
    /// 144Hz (6.9ms) and 60Hz (16.7ms) budgets. The one number the perf work kept lacking -- cube/depth pass
    /// times exist but nothing tied them to what the player actually experiences. Hot path: one timestamp,
    /// four adds; the log line is the only cold part.</summary>
    private static void FrameStats()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fsLast != 0 && GameGate.InModule)
        {
            long dt = now - _fsLast;
            FrameZones.EndFrame(dt);   // #238: fold this frame's zone split in, against the same dt
            _fsSum += dt; _fsN++;
            if (dt > _fsMax) _fsMax = dt;
            float ms = dt * 1000f / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 6.9f) _fsOver7++;
            if (ms > 16.7f) _fsOver17++;
            if (_fsN >= 900 && !ExtenderLog.Enabled)
            {
                // silent: reset the period without building a line of it (Period() carries the ledger reset)
                FrameZones.Period();
                _fsSum = 0; _fsN = 0; _fsMax = 0; _fsOver7 = 0; _fsOver17 = 0;
            }
            else if (_fsN >= 900)
            {
                float avg = _fsSum * 1000f / System.Diagnostics.Stopwatch.Frequency / _fsN;
                float max = _fsMax * 1000f / System.Diagnostics.Stopwatch.Frequency;
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                long alloc = GC.GetTotalAllocatedBytes();
                _log?.Info($"UiHostService: [frame] avg {avg:F2}ms ({1000f / avg:F0}fps) max {max:F1}ms, >6.9ms {_fsOver7}/{_fsN}, >16.7ms {_fsOver17}/{_fsN}"
                         + $", gc {gc0 - _gc0}/{gc1 - _gc1}/{gc2 - _gc2} on {(alloc - _alloc) >> 10}KiB allocated" + FrameZones.BoxLine() + FrameZones.ThreadsLine());
                _gc0 = gc0; _gc1 = gc1; _gc2 = gc2; _alloc = alloc;
                // #238: NOT inside the null-conditional -- Period() carries the period reset, and `?.` skips
                // argument evaluation, so a null log would desync it from the _fs* counters reset below
                // permanently (every later line would then average since boot).
                string zones = FrameZones.Period() + CX.Graphics.GlContextService.GpuSpanLine();
                _log?.Info(zones);                 // ...and where that frame went
                _fsSum = 0; _fsN = 0; _fsMax = 0; _fsOver7 = 0; _fsOver17 = 0;
            }
        }
        else FrameZones.Discard();   // #238: menu/loading frames are not the period's
        _fsLast = now;
    }

    // In-game gate: draw when a module is loaded and no full-screen options menu / hidden HUD is up. The walk is
    // verified against the disassembly of GetInGameGui and IsVisible -- every offset and the 4-byte width:
    //   mgr  = *g_pAppManager           (CAppManager*)
    //   app  = *mgr                     (CClientExoApp* at CAppManager+0)
    //   gui  = *(*(app+0x8)+0x90)       (CClientExoApp::GetInGameGui -- persists, non-null even on the menu)
    //   win  = *(gui+0x8)               (CGuiInGame's chat window -- NULL until a module loads = the in-area signal)
    //   opt  = *(int*)(intern+0x604)    (CClientExoApp::SetOptionsPanelOpen flag -- 1 while the ESC menu is up)
    //   hud  = *(int*)(gui+0x3d4)       (CGuiInGame::ToggleOff sets this; H key hides all HUD)
    //   show = win != null && opt == 0 && hud == 0
    // Chat suppresses CGuiInGameChatWindow::Show (#23), so the vanilla window never activates and its IsVisible
    // (win+0xc68) stays 0 -- module presence + no-ESC-menu is the in-area signal now. A null `win` is the
    // main-menu case (no module) => hidden. The [uigate] log fires only on a state transition (rare).
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private const int ExoAppInternalOff = 0x8, InGameGuiOff = 0x90, ChatWindowOff = 0x8,
                      OptionsPanelOpenOff = 0x604, GuiToggledOffOff = 0x3d4;
    private static (IntPtr win, bool opt, bool hud) _gateSig;

    /// <summary>Recompute the gate and publish it onto <see cref="GameGate.InGame"/> / <see cref="GameGate.InModule"/>.
    /// Hot, zero-alloc: guarded pointer reads at each hop; log only on transition. The module-unload edge and
    /// scale reaction are the content's to observe (chat resets its session on the InModule true->false edge).</summary>
    private static void RefreshGate()
    {
        IntPtr mgr = _appMgr != IntPtr.Zero ? *(IntPtr*)_appMgr : IntPtr.Zero;
        IntPtr app = mgr != IntPtr.Zero ? *(IntPtr*)mgr : IntPtr.Zero;
        IntPtr intern = app != IntPtr.Zero ? *(IntPtr*)(app + ExoAppInternalOff) : IntPtr.Zero;
        IntPtr gui = intern != IntPtr.Zero ? *(IntPtr*)(intern + InGameGuiOff) : IntPtr.Zero;
        IntPtr win = gui != IntPtr.Zero ? *(IntPtr*)(gui + ChatWindowOff) : IntPtr.Zero;
        bool opt = intern != IntPtr.Zero && *(int*)(intern + OptionsPanelOpenOff) != 0;   // ESC/game menu up
        bool hud = gui != IntPtr.Zero && *(int*)(gui + GuiToggledOffOff) != 0;            // H: HUD toggled off
        bool show = win != IntPtr.Zero && !opt && !hud;

        GameGate.InModule = win != IntPtr.Zero;
        GameGate.InGame = show;

        var sig = (win, opt, hud);
        if (sig != _gateSig)
        {
            _gateSig = sig;
            _log?.Info($"[uigate] win=0x{win:x} opt={opt} hud={hud} -> inGame={show} inModule={win != IntPtr.Zero}");
        }
    }

    /// <summary>Track the engine's live GUI scale (CAurora::GetGUIScale) into <see cref="Metrics.GameUiScale"/> so
    /// windowed content scales with the game's UI-scaling option. A cheap float read every in-game frame; the
    /// per-window re-bake/relayout is the content's reaction to the changed value (rare -- resolution change /
    /// options slider), keeping this hot path zero-alloc in the steady state.</summary>
    private static void SyncUiScale()
    {
        if (_getGuiScale == null) return;
        float s = _getGuiScale();
        if (s < 1f) s = 1f;   // engine floors at 1.0; guards an early 0 read before CAurora computes it
        if (System.Math.Abs(s - Metrics.GameUiScale) < 0.001f) return;
        Metrics.GameUiScale = s;
    }
}
