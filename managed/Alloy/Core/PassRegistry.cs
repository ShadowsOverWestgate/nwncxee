using System;
using System.Collections.Generic;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>
/// One Alloy pass's lifecycle, as the eight slots every pass was hand-writing (#175). A null slot means the
/// pass genuinely does not have that concern -- which is now visible in one place rather than being a gap
/// nobody notices until the pass misbehaves.
/// </summary>
/// <remarks>
/// Delegates rather than an interface with instances, for the same reason <see cref="FrameRegistry"/> uses
/// them (#170, ADR 0014's participant registry): the passes are static classes holding static GL state, and
/// their frame entry points are called from <c>[UnmanagedCallersOnly]</c> handlers, which cannot capture a
/// receiver. Ten singleton shims would be the same table written ten times over.
/// </remarks>
public sealed class PassLifecycle
{
    /// <summary>The pass, as it names itself in the log. Also the <c>/pass</c>-style lookup key for
    /// <see cref="PassRegistry.Find"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Take the log and read the boot knobs. Runs once, at Initialize, before any check.</summary>
    public Action<ExtenderLog>? Attach { get; init; }

    /// <summary>Install this pass's own engine hooks. Separate from <see cref="Attach"/> because it runs
    /// LATER, after the Alloy pass has resolved its engine symbols: a pass that hooks the engine on a client
    /// where the Alloy renderer cannot arm at all would be suppressing draws nothing replaces.</summary>
    public Action<ExtenderLog, HookService>? Install { get; init; }

    /// <summary>Off-device assertions over the pass's own contracts. Advisory by convention: a pass whose
    /// check must GATE arming asserts it itself and returns before registering.</summary>
    public Action<ExtenderLog>? SelfCheck { get; init; }

    /// <summary>Context loss or disarm: drop every GL name. Names mean nothing once the context is gone, so
    /// this forgets rather than deletes.</summary>
    public Action? ContextLost { get; init; }

    /// <summary>The world went away underneath the pass -- part table wiped, module unloaded, enrollment
    /// re-run. Drop whatever was CAPTURED from it (part lists, VDO slots, scene pointers); the GL objects
    /// are still valid and are not this slot's business.
    /// <para>Two edges, not one, and conflating them is how a pass ends up half stood-down. The old roll-call
    /// had no name for the distinction at all -- one pass spelled the pair <c>Forget</c>/<c>ContextLost</c>
    /// and every other pass called whichever one it had <c>Forget</c>.</para></summary>
    public Action? Revoke { get; init; }

    /// <summary>One clause of the period report, given the frame count of the period.</summary>
    public Func<int, string>? Stats { get; init; }

    /// <summary>The pass's live tuning state, for its chat command.</summary>
    public Func<string>? Describe { get; init; }

    /// <summary>Set one named dial, returning its old value (NaN = unknown key).</summary>
    public Func<string, float, float>? Tune { get; init; }

    /// <summary>Put every dial back to its boot value.</summary>
    public Action? Reset { get; init; }
}

/// <summary>
/// The Alloy passes' roll-call (#175). Before this, adding a pass meant writing up to eight methods and
/// remembering to wire each into four separate hardcoded call lists -- attach, self-check, context-loss and
/// the period report -- and the gaps in that table were not deliberate, they were the bugs the convention
/// produced. Registration order is <see cref="FrameGraph"/>'s order, declared once in
/// <see cref="RenderPasses"/>.
/// </summary>
/// <remarks>
/// Same shape and same zero-alloc rule as <see cref="FrameRegistry"/>: registration builds a list,
/// <see cref="Seal"/> flattens it to an array once at init, and every walk is a plain indexed loop. None of
/// the walks is per-frame -- attach and self-check are init, forget is a context edge, stats is the period
/// report -- but the array is what keeps <see cref="Forget"/> honest when a pass is added.
/// </remarks>
internal sealed class PassRegistry
{
    /// <summary>The live registry. Passes register onto this one.</summary>
    internal static readonly PassRegistry Global = new();

    private readonly List<PassLifecycle> _pending = new();
    private PassLifecycle[] _sealed = Array.Empty<PassLifecycle>();
    private bool _isSealed;

    internal bool Sealed => _isSealed;
    internal int Count => _isSealed ? _sealed.Length : _pending.Count;

