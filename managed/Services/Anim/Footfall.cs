using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Footsteps on contact, a <see cref="PoseService"/> module. The engine's walk/run footsteps are the model's
/// <c>snd_Footstep</c> animation events (bound in <c>CNWCCreature::LoadModel</c>): wherever the author keyed
/// them, per cycle, whichever foot. Here a footstep is played when a foot lands: its model-space ankle drops to
/// within <see cref="Down"/> of that foot's own stance height after rising <see cref="Up"/> above it. Each foot
/// keeps its own stance: a cycle that plants one foot a few centimetres higher than the other must not land it
/// late (one shared stance height made an uneven, galloping rhythm, live). The sound is the engine's own
/// <c>PlayFootstep</c> (surface, water splash, creak).
/// <para>The engine's own calls are skipped only for a creature this module is stepping: a humanoid the pose
/// layer tracks, locomoting, within <c>anim.distance</c>. Everything else (a creature out of range or without leg
/// bones, the idle shuffle, a fire-and-forget step) keeps the authored events.</para>
/// <para>Every step that goes ahead is handed to <see cref="CX.Alloy.Weather.Footstep"/> first: a print in lying
/// snow, a splash on rain-wet ground, and on wet ground open to the rain the creature's puddle sound in place of
/// the engine's dry one.</para>
/// </summary>
internal static unsafe class Footfall
{
    /// <summary>Model units above its stance height a foot may be and have landed.</summary>
    private const float Down = 0.03f;
    /// <summary>Model units above its stance height a foot must rise before its next landing counts.</summary>
    private const float Up = 0.06f;
    /// <summary>Seconds a foot's stance height takes to rise back after a lower plant set it.</summary>
    private const float StanceRise = 1.5f;
    /// <summary>Seconds under which a second step is the same footfall: a sprint steps every ~0.25 s.</summary>
    private const float MinGap = 0.12f;
    /// <summary>Seconds a stepping creature's ownership outlives its last frame: the engine's event fires inside
    /// <c>Gob::Animate</c>, before this frame's step is known.</summary>
    private const float Hold = 0.25f;

    /// <summary>One foot: its own stance height (model units; Seen once it has one) and whether it is in the air.</summary>
    internal struct Foot
    {
        public float Stance;
        public bool Air, Seen;
    }

    private static PlayFootstepFn _orig;
    private static bool _ours;
    internal static int Steps, Skipped;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "Footfall", SymbolMap.PlayFootstep)) return;
        _orig = hooks.InstallPlayFootstep(&OnPlayFootstep);
        if (_orig == null) log.Info("Footfall: PlayFootstep did not hook -- footsteps stay on the animation's events");
    }

    /// <summary>After the stride lock: plays a footstep for a foot that landed this frame.</summary>
    internal static void Step(ref PoseService.Slot s, IntPtr creature, float mrz, float mlz, bool snap, float dt)
    {
        s.Stepping = _orig != null && AnimKnobs.Footsteps.V && s.Loco && !snap && creature != IntPtr.Zero;
        if (!s.Stepping) { s.StepR = s.StepL = default; return; }
        s.SteppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Gait(ref s.StepR, ref s.StepL, ref s.SinceStep, mrz, mlz, dt))
        {
            _ours = true;
            if (!CX.Alloy.Weather.Footstep(creature)) _orig(creature);
            _ours = false;
            Steps++;
        }
    }

    /// <summary>Both feet for one frame: true when one landed and the last step is <see cref="MinGap"/> behind.</summary>
    internal static bool Gait(ref Foot r, ref Foot l, ref float since, float hr, float hl, float dt)
    {
        since += dt;
        bool landed = Landed(ref r, hr, dt) | Landed(ref l, hl, dt);
        if (!landed || since < MinGap) return false;
        since = 0f;
        return true;
    }

    /// <summary>One foot's contact edge: true on the frame it lands after being in the air.</summary>
    internal static bool Landed(ref Foot f, float h, float dt)
    {
        f.Stance = !f.Seen || h < f.Stance ? h : f.Stance + (h - f.Stance) * MathF.Min(1f, dt / StanceRise);
        f.Seen = true;
        float height = h - f.Stance;
        if (height > Up) { f.Air = true; return false; }
        if (!f.Air || height >= Down) return false;
        f.Air = false;
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnPlayFootstep(IntPtr creature)
    {
        if (!_ours && PoseService.Stepping(creature, (long)(Hold * System.Diagnostics.Stopwatch.Frequency))) { Skipped++; return; }
        if (_ours || !CX.Alloy.Weather.Footstep(creature)) _orig(creature);
    }

    /// <summary>A walk whose left foot plants 4 cm above the right: steps alternate, one per landing, evenly spaced.</summary>
    internal static bool SelfCheck()
    {
        Foot r = default, l = default;
        float since = 0f, dt = 1f / 60f, last = -1f, gapMin = 1e9f, gapMax = 0f;
        int n = 0;
        for (int i = 0; i < 600; i++)
        {
            float t = i * dt, ph = t * MathF.PI * 2f;
            float hr = MathF.Max(0f, MathF.Sin(ph)) * 0.15f, hl = 0.04f + MathF.Max(0f, -MathF.Sin(ph)) * 0.15f;
            if (!Gait(ref r, ref l, ref since, hr, hl, dt)) continue;
            if (t > 1f && last > 1f) { gapMin = MathF.Min(gapMin, t - last); gapMax = MathF.Max(gapMax, t - last); }
            last = t; n++;
        }
        // 10 s of a 1 Hz cycle = 20 landings; the first of each foot is not one (it has not been in the air yet)
        return n >= 18 && n <= 20 && gapMax - gapMin < 0.05f && MathF.Abs(gapMin - 0.5f) < 0.05f;
    }
}
