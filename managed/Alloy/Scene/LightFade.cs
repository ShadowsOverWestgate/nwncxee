using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-85: Alloy's own light-side simulation tick. Two engine integrators live here, ported rather
/// than replayed, so no engine code runs for their side effects:
/// <list type="bullet">
/// <item><b>The per-light fade weight</b> at <c>PartLight+0x90</c>. <c>LightManager::EnableLights</c>
/// @0x4c2d10 was the only caller of <c>ControlLightsFade</c> @0x4c2050, which is the only writer of that
/// float -- the one Alloy's area ambient/diffuse (<see cref="FrameRead"/>) and every point light
/// (<see cref="SceneInputs"/>) ride on.</item>
/// <item><b>The focus lighting</b>, the three floats behind the exported
/// <c>Scene::GetLightingValueAtFocus</c> @0x462aa0 (Scene vtable +0x138), which
/// <c>CNWCArea::GetIsDark</c> @0x705930 reads with hysteresis at 0.6062/0.866 to decide whether the player
/// stands in the dark, for <c>CNWCObject::ActivateHardCodedVisualWithHighestPriority</c>. The engine reached
/// it through <c>Gob::CollectLights</c> @0x43fa90 plus <c>LightManager::GetEmitterLighting</c> @0x4c1970;
/// both are folded into the walk below.</item>
/// </list>
/// <para><b>The weight is written back.</b> Three engine readers of <c>+0x90</c> survive the cutover --
/// <c>GetEmitterLighting</c> @0x4c1970 (still called for the particle tint), <c>GetNearestLights</c>
/// @0x4c15f0 (the GUI 3D scenes, via <c>Scene::RenderOnlyGobs</c>, which the shim does not replace) and
/// <c>RenderLightRanges</c> @0x4c35a0 (a debug draw the shim dropped) -- so the port stores into the field
/// instead of keeping a private table, and <c>alloy.enabled=0</c> hands the engine weights it recognises.</para>
/// <para><b>No active list.</b> The engine carries the lit set as a second <c>CExoArrayList</c> at
/// <c>LightManager+0x60</c>; this does not, because "in the list" and "weight &gt; 0" are the same predicate:
/// every removal path in <c>ControlLightsFade</c> zeroes the weight and every add path sets it above zero.
/// What the list bought was a linear <c>IndexOf</c> per light per frame; what replaces it is one pointer set
/// over this frame's list. Nothing outside <c>ControlLightsFade</c> and that dropped debug draw read
/// <c>+0x60</c>, and <c>LightManager::RemoveLight</c> @0x4c1f40 keeps it free of dangling entries either way.</para>
/// <para><b>Deliberately NOT ported:</b> the GL half of <c>ControlLightsFade</c> -- <c>SetLightGL</c>,
/// <c>SetAreaAmbientLight</c>/<c>SetAreaDiffuseLight</c>, <c>DisableLight</c> and
/// <c>SetHeadLightProperties</c> @0x4c12e0. Those upload the ENGINE's fixed-function light state, which under
/// the cutover only the highlight overlays could read, and Alloy lights its own frame out of
/// <see cref="FrameInputs"/>. The headlight is still COUNTED: it costs two slots and adds two colours to the
/// focus value, exactly as the engine counts it.</para>
/// <para>Hot path: once per world frame in engine context, allocating nothing past the set's first growth.
/// Two engine leaves are called rather than transcribed -- <see cref="SymbolMap.GetLightFadeSpeed"/> and
/// <see cref="SymbolMap.GetIsLightInViewVolume"/> -- because both issue no draw and write nothing anything
/// reads back (the second only memoises its last rejecting plane), and a second copy of the overbright curve
/// is a second thing to drift.</para></summary>
internal static unsafe class LightFade
{
    /// <summary><c>ControlLightsFade</c>'s own delta floor: <c>max(DeltaT, 0.0001)</c>, so a stalled frame
    /// still advances a fade.</summary>
    internal const float DtFloor = 0.0001f;
    /// <summary>A lit headlight occupies GL light slots 0 and 1 (<c>SetHeadLightProperties</c> @0x4c12e0), so
    /// it costs the area two of its <c>GetMaxLights</c> slots.</summary>
    private const int HeadLightSlots = 2;
    /// <summary><c>GetEmitterLighting</c>'s guards: a zero distance becomes <see cref="MinDist"/> and a zero
    /// range becomes <see cref="MinRange"/>, which together give its 0.9999 degenerate case.</summary>
    private const float MinDist = 1e-6f, MinRange = 0.01f;

