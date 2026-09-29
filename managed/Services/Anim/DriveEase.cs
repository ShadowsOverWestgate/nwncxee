using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Services.Anim;

/// <summary>
/// Weighty keyboard movement (<c>anim.drive</c>), the one pose module that changes how the player moves rather than
/// how it is drawn. Keyboard driving is bang-bang in the engine: full speed on the first frame, and once the keys
/// are up a coast at full speed over whatever is left of the path <c>DrivePlayer</c> laid -- it re-lays a 0.4 s lead
/// only every 250 ms, so 0.15-0.4 s of travel -- then a dead stop (docs/research/locomotion-engine.md §3.3).
/// Click-to-move already ramps and eases. This gives the drive the same shape through the engine's own lever:
/// <c>WalkUpdateLocation</c> multiplies a driving body's rate by the special-walk field (<c>CNWCCreature+0x300</c>,
/// which only the constructor and <c>ClientSideWalkCommand</c> ever write, both to 1), and the cycle's playback
/// rate with it -- so the legs wind up and down with the body, and a run slowed under half its rate plays the walk
/// exactly as the engine's own path ease does.
/// <list type="bullet">
/// <item><b>Start</b>: from <see cref="Start"/> of full speed, up to it over <see cref="RampTime"/>.</item>
/// <item><b>Stop</b>: once the keys are up (<see cref="CameraDriveService.Held"/>), an even deceleration over exactly
/// the path the engine was going to coast -- the same end point, reached twice as late -- under the engine's own
/// ease law, <c>sqrt(remaining / distance)</c> down to <see cref="Gait.Floor"/>. Keys pressed again mid-stop ramp up
/// from the speed it had got down to.</item>
/// <item><b>Walk and run</b>: a change of rate keeps the speed, and the ramp carries it to the new one.</item>
/// </list>
/// <para><b>The server</b> still drives the creature at full speed from each packet and adopts the client's claimed
/// position only past 0.5 m (<c>AIActionDrive</c>). The start leaves the client <c>(1 - Start) × RampTime / 2</c> =
/// 0.14 s of travel behind it -- 0.3 m walking, 0.6 m running at SoW's rates -- of which the server's own late start
/// (half a round trip after the key) takes back the network's share; the stop adds nothing.</para>
/// <para><b>Hot.</b> Chained on <c>WalkUpdateLocation</c>: a walking creature pays a load and a compare; the driven
/// player a waypoint walk and a square root. No allocation.</para>
/// </summary>
internal static unsafe class DriveEase
{
    /// <summary>Share of full speed a drive from standing sets off at: a walk's first step, not the engine's
    /// crawl-in from a tenth -- a drive answers a key.</summary>
    private const float Start = 0.3f;
    /// <summary>Seconds from <see cref="Start"/> to full speed: under half the engine's own click-to-move ramp, since
    /// a key wants an answer, but long enough to see the first steps gather pace.</summary>
    private const float RampTime = 0.4f;
    /// <summary>The most a run-to-walk change carries over as a multiplier on the walk, before the ramp brings it down.</summary>
    private const float MaxCarry = 2f;
    /// <summary>World units of coast under which there is nothing to ease: the engine is arriving.</summary>
    private const float MinCoast = 1e-3f;
    /// <summary>Longer than this between two driving steps is a new drive, not the old one resumed.</summary>
    private static long Gap => Stopwatch.Frequency / 4;

    private static WalkUpdateLocationFn* _next;
    /// <summary>The multiplier last written (0 while no drive is under way) and the rate it applied to (mm/s); the
    /// coast's length when the keys went up and the multiplier then (0 while they are held); when it last stepped.</summary>
    private static float _f, _rate, _coast, _from;
    private static long _at;

    internal static int Starts, Stops;

