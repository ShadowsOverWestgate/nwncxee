using System;
using System.Diagnostics;

namespace CX.Alloy;

/// <summary>CX-56 step 0: the hold's stage split. Every wait between the swallowed AreaLoaded send and
/// <see cref="Release"/> gets one <see cref="Stopwatch"/> stamp, printed as a delta from <c>_holdT0</c> on the
/// release line (and compactly on <c>TexStream</c>'s <c>[load]</c> line). Measurement only: nothing here gates
/// a release. The stamp sites sit on per-frame paths, so the state is plain <c>long</c>/<c>int</c> fields and a
/// stamp is a compare plus a timestamp read -- the strings are built once, in <see cref="Release"/>.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>What the last <see cref="Arrived"/> was: the quiet window's clock is measured from it, so the
    /// release line names which of the three restarted it last.</summary>
    private const byte WhyNone = 0, WhyClose = 1, WhyMint = 2, WhyChart = 3, WhyAdopt = 4, WhyClaim = 5;

    private static long _stEdge, _stClose, _stSweep, _stCharts, _stPack0, _stPackN, _stProbe0, _stProbeN;
    private static int _stPack0N, _stPackNN, _stChartsOffered, _stProbes;
    /// <summary>Cumulative <see cref="Adopt"/> cost under the hold, and the frames it stopped on the byte budget
    /// rather than on an empty queue: that count is what decides byte budget against call count.</summary>
    private static long _stAdoptTicks, _stAdoptBytes;
    private static int _stAdoptRects, _stAdoptCapped;
    /// <summary>The cache thread's own counters at the hold's start; the line prints the deltas.</summary>
    private static int _stInflates0;
    private static long _stAlloc0;
    /// <summary>Minted rects, ever: the sweep-quiet stamp only compares it against its own copy.</summary>
    private static long _stMints;
    /// <summary>Slots the sweep has visited since the last mint, and the mint count that pass started on.</summary>
    private static long _stSwSeen, _stSwMints;
    private static byte _stWhy;
    /// <summary>A hold has run and its stamps have not been cleared by the next send.</summary>
    private static bool _stHave;

    /// <summary>The send arms a new hold: every stamp is this hold's or it is nothing.</summary>
    private static void StampsReset()
    {
        _stEdge = _stClose = _stSweep = _stCharts = _stPack0 = _stPackN = _stProbe0 = _stProbeN = 0;
        _stPack0N = _stPackNN = _stChartsOffered = _stProbes = 0;
        _stAdoptTicks = _stAdoptBytes = 0; _stAdoptRects = _stAdoptCapped = 0;
        _stSwSeen = 0; _stSwMints = _stMints; _stWhy = WhyNone; _stHave = false;
        _stInflates0 = _cache.Inflates; _stAlloc0 = _cache.Allocated;
    }

    /// <summary>The area edge consumed at the frame anchor: the frame the bake starts the new area from.</summary>
    private static void StampEdge() { if (Held && _stEdge == 0) _stEdge = Stopwatch.GetTimestamp(); }

    /// <summary>The static set closed (<c>EndWalk</c>): the walk count is already on the close's own line.</summary>
    private static void StampClose() { if (Held && _stClose == 0) _stClose = Stopwatch.GetTimestamp(); }

    /// <summary>The first whole-table enrol sweep that minted nothing: "nothing left to enrol" measured rather
    /// than assumed. A mint restarts the pass, so this is the first quiet table's worth of slots.</summary>
    private static void StampSweep(int visited)
    {
        if (!Held || _stSweep != 0) return;
        if (_stSwMints != _stMints) { _stSwMints = _stMints; _stSwSeen = 0; }
        _stSwSeen += visited;
        if (_stSwSeen >= _slotRect.Length) _stSweep = Stopwatch.GetTimestamp();
    }

    /// <summary>The LAST frame the chart pipeline went empty, not the first: a chart offered after an empty
    /// frame re-arms it, so the stamp is the floor every enrol sits above.</summary>
    private static void StampCharts()
    {
        if (!Held) return;
        if (MeshChart.InFlight != 0 || AlloyRenderer.ChartDeferred != 0) { _stCharts = 0; return; }
        if (_stCharts == 0) { _stCharts = Stopwatch.GetTimestamp(); _stChartsOffered = MeshChart.Offered; }
    }

    /// <summary>A <see cref="Pack"/> that placed rects, with how many.</summary>
    private static void StampPack(int placed)
    {
        if (!Held || placed <= 0) return;
        long t = Stopwatch.GetTimestamp();
        if (_stPack0 == 0) { _stPack0 = t; _stPack0N = placed; }
        _stPackN = t; _stPackNN = placed;
    }

    /// <summary>An answer taken off the cache queue in <see cref="Adopt"/>, hit or miss.</summary>
    private static void StampProbe()
    {
        if (!Held) return;
        long t = Stopwatch.GetTimestamp();
        if (_stProbe0 == 0) _stProbe0 = t;
        _stProbeN = t; _stProbes++;
    }

    /// <summary>One frame's <see cref="Adopt"/>: its cost, what it uploaded, and whether the byte budget -- not
    /// an empty queue -- is what ended it.</summary>
    private static void StampAdopt(long ticks, int rects, long bytes, bool capped)
    {
        _stAdoptTicks += ticks; _stAdoptRects += rects; _stAdoptBytes += bytes;
        if (capped) _stAdoptCapped++;
    }

    /// <summary>Per present under the hold: the only stamp that has to be polled.</summary>
    private static void StampTick() => StampCharts();

    private static float StampMs(long t) => (t - _holdT0) * 1000f / Stopwatch.Frequency;
    private static string At(long t) => t == 0 ? "never" : $"{StampMs(t):F0}";

    /// <summary>The release line's stage split, all in ms from the swallowed send, and the mark that says the
    /// split is this transition's (<see cref="HeldClause"/>). Cold: once a hold.</summary>
    private static string StampsClause()
    {
        _stHave = true;
        float quiet = (Stopwatch.GetTimestamp() - _quietT) * 1000f / Stopwatch.Frequency;
        string why = _stWhy switch { WhyClose => "the close", WhyMint => "a minted rect", WhyChart => "a chart", WhyAdopt => "the area plan", WhyClaim => "a claimed rect", _ => "nothing" };
        return $"; stages ms from the send: edge {At(_stEdge)}, close {At(_stClose)} (walk {_walks}), "
             + $"sweep-quiet {At(_stSweep)}, charts quiet {At(_stCharts)} ({_stChartsOffered} offered by then), "
             + $"pack {At(_stPack0)}..{At(_stPackN)} ({_stPack0N} rects first, {_stPackNN} last), "
             + $"probe {At(_stProbe0)}..{At(_stProbeN)} ({_stProbes} answers, {_cache.Inflates - _stInflates0} blob inflates, "
             + $"{(_cache.Allocated - _stAlloc0) >> 20} MiB allocated on cx-lmcache), "
             + $"adopt {_stAdoptTicks * 1000f / Stopwatch.Frequency:F0} ms / {_stAdoptRects} rects / {_stAdoptBytes >> 20} MiB, {_stAdoptCapped} frames the byte budget capped, "
             + $"quiet {quiet:F0} ms since {why}";
    }

    /// <summary>The same split, compact, for <c>TexStream</c>'s <c>[load]</c> line beside its <c>held</c>
    /// segment; empty when the last send ran under no hold.</summary>
    internal static string HeldClause()
        => !_stHave ? string.Empty
         : $" [edge {At(_stEdge)}, close {At(_stClose)}, sweep {At(_stSweep)}, charts {At(_stCharts)}, pack {At(_stPackN)}, probe {At(_stProbeN)},"
         + $" adopt {_stAdoptTicks * 1000f / Stopwatch.Frequency:F0} ms]";
}
