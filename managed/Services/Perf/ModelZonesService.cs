using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Perf;

/// <summary>What a model load costs, measured, with no behaviour changed: brackets on <c>FindModel</c> (only its
/// OUTERMOST call is one model asked for -- the binary parse recurses for the supermodel), <c>IODispatcher::ReadSync</c>
/// (a miss IS a ReadSync, at any depth), <c>CAuroraInterface::GetResource</c> (the read inside it; byte 0 of what it
/// returns is what <c>ReadSync</c> branches on) and <c>SpawnPartsForTile</c>. Main thread only; counters never reset.</summary>
public sealed unsafe class ModelZonesService : IExtenderService
{
    private static ExtenderLog? _log;
    private static FindModelFn* _findModel;
    private static IoReadSyncFn* _readSync;
    private static AuroraGetResourceFn* _getResource;
    private static SpawnPartsForTileFn* _spawnParts;
    private static bool _armed;

    /// <summary>Everything here rides on the same chain priority: a measurement bracket wants to sit outside whatever
    /// else chains these symbols below it.</summary>
    private const int Priority = 100;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);
        _findModel = hooks.ChainFindModel(&OnFindModel, Priority);
        _readSync = hooks.ChainIoReadSync(&OnReadSync, Priority);
        _getResource = hooks.ChainAuroraGetResource(&OnGetResource, Priority);
        _spawnParts = hooks.ChainSpawnPartsForTile(&OnSpawnParts, Priority);
        if (_findModel == null || _readSync == null)
        {
            log.Always($"ModelZones: REFUSED -- FindModel {(_findModel != null ? "ok" : "MISSING")}, "
                       + $"IODispatcher::ReadSync {(_readSync != null ? "ok" : "MISSING")}; no model figures on the [load] line");
            return;
        }
        _armed = true;
        log.Info("ModelZones: timing the engine's model loads (FindModel hits/misses, the read, the parse and the"
                 + " texture demands nested in it) into the [load] line"
                 + (_getResource == null ? " -- CAuroraInterface::GetResource unresolved: no read time and no binary/ASCII split" : "")
                 + (_spawnParts == null ? " -- SpawnPartsForTile unresolved: tile misses land in `other`" : ""));
    }

    // ---- the counters, cumulative and never reset ----

    /// <summary>Outermost <c>FindModel</c> calls, and the ones that did no I/O at all.</summary>
    private static long _calls, _hits;
    /// <summary>One per <c>ReadSync</c>, i.e. one per model file actually loaded, at any depth.</summary>
    private static long _misses, _binary, _ascii;
    /// <summary>Where a miss came from: inside <c>SpawnPartsForTile</c>, nested in another model's parse
    /// (the supermodel recursion), or anything else -- a placeable, a creature, an add-in animation.</summary>
    private static long _tile, _super, _other;
    /// <summary>Non-overlapping ticks: the file reads, the parses, the ASCII share of those parses, and the
    /// texture demands nested inside the parses.</summary>
    private static long _read, _parse, _asciiParse, _tex;
    /// <summary>ReadSync calls past <see cref="MaxDepth"/>, which are counted but not timed.</summary>
    private static long _deep;

    // ---- the bracket ----

    /// <summary>Supermodel chains are a handful deep; past this a ReadSync is counted and not timed.</summary>
    private const int MaxDepth = 16;

    /// <summary>One open <c>ReadSync</c>. <c>Child</c> and <c>ChildTex</c> are what a nested ReadSync handed up,
    /// so this frame's own cost is its elapsed time minus them.</summary>
    private struct Frame
    {
        public long T0, Tex0, Child, ChildTex, Read;
        public bool Ascii, Sniffed;
    }

    private static readonly Frame[] _stack = new Frame[MaxDepth];
    private static int _depth, _getDepth, _tileDepth, _findDepth;

    /// <summary>Every model load. The outermost call is the one model asked for; the recursion under it is the
    /// supermodel chain, which the miss counters see as <c>ReadSync</c> calls instead.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnFindModel(byte* name)
    {
        int d = _findDepth++;
        long m0 = _misses;
        if (d == 0) _calls++;
        IntPtr r = (*_findModel)(name);
        _findDepth--;
        if (d == 0 && _misses == m0) _hits++;
        return r;
    }

    /// <summary>One model file, its three costs kept apart: the read (its own outermost <c>GetResource</c>), the
    /// nested texture demands (<see cref="EngineZonesService.DemandTexTicks"/>, never reset) and what is left, the
    /// parse. A nested ReadSync's whole elapsed time is handed to its parent and subtracted there, so the totals
    /// never overlap.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnReadSync(IntPtr dispatcher, byte* name)
    {
        int d = _depth++;
        _misses++;
        if (d > 0) _super++;
        else if (_tileDepth > 0) _tile++;
        else { _other++; NoteOther(name); }
        bool timed = d < MaxDepth;
        if (timed)
        {
            _stack[d] = default;
            _stack[d].T0 = FrameZones.Now;
            _stack[d].Tex0 = EngineZonesService.DemandTexTicks;
        }
        else _deep++;
        IntPtr r = (*_readSync)(dispatcher, name);
        _depth--;
        if (!timed) return r;
        Close(d, FrameZones.Now - _stack[d].T0, EngineZonesService.DemandTexTicks - _stack[d].Tex0);
        return r;
    }

    /// <summary>One closed <c>ReadSync</c> frame folded into the counters: the frame's own parse is its elapsed
    /// time less what its children handed up, its own read and its own texture demands, so no tick is counted
    /// twice. The one definition, so the SelfCheck drives it rather than restating it.</summary>
    private static void Close(int d, long elapsed, long texAll)
    {
        long selfTex = texAll - _stack[d].ChildTex;
        long parse = elapsed - _stack[d].Child - _stack[d].Read - selfTex;
        _read += _stack[d].Read;
        if (selfTex > 0) _tex += selfTex;
        if (parse > 0)
        {
            _parse += parse;
            if (_stack[d].Ascii) _asciiParse += parse;
        }
        if (_stack[d].Sniffed) { if (_stack[d].Ascii) _ascii++; else _binary++; }
        if (d > 0) { _stack[d - 1].Child += elapsed; _stack[d - 1].ChildTex += texAll; }
    }

    /// <summary>The engine's resource read. Only the calls inside a <c>ReadSync</c> are ours to time -- every
    /// texture, MTR and shader goes through here too -- and only the outermost of those, so a read nested in a
    /// read is not counted twice. An ASCII model is read here TWICE (the 4-byte peek, then <c>dofile</c>), which
    /// is the truth about what it costs.</summary>
    [UnmanagedCallersOnly]
    private static byte* OnGetResource(byte* name, byte* ext, int* size)
    {
        if (_depth == 0 || _getDepth != 0) return (*_getResource)(name, ext, size);
        _getDepth++;
        long t = FrameZones.Now;
        byte* r = (*_getResource)(name, ext, size);
        _getDepth--;
        int d = _depth - 1;
        if (d >= MaxDepth) return r;
        _stack[d].Read += FrameZones.Now - t;
        // Byte 0 of the resource is what ReadSync branches on: 0 = the binary reader, anything else = ASCII.
        if (r != null) { _stack[d].Sniffed = true; _stack[d].Ascii = *r != 0; }
        return r;
    }

    /// <summary>A tile's models, so a tile miss is not filed as `other`.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnSpawnParts(byte* resref, IntPtr pos, IntPtr rot, int id, int orientation, IntPtr parts)
    {
        _tileDepth++;
        IntPtr r = (*_spawnParts)(resref, pos, rot, id, orientation, parts);
        _tileDepth--;
        return r;
    }

    /// <summary>ponytail: the <c>[diag]</c> sample -- names the first few models loaded from neither a tile nor a
    /// supermodel during a transition, to show what else an area load pulls in. Capped for the session, and the only
    /// thing here that allocates; drop it once the offers are widened.</summary>
    private const int DiagCap = 40;
    private static int _diagOther;

    private static void NoteOther(byte* name)
    {
        if (!CX.Alloy.TexStream.InTransition || ++_diagOther > DiagCap) return;
        _log?.Info($"ModelZones: [diag] model read outside a tile spawn: {Marshal.PtrToStringAnsi((IntPtr)name)}");
    }

    // ---- reporting ----

    /// <summary>Every counter at one instant; a clause is the difference of two.</summary>
    private struct Snap
    {
        public long Calls, Hits, Misses, Binary, Ascii, Tile, Super, Other, Read, Parse, AsciiParse, Tex, Deep;
    }

    private static Snap Take() => new()
    {
        Calls = _calls, Hits = _hits, Misses = _misses, Binary = _binary, Ascii = _ascii,
        Tile = _tile, Super = _super, Other = _other,
        Read = _read, Parse = _parse, AsciiParse = _asciiParse, Tex = _tex, Deep = _deep,
    };

    private static Snap _atStart, _atEnd;

    /// <summary>Called by <see cref="CX.Alloy.TexStream"/> either side of <c>LoadArea</c>.</summary>
    internal static void MarkLoadStart() => _atStart = Take();
    internal static void MarkLoadEnd() => _atEnd = Take();

    /// <summary>What <c>LoadArea</c> itself spent on models; empty when nothing was hooked or nothing loaded.</summary>
    internal static string LoadClause() => Clause(_atStart, _atEnd);

    /// <summary>What loaded AFTER <c>LoadArea</c> returned -- the object spawns on the way to placement.</summary>
    internal static string AfterClause() => Clause(_atEnd, Take());

    /// <summary>Cold: twice per area transition.</summary>
    private static string Clause(in Snap a, in Snap b)
    {
        if (!_armed) return "";
        long calls = b.Calls - a.Calls, miss = b.Misses - a.Misses;
        if (calls == 0 && miss == 0) return "";
        double ms = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        return $"models {miss} miss ({b.Binary - a.Binary} binary / {b.Ascii - a.Ascii} ASCII, "
             + $"{b.Tile - a.Tile} tile / {b.Super - a.Super} super / {b.Other - a.Other} other) of {calls} calls ({b.Hits - a.Hits} cached), "
             + $"read {(b.Read - a.Read) / ms:F0} ms, parse {(b.Parse - a.Parse) / ms:F0} ms "
             + $"(ASCII {(b.AsciiParse - a.AsciiParse) / ms:F0} ms), nested tex {(b.Tex - a.Tex) / ms:F0} ms"
             + (b.Deep > a.Deep ? $" ({b.Deep - a.Deep} nested past the depth cap, untimed)" : "");
    }

    // ---- self-check ----

    /// <summary>Off-device: the nesting arithmetic, driven through <see cref="Close"/>. Two stand-in frames
    /// -- 100 ticks, 30 of it this model's own read and 20 its own texture demands, with an ASCII supermodel
    /// nested in it taking 40 (10 read, 5 tex). Oracle: the totals worked out by hand -- the outer parse is
    /// 100 - 40 - 30 - 20 = 10, the nested one 40 - 10 - 5 = 25, and no tick is counted twice.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ModelZones.SelfCheck");
        long read0 = _read, parse0 = _parse, tex0 = _tex, ascii0 = _asciiParse;
        long ascii1 = _ascii, binary1 = _binary;

        // The child, closed at depth 1 -- it hands its elapsed time and texture demands up to depth 0.
        _stack[0] = default;
        _stack[1] = default;
        _stack[1].Read = 10; _stack[1].Ascii = true; _stack[1].Sniffed = true;
        Close(1, 40, 5);
        long childParse = _parse - parse0;

        // ...and the parent, whose own 20 ticks of texture demands arrive on top of the child's 5.
        _stack[0].Read = 30;
        long elapsed = 100;
        Close(0, elapsed, 20 + 5);
        long parse = _parse - parse0 - childParse;

        check.Expect(childParse == 25, $"the nested parse is {childParse}, not 25");
        check.Expect(parse == 10, $"the outer parse is {parse}, not 10");
        check.Expect(_read - read0 == 40, $"the reads sum to {_read - read0}, not 40");
        check.Expect(_tex - tex0 == 25, $"the texture demands sum to {_tex - tex0}, not 25");
        check.Expect(_parse - parse0 == 35, $"the parses sum to {_parse - parse0}, not 35");
        check.Expect(_read - read0 + (_parse - parse0) + (_tex - tex0) == elapsed, "the three totals do not add up to the outermost elapsed time");
        check.Expect(_asciiParse - ascii0 == 25, $"the ASCII share is {_asciiParse - ascii0}, not 25");
        check.Expect(_ascii - ascii1 == 1 && _binary - binary1 == 0, "the sniffed child counts as one ASCII model and no binary one");
        _ascii = ascii1; _binary = binary1;

        _read = read0; _parse = parse0; _tex = tex0; _asciiParse = ascii0;
        _stack[0] = default; _stack[1] = default;

        var a = new Snap();
        var b = new Snap { Calls = 7, Hits = 4, Misses = 3, Binary = 1, Ascii = 2, Tile = 1, Super = 1, Other = 1 };
        bool armed = _armed;
        _armed = true;
        string line = Clause(a, b);
        check.Expect(Clause(a, a).Length == 0, "an area that loaded no model still prints a clause");
        check.Expect(line.StartsWith("models 3 miss (1 binary / 2 ASCII, 1 tile / 1 super / 1 other) of 7 calls (4 cached),"),
                     $"the clause reads wrong: {line}");
        _armed = armed;

        if (check.Ok) log.Info("ModelZones.SelfCheck: passed");
        return check.Ok;
    }
}
