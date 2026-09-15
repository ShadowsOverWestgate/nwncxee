using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Alloy weather (map #614): the client's weather state, captured at the engine's own entry points,
/// and the vanilla emitter silenced at its source. <c>CNWCArea::SetWeather(type, density)</c> is the one
/// call that carries state -- <c>CNWCArea::Update</c> ramps density through it over the 6 s fade and it
/// deletes/creates the six stock fx objects itself -- so with the master on the hook forwards density 0
/// (the engine's own "no weather" path, nothing to suppress downstream) and keeps the real value here.
/// Master off forwards untouched: the stock emitter draws as before. The two edge calls are logged only.
/// An interior area (<see cref="FrameInputs.Interior"/>) draws and soaks nothing whatever weather it was given;
/// the stock emitter is silenced there too, since the master promises nothing gets in.
/// <para>Rain vs snow is the weathertypes.2da row's Model; the rows are the two text knobs so a server's
/// own 2DA is one setting, not a build. ponytail: read the 2DA through <c>C2DA</c> if a third kind appears.</para></summary>
internal static unsafe partial class Weather
{
    internal enum Kind : byte { None, Rain, Snow }

    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<string> RainRows, SnowRows;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, byte, float, int> _origSet;
    private static delegate* unmanaged<IntPtr, byte, void> _origStart, _origStop;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _getModule =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
    private static readonly int ModuleArea = EngineLayout.ModuleCurrentArea;
    private static bool _off;
    /// <summary>Row -> kind, 32 rows: weathertypes.2da has 11 on SoW, 3 stock.</summary>
    private static readonly Kind[] _kinds = new Kind[32];

    /// <summary>The engine's weather as of the last SetWeather: the 2DA row, its kind, and density 0..1.
    /// Type is the last NON-ZERO row: the down ramp passes the engine's current type, which is the 0 the
    /// silence left behind.</summary>
    internal static byte Type;
    internal static Kind CurrentKind;
    internal static float Density;
    /// <summary>The area the state belongs to; a SetWeather from another area resets it.</summary>
    private static IntPtr _area;
    private static int _sets;
    /// <summary>The density the log last printed, so the ramp logs per 10% step rather than per tick.</summary>
    private static float _logged;

    static Weather()
    {
        G = KnobGroup.Declare("weather", "Graphics", "Weather", master: "Enable Alloy weather",
            masterHelp: "Rain and snow drawn by CX: wind-leaning streaks and flakes that stop under cover, wet and snowed ground. Off, the stock weather particles draw.",
            parent: "alloy.enabled", masterChanged: MasterChanged);
        Enabled = G.Master!;
        G.Status = Describe;
        DeclareKnobs();
        DeclareCoverKnobs();
        DeclareSurfaceKnobs();
        G.Section("Weather types");
        RainRows = G.Text("rainrows", "Rain rows", "1,3,6,9", Tier.Advanced,
                          help: "weathertypes.2da rows drawn as rain (stock: 1).", changed: ParseRows);
        SnowRows = G.Text("snowrows", "Snow rows", "2,4,5,10", Tier.Advanced,
                          help: "weathertypes.2da rows drawn as snow (stock: 2).", changed: ParseRows);
        ParseRows();
    }

    private static void ParseRows()
    {
        Array.Clear(_kinds);
        Fill(RainRows.V, Kind.Rain);
        Fill(SnowRows.V, Kind.Snow);
        static void Fill(string rows, Kind k)
        {
            foreach (string t in rows.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(t, out int r) && r > 0 && r < 32) _kinds[r] = k;
        }
    }

    internal static Kind KindOf(byte type) => type < _kinds.Length ? _kinds[type] : Kind.None;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "Weather", SymbolMap.AreaSetWeather, SymbolMap.AppManager, SymbolMap.GetModule))
        { _off = true; return; }
        _origSet = (delegate* unmanaged<IntPtr, byte, float, int>)hooks.Install(
            SymbolMap.AreaSetWeather, (IntPtr)(delegate* unmanaged<IntPtr, byte, float, int>)&OnSetWeather);
        if (_origSet == null) { _off = true; log.Info("Weather: SetWeather did not hook -- weather stays vanilla"); return; }
        _origStart = (delegate* unmanaged<IntPtr, byte, void>)hooks.Install(
            SymbolMap.AreaStartWeather, (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnStartWeather);
        _origStop = (delegate* unmanaged<IntPtr, byte, void>)hooks.Install(
            SymbolMap.AreaStopWeather, (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&OnStopWeather);
        InstallDraw(log);
        log.Info($"Weather: armed and {(Enabled.V ? "ON" : "IDLE")} -- rain rows {RainRows.V}, snow rows {SnowRows.V}");
    }

    /// <summary>Silence = the master is on, Alloy can still draw, and the row is one Alloy draws; anything else
    /// (fog, a dry storm, an unknown row, a device Alloy ruled out) keeps its stock emitter, so nothing is lost.</summary>
    private static bool Silence(Kind k) => !_off && Enabled.V && k != Kind.None && AlloyGate.Capable;

    /// <summary>LoadArea fires one of the two edges for every area, so a new <c>this</c> here is the area
    /// transition: the old area's weather and its soak go with it.</summary>
    private static void Enter(IntPtr area)
    {
        if (area == _area) return;
        _area = area; Type = 0; Density = 0f; CurrentKind = Kind.None;
        ResetSurface();
    }

    [UnmanagedCallersOnly]
    private static int OnSetWeather(IntPtr area, byte type, float density)
    {
        if (_origSet == null) return 0;
        Enter(area);
        if (type != 0) Type = type;
        else if (density <= 0f) Type = 0;
        float d = Math.Clamp(density, 0f, 100f) / 100f;
        Kind k = KindOf(Type);
        // The stop edge is density reaching 0 from above; the ramp's first tick is a 0 too, and is not one.
        bool stop = d <= 0f && Density > 0f;
        if (stop || k != CurrentKind || MathF.Abs(d - _logged) >= 0.1f)
        {
            _log?.Info($"Weather: {(stop ? "STOP " : "")}row {Type} ({k}) density {d * 100f:F0}%{(Silence(k) ? " -- stock emitter silenced" : "")}");
            _logged = d;
        }
        Density = d; CurrentKind = k; _sets++;
        return _origSet(area, type, Silence(k) ? 0f : density);
    }

    [UnmanagedCallersOnly]
    private static void OnStartWeather(IntPtr area, byte type)
    {
        Enter(area);
        _log?.Info($"Weather: START row {type} ({KindOf(type)}) -- ramping up over 6 s");
        if (_origStart != null) _origStart(area, type);
    }

    [UnmanagedCallersOnly]
    private static void OnStopWeather(IntPtr area, byte unused)
    {
        Enter(area);
        _log?.Info($"Weather: STOP requested -- ramping down over 6 s");
        if (_origStop != null) _origStop(area, unused);
    }

    /// <summary>A live master flip re-issues the engine's own SetWeather to the current area with the captured
    /// state, so off brings the stock emitter back mid-storm and on takes it away, in the same area (#616 A/B).</summary>
    private static void MasterChanged()
    {
        if (_off || _origSet == null || Type == 0 || Density <= 0f) return;
        IntPtr mgr = _appMgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        IntPtr mod = app == IntPtr.Zero ? IntPtr.Zero : _getModule(app);
        IntPtr area = mod == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(mod + ModuleArea);
        if (area == IntPtr.Zero || area != _area) return;
        // Through the hook, so the capture and the silence decision are the one code path.
        ((delegate* unmanaged<IntPtr, byte, float, int>)&OnSetWeather)(area, Type, Density * 100f);
    }

    internal static string Describe() => _off ? "UNAVAILABLE"
        : $"{(Enabled.V ? "on" : "off")} row {Type} {CurrentKind} density {Density * 100f:F0}% ({_sets} sets){DescribeDraw()}{SurfaceStats()}";
}