    private static float* _deltaT;
    private static short* _headLight;
    private static IntPtr* _onlyGob, _focusObject;
    private static float* _focusLighting, _hlAmbient, _hlDiffuse;
    private static GetLightFadeSpeedFn _speed;
    private static GetIsLightInViewVolumeFn _inView;
    private static LightMgrGetMaxLightsFn _maxLights;
    private static ExtenderLog? _log;
    private static bool _probed;

    /// <summary>Every symbol resolved? False stands the whole Alloy path down (the shim reports it), because a
    /// frozen fade weight is wrong-looking lighting on every surface rather than a missing effect.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The symbol this build could not resolve, for the shim's one-line refusal.</summary>
    internal static string? Missing { get; private set; }

    /// <summary>Weights that moved on the last tick, and the focus value last written -- the
    /// <c>[rspshim]</c> line's witness that the port is live, since neither has another visible surface.</summary>
    internal static int Moved { get; private set; }
    private static Vector3 _focusValue;

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _headLight = (short*)Native.Sym(SymbolMap.HeadLightEnabled);
        _onlyGob = (IntPtr*)Native.Sym(SymbolMap.LightManagerOnlyGob);
        _focusObject = (IntPtr*)Native.Sym(SymbolMap.SceneFocusObject);
        _focusLighting = (float*)Native.Sym(SymbolMap.FocusLighting);
        _hlAmbient = (float*)Native.Sym(SymbolMap.HeadlightAmbient);
        _hlDiffuse = (float*)Native.Sym(SymbolMap.HeadlightDiffuse);
        _speed = EngineFn.GetLightFadeSpeed;
        _inView = EngineFn.GetIsLightInViewVolume;
        _maxLights = EngineFn.LightMgrGetMaxLights;
        Missing = _deltaT == null ? SymbolMap.DeltaT
                : _headLight == null ? SymbolMap.HeadLightEnabled
                : _onlyGob == null ? SymbolMap.LightManagerOnlyGob
                : _focusObject == null ? SymbolMap.SceneFocusObject
                : _focusLighting == null ? SymbolMap.FocusLighting
                : _hlAmbient == null ? SymbolMap.HeadlightAmbient
                : _hlDiffuse == null ? SymbolMap.HeadlightDiffuse
                : _speed == null ? SymbolMap.GetLightFadeSpeed
                : _inView == null ? SymbolMap.GetIsLightInViewVolume
                : _maxLights == null ? SymbolMap.LightMgrGetMaxLights : null;
        Ready = Missing == null;
    }

    /// <summary>The tick, at <c>RenderSinglePass</c>'s own first step and so BEFORE the frame anchor: the two
    /// area weights it advances are what <see cref="FrameRead"/> scales <see cref="FrameInputs.AreaAmbient"/>
    /// and <see cref="FrameInputs.AreaDiffuse"/> by, inside that anchor.
    /// <para><paramref name="fullDynamic"/> is <c>g_bFullDynamicLighting</c>, which gated the engine call the
    /// same way (#235): clear, the engine lights from static buckets and never ran the fade, so neither does
    /// this. The focus lighting is NOT gated -- it was its own unconditional RSP step.</para></summary>
    internal static void Tick(IntPtr scene, bool fullDynamic)
    {
        Moved = 0;
        if (!Ready || scene == IntPtr.Zero) return;
        var sc = new Scene(scene);
        LightManager lm = sc.LightManager;
        if (lm.IsNull) return;
        AlloyProbe.Once(ref _probed, "LightManager", lm.Ptr, _log);
        float dt = MathF.Max(*_deltaT, DtFloor);
        IntPtr onlyGob = *_onlyGob;
        bool headLight = *_headLight != 0;
        IntPtr* list = (IntPtr*)lm.FrameData;
        int n = list == null ? 0 : lm.FrameCount;
        int cap = Cap(list, n, onlyGob, headLight);
        PartLight amb = lm.AreaAmbient, dif = lm.AreaDiffuse;

        // Pass 1: this frame's list. Below the cap a light travels to full weight, past it back to zero, and
        // the accumulator picks up its focus term on the way through -- one walk, both integrators.
        IntPtr focus = *_focusObject;
        Vector3 focusPos = sc.SkyPos;
        Vector3 acc = default;
        SetBegin(n);
        for (int i = 0; i < n; i++)
        {
            IntPtr p = list[i];
            if (p == IntPtr.Zero) continue;
            Insert(p);
            var light = new PartLight(p);
            if (fullDynamic) light.Fade = Advance(light, i < cap, dt);
            // Gob::CollectLights' exclusion, by owner rather than by part walk: it collected the lights in the
            // focus object's own part tree, and every part of that tree carries the object at Part+0x50.
            if (focus != IntPtr.Zero && new Part(p).Gob.Ptr == focus) continue;
            float t = Attenuate((light.Pos - focusPos).Length(), light.Range);
            if (t > 0f) acc += light.Colour * (t * light.Fade);
        }

        // Pass 2: the departed. A light that left the list is not fading anywhere -- the engine zeroed it on
        // the way out of its active list -- and a weight left standing keeps lighting the area from a light
        // the frame no longer wants. The two index -1 lights are never in the list; they are the tail's.
        if (fullDynamic)
        {
            int master = lm.MasterData == IntPtr.Zero ? 0 : lm.MasterCount;
            for (int i = 0; i < master; i++)
            {
                PartLight light = lm.Master(i);
                if (light.IsNull || light.Ptr == amb.Ptr || light.Ptr == dif.Ptr) continue;
                if (light.Fade == 0f || Has(light.Ptr)) continue;
                light.Fade = 0f;
                Moved++;
            }
            if (!amb.IsNull) amb.Fade = Area(amb, onlyGob, dt);
            if (!dif.IsNull) dif.Fade = Area(dif, onlyGob, dt);
        }

        if (headLight)
            acc += new Vector3(_hlAmbient[0] + _hlDiffuse[0], _hlAmbient[1] + _hlDiffuse[1],
                               _hlAmbient[2] + _hlDiffuse[2]);
        if (!amb.IsNull) acc += amb.Colour * amb.Fade;
        if (!dif.IsNull) acc += dif.Colour * dif.Fade;
        // Clamped at the top only, as the engine clamps it: an authored negative stays negative.
        _focusValue = new Vector3(MathF.Min(acc.X, 1f), MathF.Min(acc.Y, 1f), MathF.Min(acc.Z, 1f));
        _focusLighting[0] = _focusValue.X; _focusLighting[1] = _focusValue.Y; _focusLighting[2] = _focusValue.Z;
    }

    /// <summary>How many of this frame's lights may be LIT at once: <c>GetMaxLights</c> less the headlight's
    /// two slots and -- while a single gob is previewed in isolation -- no further than the run of leading
    /// entries belonging to it (<c>ControlLightsFade</c> @0x4c2050's <c>m_pOnlyGob</c> prologue).</summary>
    private static int Cap(IntPtr* list, int n, IntPtr onlyGob, bool headLight)
    {
        int cap = _maxLights();
        if (onlyGob == IntPtr.Zero) return headLight ? cap - HeadLightSlots : cap;
        int lead = 0;
        while (lead < n && list[lead] != IntPtr.Zero && new Part(list[lead]).Gob.Ptr == onlyGob) lead++;
        return Math.Min(cap, lead);
    }

    /// <summary>One list light's weight after one step, engine state gathered: the fade speed and the frustum
    /// test are only asked for when they can change the answer.</summary>
    private static float Advance(PartLight light, bool lit, float dt)
    {
        float w = light.Fade;
        if (lit)
        {
            if (w >= 1f) return w;
            bool ramp = Ramps(light);
            float next = Step(w, true, _speed(light.Ptr), dt, true, ramp);
            if (next != w) Moved++;
            return next;
        }
        if (w <= 0f) return 0f;
        Moved++;
        return Step(w, false, _speed(light.Ptr), dt, _inView(light.Ptr) != 0, true);
    }

    /// <summary>Does this light ramp in, or pop? The MDL light node's <c>fadingLight</c> field, defaulting to
    /// "ramp" for a light whose node is gone -- which is what the engine's own default is.</summary>
    private static bool Ramps(PartLight light)
    {
        var node = new MdlNodeLight(new Part(light.Ptr).Node.Ptr);
        return node.IsNull || node.FadingLight != 0;
    }

    /// <summary>The whole weight integrator, as arithmetic: <c>ControlLightsFade</c> @0x4c2050 in one step.
    /// <list type="bullet">
    /// <item><paramref name="lit"/>: the frame wants this light. It travels to 1 at
    /// <paramref name="speed"/> per second -- or arrives there outright when it was dark and
    /// <paramref name="ramp"/> is false (MDL <c>fadingLight 0</c>).</item>
    /// <item>not wanted: it eases back to 0 at the same speed, unless it is off screen
    /// (<paramref name="inView"/> false), when it snaps -- a fade nobody can see is one nobody should pay
    /// for.</item>
    /// </list></summary>
    internal static float Step(float w, bool lit, float speed, float dt, bool inView, bool ramp)
    {
        if (lit)
        {
            if (w <= 0f && !ramp) return 1f;
            return w >= 1f ? w : MathF.Min(1f, w + speed * dt);
        }
        if (w <= 0f) return 0f;
        return inView ? MathF.Max(0f, w - speed * dt) : 0f;
    }

    /// <summary>The two index -1 lights -- area ambient at <c>LightManager+0xa0</c>, area diffuse at
    /// <c>+0x98</c> -- which are never in the per-frame list and travel to full weight unless a lit-gob
    /// preview disowns them.</summary>
    private static float Area(PartLight light, IntPtr onlyGob, float dt)
    {
        bool ours = onlyGob == IntPtr.Zero || new Part(light.Ptr).Gob.Ptr == onlyGob;
        float w = light.Fade;
        if (ours && w >= 1f) return w;
        float next = Step(w, ours, _speed(light.Ptr), dt, true, true);
        if (next != w) Moved++;
        return next;
    }

    /// <summary>The engine's own frame delta in seconds, floored -- every ported integrator steps on the same
    /// number the engine's did (<see cref="SymbolMap.DeltaT"/>). Zero while the symbol is unresolved, which
    /// leaves the caller's fade standing rather than jumping it.</summary>
    internal static float Delta(float floor) => _deltaT == null ? 0f : MathF.Max(*_deltaT, floor);

    /// <summary><c>GetEmitterLighting</c> @0x4c1970's falloff: linear in distance over the light's RAW range
    /// (+0x70, not the adjusted radius), with its two degenerate guards. Zero or less means no
    /// contribution.</summary>
    internal static float Attenuate(float dist, float range)
    {
        if (dist == 0f) dist = MinDist;
        if (range == 0f) range = MinRange;
        return 1f - dist / range;
    }

    // ---- this frame's wanted set: open addressing over the pointers, no allocation past a growth ----

    private static IntPtr[] _set = new IntPtr[256];

    private static void SetBegin(int n)
    {
        int want = 64;
        while (want < n * 2) want <<= 1;
        if (want > _set.Length) _set = new IntPtr[want];   // a fresh array arrives zeroed
        else Array.Clear(_set, 0, _set.Length);
    }

    private static void Insert(IntPtr p)
    {
        int m = _set.Length - 1;
        for (int h = Hash(p) & m; ; h = (h + 1) & m)
        {
            if (_set[h] == p) return;
            if (_set[h] == IntPtr.Zero) { _set[h] = p; return; }
        }
    }

    private static bool Has(IntPtr p)
    {
        int m = _set.Length - 1;
        for (int h = Hash(p) & m; _set[h] != IntPtr.Zero; h = (h + 1) & m)
            if (_set[h] == p) return true;
        return false;
    }

    /// <summary>Fibonacci hash of a heap pointer with the four low bits dropped: malloc hands back aligned
    /// blocks, so those bits are constant and hashing them clusters every key into a sixteenth of the
    /// table.</summary>
    private static int Hash(IntPtr p)
    {
        ulong x = ((ulong)(long)p >> 4) * 0x9E3779B97F4A7C15UL;   // not-an-offset: the 64-bit golden-ratio odd constant
        return (int)(x >> 33);
    }

    internal static string Describe() =>
        !Ready ? $"light fade UNAVAILABLE ({Missing})"
               : $"light fade moved {Moved}, focus ({_focusValue.X:F3},{_focusValue.Y:F3},{_focusValue.Z:F3})";

    /// <summary>The integrator and the falloff, headless -- both are pure arithmetic and both fail invisibly:
    /// a wrong ramp is "the lighting looks off", a wrong falloff is a player the game thinks is in the dark.
    /// The engine-memory half (which field, which list) has no seam and is asserted by the layout probe.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // A wanted light ramps from dark and arrives, and the arrival clamps rather than overshoots.
        check.Expect(Math.Abs(Step(0f, true, 2f, 0.25f, true, true) - 0.5f) < 1e-5f, "a wanted light ramps at speed * dt");
        check.Expect(Step(0.9f, true, 2f, 0.25f, true, true) == 1f, "the ramp clamps at full weight");
        check.Expect(Step(1f, true, 10f, 1f, true, true) == 1f, "a light already at full weight stays there");
        // fadingLight 0 pops -- but only from dark, which is the engine's add path and nowhere else.
        check.Expect(Step(0f, true, 1f, 0.001f, true, false) == 1f, "fadingLight 0 arrives at full weight in one step");
        check.Expect(Math.Abs(Step(0.5f, true, 1f, 0.25f, true, false) - 0.75f) < 1e-5f, "...and a lit one still ramps");
        // Unwanted: ease out while visible, snap while not, and never go negative.
        check.Expect(Math.Abs(Step(1f, false, 2f, 0.25f, true, true) - 0.5f) < 1e-5f, "an unwanted light eases out");
        check.Expect(Step(0.1f, false, 2f, 0.25f, true, true) == 0f, "...and stops at zero, never below");
        check.Expect(Step(1f, false, 2f, 0.25f, false, true) == 0f, "an unwanted light off screen snaps out");
        check.Expect(Step(0f, false, 10f, 1f, true, true) == 0f, "a dark light nobody wants stays dark");
        // The falloff, including both degenerate guards: range 0 collapses to 0.01, distance 0 to 1e-6.
        check.Expect(Math.Abs(Attenuate(5f, 10f) - 0.5f) < 1e-5f, "falloff is linear over the RAW range");
        check.Expect(Attenuate(11f, 10f) < 0f, "past the range it is negative, i.e. no contribution");
        check.Expect(Math.Abs(Attenuate(0f, 0f) - 0.9999f) < 1e-4f, "a zero-range light at zero distance is the 0.9999 case");
        log.Info(check.Ok ? "LightFade.SelfCheck: passed" : "LightFade.SelfCheck: FAILED -- see above");
    }
}
