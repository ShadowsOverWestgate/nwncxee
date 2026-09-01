using System;
using System.Diagnostics;

namespace Sowx.Framework;

/// <summary>#238 (AC1): where the frame actually goes, on the CPU, end to end. The perf tickets before this
/// one could only report the pieces they happened to bracket -- ~13ms of a measured 150ms frame -- and every
/// technique choice downstream of that was a guess about the other 90%. This is the instrument that names it:
/// a fixed set of accumulators, one <c>Stopwatch</c> read per bracket, and a residual that is the frame time
/// minus everything claimed.
/// <para><b>Top-level vs nested.</b> <see cref="Zone.Nested"/> splits the enum. Zones below it are mutually
/// exclusive spans of the frame and are what the residual subtracts; zones above it are breakdowns INSIDE one
/// of those and are reported for shape only. A bracket that contains another passes the child's ticks as
/// <c>minus</c> so the parent reports its own work -- which is not cosmetic: <c>DrawOwned</c> runs inside the
/// #132 anchor inside <see cref="Zone.Shim"/>, so the shim's published "6ms/frame" was the whole Alloy CPU
/// frame plus the walk.</para>
/// <para><b>Averages hide the thing we are hunting.</b> A single 150ms frame in 900 moves an average by
/// 0.16ms. So the period line also reports the WORST frame's own breakdown, snapshotted when it happens --
/// that is the spike, named rather than inferred.</para>
/// <para>Hot: <see cref="Add"/> is two array writes and no allocation; the only string is the period line.</para></summary>
public static class FrameZones
{
    /// <summary>The bracket set. Order matters: everything before <see cref="Nested"/> is a top-level,
    /// non-overlapping span of the frame and counts against the residual.</summary>
    public enum Zone
    {
        /// <summary>Alloy's ownership scan (<c>AlloyRenderer.Enroll</c>), every <c>EnrollEvery</c> passes.</summary>
        Enroll,
        /// <summary>The ADR 0007 pass slot: light walk, shadow focus, and the world-caster bake.</summary>
        Shadow,
        /// <summary>The RSP shim's own work -- the bookkeeping vcalls RenderSinglePass braided into its
        /// drawing, with the Alloy bracket that runs inside its anchor subtracted out.</summary>
        Shim,
        /// <summary>The Alloy colour frame (<c>DrawOwned</c>), CPU submission side.</summary>
        Alloy,
        /// <summary>The UI window roll-call.</summary>
        Ui,
        /// <summary>The present itself. Large here means GPU- or vsync-bound, which is a different problem
        /// from every other zone on this list.</summary>
        Present,

        /// <summary>Split marker, not a bracket.</summary>
        Nested,

