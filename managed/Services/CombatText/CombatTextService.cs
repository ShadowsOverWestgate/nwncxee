using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Services.Chat.Core;
using Sowx.Services.Nameplate;
using Sowx.Ui;

namespace Sowx.Services.CombatText;

/// <summary>
/// Damage numbers and floating combat text, drawn by our own 2D UI instead of the engine's
/// <c>CGuiInGameFloatyText</c> panels. One cold hook on <c>CNWCObject::ShowFloatyText</c> -- the single
/// funnel every one of them passes through -- captures the string the engine already composed and swallows
/// the call, so no vanilla floaty panel is ever allocated.
/// <para><b>Why the vanilla settings come for free.</b> Consolidation
/// (<c>ui.floating-text-feedback-damage-totals-only</c>), the totals colour and the per-damage-type colours
/// are all applied by the engine BEFORE this seam: a per-type number arrives as
/// <c>&lt;cRGB&gt;12&lt;/c&gt;</c> and a consolidated one as the user's totals colour around the sum. We
/// parse those tags with the chat box's own <see cref="ColorRunParser"/>, which is also what makes combat-text
/// banners respect colour codes. The master <c>ui.floating-text-feedback</c> switch is upstream of the seam
/// too -- with it off we are simply never called.</para>
/// <para><b>Dependency.</b> The screen anchor is the engine's own projection, read off the object's chat
/// bubble (<c>bubble+0x154</c>). That is only refreshed every tick for every object while
/// <see cref="NameplateService"/>'s text-bubble mode-0 suppression is in force (docs/re/nameplate.md §D), so
/// this feature follows the nameplate feature: with nameplates stood down -- or for an object that owns no
/// bubble at all, which is every non-creature -- the hook hands the element back to vanilla rather than
/// swallowing one it could only have drawn at a stale point.</para>
/// </summary>
public sealed unsafe class CombatTextService : IExtenderService
{
    private static ExtenderLog? _log;
    private static CombatTextConfig _cfg = new();
    private static readonly ConfigFile _file = new("sowx-combattext.cfg");
    private static bool _configLoaded, _armed, _wasInModule;

    private static readonly FloatyTable _table = new();
    private static Window? _window;

    /// <summary>The engine's <c>Vector</c> parameter, by value. SysV puts a 12-byte all-float aggregate in
    /// xmm0 (x, y) + xmm1 (z), which is exactly what the disassembly at 0x8e7032 stores -- declaring it as
    /// three separate <c>float</c> parameters would instead read xmm0/xmm1/xmm2 and mis-tint every element.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3 { public float X, Y, Z; }

#if WINDOWS
    /// <summary>Win64 passes any by-value aggregate over 8 bytes as a pointer to a caller-made copy.</summary>
    private struct ColourArg { public Vec3* P; }
#else
    private struct ColourArg { public Vec3 V; }
#endif

    private static delegate* unmanaged<IntPtr, IntPtr, ColourArg, IntPtr, int, void> _origShowFloaty;

    /// <summary>The ShowFloatyText unit (#420): the funnel itself. Nameplates are a runtime prerequisite
    /// (checked per call, not here).</summary>
    private static readonly string[] RequiredSymbols = { SymbolMap.ShowFloatyText, SymbolMap.ExoStringDtor };

    /// <summary>Glyph height of the face the animation samples (#471): the largest an element ever gets --
    /// effective UI px × the spawn pop × the banner multiplier -- so every frame is a
    /// minification. Capped at <see cref="TextRig.AtlasMaxPx"/>: past it (UI 18px needs textscale &gt; 3.05)
    /// the top of the pop magnifies again. Written cold; read on the frame.</summary>
    private static volatile float _peakPx;

    /// <summary>The peak height for the current UI size and multipliers.</summary>
    private static float PeakPx(CombatTextConfig cfg)
    {
        float px = PeakPxRaw(cfg);
        return px > TextRig.AtlasMaxPx ? TextRig.AtlasMaxPx : px;
    }

