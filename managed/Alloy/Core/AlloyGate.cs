using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Why a frame is not Alloy's. Three states rather than a bool, because a bug report has to
/// distinguish them: ADR 0010's fourth bullet is exactly "this machine cannot run it" vs "this player chose
/// not to".</summary>
internal enum AlloyStand
{
    /// <summary>Alloy draws this frame.</summary>
    Alloy,
    /// <summary>The config asked for the engine renderer. Nothing is wrong.</summary>
    Choice,
    /// <summary>The device, the driver or the engine's own settings cannot support the Alloy frame.</summary>
    Incapable,
    /// <summary>There is no world frame to draw: no module, or the renderer never armed.</summary>
    Idle,
}

/// <summary>
/// #263 (ADR 0018 strand 1 step 8): the ONE place a frame is decided to be Alloy's or the engine's, and it
/// decides ONCE. The config half is <c>vanilla=1</c> in <c>sowx.cfg</c>; the capability half is asked at the
/// first present and frozen the moment it answers. After that nothing re-asks, so the two resource worlds
/// are never warm at once -- which is what makes exclusive residency ownership (steps 4-7) affordable.
/// </summary>
/// <remarks>
/// <para>#216 made the verdict frame-atomic; #263 makes it session-atomic. What went with the live toggle is
/// the whole hand-back vocabulary: under the #215 shim an unowned part is <i>nothing</i> (#236), so a
/// refusal taken after the frame is Alloy's cannot buy a vanilla frame -- there is no engine frame behind it
/// and no way to warm one mid-session. Such a refusal is therefore a <see cref="Fail"/>: a log line, one
/// on-screen notice naming the restart, and no mode change.</para>
/// <para>ADR 0010 stands: both renderers ship, and a device that cannot run Alloy still runs every other
/// extender feature. What is gone is <i>live toggling</i>, not the branch. <c>/vanilla</c> reports the two
/// pieces of state and names the setting.</para>
/// </remarks>
internal static class AlloyGate
{
    /// <summary>The <c>sowx.cfg</c> key. Config bucket (ADR 0013) and restart-only by construction: it
    /// decides what installs, and half of what installs suppresses an engine path Alloy replaces.</summary>
    internal const string VanillaKey = "vanilla";

    private static AlloyStand _stand = AlloyStand.Idle;
    private static string _why = "the first frame has not been latched yet";
    /// <summary>The config half, read once at <see cref="Arm"/>. Separate state from the capability half by
    /// ADR 0010, so "I asked for vanilla" never reads as "this machine cannot".</summary>
    private static bool _optedOut;
    /// <summary>The frozen verdict, once the capability half has answered.</summary>
    private static AlloyStand _decided = AlloyStand.Idle;
    private static bool _frozen;
    private static string _decidedWhy = "";
    /// <summary>A failure taken after the decision froze on Alloy. Reported, never acted on.</summary>
    private static string _failed = "";
    private static ExtenderLog? _log;

    private const string NoModule = "no module is loaded";

    /// <summary>The latched verdict every render site reads. True only in <see cref="AlloyStand.Alloy"/>.</summary>
    internal static bool Alloy => _stand == AlloyStand.Alloy;

    internal static AlloyStand Stand => _stand;

    /// <summary>Why this frame is not Alloy's, or <c>""</c> when it is. A constant string on every path --
    /// this is read from the frame.</summary>
    internal static string Reason => _why;

    /// <summary>Did the config ask for the engine renderer? The override half of ADR 0010's split.</summary>
    internal static bool OptedOut => _optedOut;

    /// <summary>Has the device been ruled out? False alone means nothing about what is rendering; pair it
    /// with <see cref="Stand"/>.</summary>
    internal static bool Capable => _stand != AlloyStand.Incapable && _failed.Length == 0;

    /// <summary>...and why not, for the bug report. Also carries a post-decision <see cref="Fail"/>.</summary>
    internal static string CapabilityReason =>
        _failed.Length != 0 ? _failed : _stand == AlloyStand.Incapable ? _why : "";

    /// <summary>A failure taken after the frame was already Alloy's. There is nothing behind it and no way to
    /// warm the engine's resource world mid-session, so this is a HARD FAILURE: said once, on screen and in
    /// the log, naming the restart. First reason wins -- everything after it is downstream.</summary>
    internal static void Fail(string why)
    {
        if (_failed.Length != 0) return;
        _failed = why;
        _log?.Info($"[alloygate] HARD FAILURE -- {why}");
        Notice.Show(_log, $"Alloy renderer failed: {why}. Set {VanillaKey}=1 in sowx.cfg and restart to "
                        + "play on the engine renderer.");
    }