        /// <summary>Nested in <see cref="Alloy"/>: revalidate + route + stage every owned part.</summary>
        Build,
        /// <summary>#322: nested in <see cref="Build"/>: the routing walk (identity, live alpha, tile fade,
        /// blended sort) -- the half an incremental build keeps O(world).</summary>
        Scan,
        /// <summary>#322: nested in <see cref="Build"/>: the staging walk (records, textures, draw args,
        /// batch cuts) -- the half #322 wants proportional to the changed set. This split IS the ticket's
        /// step 1: it attributes the Build monolith before any phase boundary is drawn.</summary>
        Restage,
        /// <summary>#324: inside <see cref="Restage"/> (NOT subtracted from it): the two spans of the staging
        /// walk that hold its engine-pointer derefs -- dissolve, the texture re-fetches, the
        /// VDO item range, the node matrix, the colour triples, the water body -- together with the owned
        /// bookkeeping interleaved with them. An UPPER BOUND on the unmovable half, so Restage minus Chase is
        /// a lower bound on what a GPU pass could take. SAMPLED 1-in-<c>AlloyRenderer.ChaseEvery</c> parts
        /// and extrapolated; parts that skip out mid-span are not sampled. An estimate, not a bracket sum.</summary>
        Chase,
        /// <summary>#322: nested in <see cref="Shadow"/>: the LightManager master-list walk.</summary>
        Lwalk,
        /// <summary>#322: nested in <see cref="Shadow"/>: <c>CasterPass.OnPassSlot</c> -- cube ranking,
        /// mover scans and the view bakes.</summary>
        Bake,
        /// <summary>#322: nested in <see cref="Alloy"/>: the light walk upload + cluster build.</summary>
        Lights,
        /// <summary>#322: nested in <see cref="Alloy"/>: the colour-pass loop's own CPU work, minus the
        /// nested Issue/Cull/Post/Probe brackets inside it -- prepass, hi-Z, grass, water, beams,
        /// particles, state churn. Added because the Alloy zone measured ~6ms MORE than its children
        /// claimed, and an unclaimed tower cannot be cut.</summary>
        Passes,
        /// <summary>#322: nested in <see cref="Alloy"/>: the post chain -- Begin (target setup) plus
        /// bloom/composite/highlight at the PostProcess slot.</summary>
        Post,
        /// <summary>#322: nested in <see cref="Passes"/>: the depth prepass submission loop + the hi-Z
        /// pyramid build.</summary>
        Prepass,
        /// <summary>#322: nested in <see cref="Passes"/>: the full-screen copies -- the water snapshot and
        /// the soft-particle depth take. Small numbers here are honest; a large one is a GPU sync stall
        /// wearing a memcpy's name.</summary>
        Blit,
        /// <summary>#322: nested in <see cref="Passes"/>: the particle pass CPU (build + upload + submit),
        /// minus its Blit child.</summary>
        Parts,
        /// <summary>Nested in <see cref="Alloy"/>: the wholesale per-draw SSBO / handle / bone uploads.</summary>
        Upload,
        /// <summary>Nested in <see cref="Alloy"/>: the multidraw submission itself.</summary>
        Issue,
        /// <summary>Nested in <see cref="Alloy"/>: force-solving every owned skinmesh pose through the engine.</summary>
        Pose,
        /// <summary>Nested in <see cref="Alloy"/>: owned-texture/buffer drains and re-copies.</summary>
        Own,
        /// <summary>Nested in <see cref="Alloy"/>: the six-face IBL probe capture.</summary>
        Probe,
        /// <summary>#243: nested in <see cref="Alloy"/>: staging the cull's source list and the two GPU
        /// dispatches over it. CPU-side only -- what the dispatches themselves cost is GPU time and shows up
        /// in <see cref="Present"/> or in nothing at all.</summary>
        Cull,

        Count,
    }

    private const int N = (int)Zone.Count;
    private static readonly long[] _frame = new long[N];    // this frame's ticks, per zone
    private static readonly long[] _sum = new long[N];      // the period's
    private static readonly long[] _worst = new long[N];    // ...and the worst single frame's, snapshotted
    private static readonly long[] _worstDrew = new long[N]; // ...and the worst that reached the scene render
    private static long _dtSum, _dtWorst, _dtWorstDrew;
    private static int _frames;

    /// <summary>Ticks this zone has accumulated so far THIS frame -- the value a parent bracket samples
    /// before and after its child so it can subtract it.</summary>
    public static long Ticks(Zone z) => _frame[(int)z];

    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Close a bracket opened at <paramref name="t0"/>, optionally minus ticks a nested bracket
    /// already claimed. Never accumulates negative: a child measured across a boundary this parent did not
    /// span would otherwise credit the parent time it did not spend.</summary>
    public static void Add(Zone z, long t0, long minus = 0)
    {
        long dt = Stopwatch.GetTimestamp() - t0 - minus;
        if (dt > 0) _frame[(int)z] += dt;
    }

    /// <summary>#324: close a bracket taken on 1-in-<paramref name="every"/> iterations of a walk, credited
    /// times <paramref name="every"/>. Timestamping every part of a 14k walk would cost more than the half
    /// being measured; the sample is the instrument.</summary>
    public static void AddSampled(Zone z, long t0, int every)
    {
        long dt = Stopwatch.GetTimestamp() - t0;
        if (dt > 0) _frame[(int)z] += dt * every;
    }

