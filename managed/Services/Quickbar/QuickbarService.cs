using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Chat;
using Sowx.Services.Chat.Render;
using Sowx.Ui;

namespace Sowx.Services.Quickbar;

/// <summary>
/// Quickbar chrome service: replaces the native <c>CPanelQuickBar::Reposition</c> to fix centering,
/// suppress the buggy <c>GUI_QBAR_ENDS</c> model, and draw configurable full-width background bars
/// via the existing GL pipeline. Settings opened by <c>/qbar</c> chat command (no cog icon — the panel
/// is position-independent). Produces <see cref="RenderFrame"/>s consumed by <see cref="ChatService"/>
/// alongside chat windows — no second SwapBuffers hook.
/// </summary>
public sealed unsafe class QuickbarService : IExtenderService
{
    private static ExtenderLog? _log;

    private static readonly IntPtr _guiMan = Native.Sym(SymbolMap.GuiMan);
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    private const int QuickbarModelW = 800;
    private const int QuickbarBaseH = 36;

    /// <summary>CGuiManager viewport width/height offsets (int32 pair).</summary>
    private const int GuiManVpWOff = 0xb8, GuiManVpHOff = 0xbc;
    /// <summary>CClientExoAppInternal → CGuiInGame* offset.</summary>
    private const int InGameGuiOff = 0x90;
    /// <summary>CGuiInGame → CPanelQuickBar* offset.</summary>
    private const int QuickbarPanelOff = 0x60;
    /// <summary>CClientExoApp → CClientExoAppInternal* offset.</summary>
    private const int ExoAppInternalOff = 0x8;

    private static QuickbarConfig _config = new();
    private static readonly QuickbarSettings _settings = new();
    private static readonly ConfigFile _file = new("sowx-quickbar.cfg");

    private static volatile int _vpW, _vpH;
    private static float _guiScale = 1f;

    private static bool _dirty = true;
    private static bool _configLoaded;
    private static volatile bool _live;

    // Compass hide/show engine calls.
    private static delegate* unmanaged<IntPtr, void> _hideCompass, _showCompass;
    private static bool _compassApplied;
    private static bool _qbPressConsumed;

    private static volatile RenderFrame[] _extraFrames = Array.Empty<RenderFrame>();
    private static VertexBatch? _bandBatch, _settingsBatch;
    /// <summary>Extra frames this service wants drawn (band under chat, settings on top).</summary>
    public static RenderFrame[] ExtraFrames => _extraFrames;

    /// <summary>#232 second mode, read at frame time by the Alloy outline-highlight pass (a bool field
    /// read; no allocation, safe from the render thread).</summary>
    public static bool OutlineHighlight => _config.OutlineHl;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        QuickbarConfig.SelfCheck(log);
        QuickbarSettings.SelfCheck(log);
        CommandRegistry.Global.Register("/settings", "toggle the quickbar settings panel",
            _ => { ToggleSettings(); return null; });
        CommandRegistry.Global.Register("/compass", "toggle the compass", _ => { ToggleCompass(); return null; });

        IntPtr tramp = hooks.Install(
            SymbolMap.QuickbarReposition,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnReposition);
        log.Info(tramp != IntPtr.Zero
            ? "QuickbarService: Reposition hook installed"
            : "QuickbarService: Reposition hook not installed (symbol unresolved)");

