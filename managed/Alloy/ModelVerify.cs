using System;
using System.Collections.Generic;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The live animation-lookup check (<c>/models verify 1</c>): for every model the ENGINE has loaded, ask
/// the engine's own exported <see cref="SymbolMap.FindAnimation"/> and <see cref="SymbolMap.FindAnimScale"/> and
/// compare POINTERS with <see cref="ModelCache"/>'s port -- a hand-written copy of the same traversal would pass
/// a misported walk ORDER. Driven off the engine's <c>List&lt;Model*&gt;</c>, the only engine memory it reads.</summary>
internal static unsafe class ModelVerify
{
    /// <summary>Mismatches named in the log before the check only counts them.</summary>
    private const int LogCap = 12;
    /// <summary>Animation scales agree to this; both sides multiply the same floats, so the only difference
    /// possible is the order.</summary>
    private const float ScaleEpsilon = 1e-4f;
    /// <summary>Both <c>Model</c> and <c>Animation</c> hold their name as MaxTree's <c>char[64]</c>
    /// (<c>MaxTree::MaxTree</c> @0x43a5b0 strncpy()s with a 0x3f cap).</summary>
    private const int NameChars = 64;
    /// <summary>A chain this long would be a cycle the engine could not have survived; the walk that collects
    /// the NAMES to ask about stops rather than trusting it.</summary>
    private const int MaxChain = 64;
    /// <summary>A name no model has, so every model is also asked the question whose only right answer is the
    /// chain's <c>"default"</c> at the TERMINAL model -- not at the model asked about -- or nothing.</summary>
    private const string Absent = "cx113_no_such_anim";
    /// <summary>Models named in the one-off sample line, per kind.</summary>
    private const int SampleCap = 6;

    private static ExtenderLog? _log;
    private static IntPtr _table;
    private static long _runs, _models, _chained, _anims, _pass, _failFound, _failScale, _failMemo;
    private static int _logged;
    /// <summary>The check cannot run on this client; said once, then silent.</summary>
    private static bool _stoodDown;
    /// <summary>The one-off sample line has been printed.</summary>
    private static bool _sampled;

