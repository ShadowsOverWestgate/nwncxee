using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Why a frame is not Alloy's. Four states rather than a bool, because a bug report has to
/// distinguish them: ADR 0010's fourth bullet is exactly "this machine cannot run it" vs "this player chose
/// not to".</summary>
internal enum AlloyStand
{
    /// <summary>Alloy draws this frame.</summary>
    Alloy,
    /// <summary>The player or the config asked for the engine renderer. Nothing is wrong.</summary>
    Choice,
    /// <summary>The device, the driver or the engine's own settings cannot support the Alloy frame. Latched
    /// where the cause cannot come back; this is the state that raises an on-screen dropout notice.</summary>
    Incapable,
    /// <summary>There is no world frame to draw: no module, or the renderer never armed.</summary>
    Idle,
}

/// <summary>
/// #216 (ADR 0016 step 7): the ONE place a frame is decided to be Alloy's or the engine's. Latched once per
/// frame at <see cref="FramePhase.Start"/> -- which sits between scene frames -- so a frame is wholly one or
/// wholly the other, with no third state.
/// </summary>
/// <remarks>
/// <para>Before the cutover a mid-frame refusal stood the Alloy pass down and the engine's own frame was
/// still behind it; after #215 there is nothing behind it. So every refusal that can be asked before the
/// frame is taken is asked here, and the few that cannot latch <see cref="Incapable"/> instead -- which
/// costs one frame, never the world. <c>docs/alloy-refusals.md</c> is the set, entry by entry.</para>
/// <para>The verdict is the whole frame decision, and no site re-asks a term it already covers. The one
/// exception is <c>GlContextService.Available</c>: a site that dereferences the 4.6 context still checks it
/// live (or goes through <c>Enter</c>, which does), because losing the context mid-frame is a crash rather
/// than a look.</para>
/// <para>ADR 0010's unpaid promise: <see cref="RenderMode"/> carries the override and only the override,
/// this carries the detected capability, and <c>/vanilla</c> reports both -- so a bug report can tell "this
/// player chose not to" from "this machine cannot".</para>
/// </remarks>
internal static class AlloyGate
{
    private static AlloyStand _stand = AlloyStand.Idle;
    private static string _why = "the first frame has not been latched yet";
    /// <summary>The capability latch: "" while the device can still run the Alloy frame. Separate state from
    /// the override by ADR 0010, and separate from each pass's own <see cref="StandDown"/>, which speaks for
    /// one class rather than for the frame.</summary>
    private static string _incapable = "";
    private static ExtenderLog? _log;

    /// <summary>The latched verdict every render site reads. True only in <see cref="AlloyStand.Alloy"/>.</summary>
    internal static bool Alloy => _stand == AlloyStand.Alloy;

    internal static AlloyStand Stand => _stand;

    /// <summary>Why this frame is not Alloy's, or <c>""</c> when it is. A constant string on every path --
    /// this is read from the frame.</summary>
    internal static string Reason => _why;

    /// <summary>Has the device been ruled out? False alone means nothing about what is rendering; pair it
    /// with <see cref="Stand"/>.</summary>
    internal static bool Capable => _incapable.Length == 0;

    /// <summary>...and why not, for the bug report.</summary>
    internal static string CapabilityReason => _incapable;

    /// <summary>Latch a capability failure. First reason wins: the first thing that went wrong is the one
    /// worth reporting, and everything after it is downstream. Idempotent, and safe to call from a frame --
    /// the log line only fires on the transition, at the next latch.</summary>
    internal static void Incapable(string why)
    {
        if (_incapable.Length == 0) _incapable = why;
    }

    /// <summary>Context loss: the target-shaped failures latched above are facts about a context that no
    /// longer exists, so the next one gets a clean try. A cause that is really about the source (a program
    /// that would not build) re-latches on its own the moment the rebuild fails again.</summary>
    internal static void Forget() => _incapable = "";