    /// <summary>The uncapped peak, so the status line can say when the cap is in force.</summary>
    private static float PeakPxRaw(CombatTextConfig cfg)
    {
        return MathF.Round(Metrics.DesignFontPx * Metrics.Scale * FloatyAnim.PopFrom * cfg.TextScale);
    }

    /// <summary>COLD: (re)bake the peak face, dropping the previous one so the sheet does not accumulate every
    /// ceiling ever tried. Runs at arm time, after a multiplier change, and after a UI font change.</summary>
    private static void RequestPeakFace()
    {
        float px = PeakPx(_cfg);
        if (px == _peakPx && TextRig.BakedFace(px) != null) return;
        float old = _peakPx;
        if (old != 0f && old != px) TextRig.ReleaseFace(old);
        _peakPx = TextRig.Face(px) != null ? px : 0f;
        _log?.Info($"CombatTextService: peak face {(_peakPx > 0f ? $"{_peakPx}px" : "unavailable, drawing from the UI face")}");
    }

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Every pure piece self-checks BEFORE any stand-down return -- run-demo's only gate.
        CombatTextConfig.SelfCheck(log);
        FloatyAnim.SelfCheck(log);
        FloatyTable.SelfCheck(log);
        CombatTextNative.SelfCheck(log);

        if (!GameGate.OwnsFramePresent)
        { log.Info("CombatTextService: standing down (framework not owning frame present)"); return; }
        if (!SymbolMap.Require(log, "CombatTextService", RequiredSymbols)) return;   // #420: whole or nothing