    /// <summary>Has a post-decision failure been latched?</summary>
    internal static bool Failed => _failed.Length != 0;

    /// <summary>The whole refusal set, in report order: configuration and choice first (they are not
    /// failures), then capability. Asked until it answers <see cref="AlloyStand.Alloy"/> or
    /// <see cref="AlloyStand.Choice"/>, then never again. Allocation-free -- every reason is a literal.
    /// <para>An <see cref="AlloyStand.Incapable"/> answer here does NOT freeze: before the first Alloy frame
    /// it means "not yet" as often as "never" (the 4.6 context is created from a present, the programs build
    /// on first use), and the cost of asking again is one branch on a frame that is rendering vanilla anyway.
    /// It freezes the moment it stops being true.</para>
    /// <para>The gamma entry is the one ADR 0016 names by hand: it gates the Alloy DECODES, which live in the
    /// stages and cannot be switched per pass, so a client with <c>GAMMA_CORRECTION</c> off has no
    /// self-consistent Alloy variant at all.</para></summary>
    private static AlloyStand Evaluate(out string why)
    {
        if (_optedOut)
        { why = $"{VanillaKey}=1 in sowx.cfg -- the engine renderer was asked for"; return AlloyStand.Choice; }
        if (!PartTable.Active)
        { why = "drawhooks is off in sowx.cfg, so nothing is enrolled to draw"; return AlloyStand.Choice; }
        if (!PostChain.Configured)
        { why = "hdr=0 in sowx.cfg, and the Alloy stages have no display-space variant"; return AlloyStand.Choice; }
        // ahead of the capability block: an opt-out reported as "this device cannot" misdirects the report
        if (GlContextService.OptedOut)
        { why = "gl46=0 (or lockchat) in sowx.cfg"; return AlloyStand.Choice; }
        // ...and only now. EVERY opt-out above returns from Initialize before _armed is set, so asking
        // "never armed" ahead of them reports the mechanism instead of the request -- which is the ADR 0010
        // misdirection this chain exists to prevent.
        if (!AlloyRenderer.ArmedForFrame)
        { why = "the Alloy renderer never armed (its hooks did not install)"; return AlloyStand.Idle; }
        // ...and ahead of it for the OTHER reason: the capability terms below are only answerable on a warm
        // client. The 4.6 context is created from a present inside a world frame, so asking them at the main
        // menu answers "no 4.6 context on this device" every single boot -- which is what the pre-#263 chain
        // avoided by putting this term here, and what putting it after them reintroduced.
        if (!GameGate.InModule) { why = NoModule; return AlloyStand.Idle; }
        if (!GlContextService.Available)
        { why = "no GL 4.6 share-listed context on this device"; return AlloyStand.Incapable; }
        if (!AlloyRenderer.DrawCapable)
        { why = "an Alloy program did not build, or glMultiDrawElementsBaseVertex/glBindBufferBase is missing";
          return AlloyStand.Incapable; }
        // #264: the engine's bucket walk is gone, so the compute cull is the frame's ONLY visibility test.
        // A driver that cannot dispatch it would draw every enumerated part of the area every frame -- which
        // is a device that cannot support the Alloy frame, exactly what this state is for.
        if (!CullPass.Capable())
        { why = "GL compute or glMultiDrawElementsIndirectCount is missing -- Alloy has no visibility test";
          return AlloyStand.Incapable; }
        if (PostChain.Failed)
        { why = "the HDR target cannot arm on this device"; return AlloyStand.Incapable; }
        if (PostChain.GammaOff())
        { why = "the engine's gamma pipeline is off (GetGlobalShaderParami(0) == 0)"; return AlloyStand.Incapable; }
        why = "";
        return AlloyStand.Alloy;
    }

    /// <summary>Only a POSITIVE answer decides. <see cref="AlloyStand.Idle"/> and
    /// <see cref="AlloyStand.Incapable"/> both mean "not yet" as often as "never" -- the main menu is Idle,
    /// and the 4.6 context is created from a present inside a world frame -- so freezing either would be a
    /// renderer that never arms.</summary>
    private static bool Freezes(AlloyStand v) => v == AlloyStand.Alloy || v == AlloyStand.Choice;

    /// <summary>The latch, from the present's Start phase -- which sits between scene frames, so a frame is
    /// wholly one renderer's or wholly the other's. Once <see cref="_decided"/> holds a verdict the only term
    /// that still moves is whether there is a world frame at all.</summary>
    internal static void OnFrameStart()
    {
        AlloyStand was = _stand;
        string wasWhy = _why;

        if (!_frozen)
        {
            AlloyStand v = Evaluate(out string vWhy);
            if (Freezes(v)) { _frozen = true; _decided = v; _decidedWhy = vWhy; }
            else { _stand = v; _why = vWhy; Report(was, wasWhy); return; }
        }
        bool idle = _decided == AlloyStand.Alloy && !GameGate.InModule;
        _stand = idle ? AlloyStand.Idle : _decided;
        _why = idle ? NoModule : _decidedWhy;
        Report(was, wasWhy);
    }