    /// <summary>Join the roll-call. Refused (and logged) once sealed: a late join would be silently absent
    /// from the context-loss walk, which is exactly the class of bug this registry exists to end.</summary>
    internal bool Register(PassLifecycle pass, ExtenderLog? log = null)
    {
        if (_isSealed)
        {
            log?.Info($"PassRegistry: '{pass.Name}' registered after Seal -- refused");
            return false;
        }
        _pending.Add(pass);
        return true;
    }

    /// <summary>Flatten to the array the walks use. Once, at init.</summary>
    internal void Seal()
    {
        _sealed = _pending.ToArray();
        _isSealed = true;
    }

    internal void Attach(ExtenderLog log)
    { for (int i = 0; i < _sealed.Length; i++) _sealed[i].Attach?.Invoke(log); }

    internal void Install(ExtenderLog log, HookService hooks)
    { for (int i = 0; i < _sealed.Length; i++) _sealed[i].Install?.Invoke(log, hooks); }

    /// <summary>Run every registered pass's own check. Named apart from <see cref="SelfCheck"/> below,
    /// which is the REGISTRY's check rather than the passes'.</summary>
    internal void RunSelfChecks(ExtenderLog log)
    { for (int i = 0; i < _sealed.Length; i++) _sealed[i].SelfCheck?.Invoke(log); }

    /// <summary>Every registered pass drops its GL names. The whole reason the registry exists: a pass that
    /// forgets to stand down on context loss keeps stale ids and draws from a dead context.</summary>
    internal void ContextLost()
    { for (int i = 0; i < _sealed.Length; i++) _sealed[i].ContextLost?.Invoke(); }

    /// <summary>...and the world edge. Same argument: <c>GrassField.Forget</c> had NO caller at all before
    /// this registry, so grass kept its patch table across a part-table wipe.</summary>
    internal void Revoke()
    { for (int i = 0; i < _sealed.Length; i++) _sealed[i].Revoke?.Invoke(); }

    /// <summary>The period report's pass clauses, comma-joined. Cold -- once per report -- so the string
    /// building here is deliberate and never on a frame.</summary>
    internal string Stats(int frames)
    {
        string outp = "";
        for (int i = 0; i < _sealed.Length; i++)
        {
            string? s = _sealed[i].Stats?.Invoke(frames);
            if (string.IsNullOrEmpty(s)) continue;
            outp = outp.Length == 0 ? s : outp + ", " + s;
        }
        return outp;
    }

    /// <summary>The pass registered under <paramref name="name"/>, or null. What lets one chat-command
    /// handler serve every tunable pass instead of one near-identical handler each.</summary>
    internal PassLifecycle? Find(string name)
    {
        var a = _isSealed ? _sealed : (IReadOnlyList<PassLifecycle>)_pending;
        for (int i = 0; i < a.Count; i++)
            if (string.Equals(a[i].Name, name, StringComparison.OrdinalIgnoreCase)) return a[i];
        return null;
    }

    /// <summary>The registry's own contract: the walks skip null slots, refuse late joins, and reach every
    /// registered pass -- the last being the acceptance criterion the ticket states and nothing on-device
    /// can prove.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"PassRegistry.SelfCheck FAIL: {w}"); } }

        var r = new PassRegistry();
        int forgot = 0;
        int revoked = 0;
        r.Register(new PassLifecycle { Name = "alpha", ContextLost = () => forgot++, Revoke = () => revoked++,
                                       Stats = f => $"alpha {f}" });
        r.Register(new PassLifecycle { Name = "beta" });   // every slot null: must be skipped, not crash
        r.Register(new PassLifecycle { Name = "gamma", ContextLost = () => forgot++, Stats = _ => "" });

        E(!r.Sealed && r.Count == 3, "registration counts before seal");
        E(r.Find("BETA") != null, "lookup is case-insensitive");
        E(r.Find("delta") == null, "an unregistered name finds nothing");

        r.Seal();
        r.ContextLost();
        E(forgot == 2, $"context loss reaches every pass that has one (saw {forgot})");
        r.Revoke();
        E(revoked == 1, $"the world edge is a SEPARATE walk, not the same one (saw {revoked})");
        E(r.Stats(7) == "alpha 7", "stats joins the non-empty clauses only");
        r.Attach(log); r.RunSelfChecks(log);   // all-null slots: a no-op, not a null deref

        E(!r.Register(new PassLifecycle { Name = "late" }), "a post-Seal registration is refused");
        E(r.Find("late") == null, "and the refused pass is not in the walk");

        log.Info(ok ? $"PassRegistry.SelfCheck: passed" : "PassRegistry.SelfCheck: FAILED");
    }
}
