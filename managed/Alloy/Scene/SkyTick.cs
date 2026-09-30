using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-87: the sky's SIMULATION, owned. <c>Scene::RenderSkyBoxes</c> @0x466910 was still being called from the
/// shim for three side effects, and this is those three: the cross-fade alpha onto each dome
/// (<c>gob+0x108</c>), <c>Gob::ProcessGob</c>, and the per-frame <c>Gob::SetPosition(Scene+0x190)</c> that
/// parks every dome at the scene FOCUS (<c>Scene+0x190</c>, written by
/// <c>Scene::SetSceneFocus(Vector const&amp;)</c> @0x477890 -- the camera's look-at point, which is what a dome
/// parked there follows). With them here the shim calls no engine sky routine at all, so no engine
/// draw is swallowed on the sky's account (CX-81).
/// <para><b>There is no integrator here, and that is a finding.</b> The cross-fade t is NOT integrated by
/// <c>RenderSkyBoxes</c> -- it reads <c>Scene+0x40</c>. The integration is
/// <c>CNWCArea::UpdateDayNightCycle</c> @0x706a20 -&gt; <c>CNWCArea::UpdateSkyBox</c> @0x706960, which writes
/// the area's own progress at <c>+0x3c4</c>, hands t to the Scene through vtable slot <c>+0x160</c>, and at
/// t &gt;= 1 completes the transition with <c>SetCurrentSkyBoxTime</c>. That chain hangs off
/// <c>CNWCArea::Update</c>, in the game update, which the shim does not touch and CX-81 does not reach. So
/// dawn, dusk and area entry keep the engine's own timing exactly; what is ported is only the per-frame
/// APPLICATION of it.</para>
/// <para><b>And no node animation.</b> <c>Gob::Render</c> @0x452260 animates nothing -- it calls
/// <c>ProcessGob</c> (four lines: refresh the tree's geometry flags on a dirty byte) and then draws. Whatever
/// animation a sky model has comes from <c>Scene::Animate</c> @0x469810, outside RSP and untouched. Removing
/// this call therefore cannot remove animation, which is why none is reimplemented here.</para>
/// </summary>
internal static unsafe class SkyTick
{
    private static GobProcessGobFn _process;
    private static ExtenderLog? _log;
    private static bool _off;
    private static int _ticked, _parked;

    internal static void Install(ExtenderLog log)
    {
        _log = log;
        _process = EngineFn.GobProcessGob;
        if (_process == null)
        {
            _off = true;
            log.Always("SkyTick: Gob::ProcessGob unresolved -- the fade and the park still run, but a sky model "
                     + "whose geometry changes keeps the visibility flags it had (CX-87)");
        }
    }

    /// <summary>The shim's replacement for <c>Scene::RenderSkyBoxes</c>, at the same point in RSP's order and
    /// ahead of the frame anchor -- <see cref="SkyBox.Enroll"/> reads the visibility byte
    /// <see cref="SymbolMap.GobProcessGob"/> writes, and the parked position it corrects against.
    /// <para>#91's rule: a GUI 3D panel drives <c>RenderSinglePass</c> with a Scene of its own, and a scene
    /// with no tile BSP (<c>Scene+0x188</c>) is one of those. It has no skybox to tick -- the gates below
    /// would answer "nothing" anyway -- but the world sky's state must never be reachable from one.</para>
    /// <para>Hot: three gobs, a handful of field writes, no allocation.</para></summary>
    internal static void Run(IntPtr scene)
    {
        if (scene == IntPtr.Zero) return;
        if (new CX.Engine.Scene(scene).BspRoot == IntPtr.Zero) return;
        _ticked += Tick(scene, _process);
    }

    /// <summary>The engine's own gates and writes, without the draws. Takes the ProcessGob pointer rather
    /// than reading the static, so <see cref="SelfCheck"/> can run the whole thing over a stand-in Scene with
    /// no engine call in it.
    /// <para>Order is <c>RenderSkyBoxes</c>'s: the settled sky at <c>1 - t</c>, then the one fading in at
    /// <c>t</c> (both gated exactly as the engine gates them -- slot -1 is "no sky", and the desired sky needs
    /// <c>t &gt; 0</c>, <c>DAT_013eeeb0</c>), then the cross-fade gob, whose alpha the engine does NOT write
    /// here. Returns how many domes were ticked.</para></summary>
    internal static int Tick(IntPtr scene, GobProcessGobFn process)
    {
        var sc = new CX.Engine.Scene(scene);
        if (sc.SkyOff != 0) return 0;
        Vector3 focus = sc.SkyPos;   // Scene+0x190: the scene focus, which is what RenderSkyBoxes parks at
        float t = sc.SkyT;
        int cur = sc.SkyCur, want = sc.SkyDesired;
        Span<IntPtr> sky = sc.SkyGobs;
        int n = 0;
        if (cur != -1) n += One(sky[cur], 1f - t, true, focus, process);
        if (t > 0f && want != -1) n += One(sky[want], t, true, focus, process);
        var fade = sc.SkyFadeGob;
        if (!fade.IsNull) n += One(fade.Ptr, 0f, false, focus, process);
        return n;
    }