    /// <summary>Drop this frame's brackets without folding them into the period -- the loading-screen and
    /// main-menu case. Without it the first in-module frame after a menu would carry the menu's ticks.</summary>
    public static void Discard() => Array.Clear(_frame, 0, N);

    /// <summary>Did this frame reach the scene render at all? A frame with every renderer zone at zero spent
    /// its time somewhere no bracket here can see -- in practice engine asset loading.</summary>
    private static bool Drew()
    {
        for (int i = 0; i < (int)Zone.Nested; i++)
            if (i != (int)Zone.Ui && i != (int)Zone.Present && _frame[i] != 0) return true;
        return false;
    }

    /// <summary>The frame boundary: fold this frame into the period, keep it if it is the worst one, and
    /// reset. <paramref name="dt"/> is the present-to-present time the zones are measured against.</summary>
    public static void EndFrame(long dt)
    {
        _dtSum += dt; _frames++;
        if (dt > _dtWorst) { _dtWorst = dt; Array.Copy(_frame, _worst, N); }
        if (dt > _dtWorstDrew && Drew()) { _dtWorstDrew = dt; Array.Copy(_frame, _worstDrew, N); }
        if (_enrollMarked && _enrollN < _enrollTicks.Length) _enrollTicks[_enrollN++] = _frame[(int)Zone.Enroll];
        _enrollMarked = false;
        for (int i = 0; i < N; i++) { _sum[i] += _frame[i]; _frame[i] = 0; }
    }

    /// <summary>#324: the Enroll ticks of every CADENCE frame this period -- a frame the enrolment walk ran
    /// on, marked by <see cref="MarkEnrollFrame"/>. The p99 over that population is the metric move #5 (a
    /// fixed enrolment slot budget) is judged by; over all frames it would only be the walk's mean. The
    /// walk runs every 12-20 frames, so a 900-frame period never fills the array.</summary>
    private static readonly long[] _enrollTicks = new long[1024];
    private static int _enrollN;
    private static bool _enrollMarked;

    /// <summary>Call on a frame the enrolment walk ran, before <see cref="EndFrame"/>.</summary>
    public static void MarkEnrollFrame() => _enrollMarked = true;

    private static string EnrollP99()
    {
        int n = _enrollN;
        _enrollN = 0;
        if (n == 0) return " || Enroll: no walk this period";
        var s = new long[n];
        Array.Copy(_enrollTicks, s, n);
        Array.Sort(s);
        double k = 1000.0 / Stopwatch.Frequency;
        return $" || Enroll walks {n}: p99 {s[(int)(n * 0.99)] * k:F1}ms p50 {s[n / 2] * k:F1}ms";
    }

    /// <summary>The period line, and the reset with it. Three rows: the average frame's split, the worst
    /// frame's, and the worst frame that actually rendered. <c>other</c> is the residual -- frame time minus
    /// every top-level zone -- and a large one is itself the finding (engine simulation, input, 2D UI: work
    /// no Alloy bracket can see). The third row is dropped when it IS the second one, which is the ordinary
    /// case; it appearing at all means the worst frame of the period never reached the scene render.</summary>
    public static string Period()
    {
        int frames = Math.Max(_frames, 1);
        string line = "FrameZones: [zones] " + Row(_sum, _dtSum, frames)
                    + " || worst frame " + Row(_worst, _dtWorst, 1);
        if (_dtWorstDrew != _dtWorst) line += " || worst that RENDERED " + Row(_worstDrew, _dtWorstDrew, 1);
        line += EnrollP99();
        Array.Clear(_sum, 0, N); Array.Clear(_worst, 0, N); Array.Clear(_worstDrew, 0, N);
        _dtSum = 0; _dtWorst = 0; _dtWorstDrew = 0; _frames = 0;
        return line;
    }

