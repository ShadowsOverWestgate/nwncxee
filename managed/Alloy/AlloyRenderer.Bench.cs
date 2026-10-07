using System;
using System.Diagnostics;
using CX.Framework;

namespace CX.Alloy;

/// <summary><c>/alloy bench</c>: the GPU-cost A/B, run by the renderer instead of by hand in chat. With the camera
/// held still it steps a fixed list of settings, lets each settle for <see cref="BenchSettle"/> frames, measures
/// <see cref="BenchMeasure"/> more (frame time, the colour-bracket GPU span, the lit colour terms, every GPU term),
/// restores every knob it touched and logs one <c>[bench]</c> line per step against the first.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int BenchSettle = 45, BenchMeasure = 150;
    private static Knob[]? _benchKnobs;
    private static string[]? _benchWas;
    /// <summary>Per step: its label and the (knob index, value) pairs it sets over the restored baseline.</summary>
    private static (string Label, (int K, string V)[] Set)[]? _benchSteps;
    private static int _benchStep = -1, _benchFrame;
    private static long _benchT0;
    private static double _benchSpan0; private static int _benchSpanN0;
    private static readonly ulong[] _benchSum0 = new ulong[(int)GpuTerm.Count];
    private static readonly int[] _benchN0 = new int[(int)GpuTerm.Count];
    /// <summary>The hot-path census at the window's start: what the scene's movers did to the step, apart from the knob.</summary>
    private static long _benchHot0, _benchSph0, _benchEng0, _benchMov0;
    private static double _benchBaseFrame, _benchBaseSpan, _benchBaseLit, _benchBaseAll;

    /// <summary>The button: arm the run (cold). A second press while one runs aborts it and restores.</summary>
    internal static void BenchArm()
    {
        if (_benchStep >= 0) { BenchFinish("aborted"); return; }
        _benchKnobs = new Knob[] { Shadows.Soft, Shadows.CubeDist, Lighting.BinCap, Lighting.BinReach, Lightmap.Movers,
                                   Weather.Enabled, Lighting.Ssr, Lighting.SsrPlanar, Renderer.Scale,
                                   Shadows.Taps, Shadows.Cap, Lighting.Relevance, Lighting.MaxLit, Lighting.HiddenLights,
                                   Weather.EdgeTaps, Weather.Splash, Shadows.Dist, Shadows.SunFull, Lighting.Probe, Ao.On,
                                   Weather.FilmLights, Weather.Cover, Weather.WetStrength, Weather.RunOff, Weather.Map,
                                   Weather.SnowDepth, Weather.SnowPile, Weather.Impacts, Weather.ShelterCache, Lightmap.MoverTaps };
        _benchWas = Array.ConvertAll(_benchKnobs, k => k.FormatStored());
        _benchSteps = new (string, (int, string)[])[]
        {
            ("baseline", Array.Empty<(int, string)>()),
            ("shadow.soft 0 (no contact-hardening search, no disc)", new[] { (0, "0") }),
            ("shadow.taps 0 (no cube soft filter)", new[] { (9, "0") }),
            ("shadow.cap 0 (every light one hard tap)", new[] { (10, "0") }),
            ("shadow.cubedist 5 (cube shadows ~off)", new[] { (1, "5") }),
            ("shadow.cubedist 15", new[] { (1, "15") }),
            ("light.bincap 8", new[] { (2, "8") }),
            ("light.binreach 0.5", new[] { (3, "0.5") }),
            ("light.relevance 0", new[] { (11, "0") }),
            ("light.maxlit 64", new[] { (12, "64") }),
            ("light.hidden 0 (no mirror walk of baked statics)", new[] { (13, "0") }),
            ("lightmap.movertaps 0 (hot lights: ray test only, no light body or shadow reads)", new[] { (29, "0") }),
            ("lightmap.movers 0 (no hot static lights)", new[] { (4, "0") }),
            ("weather.edgetaps 1", new[] { (14, "1") }),
            ("weather.splash 0 (no ripples)", new[] { (15, "0") }),
            ("weather.filmlights 0 (no static highlights on puddles)", new[] { (20, "0") }),
            ("weather.shelcache 0 (shelter disk per pixel everywhere)", new[] { (28, "0") }),
            ("weather.cover 0 (no shelter: no edge disk, no exposure)", new[] { (21, "0") }),
            ("weather.runoff 0 (no wall streaks / foot band)", new[] { (23, "0") }),
            ("weather.map 0 (no wetness map: estimate puddles)", new[] { (24, "0") }),
            ("weather.impacts 0 (no splash sprites)", new[] { (27, "0") }),
            ("weather.snowdepth 0 (snow only: no relief march)", new[] { (25, "0") }),
            ("weather.snowpile 0 (snow only: no wall drift)", new[] { (26, "0") }),
            ("weather.wet 0 (rain only: precipitation, dry surfaces)", new[] { (22, "0") }),
            ("weather.enabled 0 (dry ground)", new[] { (5, "0") }),
            ("shadow.dist 120", new[] { (16, "120") }),
            ("shadow.sunfull 2", new[] { (17, "2") }),
            ("light.probe 0", new[] { (18, "0") }),
            ("ao.on 0", new[] { (19, "0") }),
            ("light.ssrplanar 0", new[] { (7, "0") }),
            ("light.ssr 0 + ssrplanar 0", new[] { (6, "0"), (7, "0") }),
            ("alloy.scale 0.5 (GPU unbound: frame = CPU)", new[] { (8, "0.5") }),
            ("baseline again (drift)", Array.Empty<(int, string)>()),
        };
        foreach (Knob k in _benchKnobs)
            if (k.Enforced) { _log?.Always($"AlloyRenderer: [bench] refused -- {k.Key} is held by the server"); return; }
        _log?.Always($"AlloyRenderer: [bench] {_benchSteps.Length} steps x ({BenchSettle} settle + {BenchMeasure} measured) frames -- hold the camera still");
        _benchStep = 0;
        BenchApply();
    }

    /// <summary>Every knob back to what it was, then this step's values over it.</summary>
    private static void BenchApply()
    {
        for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
        foreach ((int k, string v) in _benchSteps![_benchStep].Set) _benchKnobs[k].Parse(v);
        _benchFrame = 0;
    }

    /// <summary>Once per present. Idle: one compare.</summary>
    private static void BenchTick()
    {
        if (_benchStep < 0) return;
        _benchFrame++;
        if (_benchFrame == BenchSettle)
        {
            _benchT0 = Stopwatch.GetTimestamp();
            _benchSpan0 = _spanSum; _benchSpanN0 = _spanN;
            for (int t = 0; t < _qTerm.Length; t++) { _benchSum0[t] = _qTerm[t].Sum; _benchN0[t] = _qTerm[t].N; }
            _benchHot0 = CasterPass.HotSlots; _benchSph0 = CasterPass.HotSpheres; _benchEng0 = CasterPass.HotEngulf; _benchMov0 = CasterPass.MoverFrames;
            return;
        }
        if (_benchFrame < BenchSettle + BenchMeasure) return;
        // a period line reset the meters mid-window: this step's deltas are garbage, so measure it again
        if (_spanN < _benchSpanN0 || _qTerm[(int)GpuTerm.Opaque].N < _benchN0[(int)GpuTerm.Opaque]) { _benchFrame = BenchSettle - 1; return; }
        double frame = (Stopwatch.GetTimestamp() - _benchT0) * 1000.0 / Stopwatch.Frequency / BenchMeasure;
        double span = _spanN > _benchSpanN0 ? (_spanSum - _benchSpan0) / (_spanN - _benchSpanN0) / 1e6 : 0.0;
        double lit = BenchTerm(GpuTerm.Opaque) + BenchTerm(GpuTerm.OpaqueKey) + BenchTerm(GpuTerm.Cutout) + BenchTerm(GpuTerm.CutoutKey);
        double all = 0.0;
        for (int t = 0; t < _qTerm.Length; t++) all += BenchTerm((GpuTerm)t);
        if (_benchStep == 0) { _benchBaseFrame = frame; _benchBaseSpan = span; _benchBaseLit = lit; _benchBaseAll = all; }
        _log?.Always($"AlloyRenderer: [bench] {_benchSteps![_benchStep].Label}: frame {frame:F2} ms ({1000.0 / frame:F0} fps, {frame - _benchBaseFrame:+0.00;-0.00}), "
                   + $"gpu span {span:F2} ({span - _benchBaseSpan:+0.00;-0.00}), lit colour {lit:F2} ({lit - _benchBaseLit:+0.00;-0.00}), "
                   + $"all gpu terms {all:F2} ({all - _benchBaseAll:+0.00;-0.00}); scene: hot lights {(CasterPass.HotSlots - _benchHot0) / (double)BenchMeasure:F1}/f, "
                   + $"mover spheres {(CasterPass.HotSpheres - _benchSph0) / (double)BenchMeasure:F1}/f ({(CasterPass.HotEngulf - _benchEng0) / (double)BenchMeasure:F1} round their light), "
                   + $"moving casters {(CasterPass.MoverFrames - _benchMov0) / (double)BenchMeasure:F1}/f");
        if (++_benchStep >= _benchSteps.Length) { BenchFinish("done"); return; }
        BenchApply();
    }

    /// <summary>One term's mean over the measured window, in ms; 0 when it was not measured.</summary>
    private static double BenchTerm(GpuTerm t)
    {
        int n = _qTerm[(int)t].N - _benchN0[(int)t];
        return n > 0 && _qTerm[(int)t].Sum >= _benchSum0[(int)t] ? (_qTerm[(int)t].Sum - _benchSum0[(int)t]) / (double)n / 1e6 : 0.0;
    }

    private static void BenchFinish(string why)
    {
        for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
        _benchStep = -1;
        _log?.Always($"AlloyRenderer: [bench] {why} -- every knob restored");
    }
}