        _origShowFloaty = (delegate* unmanaged<IntPtr, IntPtr, ColourArg, IntPtr, int, void>)hooks.Install(
            SymbolMap.ShowFloatyText,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, ColourArg, IntPtr, int, void>)&OnShowFloatyText);
        if (_origShowFloaty == null)
        {
            log.Info("CombatTextService: standing down (ShowFloatyText unresolved) -- vanilla damage numbers "
                   + "and combat text draw as they always did");
            return;
        }

        // Never hit-tests: this is world-anchored text over the 3D scene, and a UI rect here would take the
        // contextual cursor and swallow world clicks (see the nameplate feature's HitTestOverride trap).
        _window = new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        UiHost.SendToBack(_window);

        CommandRegistry.Global.Register("/combattext", "report or tune the damage numbers and combat text",
                                        Cmd, "/ct");
        TextRig.FontChanged += RequestPeakFace;
        RequestPeakFace();
        _armed = true;
        log.Info("CombatTextService: armed (vanilla floaty text swallowed at ShowFloatyText)");
    }

    // ---- the hook ----------------------------------------------------------------------------

    /// <summary>The one seam. COLD -- it runs on the message-handling thread, once per damage number or
    /// banner -- so resolving the strings and parsing the colour runs here is fine; the render thread only
    /// ever sees the parsed result.
    /// <para>Anything we cannot draw better is handed straight back to the original, so vanilla is untouched:
    /// that is the whole opt-out, and it is also what keeps an element we could not have anchored from being
    /// lost between the two renderers.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnShowFloatyText(IntPtr self, IntPtr text, ColourArg colourArg, IntPtr font, int background)
    {
        CombatTextConfig cfg = _cfg;   // one read: LoadConfig replaces the reference on the render thread
#if WINDOWS
        Vec3 colour = *colourArg.P;   // Win64: a 12-byte Vector by value arrives as a pointer
#else
        Vec3 colour = colourArg.V;
#endif
        // Hand back to vanilla unless we can actually draw this one BETTER. Swallowing an element we then
        // fail to place would lose it outright, so every reason it might not reach the screen is tested here,
        // on the cold path, and not in the draw:
        //   - the config has not loaded yet, so an `on=0` file has not been honoured
        //   - the nameplate feature is stood down, so nothing keeps the object's screen anchor fresh
        //   - the object owns no chat bubble, so it has no anchor at all (a placeable or door taking damage:
        //     only creatures get one, via CNWCCreature::SetName -- docs/re/nameplate.md §D)
        if (!_armed || !_configLoaded || !cfg.On || self == IntPtr.Zero
            || !NameplateService.Suppressing || NameplateNative.Bubble(self) == IntPtr.Zero)
        {
            if (_origShowFloaty != null) _origShowFloaty(self, text, colourArg, font, background);
            return;
        }

        try
        {
            string body = NameplateNative.ReadExo(text);
            string fontName = NameplateNative.ReadExo(font);
            if (OperatingSystem.IsWindows()) { Native.DestroyExoString(text); Native.DestroyExoString(font); }   // Win64: the swallowed callee owned both copies
            if (body.Length == 0) return;                 // an empty floaty draws nothing in either renderer

            long now = Environment.TickCount64;
            FloatyKind kind = CombatTextNative.IsDamageFont(fontName) ? FloatyKind.Damage : FloatyKind.Banner;
            _table.Note(NameplateNative.IdOf(self), kind, ColorRunParser.Parse(body),
                        colour.X, colour.Y, colour.Z, now);
        }
        catch (Exception e)
        {
            // A managed throw must never unwind into the engine's C++ frame. Losing one number is nothing;
            // losing the client is not.
            _log?.Info($"[combattext] capture failed: {e.GetType().Name}");
        }
    }

    // ---- config ------------------------------------------------------------------------------

    private static void LoadConfig()
    {
        if (!_file.Load(_log, t => _cfg = CombatTextConfig.Parse(t))) return;
        _configLoaded = true;
        _file.Baseline(_cfg.ToText());
    }

    private static void SaveConfig() => _file.Save(_cfg.ToText(), _log);

    // ---- the per-frame draw ------------------------------------------------------------------

    /// <summary>Element geometry, reused. Doubling is a guard, not a path.</summary>
    private static float[] _verts = new float[4096];

    /// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs -- the chat renderer's.</summary>
    private static readonly float[] OutlineOffsets =
    { -1f, -1f, 0f, -1f, 1f, -1f, -1f, 0f, 1f, 0f, -1f, 1f, 0f, 1f, 1f, 1f };

    private static void Draw()
    {
        if (!_armed) return;
        if (!_configLoaded)
        {
            LoadConfig();
            // ponytail: pool thread so the cold bake leaves the frame; TextRig's bake lock orders it against SetFont.
            if (_configLoaded && PeakPx(_cfg) != _peakPx) System.Threading.ThreadPool.QueueUserWorkItem(_ => RequestPeakFace());
        }

        // Session reset: object ids are only unique within a module, so a number carried across an unload
        // would land on whatever inherits the id.
        if (_wasInModule && !GameGate.InModule) _table.Clear();
        _wasInModule = GameGate.InModule;

        long now = Environment.TickCount64;
        int count = _table.Advance(now, _cfg);   // BEFORE every early return: the hook ring is fixed-size
        if (count == 0 || !GameGate.InGame || !_cfg.On) return;

        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || !NameplateNative.Ready) return;

        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        if (vpW <= 0f || vpH <= 0f) return;
        if (!NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh))
        { rx = 0f; ry = 0f; rw = vpW; rh = vpH; }
        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        float gui = Metrics.GameUiScale;
        float ui = Metrics.Scale;
        float uiPx = Metrics.DesignFontPx * ui;
        float peakPx = _peakPx;
        GlyphAtlas face = TextRig.BakedFace(peakPx) ?? atlas;
        float facePx = face == atlas ? uiPx : peakPx;
        float down = uiPx / facePx;

        int n = 0;
        for (int i = 0; i < count; i++)
        {
            FloatyTable.Slot s = _table.Live[i];
            IntPtr obj = NameplateNative.GameObject(s.Id);
            if (obj == IntPtr.Zero) continue;                       // despawned: it just stops drawing
            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay)) continue;
            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out float x, out float y);
            if (x < 0f || y < 0f || x > vpW || y > vpH) continue;
            // The engine's projection never checks the sign of its denominator, so an object BEHIND the camera
            // lands on a MIRRORED anchor that passes the rect test above (docs/re/nameplate.md §5).
            if (hasCam && hasFwd)
            {
                NameplateNative.Position(obj, out float ox, out float oy, out float oz);
                if ((ox - cx) * fx + (oy - cy) * fy + (oz - cz) * fz <= 0f) continue;
            }

            Place(s, now, ui, atlas.LineHeight, ref x, ref y, out float scale, out float alpha);
            if (alpha <= 0f) continue;

            float k = scale * down;
            float w = Measure(s, face, facePx) * k;
            EmitSlot(ref n, face, s, x - w * 0.5f, y, k, scale, alpha);
        }

        if (n > 0) TextRig.DrawBatch(_verts, n / VertexBatch.Floats);
    }

    /// <summary>Where and how big one element is this frame, per kind. The one place the behaviours the
    /// feature promises are chosen: a damage number scrolls and fades, a banner punches in and drifts.</summary>
    private static void Place(FloatyTable.Slot s, long now, float ui, float lineH,
                              ref float x, ref float y, out float scale, out float alpha)
    {
        long age = now - s.SpawnMs;
        float t = FloatyAnim.T(age, s.LifeMs);
        alpha = FloatyAnim.Fade(t);
        scale = FloatyAnim.Pop(age);

        // Everything on one head is a column. The step is the larger of the knob and a line of text, so a
        // bigger font never lets two rows grow into each other.
        float step = _cfg.StackPx * ui;
        if (step < lineH) step = lineH;
        y -= s.Stack * step;

        switch (s.Kind)
        {
            case FloatyKind.Damage:
                y -= _cfg.RisePx * ui * FloatyAnim.EaseOut(t);
                break;

            case FloatyKind.Banner:
                // Banners have their own column, lifted clear of the damage one.
                scale *= _cfg.TextScale;
                y -= _cfg.BannerLiftPx * ui + (_cfg.RisePx * 0.35f) * ui * FloatyAnim.EaseOut(t);
                break;
        }
    }

    /// <summary>Text width in <paramref name="atlas"/>'s px at scale 1, cached per slot and invalidated by an atlas
    /// re-bake or a change of measured face height.</summary>
    private static float Measure(FloatyTable.Slot s, GlyphAtlas atlas, float facePx)
    {
        if (s.MeasuredW >= 0f && s.MeasureEpoch == TextRig.AtlasEpoch && s.MeasurePx == facePx) return s.MeasuredW;
        float w = 0f;
        for (int r = 0; r < s.Runs.Length; r++)
            foreach (Rune ru in s.Runs[r].Text.EnumerateRunes()) w += atlas.AdvanceOf(ru);
        s.MeasuredW = w;
        s.MeasureEpoch = TextRig.AtlasEpoch;
        s.MeasurePx = facePx;
        return w;
    }

    /// <summary><paramref name="scale"/> is the quad factor on the face's glyphs; <paramref name="anim"/> is the
    /// animation scale relative to the UI size, which is what the outline halo grows with.</summary>
    private static void EmitSlot(ref int n, GlyphAtlas atlas, FloatyTable.Slot s,
                                 float penX, float baseline, float scale, float anim, float alpha)
    {
        // The halo grows with the element: a 1px outline behind a 1.3x banner is a grey fringe, not an outline.
        float halo = anim < 1f ? 1f : anim;
        for (int r = 0; r < s.Runs.Length; r++)
        {
            ColorRun run = s.Runs[r];
            // A rune the parser left at plain white takes the engine's own Vector colour -- the same
            // fallback rule the plate's override lines use for an untagged run.
            bool plain = run.R == 1f && run.G == 1f && run.B == 1f;
            float cr = plain ? s.BaseR : run.R, cg = plain ? s.BaseG : run.G, cb = plain ? s.BaseB : run.B;
            foreach (Rune ru in run.Text.EnumerateRunes())
            {
                if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru) * scale; continue; }
                if (_cfg.Outline)
                    for (int o = 0; o < OutlineOffsets.Length; o += 2)
                        EmitGlyph(ref n, gl, penX + OutlineOffsets[o] * halo,
                                  baseline + OutlineOffsets[o + 1] * halo, scale, 0f, 0f, 0f, alpha);
                EmitGlyph(ref n, gl, penX, baseline, scale, cr, cg, cb, alpha);
                penX += gl.Advance * scale;
            }
        }
    }

    private static void EmitGlyph(ref int n, in Glyph gl, float penX, float baseline, float scale,
                                  float r, float g, float b, float a)
    {
        if (gl.W <= 0) return;
        Grow(n + 6 * VertexBatch.Floats);
        float x0 = penX + gl.XOff * scale, y0 = baseline + gl.YOff * scale;
        float x1 = x0 + gl.W * scale, y1 = y0 + gl.H * scale;
        Vtx(ref n, x0, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1, y0, gl.U1, gl.V0, r, g, b, a);
        Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0, y1, gl.U0, gl.V1, r, g, b, a);
    }

    private static void Grow(int need)
    {
        while (need > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
    }

    private static void Vtx(ref int n, float x, float y, float u, float v, float r, float g, float b, float a)
    { float[] q = _verts; q[n++] = x; q[n++] = y; q[n++] = u; q[n++] = v; q[n++] = r; q[n++] = g; q[n++] = b; q[n++] = a; }

    // ---- the command -------------------------------------------------------------------------

    private static string? Cmd(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0)
            return $"[combattext] on={_cfg.On} max={_cfg.Max} rise={F(_cfg.RiseMs)}ms/{F(_cfg.RisePx)}px "
                 + $"stack={F(_cfg.StackPx)}px lift={F(_cfg.BannerLiftPx)}px "
                 + $"text={F(_cfg.TextMs)}ms x{F(_cfg.TextScale)} outline={_cfg.Outline} "
                 + $"| live={_table.LiveCount} overflow={_table.Overflow} peakface={F(_peakPx)}px{(PeakPxRaw(_cfg) > TextRig.AtlasMaxPx ? " (capped)" : "")}";

        switch (a[0])
        {
            case "on": _cfg.On = Flag(a); break;
            case "max": _cfg.Max = CombatTextConfig.Clamp(Num(a, _cfg.Max), CombatTextConfig.MaxMin, CombatTextConfig.MaxMax); break;
            case "risems": _cfg.RiseMs = ClampF(a, _cfg.RiseMs, CombatTextConfig.LifeMin, CombatTextConfig.LifeMax); break;
            case "risepx": _cfg.RisePx = ClampF(a, _cfg.RisePx, CombatTextConfig.RiseMin, CombatTextConfig.RiseMax); break;
            case "stack": _cfg.StackPx = ClampF(a, _cfg.StackPx, CombatTextConfig.StackMin, CombatTextConfig.StackMax); break;
            case "lift": _cfg.BannerLiftPx = ClampF(a, _cfg.BannerLiftPx, CombatTextConfig.LiftMin, CombatTextConfig.LiftMax); break;
            case "textms": _cfg.TextMs = ClampF(a, _cfg.TextMs, CombatTextConfig.LifeMin, CombatTextConfig.LifeMax); break;
            case "textscale": _cfg.TextScale = ClampF(a, _cfg.TextScale, CombatTextConfig.ScaleMin, CombatTextConfig.ScaleMax); break;
            case "outline": _cfg.Outline = Flag(a); break;
            default:
                return "[combattext] usage: /combattext | /combattext <on|max|risems|risepx|stack|lift|"
                     + "textms|textscale|outline> <value>";
        }
        SaveConfig();
        if (a[0] is "textscale") RequestPeakFace();   // cold: the command runs on the input thread
        return Cmd("");
    }

    private static bool Flag(string[] a) => a.Length < 2 || a[1] is "1" or "on" or "true" or "yes";
    private static int Num(string[] a, int fallback) => a.Length > 1 && int.TryParse(a[1], out int v) ? v : fallback;

    private static float ClampF(string[] a, float fallback, float lo, float hi)
    {
        float v = a.Length > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float p)
                  ? p : fallback;
        return CombatTextConfig.Clamp(v, lo, hi);
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
