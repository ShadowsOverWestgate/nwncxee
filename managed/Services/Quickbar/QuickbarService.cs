using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Chat;
using Sowx.Services.Chat.Core;
using Sowx.Services.Chat.Render;

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
    private static string _savedText = "";
    private static string ConfigPath => Native.ConfigPath("sowx-quickbar.cfg");

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

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        QuickbarConfig.SelfCheck(log);
        QuickbarSettings.SelfCheck(log);

        IntPtr tramp = hooks.Install(
            SymbolMap.QuickbarReposition,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnReposition);
        log.Info(tramp != IntPtr.Zero
            ? "QuickbarService: Reposition hook installed"
            : "QuickbarService: Reposition hook not installed (symbol unresolved)");

        _setPosScreen = (delegate* unmanaged<IntPtr, int, int, void>)Native.Sym(SymbolMap.SetPositionScreen);
        _hideCompass = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.HideCompass);
        _showCompass = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ShowCompass);
    }

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

        if (_dirty) { RebuildChrome(); ChatService.RequestRebuild(); }
    }

    /// <summary>Go dormant off-module.</summary>
    public static void Sleep()
    {
        _live = false;
        _settings.Reset();
        _compassApplied = false;
        _extraFrames = Array.Empty<RenderFrame>();
    }

    /// <summary>True while the quickbar settings panel is open.</summary>
    public static bool SettingsOpen => _settings.Open;

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
            int vpW = _vpW > 0 ? _vpW : TextRenderer.ViewportW;
            int vpH = _vpH > 0 ? _vpH : TextRenderer.ViewportH;
            _settings.OpenAt(vpW, vpH, _config);
        }
        RebuildChrome();
        ChatService.RequestRebuild();
    }

    private static void LoadConfig()
    {
        _configLoaded = true;
        try { if (System.IO.File.Exists(ConfigPath)) _config = QuickbarConfig.Parse(System.IO.File.ReadAllText(ConfigPath)); }
        catch (Exception e) { _log?.Info($"QuickbarService: config load failed: {e.Message}"); }
        _savedText = _config.ToText();
        _dirty = true;
    }

    private static void SaveConfig()
    {
        string text = _config.ToText();
        if (text == _savedText) return;
        _savedText = text;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
            System.IO.File.WriteAllText(ConfigPath, text);
        }
        catch (Exception e) { _log?.Info($"QuickbarService: config save failed: {e.Message}"); }
    }

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
        GlyphAtlas? atlas = TextRenderer.Atlas;
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

    private static void EmitSettingsPanel(VertexBatch b, GlyphAtlas atlas)
    {
        float x = _settings.X, y = _settings.Y, w = _settings.W;
        float vpad = (QuickbarSettings.RowH - atlas.LineHeight) / 2f;
        float baseDrop = atlas.LineHeight - atlas.Ascent + atlas.Ascent;

        const float bgR = 0.02f, bgG = 0.02f, bgB = 0.03f;
        const float tbR = 0.10f, tbG = 0.12f, tbB = 0.16f;
        b.Rect(x, y, x + w, y + _settings.H, bgR, bgG, bgB, 1f);
        b.Rect(x, y, x + w, y + QuickbarSettings.TitleH, tbR, tbG, tbB, 1f);
        DrawString(b, atlas, "Settings", x + QuickbarSettings.Pad, y + baseDrop, 0xd8dee9u,
                   x + w - QuickbarSettings.CloseW);
        DrawString(b, atlas, "×", x + w - QuickbarSettings.CloseW + 5f, y + baseDrop, 0xB8C0CCu, x + w);

        const float acR = 0.20f, acG = 0.34f, acB = 0.52f, acA = 0.95f;
        const float ftR = 0.02f, ftG = 0.02f, ftB = 0.03f;
        const float cfR = 0.55f, cfG = 0.55f, cfB = 0.55f, cfA = 1f;

        foreach (SetRow r in _settings.Rows)
        {
            DrawString(b, atlas, r.Label, x + QuickbarSettings.Pad, r.Top + baseDrop + vpad,
                       0xd8dee9u, x + w * 0.42f - 2f);

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float cs = SettingsWindow.CheckSize;
                    float cbx = x + w - QuickbarSettings.Pad - cs;
                    float cby = r.Top + (QuickbarSettings.RowH - cs) / 2f;
                    b.Rect(cbx, cby, cbx + cs, cby + cs, cfR, cfG, cfB, cfA);
                    if (r.On)
                        b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, acR, acG, acB, acA);
                    else
                        b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, ftR, ftG, ftB, 1f);
                    break;
                }
                case SetKind.Cycle:
                {
                    float bx0 = x + w * 0.42f, bx1 = x + w - QuickbarSettings.Pad;
                    b.Rect(bx0, r.Top + 3, bx1, r.Top + QuickbarSettings.RowH - 3, tbR, tbG, tbB, 1f);
                    DrawString(b, atlas, r.Value, bx0 + 4f, r.Top + baseDrop + vpad, 0xd8dee9u, bx1 - 2f);
                    break;
                }
            }
        }
    }

    private static float DrawString(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                                    uint rgb, float clipX)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        foreach (System.Text.Rune ru in s.EnumerateRunes())
        {
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
        }
        return penX;
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