    /// <summary>The whole refusal set, in report order: configuration and choice first (they are not
    /// failures), then scope, then capability. Allocation-free -- every reason is a literal.
    /// <para>The gamma entry is the one ADR 0016 names by hand: it gates the Alloy DECODES, which live in the
    /// stages and cannot be switched per pass, so a client with <c>GAMMA_CORRECTION</c> off has no
    /// self-consistent Alloy variant at all.</para></summary>
    private static AlloyStand Evaluate(out string why)
    {
        if (!AlloyRenderer.ArmedForFrame)
        { why = "the Alloy renderer never armed (its hooks did not install)"; return AlloyStand.Idle; }
        if (!RenderMode.Alloy)
        { why = "/vanilla -- the engine renderer was asked for"; return AlloyStand.Choice; }
        if (!PartTable.Active)
        { why = "drawhooks is off in sowx.cfg, so nothing is enrolled to draw"; return AlloyStand.Choice; }
        if (!PostChain.Configured)
        { why = "hdr=0 in sowx.cfg, and the Alloy stages have no display-space variant"; return AlloyStand.Choice; }
        if (!GameGate.InModule)
        { why = "no module is loaded"; return AlloyStand.Idle; }
        // ahead of the capability block: an opt-out reported as "this device cannot" misdirects the report
        if (GlContextService.OptedOut)
        { why = "gl46=0 (or lockchat) in sowx.cfg"; return AlloyStand.Choice; }
        if (!GlContextService.Available)
        { why = "no GL 4.6 share-listed context on this device"; return AlloyStand.Incapable; }
        if (!AlloyRenderer.DrawCapable)
        { why = "an Alloy program did not build, or glMultiDrawElementsBaseVertex/glBindBufferBase is missing";
          return AlloyStand.Incapable; }
        if (PostChain.Failed)
        { why = "the HDR target cannot arm on this device"; return AlloyStand.Incapable; }
        if (PostChain.GammaOff())   // unlatched: a live engine setting, so turning it back on recovers
        { why = "the engine's gamma pipeline is off (GetGlobalShaderParami(0) == 0)"; return AlloyStand.Incapable; }
        if (!Capable) { why = _incapable; return AlloyStand.Incapable; }
        why = "";
        return AlloyStand.Alloy;
    }

    /// <summary>The latch, from the present's Start phase -- after <see cref="RenderMode.OnFrameStart"/>, so
    /// this frame's verdict sees this frame's override.</summary>
    internal static void OnFrameStart()
    {
        AlloyStand was = _stand;
        string wasWhy = _why;
        _stand = Evaluate(out _why);
        // Reference compare, not value: every reason is a literal, and this runs on every frame.
        if (_stand == was && ReferenceEquals(_why, wasWhy)) return;
        if (_stand == AlloyStand.Alloy)
        {
            _log?.Info("[alloygate] ALLOY -- the frame is Alloy's");
            // A recovery is worth telling the player about only if they saw the dropout.
            if (was == AlloyStand.Incapable) Notice.Show(_log, "Alloy renderer: recovered.");
            return;
        }
        _log?.Info($"[alloygate] {_stand} -- {_why}");
        // Only a capability dropout is news: the /vanilla echo already covers the player's own choice, and
        // Idle is the main menu.
        if (_stand == AlloyStand.Incapable)
            Notice.Show(_log, $"Alloy renderer off -- {_why}. The engine is rendering (vanilla mode).");
    }

    /// <summary>Join the frame, after the override latch. Called from <c>AlloyRenderer.Initialize</c>.</summary>
    internal static void Arm(ExtenderLog log)
    {
        _log = log;
        SelfCheck(log);
        FrameRegistry.Global.Register(FramePhase.Start, 1, OnFrameStart, "AlloyGate.verdict", log);
    }

    /// <summary>Off-device: the capability latch is separate from the override, first reason wins, and a
    /// latch taken mid-frame does not move the CURRENT verdict -- it moves the next one. That last property
    /// is the whole point of the ticket, and nothing on a live client can demonstrate it more cheaply.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyGate.SelfCheck FAIL: {w}"); } }

        string savedCap = _incapable;
        AlloyStand savedStand = _stand;
        string savedWhy = _why;
        try
        {
            _incapable = "";
            E(Capable, "a fresh gate is capable");
            Incapable("first");
            Incapable("second");
            E(!Capable && CapabilityReason == "first", "the first reason wins and later ones are ignored");
            Forget();
            E(Capable && CapabilityReason.Length == 0, "context loss clears the capability latch");

            _stand = AlloyStand.Alloy;
            Incapable("mid-frame");
            E(Alloy, "a latch taken mid-frame leaves THIS frame Alloy's -- the frame is already taken");
            E(!Capable, "...and the capability state it moved is what the NEXT latch reads");
            Forget();
        }
        finally { _incapable = savedCap; _stand = savedStand; _why = savedWhy; }

        log.Info(ok ? "AlloyGate.SelfCheck: passed" : "AlloyGate.SelfCheck: FAILED");
    }
}