    /// <summary>Hang the check off the cache's area edge and its period line. The cache stays engine-free: it
    /// knows these two delegates and nothing about what is behind them.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _table = Native.Sym(SymbolMap.ModelTable);
        ModelCache.RunVerify = Run;
        ModelCache.VerifyClause = Clause;
    }

    /// <summary>The LAST run's population and this session's failure totals -- never a per-run count mixed
    /// with a cumulative one on the same line.</summary>
    private static string Clause()
    {
        if (_runs == 0) return "";
        long fail = _failFound + _failScale + _failMemo;
        return $"verify run {_models} models ({_chained} chained) {_anims} anims {_pass} pass, session {fail} fail";
    }

    /// <summary>One comparison pass over the engine's whole model table. Cold: an area edge, behind a knob
    /// that is off by default.</summary>
    internal static void Run()
    {
        if (!ModelKnobs.Verify.V || _stoodDown) return;
        if (_table == IntPtr.Zero || EngineFn.FindAnimation == null || EngineFn.FindAnimScale == null)
        {
            _stoodDown = true;
            _log?.Always("ModelVerify: Models, FindAnimation or FindAnimScale did not resolve -- there is no "
                       + "oracle to compare against, so the check stands down for the session");
            return;
        }
        var table = new EngineList(_table);
        IntPtr* items = (IntPtr*)table.Items;
        int n = table.Count;
        if (items == null || n <= 0)
        {
            _log?.Info("[models] verify: the engine has no model loaded yet");
            return;
        }
        _runs++;

        // Mirror every engine model, then link the mirrors by the engine's own supermodel POINTER: the engine's
        // chain is the ground truth for the shape, and CX's lookup over it is what is under test.
        var mirror = new Dictionary<IntPtr, ModelDef>(n);
        var source = new Dictionary<AnimDef, IntPtr>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
            if (items[i] != IntPtr.Zero && !mirror.ContainsKey(items[i]))
                mirror[items[i]] = Mirror(items[i], source);
        int chained = 0;
        foreach (var kv in mirror)
        {
            IntPtr sup = new Model(kv.Key).Super.Ptr;
            if (sup != IntPtr.Zero && mirror.TryGetValue(sup, out ModelDef? up)) { kv.Value.Super = up; chained++; }
        }

        // The memo is built on mirror models, which are not in the cache's table and so are never evicted:
        // the memo entries and the memo's own counters are handed back below.
        (long hits, long misses) = ModelCache.ResolveCounters;
        long models = 0, anims = 0, pass = 0, fails = 0;
        var names = new List<string>();
        try
        {
            foreach (var kv in mirror)
            {
                models++;
                names.Clear();
                Union(kv.Key, names);
                for (int i = 0; i < names.Count; i++)
                {
                    anims++;
                    if (One(kv.Value, kv.Key, names[i], source)) pass++; else fails++;
                }
            }
        }
        finally
        {
            foreach (var kv in mirror) ModelCache.ForgetResolution(kv.Value);
            ModelCache.RestoreResolveCounters(hits, misses);
        }
        _models = models; _anims = anims; _pass = pass; _chained = chained;
        if (!_sampled && chained != 0) { _sampled = true; Sample(mirror); }
        _log?.Info($"[models] verify {(fails == 0 ? "PASS" : "FAIL")}: {models} engine models "
                 + $"({chained} with a supermodel), {anims} animation lookups, {pass} agree, {fails} differ "
                 + $"(session {_failFound} not-found, {_failScale} wrong-scale, {_failMemo} memo)");
    }

    /// <summary>Once per session, name a handful of what the table actually held and how deep their chains
    /// went: a PASS over models that all turn out to have no supermodel is not evidence about chains, and this
    /// is what says so on the line instead of leaving it to be assumed.</summary>
    private static void Sample(Dictionary<IntPtr, ModelDef> mirror)
    {
        var chained = new List<string>();
        var flat = new List<string>();
        foreach (var kv in mirror)
        {
            if (kv.Value.Super != null)
            {
                if (chained.Count >= SampleCap) continue;
                var sb = new System.Text.StringBuilder(kv.Value.Name);
                for (ModelDef? m = kv.Value.Super; m != null; m = m.Super) sb.Append(" <- ").Append(m.Name);
                sb.Append(" (").Append(kv.Value.Anims.Length).Append(" anims)");
                chained.Add(sb.ToString());
            }
            else if (flat.Count < SampleCap) flat.Add($"{kv.Value.Name} ({kv.Value.Anims.Length} anims)");
        }
        if (chained.Count != 0) _log?.Info($"[models] verify chains: {string.Join("; ", chained)}");
        if (flat.Count != 0) _log?.Info($"[models] verify no-supermodel sample: {string.Join("; ", flat)}");
    }

    /// <summary>The three fields the lookup reads, plus one <see cref="AnimDef"/> per engine <c>Animation*</c>
    /// with the pointer it came from recorded in <paramref name="source"/> -- which is what lets CX's answer be
    /// compared with the engine's by pointer.</summary>
    private static ModelDef Mirror(IntPtr p, Dictionary<AnimDef, IntPtr> source)
    {
        var m = new Model(p);
        var def = new ModelDef { Name = NameOf(p), AnimScale = m.AnimScale };
        IntPtr sup = m.Super.Ptr;
        if (sup != IntPtr.Zero) def.SuperName = NameOf(sup);
        int cnt = m.AnimCount;
        IntPtr* arr = (IntPtr*)m.Anims;
        if (arr == null || cnt <= 0) return def;
        def.Anims = new AnimDef[cnt];
        for (int i = 0; i < cnt; i++)
        {
            var a = new AnimDef { Name = NameOf(arr[i]) };
            def.Anims[i] = a;
            source[a] = arr[i];
        }
        return def;
    }

    /// <summary>One animation name, both ways: CX's port against the engine's own two functions.</summary>
    private static bool One(ModelDef cx, IntPtr eng, string name, Dictionary<AnimDef, IntPtr> source)
    {
        AnimDef? mine = ModelCache.FindAnimation(cx, name, out ModelDef? myOwner);
        IntPtr theirs;
        byte* nm = stackalloc byte[NameChars + 1];
        int k = 0;
        for (; k < NameChars && k < name.Length; k++) nm[k] = (byte)name[k];
        nm[k] = 0;
        theirs = EngineFn.FindAnimation(eng, nm);
        IntPtr mineAt = mine != null && source.TryGetValue(mine, out IntPtr at) ? at : IntPtr.Zero;
        if (mineAt != theirs)
        {
            _failFound++;
            Say($"ModelVerify: {cx.Name}/{name}: CX answers {(mine == null ? "nothing" : mine.Name + " in " + myOwner?.Name)}, "
              + $"the engine answers {(theirs == IntPtr.Zero ? "nothing" : NameOf(theirs))}");
            return false;
        }
        if (mine == null) return true;
        float ms = ModelCache.FindAnimScale(cx, mine), es = EngineFn.FindAnimScale(eng, theirs);
        if (MathF.Abs(ms - es) > ScaleEpsilon * MathF.Max(1f, MathF.Abs(es)))
        {
            _failScale++;
            Say($"ModelVerify: {cx.Name}/{name}: CX scale {ms}, the engine {es}");
            return false;
        }
        // ...and the memo the animation runtime actually reads, against the walk it was built from.
        ModelCache.AnimLookup memo = ModelCache.Resolve(cx, name);
        if (!ReferenceEquals(memo.Anim, mine) || !ReferenceEquals(memo.Owner, myOwner)
            || MathF.Abs(memo.Scale - ms) > ScaleEpsilon * MathF.Max(1f, MathF.Abs(ms)))
        {
            _failMemo++;
            Say($"ModelVerify: {cx.Name}/{name}: the resolution memo disagrees with the walk");
            return false;
        }
        return true;
    }

    /// <summary>Every animation name the engine's chain from <paramref name="eng"/> names, once each, plus
    /// <c>"default"</c> and <see cref="Absent"/> -- the two cases that exercise the terminal fallback and the
    /// refusal. Reading the arrays is the only thing this walk is for; the lookups themselves go to the
    /// engine.</summary>
    private static void Union(IntPtr eng, List<string> into)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var m = new Model(eng);
        for (int d = 0; d < MaxChain && !m.IsNull; d++)
        {
            int cnt = m.AnimCount;
            IntPtr* arr = (IntPtr*)m.Anims;
            if (arr != null)
                for (int i = 0; i < cnt; i++)
                {
                    string nm = NameOf(arr[i]);
                    if (nm.Length != 0 && seen.Add(nm)) into.Add(nm);
                }
            if (m.Super.IsNull) break;
            m = m.Super;
        }
        if (seen.Add("default")) into.Add("default");
        into.Add(Absent);
    }

    /// <summary>A <c>Model*</c>'s or <c>Animation*</c>'s name as a string -- MaxTree's field, so one reader does
    /// both. Cold path only (an area edge), so the allocation is deliberate.</summary>
    private static string NameOf(IntPtr modelOrAnim)
    {
        if (modelOrAnim == IntPtr.Zero) return "";
        byte* p = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new Animation(modelOrAnim).Name);
        int n = 0;
        while (n < NameChars && p[n] != 0) n++;
        return System.Text.Encoding.ASCII.GetString(p, n);
    }

    private static void Say(string line)
    {
        if (++_logged <= LogCap) _log?.Always(line);
    }
}