    /// <summary>One dome. <c>Gob::SetPosition</c> @0x437190 is a 12-byte store to <c>gob+0xa8</c> and nothing
    /// else, so the park is the field write -- which also keeps this side clear of a by-value
    /// <c>Vector</c> return whose hidden-buffer order differs per OS (<c>?SetPosition@Gob@@UEAA?AVVector@@V2@@Z</c>).</summary>
    private static int One(IntPtr gobPtr, float alpha, bool writeAlpha, Vector3 focus, GobProcessGobFn process)
    {
        if (gobPtr == IntPtr.Zero) return 0;
        var g = new Gob(gobPtr);
        if (writeAlpha) g.Alpha = alpha;
        if (process != null) process(gobPtr);
        g.Position = focus;
        _parked++;
        return 1;
    }

    internal static void ResetPeriod() { _ticked = 0; _parked = 0; }

    internal static string Stats() => $"sky tick {_ticked} domes, {_parked} parked"
                                      + (_off ? " (ProcessGob UNRESOLVED: visibility flags frozen)" : "");

    /// <summary>Off-device: the alpha the cross-fade puts on each dome and the gates that decide which domes
    /// exist at all -- the one part of the tick that is a decision rather than a field copy. Get the
    /// complement wrong and the horizon brightens or darkens through every dawn; miss a gate and a dome is
    /// left at last frame's alpha, or an interior gets one.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // a SIZE, not a field read: the stand-in Scene has to reach past the focus vector the tick parks at
        int sceneBytes = EngineLayout.SceneSkyPos + 12;
        byte* scene = stackalloc byte[sceneBytes];
        for (int i = 0; i < sceneBytes; i++) scene[i] = 0;
        IntPtr s = (IntPtr)scene;
        var sc = new CX.Engine.Scene(s);

        int gobBytes = EngineLayout.GobAlpha + 4;
        byte* a = stackalloc byte[gobBytes];
        byte* b = stackalloc byte[gobBytes];
        for (int i = 0; i < gobBytes; i++) { a[i] = 0; b[i] = 0; }
        sc.SkyGobs[1] = (nint)a; sc.SkyGobs[2] = (nint)b;
        float AlphaOf(byte* g) => new Gob((IntPtr)g).Alpha;

        sc.SkyCur = -1; sc.SkyDesired = -1;
        check.Expect(Tick(s, null) == 0, "no current sky and no fade gob ticks nothing");

        sc.SkyCur = 1; sc.SkyT = 0f;
        check.Expect(Tick(s, null) == 1 && AlphaOf(a) == 1f, "a settled sky is one dome at full alpha");

        sc.SkyOff = 1;
        new Gob((IntPtr)a).Alpha = 0.5f;
        check.Expect(Tick(s, null) == 0 && AlphaOf(a) == 0.5f,
          "TurnOffSkyBox wins over everything -- and writes no alpha, so nothing moves while the sky is off");
        sc.SkyOff = 0;

        sc.SkyDesired = 2; sc.SkyT = 0.25f;
        check.Expect(Tick(s, null) == 2 && AlphaOf(a) == 0.75f && AlphaOf(b) == 0.25f,
          "a cross-fade is (1-t) on the settled dome and t on the incoming one: the two sum to 1");

        sc.SkyT = 1f;
        check.Expect(Tick(s, null) == 2 && AlphaOf(a) == 0f && AlphaOf(b) == 1f,
          "...and at t=1 the incoming dome is the whole sky, which is where UpdateSkyBox completes the slot swap");

        sc.SkyT = 0f;
        check.Expect(Tick(s, null) == 1, "t at the epsilon leaves the desired dome out -- the engine's own DAT_013eeeb0 test");

        // the parked position: the engine's SetPosition(Scene+0x190), every frame, on every dome it ticks
        int posBytes = EngineLayout.GobPosition + 12;
        byte* c = stackalloc byte[posBytes > gobBytes ? posBytes : gobBytes];
        for (int i = 0; i < (posBytes > gobBytes ? posBytes : gobBytes); i++) c[i] = 0;
        sc.SkyGobs[1] = (nint)c; sc.SkyCur = 1; sc.SkyDesired = -1;
        sc.SkyPos = new Vector3(3f, 4f, 5f);
        Tick(s, null);
        check.Expect(new Gob((IntPtr)c).Position == new Vector3(3f, 4f, 5f),
          "every ticked dome is parked at the scene focus, which is what makes 'after opaque' safe at depth 1");

        // the fade gob's alpha is set by the weather code, NOT here: the engine's third branch writes none
        byte* f = stackalloc byte[gobBytes];
        for (int i = 0; i < gobBytes; i++) f[i] = 0;
        new Gob((IntPtr)f).Alpha = 0.25f;
        sc.SkyFadeGob = new Gob((IntPtr)f);
        check.Expect(Tick(s, null) == 2 && AlphaOf(f) == 0.25f,
          "the cross-fade gob is ticked and parked but its alpha is left alone -- RenderSkyBoxes writes none");

        log.Info(check.Ok ? "SkyTick.SelfCheck: passed" : "SkyTick.SelfCheck: FAILED");
    }
}
