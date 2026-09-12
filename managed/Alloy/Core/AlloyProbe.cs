using System;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>
/// #456: the first-sighting layout probe, as Alloy calls it. <see cref="EngineLayout.Probe"/> walks the whole
/// row table by class name, so it is a boot-time check wearing a frame-loop signature -- this wrapper is the
/// per-site one-shot latch that keeps it out of the frame, and the one place the failure verdict is spelled.
/// </summary>
/// <remarks>A failed probe is an <see cref="AlloyGate.Fail"/> rather than a stand-down: by the time a pointer
/// of the class is in hand the frame is already Alloy's and there is no engine frame behind it (#236).</remarks>
internal static class AlloyProbe
{
    /// <summary>Probe <paramref name="cls"/> against this instance, once. <paramref name="done"/> is the
    /// caller's own latch -- a plain static bool, so the second call is one branch.</summary>
    internal static void Once(ref bool done, string cls, IntPtr obj, ExtenderLog? log)
    {
        if (done || obj == IntPtr.Zero) return;
        done = true;
        // The per-row "probe FAILED -- Class.Field at +0x.." line is EngineLayout's, and it needs the log to
        // print it: without the offset the notice below names a class and nothing actionable.
        if (!EngineLayout.Probe(cls, obj, log))
            AlloyGate.Fail($"the engine layout probe failed on {cls} -- this build's offsets are wrong");
    }
}
