using System;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Ui;

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
        SettingsPanel.SelfCheck(log);   // #166 shared settings panel: hit-test, drag, edit buffer (off-device)
        if (GameGate.Locked)   // lockchat: install nothing -> vanilla rendering intact
        {
            log.Info("UiHostService: lockchat set -- framework stood down, vanilla rendering left intact");
            return;
        }
        _getGuiScale = (delegate* unmanaged<float>)Native.Sym(SymbolMap.GetGuiScale);
        _swapOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwap);
        log.Info(_swapOrig != null
            ? "UiHostService: frame present owned (SwapBuffers) -- driving the window registry"
            : "UiHostService: frame present not installed (Install returned Zero: symbol unresolved or already hooked)");
    }

    private static delegate* unmanaged<IntPtr, void> _swapOrig;
    private static delegate* unmanaged<float> _getGuiScale;   // CAurora::GetGUIScale -> live GUI-scale multiplier
    private static bool _announced;

    /// <summary>The frame present: announce once, refresh the gate + UI scale, run the z-order draw roll-call,
    /// then present. Hot (every frame) and zero-alloc: pointer reads + a cached-delegate roll-call; the gate log
    /// fires only on a state transition and the scale reaction is the content's (rare, cold-shaped) concern.</summary>
    [UnmanagedCallersOnly]
    private static void OnSwap(IntPtr window)
    {
        if (!_announced)
        {
            _announced = true;
            _log?.Info("UiHostService: first frame presented -- hook is LIVE");
        }
        FrameRegistry.Global.Run(FramePhase.Start);
        RefreshGate();
        if (GameGate.InGame) SyncUiScale();
        UiHost.DrawFrame();                       // draw every registered window in z-order (content-owned)
        FrameRegistry.Global.Run(FramePhase.PrePresent);
        if (_swapOrig != null) _swapOrig(window); // present
        FrameStats();
        FrameRegistry.Global.Run(FramePhase.PostPresent);
    }

    private static long _fsLast, _fsSum, _fsMax;
    private static int _fsN, _fsOver7, _fsOver17;

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
            _fsSum += dt; _fsN++;
            if (dt > _fsMax) _fsMax = dt;
            float ms = dt * 1000f / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 6.9f) _fsOver7++;
            if (ms > 16.7f) _fsOver17++;
            if (_fsN >= 900)
            {
                float avg = _fsSum * 1000f / System.Diagnostics.Stopwatch.Frequency / _fsN;
                float max = _fsMax * 1000f / System.Diagnostics.Stopwatch.Frequency;
                _log?.Info($"UiHostService: [frame] avg {avg:F2}ms ({1000f / avg:F0}fps) max {max:F1}ms, >6.9ms {_fsOver7}/{_fsN}, >16.7ms {_fsOver17}/{_fsN}");
                _fsSum = 0; _fsN = 0; _fsMax = 0; _fsOver7 = 0; _fsOver17 = 0;
            }
        }
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
