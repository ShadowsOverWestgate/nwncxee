namespace CX.Framework;

/// <summary>
/// One pass's fallback-to-vanilla latch: dedupe the reason, log it once, return false. <see cref="Refuse"/> is
/// per-pass and NOT latched, because a GUI 3D panel may refuse while the world pass keeps arming;
/// <see cref="Fail"/> latches, because the machine or driver cannot do it at all. Both arguments must be CONSTANT
/// strings: the reason is compared before it is logged, so an interpolated one allocates on every refusal.
/// </summary>
public sealed class StandDown
{
    private readonly string _who;
    private string _last = "";

    public StandDown(string who) => _who = who;

    /// <summary>Latched: this class cannot work in this session at all.</summary>
    public bool Failed { get; private set; }

    /// <summary>How many times a pass refused, latched or not. The one number that separates "no content of
    /// this kind here" from "one thing keeps refusing and takes the class down with it".</summary>
    public int Refusals { get; private set; }

    /// <summary>The last distinct reason, or <c>""</c> if the pass never refused. For <c>Describe</c>.</summary>
    public string Reason => _last;

    /// <summary>Per-pass refusal. Always false, so it reads as <c>return sd.Refuse(log, "...", "...")</c>.</summary>
    public bool Refuse(ExtenderLog? log, string why, string consequence)
    {
        Refusals++;
        if (_last != why)
        {
            _last = why;
            log?.Info($"{_who}: {why} -- {consequence}");
        }
        return false;
    }

    /// <summary>...and the latching kind, for a failure that cannot come back.</summary>
    public bool Fail(ExtenderLog? log, string why, string consequence)
    {
        Failed = true;
        return Refuse(log, why, consequence);
    }

    /// <summary>Context loss: forget the latch and the last reason so the next context can arm and, if it
    /// refuses again, say so rather than swallowing the line as a duplicate.</summary>
    public void Forget()
    {
        Failed = false;
        _last = "";
    }
}