    /// <summary>Log and, for a capability verdict, tell the player. Reference compare, not value: every
    /// reason is a literal, and this runs on every frame.</summary>
    private static void Report(AlloyStand was, string wasWhy)
    {
        if (_stand == was && ReferenceEquals(_why, wasWhy)) return;
        if (_stand == AlloyStand.Alloy) { _log?.Info("[alloygate] ALLOY -- the frame is Alloy's"); return; }
        _log?.Info($"[alloygate] {_stand} -- {_why}");
        // Only a capability verdict is news: the config choice is the player's own, and Idle is the main menu.
        if (_stand == AlloyStand.Incapable)
            Notice.Show(_log, $"Alloy renderer off -- {_why}. The engine is rendering (vanilla mode).");
    }

    /// <summary>Read the config half and join the frame. Called from <c>AlloyRenderer.Initialize</c>, ahead
    /// of every stand-down -- <c>/vanilla</c> reports this state even in a session that armed nothing.</summary>
    internal static void Arm(ExtenderLog log)
    {
        _log = log;
        _optedOut = Settings.Bool(VanillaKey, false);
        log.Info(_optedOut
            ? $"[alloygate] {VanillaKey}=1 -- the engine renders everything this session ({VanillaKey}=0 in "
              + "sowx.cfg and restart for the Alloy renderer)"
            : $"[alloygate] Alloy requested ({VanillaKey}=1 in sowx.cfg and restart for the engine renderer)");
        SelfCheck(log);
        FrameRegistry.Global.Register(FramePhase.Start, 1, OnFrameStart, "AlloyGate.verdict", log);
    }

    /// <summary>Off-device: the decision freezes once and a post-decision failure does not move it. That last
    /// property is the whole point of the ticket, and nothing on a live client can demonstrate it cheaply.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyGate.SelfCheck FAIL: {w}"); } }

        (bool optedOut, bool frozen, AlloyStand decided, string decidedWhy, string failed, AlloyStand stand,
         string why) saved = (_optedOut, _frozen, _decided, _decidedWhy, _failed, _stand, _why);
        // Fail() speaks to the chatbox as well as the log, and the sink is independent of _log: without
        // this the check would put a fabricated failure notice on screen the moment service init order
        // happens to put a sink in place first.
        ExtenderLog? quiet = _log; _log = null;
        Action<string>? sink = Notice.Sink; Notice.Sink = null;
        try
        {
            _optedOut = true; _frozen = false; _decided = AlloyStand.Idle; _decidedWhy = ""; _failed = "";
            OnFrameStart();
            E(!Alloy && Stand == AlloyStand.Choice, "the config opt-out is a Choice, not a device verdict");
            E(Capable, "...and it says nothing about the device");
            _optedOut = false;
            OnFrameStart();
            E(Stand == AlloyStand.Choice, "the decision is frozen: clearing the config does not un-decide it");

            // ...but a NEGATIVE answer must not freeze, or a client that boots to the main menu (Idle) or
            // whose 4.6 context is not up yet (Incapable) would never arm at all. This is the one the live
            // client caught: the capability block asked at the menu and put a dropout notice on screen.
            // Asserted on the rule rather than through Evaluate, whose answer here is whatever the host
            // running the check happens to be -- which is exactly the coupling that made the first version
            // of this assertion pass on one host and fail on another.
            E(Freezes(AlloyStand.Alloy) && Freezes(AlloyStand.Choice),
              "a positive verdict decides the session");
            E(!Freezes(AlloyStand.Idle) && !Freezes(AlloyStand.Incapable),
              "an Idle or Incapable verdict does not freeze the decision");

            _frozen = true; _decided = AlloyStand.Alloy; _decidedWhy = ""; _stand = AlloyStand.Alloy;
            Fail("first"); Fail("second");
            E(CapabilityReason == "first", "the first failure reason wins and later ones are ignored");
            E(Alloy, "a failure after the decision leaves the frame Alloy's -- there is nothing behind it");
            OnFrameStart();
            E(Stand != AlloyStand.Incapable, "...and does not move the next verdict either: it is restart-only");
        }
        finally
        {
            (_optedOut, _frozen, _decided, _decidedWhy, _failed, _stand, _why) = saved;
            _log = quiet; Notice.Sink = sink;
        }

        log.Info(ok ? "AlloyGate.SelfCheck: passed" : "AlloyGate.SelfCheck: FAILED");
    }
}