        _setPosScreen = (delegate* unmanaged<IntPtr, int, int, void>)Native.Sym(SymbolMap.SetPositionScreen);
        _hideCompass = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.HideCompass);
        _showCompass = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ShowCompass);

        // Register a framework Window so the input layer routes mouse events to the settings panel (#64). The
        // quickbar draws through ChatService's frame list (no DrawContent here); it only needs input + a hit-test
        // override (its panel is the only clickable region, and only while open). Skipped under LOCKCHAT, where
        // the framework input layer is down and the quickbar is inert (Tick never runs).
        if (GameGate.OwnsFramePresent)
            _window = new Window(0, 0, 0, 0)
            {
                Focusable = false,
                OnMouseButton = OnMouseButton,
                OnMouseMotion = OnMouseMotion,
                HitTestOverride = (px, py) => MouseOverUi((int)px, (int)py),
            };
    }

    private static Window? _window;

    private static delegate* unmanaged<IntPtr, int, int, void> _setPosScreen;

    [UnmanagedCallersOnly]
    private static void OnReposition(IntPtr self)
    {
        if (!_configLoaded) LoadConfig();

        int vpW = 0;
        if (_guiMan != IntPtr.Zero)
        {
            IntPtr gm = *(IntPtr*)_guiMan;
            if (gm != IntPtr.Zero) { vpW = *(int*)(gm + GuiManVpWOff); _vpH = *(int*)(gm + GuiManVpHOff); }
        }
        _vpW = vpW;

        if (_setPosScreen == null) return;

        if (_config.Hidden)
        {
            _setPosScreen(self, -9999, -9999);
        }
        else
        {
            int x = _config.Align switch
            {
                QbarAlign.Left => 0,
                QbarAlign.Right => vpW - QuickbarModelW,
                _ => (vpW - QuickbarModelW) / 2,
            };
            _setPosScreen(self, x, 0);
        }
        _dirty = true;
    }

    /// <summary>Called each frame by ChatService to refresh viewport + rebuild chrome if dirty.</summary>
    public static void Tick(float guiScale)
    {
        _live = true;
        if (!_configLoaded) LoadConfig();

        int vpW = 0, vpH = 0;
        if (_guiMan != IntPtr.Zero)
        {
            IntPtr gm = *(IntPtr*)_guiMan;
            if (gm != IntPtr.Zero) { vpW = *(int*)(gm + GuiManVpWOff); vpH = *(int*)(gm + GuiManVpHOff); }
        }
        if (vpW != _vpW || vpH != _vpH) { _vpW = vpW; _vpH = vpH; _dirty = true; }

        if (System.Math.Abs(guiScale - _guiScale) > 0.001f) { _guiScale = guiScale; _dirty = true; }

        ApplyCompass();

        if (_config.GridOn && _config.GridPx > 0)
        {
            Chat.Core.ChatWindow.SnapPx = _config.GridPx;
            Chat.Core.ChatWindow.SnapCX = vpW / 2f;
            Chat.Core.ChatWindow.SnapCY = vpH / 2f;
        }
        else
        {
            Chat.Core.ChatWindow.SnapPx = 0;
        }

        if (_config.EdgeSnap && vpW > 0)
        {
            Chat.Core.ChatWindow.EdgeThresh = Chat.Core.ChatWindow.EdgeMagnet;
            Chat.Core.ChatWindow.EdgeVpW = vpW;
            Chat.Core.ChatWindow.EdgeVpH = vpH;
        }
        else
        {
            Chat.Core.ChatWindow.EdgeThresh = 0;
        }

        if (_dirty) { RebuildChrome(); ChatService.RequestRebuild(); }
    }

    /// <summary>Go dormant off-module.</summary>
    public static void Sleep()
    {
        _live = false;
        _settings.Reset();
        _compassApplied = false;
        _extraFrames = Array.Empty<RenderFrame>();
        Chat.Core.ChatWindow.SnapPx = 0;
        Chat.Core.ChatWindow.EdgeThresh = 0;
    }

    /// <summary>True while the quickbar settings panel is open.</summary>
    public static bool SettingsOpen => _settings.Open;

    /// <summary>The quickbar button-bar rect (bottom-centered/aligned 800x36), for window snapping. False when
    /// hidden or the viewport isn't known yet. The quickbar itself never drags -- it is only ever an obstacle.</summary>
    public static bool SnapRectOut(out (float X, float Y, float W, float H) r)
    {
        int vpW = _vpW, vpH = _vpH;
        if (_config.Hidden || vpW <= 0 || vpH <= 0) { r = default; return false; }
        float qW = QuickbarModelW, qH = QuickbarBaseH;
        float qX = _config.Align switch { QbarAlign.Left => 0f, QbarAlign.Right => vpW - qW, _ => (vpW - qW) / 2f };
        r = (qX, vpH - qH, qW, qH);
        return true;
    }

    /// <summary>Toggle compass visibility (called from <c>/compass</c> chat command).</summary>
    public static void ToggleCompass()
    {
        _config.CompassHidden = !_config.CompassHidden;
        ApplyCompass();
        SaveConfig();
    }

    /// <summary>Toggle the settings panel on/off (called from <c>/qbar</c> chat command).</summary>
    public static void ToggleSettings()
    {
        if (_settings.Open)
        {
            _settings.Reset();
        }
        else
        {
            int vpW = _vpW > 0 ? _vpW : TextRig.ViewportW;
            int vpH = _vpH > 0 ? _vpH : TextRig.ViewportH;
            _settings.OpenAt(vpW, vpH, _config);
            _log?.Info($"[settings] opened at viewport {vpW}x{vpH} (guiman {_vpW}x{_vpH}, rig {TextRig.ViewportW}x{TextRig.ViewportH})");
        }
        RebuildChrome();
        ChatService.RequestRebuild();
    }

    /// <summary>Restore the settings. A no-op that stays retryable until <see cref="ConfigFile"/>'s deferral
    /// gate opens (our loader runs pre-main), so the callers below just keep asking.</summary>
    private static void LoadConfig()
    {
        if (!_file.Load(_log, ApplyText)) return;
        _configLoaded = true;
        _file.Baseline(_config.ToText());   // a load must not provoke a write of what is already on disk
        _dirty = true;
    }

    private static void ApplyText(string text) => _config = QuickbarConfig.Parse(text);

    private static void SaveConfig() => _file.Save(_config.ToText(), _log);

    /// <summary>Apply compass visibility to the engine. Edge-triggered: only calls Hide/Show when the
    /// state changes (not every frame).</summary>
    private static void ApplyCompass()
    {
        bool want = _config.CompassHidden;
        if (want == _compassApplied) return;
        IntPtr gui = LiveGuiInGame();
        if (gui == IntPtr.Zero) return;
        _compassApplied = want;
        if (want && _hideCompass != null) _hideCompass(gui);
        else if (!want && _showCompass != null) _showCompass(gui);
    }

    /// <summary>Rebuild the chrome vertex batch: full-width band (if bars on and quickbar visible).</summary>
    private static void RebuildChrome()
    {
        _dirty = false;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || _vpW <= 0 || _vpH <= 0)
        {
            _extraFrames = Array.Empty<RenderFrame>();
            return;
        }

        int vpW = _vpW, vpH = _vpH;
        VertexBatch b = _bandBatch ??= new VertexBatch(0, 0);
        b.Reset(atlas.SolidU, atlas.SolidV);

        if (_config.Bars && !_config.Hidden)
        {
            float qW = QuickbarModelW;
            float qH = QuickbarBaseH;
            float qX = _config.Align switch
            {
                QbarAlign.Left => 0f,
                QbarAlign.Right => vpW - qW,
                _ => (vpW - qW) / 2f,
            };
            float qY = vpH - qH;

            const float barR = 0f, barG = 0f, barB = 0f, barA = 0.55f;
            if (qX > 0f)
                b.Rect(0f, qY, qX, (float)vpH, barR, barG, barB, barA);
            if (qX + qW < vpW)
                b.Rect(qX + qW, qY, (float)vpW, (float)vpH, barR, barG, barB, barA);
        }

        if (_config.GridOn && _config.GridPx > 0)
        {
            int gp = _config.GridPx;
            float cx = vpW / 2f, cy = vpH / 2f;
            for (float lx = cx % gp; lx < vpW; lx += gp)
            {
                bool ctr = System.Math.Abs(lx - cx) < 1f;
                b.Rect(lx, 0, lx + 1, vpH, ctr ? 0.8f : 0f, 0f, 0f, ctr ? 0.4f : 0.3f);
            }
            for (float ly = cy % gp; ly < vpH; ly += gp)
            {
                bool ctr = System.Math.Abs(ly - cy) < 1f;
                b.Rect(0, ly, vpW, ly + 1, ctr ? 0.8f : 0f, 0f, 0f, ctr ? 0.4f : 0.3f);
            }
        }

        int vc = b.VertexCount;
        var band = new RenderFrame { Verts = b.ToArray(), Count = vc, BodyStart = vc, BodyEnd = vc };

        if (_settings.Open)
        {
            VertexBatch sb = _settingsBatch ??= new VertexBatch(0, 0);
            sb.Reset(atlas.SolidU, atlas.SolidV);
            EmitSettingsPanel(sb, atlas);
            int svc = sb.VertexCount;
            var settingsFrame = new RenderFrame { Verts = sb.ToArray(), Count = svc, BodyStart = svc, BodyEnd = svc };
            _extraFrames = new[] { band, settingsFrame };
        }
        else
        {
            _extraFrames = vc > 0 ? new[] { band } : Array.Empty<RenderFrame>();
        }
    }

    /// <summary>Hand the open panel to the shared <see cref="SettingsPanel"/> draw, in the quickbar's chrome
    /// colours.</summary>
    private static void EmitSettingsPanel(VertexBatch b, GlyphAtlas atlas)
    {
        float baseDrop = atlas.LineHeight;
        _settings.Panel.Emit(b, atlas, baseDrop, new SettingsPanel.Palette
        {
            Body = (0.02f, 0.02f, 0.03f, 1f),
            TitleBar = (0.10f, 0.12f, 0.16f, 1f),
            Active = (0.20f, 0.34f, 0.52f, 0.95f),
            TextRgb = 0xd8dee9u,
        });
    }

    // ---- input (settings panel only — no cog, opened by /qbar command) ----

    /// <summary>Handle a mouse button press/release. Only consumes when the settings panel is open.</summary>
    public static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (!_live || button != 1) return false;

        if (down)
        {
            _qbPressConsumed = false;
            if (!_settings.Open) return false;
            _qbPressConsumed = true;
            QbarAlign alignBefore = _config.Align;
            bool hiddenBefore = _config.Hidden;
            bool compassBefore = _config.CompassHidden;
            _settings.MouseDown(x, y, _config);
            if (_config.Align != alignBefore || _config.Hidden != hiddenBefore) RepositionQuickbar();
            if (_config.CompassHidden != compassBefore) ApplyCompass();
            RebuildChrome();
            ChatService.RequestRebuild();
            return true;
        }

        if (!_qbPressConsumed) return false;
        _qbPressConsumed = false;
        _settings.MouseUp();
        SaveConfig();
        RebuildChrome();
        ChatService.RequestRebuild();
        return true;
    }

    /// <summary>Handle pointer motion. Only consumes when the settings panel is being dragged.</summary>
    public static bool OnMouseMotion(int x, int y)
    {
        if (!_live || !_settings.Open) return false;
        if (_settings.MouseMove(x, y))
        {
            RebuildChrome();
            ChatService.RequestRebuild();
            return true;
        }
        return false;
    }

    /// <summary>True when the pointer is over the quickbar settings panel.</summary>
    public static bool MouseOverUi(int x, int y) => _live && _settings.Open && _settings.Contains(x, y);

    private static void RepositionQuickbar()
    {
        IntPtr qbar = LiveQuickbar();
        if (qbar == IntPtr.Zero || _setPosScreen == null) return;

        if (_config.Hidden)
        {
            _setPosScreen(qbar, -9999, -9999);
            return;
        }
        int vpW = _vpW;
        int x = _config.Align switch
        {
            QbarAlign.Left => 0,
            QbarAlign.Right => vpW - QuickbarModelW,
            _ => (vpW - QuickbarModelW) / 2,
        };
        _setPosScreen(qbar, x, 0);
    }

    private static IntPtr LiveQuickbar()
    {
        IntPtr gui = LiveGuiInGame();
        if (gui == IntPtr.Zero) return IntPtr.Zero;
        return *(IntPtr*)(gui + QuickbarPanelOff);
    }

    /// <summary>Walk to CGuiInGame* (the in-game GUI container). Used for quickbar + compass.</summary>
    private static IntPtr LiveGuiInGame()
    {
        if (_appMgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return IntPtr.Zero;
        IntPtr intern = *(IntPtr*)(app + ExoAppInternalOff);
        if (intern == IntPtr.Zero) return IntPtr.Zero;
        return *(IntPtr*)(intern + InGameGuiOff);
    }
}