    private static string Row(long[] t, long dt, int frames)
    {
        double k = 1000.0 / Stopwatch.Frequency / frames;
        long claimed = 0;
        for (int i = 0; i < (int)Zone.Nested; i++) claimed += t[i];
        var sb = new System.Text.StringBuilder();
        sb.Append($"{dt * k:F1}ms =");
        for (int i = 0; i < (int)Zone.Nested; i++) sb.Append($" {(Zone)i}:{t[i] * k:F1}");
        sb.Append($" other:{(dt - claimed) * k:F1}  (");
        for (int i = (int)Zone.Nested + 1; i < N; i++)
        {
            if (i > (int)Zone.Nested + 1) sb.Append(' ');
            sb.Append($"{(Zone)i}:{t[i] * k:F1}");
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Off-device: subtraction, worst-frame capture and the period reset. All three are load-bearing
    /// -- a parent that failed to subtract its child would double-count the Alloy frame into the shim (which
    /// is exactly the reading this ticket is correcting), and an averaged-away worst frame is the spike the
    /// whole instrument exists to name.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        Array.Clear(_frame, 0, N); Array.Clear(_sum, 0, N);
        Array.Clear(_worst, 0, N); Array.Clear(_worstDrew, 0, N);
        _dtSum = 0; _dtWorst = 0; _dtWorstDrew = 0; _frames = 0;

        long f = Stopwatch.Frequency;
        // A bracket whose child claimed more than the parent spent contributes NOTHING -- never a negative,
        // which would credit the parent's siblings with time nobody spent. The unsubtracted arm must still
        // record something, or the subtraction would be indistinguishable from a dead bracket.
        long t0 = Stopwatch.GetTimestamp() - f;   // a synthetic bracket one second wide
        _frame[(int)Zone.Alloy] = 100;
        Add(Zone.Shim, t0, f * 2);
        bool ok = _frame[(int)Zone.Shim] == 0;
        Add(Zone.Shim, t0);
        ok &= _frame[(int)Zone.Shim] >= f;
        if (!ok) log.Info("FrameZones.SelfCheck: FAILED -- a parent bracket did not subtract its child");
        // #324: a sampled bracket is credited `every` times over, so the split reads as a whole-walk estimate.
        AddSampled(Zone.Chase, t0, 16);
        bool s = _frame[(int)Zone.Chase] >= f * 16 && _frame[(int)Zone.Chase] < f * 17;
        if (!s) log.Info("FrameZones.SelfCheck: FAILED -- the sampled bracket was not extrapolated");

        EndFrame(f);                      // a 1000ms frame that rendered -- the worst so far, on both latches
        _frame[(int)Zone.Enroll] = f;
        MarkEnrollFrame();                // #324: the one cadence frame -- the p99 population is this alone
        EndFrame(f / 2);                  // ...and a faster one, which must not displace it
        bool w = _worst[(int)Zone.Alloy] == 100 && _worst[(int)Zone.Enroll] == 0;
        if (!w) log.Info("FrameZones.SelfCheck: FAILED -- the worst frame is not the one that was kept");

        // The transition stall: longer than anything else in the period, and every renderer zone at zero.
        // It must take the overall latch and must NOT take the rendered one, or one area load per period
        // blinds the instrument to the gameplay spike for the rest of it.
        _frame[(int)Zone.Ui] = 7;
        EndFrame(f * 4);
        bool t = _dtWorst == f * 4 && _worst[(int)Zone.Ui] == 7
                 && _dtWorstDrew == f && _worstDrew[(int)Zone.Alloy] == 100;
        if (!t) log.Info("FrameZones.SelfCheck: FAILED -- a non-rendering stall claimed the rendered-frame latch");

        string line = Period();
        bool r = line.Contains("Enroll:") && line.Contains("other:") && line.Contains("worst frame")
                 && line.Contains("worst that RENDERED") && line.Contains("Enroll walks 1: p99 1000")
                 && _frames == 0 && _dtWorst == 0 && _dtWorstDrew == 0;
        if (!r) log.Info("FrameZones.SelfCheck: FAILED -- the period line or its reset drifted");
        log.Info(ok && s && w && t && r ? "FrameZones.SelfCheck: passed"
                                   : "FrameZones.SelfCheck: see failures above");
    }
}