    /// <summary>Chains <c>WalkUpdateLocation</c> outside the step guard, so the multiplier is in place before the
    /// engine steps and the guard tests.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "Pose.drive", "CNWCObject", "CNWCCreature", "CClientExoApp", "CClientExoAppInternal")
            || !SymbolMap.Require(log, "Pose.drive", SymbolMap.WalkUpdateLocation)) return;
        _next = hooks.ChainWalkUpdateLocation(&OnWalk, 60);
        log.Info(_next == null ? "Pose: keyboard weight standing down (WalkUpdateLocation chain refused)"
                               : "Pose: chained CNWCCreature::WalkUpdateLocation (keyboard weight)");
    }

    [UnmanagedCallersOnly]
    private static int OnWalk(IntPtr cre)
    {
        var c = new CNWCCreature(cre);
        bool mine = c.Driving != 0 && IsPlayer(cre);
        if (mine && AnimKnobs.Drive.V && CameraDriveService.Live) c.SpecialWalk = Step(c, cre);
        else if (mine && _f != 0f) { c.SpecialWalk = 1f; _f = 0f; }
        int r = (*_next)(cre);
        // arrival clears the flag inside the step: the next drive starts from standing
        if (mine && c.Driving == 0) _f = 0f;
        return r;
    }

    private static bool IsPlayer(IntPtr cre)
    {
        IntPtr app = CameraService.App();
        return app != IntPtr.Zero && new CNWCObject(cre).Id == new CClientExoApp(app).Internal.PlayerCreatureId;
    }

    /// <summary>This step's multiplier for the driven player.</summary>
    private static float Step(CNWCCreature c, IntPtr cre)
    {
        var o = new CNWCObject(cre);
        float rate = Gait.Rate(o.Animation, c);
        if (!(rate > 0f)) return 1f;
        long now = Stopwatch.GetTimestamp();
        if (_f <= 0f || now - _at > Gap) { _f = Start; _coast = 0f; Starts++; }
        else if (rate != _rate && _rate > 0f) _f = Carry(_f, _rate, rate);
        _rate = rate;
        _at = now;
        if (CameraDriveService.Held)
        {
            _coast = 0f;
            return _f = Ramp(_f, o.StepMs * 0.001f);
        }
        float left = Gait.Remaining(c);
        if (_coast <= 0f) { _coast = MathF.Max(left, MinCoast); _from = _f; Stops++; }
        return _f = Ease(_from, left, _coast);
    }

    /// <summary>One step of the ramp toward full speed, from either side: a run-to-walk carry comes down at the pace
    /// a start goes up.</summary>
    internal static float Ramp(float f, float dt)
    {
        float k = dt * (1f - Start) / RampTime;
        return f + Math.Clamp(1f - f, -k, k);
    }

    /// <summary>The multiplier <paramref name="left"/> world units short of a stop that set out at <paramref name="from"/>
    /// with <paramref name="coast"/> to go: speed squared falls in step with distance, which is an even deceleration.</summary>
    internal static float Ease(float from, float left, float coast)
        => MathF.Max(Gait.Floor, from * MathF.Sqrt(Math.Clamp(left / coast, 0f, 1f)));

    /// <summary>The multiplier that keeps the speed <paramref name="f"/> × <paramref name="was"/> on a new
    /// <paramref name="rate"/> (all mm/s).</summary>
    internal static float Carry(float f, float was, float rate) => Math.Clamp(f * was / rate, Gait.Floor, MaxCarry);

    /// <summary>A 2 m/s walk driven and let go with 0.8 m of lead left, stepped as the engine steps it: the start
    /// sets off at <see cref="Start"/> and is at full speed within a frame of <see cref="RampTime"/>;
    /// the stop covers exactly the coast, never speeds up, and takes about twice the engine's 0.4 s; a walk-to-run
    /// change keeps the speed both ways.</summary>
    internal static bool SelfCheck()
    {
        const float V = 2f, Dt = 0.007f, Lead = 0.8f;
        float f = Start, t = 0f;
        while (f < 1f && t < 1f) { f = Ramp(f, Dt); t += Dt; }
        bool start = MathF.Abs(t - RampTime) <= Dt + 1e-4f && f == 1f;

        float left = Lead, walked = 0f, was = f, time = 0f;
        bool steady = true;
        while (left > 0f && time < 3f)
        {
            float g = Ease(f, left, Lead);
            steady &= g <= was + 1e-6f;
            was = g;
            float step = MathF.Min(left, V * g * Dt);
            left -= step; walked += step; time += Dt;
        }
        bool stop = steady && MathF.Abs(walked - Lead) < 1e-4f && time > 0.75f && time < 0.95f;

        bool carry = MathF.Abs(Carry(1f, 2000f, 4000f) * 4000f - 2000f) < 1e-3f
                     && MathF.Abs(Carry(1f, 4000f, 2000f) * 2000f - 4000f) < 1e-3f
                     && Carry(1f, 6000f, 2000f) == MaxCarry && Ramp(MaxCarry, Dt) < MaxCarry;
        return start && stop && carry;
    }
}
