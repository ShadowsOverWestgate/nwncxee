using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>One persisted layout row (CX-692): a <b>hoist</b> (<see cref="Fn"/> set: a leaf or submenu deep in the
/// tree spliced into the top list, validated by <c>(Fn, Param)</c> at <see cref="Path"/>), a <b>reorder</b>
/// (<see cref="Fn"/> empty: the slot row at <see cref="Path"/> fixed at <see cref="At"/> within its own list, no
/// validator -- it only repositions whatever the engine put there), or a <b>hide</b> (CX-694, <see cref="Hidden"/>:
/// the slot row at <see cref="Path"/> dropped from its list; claims no index), or a <b>folder</b> (CX-694,
/// <see cref="Folder"/> set, no path: a named submenu row in the top list whose members are the hoists with
/// <see cref="In"/> = its name). <see cref="At"/> is the row's index among the slot rows of its list, written back
/// after every compose. A hoist is copied to another root kind as a row of that kind with the same path and
/// validator ("Also show on"): the engine builds the same subtree under every world root, with that root's target.</summary>
internal sealed class RadialPin
{
    public RootKind Root;
    public int[] Path = Array.Empty<int>();
    public bool Hidden;
    /// <summary>A folder row: its name. Names are unique per root; a rename onto an existing name merges.</summary>
    public string Folder = "";
    /// <summary>The folder this hoist sits in ("" = the top list).</summary>
    public string In = "";
    /// <summary>The <see cref="PlayerIdentity.Key"/> this row belongs to (CX-694); "" = a row written before the key
    /// existed, adopted by the first identity that loads.</summary>
    public string Who = "";
    /// <summary>The Itanium name of the leaf's handler, <c>vt:&lt;off&gt;:&lt;_ZTV&gt;+&lt;n&gt;</c> for a virtual one,
    /// <c>rva:&lt;hex&gt;</c> on Windows, <see cref="RadialPins.SubToken"/> for a hoisted submenu; "" = a reorder.</summary>
    public string Fn = "";
    public long Param;
    public int At;
    /// <summary>The breadcrumb as seen at pin time (<c>Cast Spell &gt; Wizard &gt; 3 &gt; Fireball</c>): the greyed row's text.</summary>
    public string Label = "";
    /// <summary>The leaf's icon resref at pin time: a self pin has no live leaf to read one from.</summary>
    public string Icon = "";
    /// <summary>A self pin (CX-694, <c>fn=self:&lt;key&gt;</c>): fired on a stand-in of the self menu wherever it is shown,
    /// never walked; its path is the source breadcrumb only.</summary>
    public bool IsSelf => Fn.StartsWith(SelfActions.Prefix, StringComparison.Ordinal);
    /// <summary>The leaf label alone.</summary>
    public string LeafLabel { get { int cut = Label.LastIndexOf(RadialPins.Sep, StringComparison.Ordinal); return cut < 0 ? Label : Label.Substring(cut + RadialPins.Sep.Length); } }
    public bool IsHoist => Fn.Length > 0;
    public bool IsFolder => Folder.Length > 0;
    /// <summary>The list this row sits in: the top list for a hoist or folder, the parent list for a reorder.</summary>
    public ReadOnlySpan<int> ListPath => IsHoist || IsFolder ? ReadOnlySpan<int>.Empty : Path.AsSpan(0, Path.Length - 1);
    public bool SameList(RadialPin o) => Root == o.Root && In == o.In && ListPath.SequenceEqual(o.ListPath);
    /// <summary>Shares <paramref name="o"/>'s list and claims an index there (a hide claims none).</summary>
    public bool Claims(RadialPin o) => !Hidden && SameList(o);
}

/// <summary>The tag of a hoisted row: the materialized parent menu at the path and the slot in it, the pin, and the
/// validation verdict <see cref="RadialService"/> refuses on regardless of the row's Disabled flag.</summary>
internal sealed class RadialRowTag
{
    public IntPtr Menu;
    public int Slot;
    public RadialPin Pin = null!;
    public bool Valid;
}

/// <summary>
/// The persisted radial layout (CX-692, spec §4.2, ADR 0025 amendment): the <c>radial.p&lt;N&gt;.*</c> bag parsed
/// into <see cref="RadialPin"/> rows at first open, replaced whole on every change. Pure parts (parse/format, the
/// position model <see cref="Compose"/>, <see cref="Validate"/>) are self-checked; the engine-facing part is
/// <see cref="FnToken"/>, the address-to-name step a persisted handler needs: Linux <c>dladdr</c> gives the exported
/// Itanium name of the leaf's fn (or the <c>_ZTV</c> of the handler's class for a virtual thunk). Linux-only by design:
/// the Windows handlers are not exported (research §6), so there the token is the fn's RVA, compared against the
/// current tree's fn at the same path -- which is what "never fire the wrong action" needs.
/// <para>Position model: a list is its vanilla slot rows in slot order plus this root's hoists; a persisted row claims
/// index <see cref="RadialPin.At"/> (ascending, first free at or after it), the unclaimed vanilla rows fill the
/// gaps in slot order. No rows = vanilla order; reset = drop the root's rows. Leading rows with a null tag (the
/// item root's header) are anchored and not counted.</para>
/// <para>One layout per (server, character): every row carries the <see cref="PlayerIdentity.Key"/> it was made
/// under; <see cref="Load"/> keeps the current key's rows live and the rest aside, and a save writes both back.</para>
/// <para>Folders (CX-694): one level of named rows in the top list; a hoist's <see cref="RadialPin.In"/> names its
/// folder, and the folder's member list is a list of its own for the position model (<see cref="RadialPin.SameList"/>).
/// ponytail: one level -- a folder in a folder is a cascade again.</para>
/// </summary>
internal static unsafe class RadialPins
{
    public const string SubToken = "sub";
    public const string Sep = " > ";
    private static readonly List<RadialPin> _rows = new();
    /// <summary>Other identities' rows, carried through every save untouched.</summary>
    private static readonly List<RadialPin> _other = new();
    private static string? _who;
    /// <summary>Set once a key with a character has loaded: the old file's key-less rows went to it, or to no one.</summary>
    private static bool _adopted;
    private static bool _nameWarned;
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();

    public static IReadOnlyList<RadialPin> Rows => _rows;
    /// <summary>The key a new row is stamped with.</summary>
    private static string Who => _who ?? "";

    /// <summary>Parse the bag once, then on every open split the rows by <paramref name="who"/>: the current
    /// identity's rows are the live set, the rest wait in <see cref="_other"/>. Rows with no key are adopted by the
    /// first identity that loads (the pre-CX-694 file) and take its key at the next save.</summary>
    public static void Load(ExtenderLog? log, string who)
    {
        if (_who == who) return;
        if (_who == null)
        {
            var (rows, bad) = Parse(RadialKnobs.Layout.Pairs);
            _other.AddRange(rows);
            log?.Info($"[radial] pins loaded n={rows.Count} bad={bad}");
        }
        if (Split(who)) Save(log, "adopted");   // the first opener owns the old file for good, not just this session
        log?.Info($"[radial] pins for \"{who}\": n={_rows.Count} other={_other.Count}");
    }

    /// <summary>Make <paramref name="who"/>'s rows the live set. True when key-less rows were adopted.</summary>
    private static bool Split(string who)
    {
        _other.AddRange(_rows);
        _rows.Clear();
        bool adopt = !_adopted && PlayerIdentity.HasCharacter(who);   // a key with no character never adopts the old file
        bool adopted = false;
        _adopted |= adopt;
        _who = who;
        for (int i = _other.Count - 1; i >= 0; i--)
        {
            RadialPin p = _other[i];
            if (p.Who != who && !(adopt && p.Who.Length == 0)) continue;
            adopted |= p.Who.Length == 0;
            p.Who = who;
            _rows.Add(p);
            _other.RemoveAt(i);
        }
        _rows.Reverse();
        return adopted;
    }

    /// <summary>Rows of <paramref name="root"/> whose list is <paramref name="listPath"/> (empty = the top list) in
    /// folder <paramref name="in"/> ("" = not in one).</summary>
    public static List<RadialPin> ForList(RootKind root, ReadOnlySpan<int> listPath, string @in = "")
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.In == @in && p.ListPath.SequenceEqual(listPath)) r.Add(p);
        return r;
    }

    /// <summary>The top-level reorders of <paramref name="root"/> filed in a folder (CX-694): their slots leave the top list.</summary>
    public static List<RadialPin> Filed(RootKind root)
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.In.Length > 0 && !p.IsHoist) r.Add(p);
        return r;
    }

    /// <summary>The folder rows of <paramref name="root"/>, in index order.</summary>
    public static List<RadialPin> Folders(RootKind root)
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsFolder) r.Add(p);
        r.Sort((a, b) => a.At.CompareTo(b.At));
        return r;
    }

    public static RadialPin? FindFolder(RootKind root, string name)
    {
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsFolder && p.Folder == name) return p;
        return null;
    }

    /// <summary>A folder row at index <paramref name="at"/> of the top list (the rows from there shift down one).</summary>
    public static RadialPin AddFolder(RootKind root, string name, int at)
    {
        var pin = new RadialPin { Root = root, Folder = name, At = at, Who = Who };
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
        return pin;
    }

    /// <summary>Move a hoist or a top-level reorder to folder <paramref name="in"/> ("" = the top list) at index
    /// <paramref name="at"/>: it leaves its old list (the rows below shift up) and claims the index in the new one (the
    /// rows from there shift down). A folder row is never refiled (one level); a hide has no list to file in.</summary>
    public static void Refile(RadialPin pin, string @in, int at)
    {
        if (pin.IsFolder || pin.Hidden || pin.Path.Length > 1 && !pin.IsHoist) return;
        Remove(pin);
        pin.In = @in; pin.At = at;
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
    }

    /// <summary>Drop a folder row; its members return to the top list at the folder's index, in their order.</summary>
    public static int RemoveFolder(RadialPin folder)
    {
        if (!folder.IsFolder) return 0;
        var members = ForList(folder.Root, ReadOnlySpan<int>.Empty, folder.Folder);
        members.Sort((a, b) => a.At.CompareTo(b.At));
        int at = folder.At;
        Remove(folder);
        foreach (RadialPin m in members) Refile(m, "", at++);
        return members.Count;
    }

    /// <summary>Rename a folder; onto an existing name of the same root the two merge (this row goes, its members
    /// follow the name).</summary>
    public static void RenameFolder(RadialPin folder, string name)
    {
        if (!folder.IsFolder || name.Length == 0 || name == folder.Folder) return;
        foreach (RadialPin p in _rows) if (p.Root == folder.Root && p.In == folder.Folder) p.In = name;
        if (FindFolder(folder.Root, name) != null) Remove(folder); else folder.Folder = name;
    }

    /// <summary>The hoist (or the reorder / hide) at <paramref name="path"/>.</summary>
    public static RadialPin? Find(RootKind root, ReadOnlySpan<int> path, bool hoist)
    {
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsHoist == hoist && p.Path.AsSpan().SequenceEqual(path)) return p;
        return null;
    }

    /// <summary>Add a row at index <paramref name="at"/> of its list (the list's rows from there shift down one);
    /// <paramref name="in"/> files it in a folder (a hoist, or a top-level reorder: a filed one leaves the top list).</summary>
    public static RadialPin AddAt(RootKind root, int[] path, string fn, long param, string label, int at, string @in = "", string icon = "")
    {
        var pin = new RadialPin { Root = root, Path = path, Fn = fn, Param = param, At = at, Label = label, Who = Who, In = @in, Icon = icon };
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
        return pin;
    }

    /// <summary><see cref="AddAt"/> from a template: the same path, handler, param and label as a row of
    /// <paramref name="root"/>.</summary>
    public static RadialPin AddLike(RadialPin like, RootKind root, int at, string @in = "")
        => AddAt(root, like.Path, like.Fn, like.Param, like.Label, at, @in, like.Icon);

    /// <summary>Drop a row; the rows below it in its list shift up one (a hide claimed no index: nothing shifts).</summary>
    public static void Remove(RadialPin pin)
    {
        if (!_rows.Remove(pin) || pin.Hidden) return;
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At > pin.At) p.At--;
    }

    /// <summary>Move the row at <paramref name="from"/> (a slot-row index in its list) to <paramref name="to"/>: the
    /// moved row is fixed there (found or, for a vanilla row, added as a reorder), the persisted rows between the
    /// two indices shift one toward <paramref name="from"/>, flowing rows just flow around them.</summary>
    public static void Move(RootKind root, int[] path, RadialPin? moved, int from, int to)
    {
        moved ??= Find(root, path, hoist: false) ?? new RadialPin { Root = root, Path = path, Who = Who };
        if (!_rows.Contains(moved)) _rows.Add(moved);
        foreach (RadialPin p in _rows)
        {
            if (p == moved || !p.Claims(moved)) continue;
            if (from < to && p.At > from && p.At <= to) p.At--;
            else if (to < from && p.At >= to && p.At < from) p.At++;
        }
        moved.At = to;
    }

    /// <summary>Hide the slot row at <paramref name="path"/> (CX-694): its reorder, if any, goes with it.</summary>
    public static RadialPin Hide(RootKind root, int[] path)
    {
        RadialPin? old = Find(root, path, hoist: false);
        if (old != null) Remove(old);
        var pin = new RadialPin { Root = root, Path = path, Hidden = true, Who = Who };
        _rows.Add(pin);
        return pin;
    }

    /// <summary>Hides of <paramref name="root"/>'s list at <paramref name="listPath"/> (the popup's Show hidden count).</summary>
    public static int HiddenCount(RootKind root, int[] listPath)
    {
        int n = 0;
        foreach (RadialPin p in _rows) if (p.Hidden && p.Root == root && p.ListPath.SequenceEqual(listPath)) n++;
        return n;
    }

    /// <summary>Drop every hide of <paramref name="root"/>'s list at <paramref name="listPath"/>; the rows flow back.</summary>
    public static int Unhide(RootKind root, int[] listPath)
        => _rows.RemoveAll(p => p.Hidden && p.Root == root && p.ListPath.SequenceEqual(listPath));

    public static int Reset(RootKind root) => _rows.RemoveAll(p => p.Root == root);

    public static void Save(ExtenderLog? log, string what)
    {
        var all = new List<RadialPin>(_rows.Count + _other.Count);
        all.AddRange(_rows); all.AddRange(_other);
        RadialKnobs.Layout.Replace(Format(all));
        log?.Info($"[radial] pin {what} saved n={_rows.Count} other={_other.Count}");
    }

    /// <summary>Lay a list out: <paramref name="fixedRows"/> (this list's rows with their items -- a hoist's spliced
    /// item, a reorder's vanilla item) claim their indices, the rest of <paramref name="vanilla"/> flows in slot
    /// order; each row's <see cref="RadialPin.At"/> becomes the index it got. Leading null-tag rows are kept in place.</summary>
    public static CascadeItem[] Compose(CascadeItem[] vanilla, List<(RadialPin Pin, CascadeItem Item)> fixedRows)
    {
        int lead = 0;
        while (lead < vanilla.Length && vanilla[lead].Tag == null) lead++;
        var flow = new List<CascadeItem>();
        for (int i = lead; i < vanilla.Length; i++)
        {
            bool claimed = false;
            foreach (var f in fixedRows)
                if (!f.Pin.IsHoist && vanilla[i].Tag is int s && f.Pin.Path.Length > 0 && s == f.Pin.Path[^1]) { claimed = true; break; }
            if (!claimed) flow.Add(vanilla[i]);
        }
        int n = flow.Count + fixedRows.Count;
        var result = new CascadeItem[lead + n];
        Array.Copy(vanilla, result, lead);
        var taken = new bool[n];
        var order = new int[fixedRows.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => fixedRows[a].Pin.At != fixedRows[b].Pin.At ? fixedRows[a].Pin.At.CompareTo(fixedRows[b].Pin.At) : a.CompareTo(b));
        foreach (int i in order)
        {
            var (pin, item) = fixedRows[i];
            int t = Math.Clamp(pin.At, 0, n - 1), k = t;
            while (k < n && taken[k]) k++;
            if (k >= n) { k = t; while (taken[k]) k--; }
            taken[k] = true;
            result[lead + k] = item;
            pin.At = k;
        }
        for (int k = 0, f = 0; k < n; k++) if (!taken[k]) result[lead + k] = flow[f++];
        return result;
    }

    /// <summary>Bit 37 of a cast-leaf param: set by <c>CreateSpellSubRadial</c> @0x6f0590 only while a spontaneous
    /// caster has no uses of that spell left, cleared by <c>SetButtonSpellMenuID</c> @0x6aac50 as transient -- so it
    /// is no part of a leaf's identity and is masked on both sides of a compare.</summary>
    public const long TransientBit = 1L << 37;

    /// <summary>A hoist matches the leaf at its path when the packed param is equal (less <see cref="TransientBit"/>)
    /// and the handler is the same function: both params are read raw from a slot, so equality is exact.</summary>
    public static bool Validate(RadialPin pin, in RadialSlot s)
    {
        if (!pin.IsHoist || (s.Param & ~TransientBit) != (pin.Param & ~TransientBit)) return false;
        string fn = pin.Fn;
        if (fn.StartsWith(SubToken, StringComparison.Ordinal))
        {
            if (!s.IsSubMenu) return false;
            if (fn.Length == SubToken.Length) return s.Fn == IntPtr.Zero;   // a submenu with no entry: nothing else to fire
            fn = fn.Substring(SubToken.Length + 1);
        }
        else if (s.IsSubMenu || s.Kind != SlotKind.Entry) return false;
        if (s.Fn == IntPtr.Zero) return false;
        if (OperatingSystem.IsWindows() || fn.StartsWith("vt:", StringComparison.Ordinal) || fn.StartsWith("rva:", StringComparison.Ordinal))
            return fn == EntryToken(s);
        return SymbolMap.Resolve(fn, null) == s.Fn;
    }

    /// <summary>The persistable identity of a slot's handler: <see cref="SubToken"/> for a submenu, suffixed
    /// <c>:&lt;entry&gt;</c> when the slot also carries an entry (vanilla fires it on the descent, so it is part of what
    /// the pin may invoke); else the entry's <see cref="EntryToken"/>. "" = not persistable.</summary>
    public static string FnToken(in RadialSlot s)
    {
        if (s.IsSubMenu)
        {
            if (s.Fn == IntPtr.Zero) return SubToken;
            string e = EntryToken(s);
            return e.Length == 0 ? "" : SubToken + ":" + e;
        }
        return s.Kind == SlotKind.Entry ? EntryToken(s) : "";
    }

    /// <summary>The fn's exported name (Linux, exact <c>dladdr</c> hit only; a virtual thunk names the handler's
    /// <c>_ZTV</c> and the vptr's offset into it), or its RVA on Windows. "" = an entry-less slot or an unnamed fn.</summary>
    private static string EntryToken(in RadialSlot s)
    {
        if (s.Fn == IntPtr.Zero) return "";
        if (OperatingSystem.IsWindows()) return $"rva:{(long)s.Fn - (long)Base:x}";
        if (((long)s.Fn & 1) != 0)
        {
            if (s.Obj == IntPtr.Zero) return "";
            IntPtr vptr = *(IntPtr*)(s.Obj + s.Adj);
            if (Dladdr(vptr, out DlInfo vi) == 0 || vi.SName == IntPtr.Zero) return "";
            return $"vt:{(long)s.Fn - 1:x}:{Marshal.PtrToStringAnsi(vi.SName)}+{(long)vptr - (long)vi.SAddr:x}";
        }
        if (Dladdr(s.Fn, out DlInfo info) == 0 || info.SName == IntPtr.Zero || info.SAddr != s.Fn) return "";
        return Marshal.PtrToStringAnsi(info.SName) ?? "";
    }

    /// <summary>Log once that a handler could not be named (the pin is refused, never stored by address).</summary>
    public static void WarnUnnamed(ExtenderLog? log, in RadialSlot s)
    {
        if (_nameWarned) return;
        _nameWarned = true;
        log?.Info($"[radial] pin refused: handler fn=0x{(long)s.Fn:x} has no exported name (logged once)");
    }

    public static string PathText(ReadOnlySpan<int> path)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < path.Length; i++) { if (i > 0) sb.Append('/'); sb.Append(path[i]); }
        return sb.ToString();
    }

    public static string Breadcrumb(IReadOnlyList<string> labels) => string.Join(Sep, labels).Replace("\n", "").Replace("\r", "");

    /// <summary>The greyed row for an unresolved hoist: the leaf label, the parents' breadcrumb in the count column
    /// (the disabled alpha mutes both), disabled, tag invalid.</summary>
    public static CascadeItem Greyed(RadialPin pin)
    {
        int cut = pin.Label.LastIndexOf(Sep, StringComparison.Ordinal);
        string leaf = pin.LeafLabel;
        string parents = cut < 0 ? "" : "(" + pin.Label.Substring(0, cut) + ")";
        return new CascadeItem(leaf.Length > 0 ? leaf : "?", disabled: true, hasChildren: pin.Fn.StartsWith(SubToken, StringComparison.Ordinal), count: parents,
            tag: new RadialRowTag { Pin = pin, Slot = -1, Valid = false });
    }

    // ---- rows <-> pairs -------------------------------------------------------------------------

    /// <summary>Parse the bag: rows grouped by <c>p&lt;N&gt;</c>; a row missing root/at, with an unparsable field, a
    /// slot outside 0..7, a reorder or hide with an empty path, a hoist with a one-step path, a hidden hoist, a folder
    /// row with a path, fn or hide, or an <c>in</c> on a hide, a folder or a deeper reorder is skipped and counted; a
    /// member of a folder row that is not in the file is un-filed.</summary>
    public static (List<RadialPin> Rows, int Bad) Parse(KeyValuePair<string, string>[] pairs)
    {
        var byN = new SortedDictionary<int, Dictionary<string, string>>();
        int bad = 0;
        foreach (var kv in pairs)
        {
            string k = kv.Key;
            int dot = k.IndexOf('.');
            if (k.Length < 3 || k[0] != 'p' || dot < 2 || !int.TryParse(k.AsSpan(1, dot - 1), out int n)) { bad++; continue; }
            if (!byN.TryGetValue(n, out var f)) byN[n] = f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            f[k.Substring(dot + 1)] = kv.Value;
        }
        var rows = new List<RadialPin>();
        foreach (var f in byN.Values)
        {
            if (!f.TryGetValue("root", out string? root) || !Enum.TryParse(root, true, out RootKind kind) || kind == RootKind.Unknown
                || !f.TryGetValue("at", out string? ats) || !int.TryParse(ats, out int at) || at < 0)
            { bad++; continue; }
            string folder = f.TryGetValue("folder", out string? fo) ? fo.Trim() : "";
            string @in = f.TryGetValue("in", out string? ins) ? ins.Trim() : "";
            string ps = f.TryGetValue("path", out string? pv) ? pv : "";
            string[] parts = ps.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var path = new int[parts.Length];
            bool okPath = parts.Length > 0 || folder.Length > 0;
            for (int i = 0; i < parts.Length && okPath; i++) okPath = int.TryParse(parts[i], out path[i]) && (uint)path[i] < RadialTree.SlotCount;
            string fn = f.TryGetValue("fn", out string? fs) ? fs.Trim() : "";
            long param = 0;
            bool okParam = !f.TryGetValue("param", out string? prm) || TryHex(prm, out param);
            bool hidden = f.TryGetValue("hide", out string? hs) && hs.Trim() == "1";
            bool self = fn.StartsWith(SelfActions.Prefix, StringComparison.Ordinal);   // a self pin is never walked, so a one-step source path is fine
            if (!okPath || !okParam || (fn.Length > 0 && !self && path.Length < 2) || (hidden && fn.Length > 0)
                || (folder.Length > 0 && (path.Length > 0 || fn.Length > 0 || hidden)) || (@in.Length > 0 && (hidden || folder.Length > 0 || fn.Length == 0 && path.Length > 1))) { bad++; continue; }
            rows.Add(new RadialPin { Root = kind, Path = path, Fn = fn, Param = param, At = at, Hidden = hidden, Label = f.TryGetValue("label", out string? l) ? l : "",
                                     Who = f.TryGetValue("who", out string? w) ? w : "", Folder = folder, In = @in, Icon = f.TryGetValue("icon", out string? ic) ? ic.Trim() : "" });
        }
        foreach (RadialPin p in rows)   // a member whose folder row is gone (a hand edit) returns to the top list rather than vanish
            if (p.In.Length > 0 && !rows.Exists(f => f.IsFolder && f.Root == p.Root && f.Who == p.Who && f.Folder == p.In)) p.In = "";
        return (rows, bad);
    }

    public static List<KeyValuePair<string, string>> Format(List<RadialPin> rows)
    {
        var o = new List<KeyValuePair<string, string>>(rows.Count * 7);
        for (int i = 0; i < rows.Count; i++)
        {
            RadialPin p = rows[i];
            string k = "p" + i + ".";
            o.Add(new(k + "root", p.Root.ToString()));
            if (p.Path.Length > 0) o.Add(new(k + "path", PathText(p.Path)));
            o.Add(new(k + "at", p.At.ToString()));
            if (p.IsFolder) o.Add(new(k + "folder", p.Folder));
            if (p.In.Length > 0) o.Add(new(k + "in", p.In));
            if (p.IsHoist)
            {
                o.Add(new(k + "fn", p.Fn));
                o.Add(new(k + "param", "0x" + ((ulong)p.Param).ToString("x")));
            }
            if (p.Hidden) o.Add(new(k + "hide", "1"));
            if (p.Label.Length > 0) o.Add(new(k + "label", p.Label));
            if (p.Icon.Length > 0) o.Add(new(k + "icon", p.Icon));
            if (p.Who.Length > 0) o.Add(new(k + "who", p.Who));
        }
        return o;
    }

    private static bool TryHex(string s, out long v)
    {
        v = 0;
        ReadOnlySpan<char> t = s.AsSpan().Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Slice(2);
        bool ok = ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong u);
        v = (long)u;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo { public IntPtr FName, FBase, SName, SAddr; }

    /// <summary>Linux only (never bound on Windows, where <see cref="FnToken"/> takes the RVA branch first).</summary>
    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int Dladdr(IntPtr addr, out DlInfo info);

    // ---- self-check ----------------------------------------------------------------------------

    /// <summary>Off-device: the row parse/format round-trip with garbage rows counted, the position model
    /// (pin / unpin / move incl. a far move / hide / reset and the compose invariant <c>compose(rows)[row.At] == row</c>), the splice over
    /// fake rows with an anchored header, the greyed row's text, and validation on fake <c>(fn, param)</c> pairs
    /// (a real exported address on Linux, RVA arithmetic on Windows). Nothing engine-side.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var pairs = new[]
        {
            new KeyValuePair<string, string>("p0.root", "Creature"), new("p0.path", "1/4/3/0"), new("p0.fn", "_ZN16CClientSpellMenu14RadialMenuCastEl"),
            new("p0.param", "0x1300007a"), new("p0.at", "0"), new("p0.label", "Cast Spell > Wizard > 3 > Fireball"),
            new("p1.root", "Creature"), new("p1.path", "7"), new("p1.at", "1"),
            new("p2.root", "Bogus"), new("p2.path", "1"), new("p2.at", "0"),
            new("p3.root", "Door"), new("p3.path", "9"), new("p3.at", "0"),
            new("p4.root", "Door"), new("p4.path", "2"), new("p4.at", "x"),
            new("p5.root", "Door"), new("p5.path", "2"), new("p5.fn", "x"), new("p5.at", "0"),
            new("junk", "1"),
            new("p6.root", "Placeable"), new("p6.path", "3/1"), new("p6.fn", SubToken), new("p6.param", "0xffffffffffffffff"), new("p6.at", "2"),
        };
        var (rows, bad) = Parse(pairs);
        check.Expect(rows.Count == 3 && bad == 5, $"parse keeps 3 rows, counts 5 bad ({rows.Count}/{bad})");
        check.Expect(rows.Count == 3 && rows[0].IsHoist && rows[0].Path.Length == 4 && rows[0].Param == 0x1300007a && rows[0].Label.EndsWith("Fireball")
          && !rows[1].IsHoist && rows[1].Path[0] == 7 && rows[1].ListPath.Length == 0 && rows[2].Param == -1 && rows[2].Fn == SubToken,
          "parsed fields: hoist/reorder, path, hex param incl. negative, label");
        var again = Parse(Format(rows).ToArray());
        check.Expect(again.Bad == 0 && again.Rows.Count == 3 && Format(again.Rows).Count == Format(rows).Count
          && again.Rows[0].Fn == rows[0].Fn && again.Rows[2].Param == -1 && again.Rows[1].At == 1, "format/parse round-trip is lossless");
        check.Expect(rows[0].SameList(rows[1]) && !rows[0].SameList(rows[2]), "a hoist and a top-level reorder share the top list");

        CascadeItem[] V(params int[] slots) { var a = new CascadeItem[slots.Length]; for (int i = 0; i < slots.Length; i++) a[i] = new CascadeItem("s" + slots[i], tag: slots[i]); return a; }
        string Order(CascadeItem[] a) { var sb = new StringBuilder(); foreach (var it in a) sb.Append(it.Label).Append(' '); return sb.ToString().TrimEnd(); }
        var top = V(0, 1, 2, 3);
        check.Expect(Order(Compose(top, new())) == "s0 s1 s2 s3", "no rows = vanilla order");

        // Position model on a private row set.
        _rows.Clear();
        var fire = new CascadeItem("Fireball", tag: new RadialRowTag { Valid = true });
        var meta = new CascadeItem("Meta", hasChildren: true, tag: new RadialRowTag { Valid = true });
        RadialPin? pm = null;
        RadialPin pf = AddAt(RootKind.Creature, new[] { 1, 4, 3, 0 }, "fn", 7, "Cast Spell > Fireball", 0);
        List<(RadialPin, CascadeItem)> Fixed(CascadeItem[] vanilla)
        {
            var f = new List<(RadialPin, CascadeItem)>();
            foreach (RadialPin p in ForList(RootKind.Creature, ReadOnlySpan<int>.Empty))
            {
                if (p.IsHoist) { f.Add((p, p == pm ? meta : fire)); continue; }
                foreach (var it in vanilla) if (it.Tag is int s && s == p.Path[^1]) { f.Add((p, it)); break; }
            }
            return f;
        }
        List<(RadialPin, CascadeItem)> last = new();
        CascadeItem[] Run(CascadeItem[] vanilla) => Compose(vanilla, last = Fixed(vanilla));
        bool Invariant(CascadeItem[] composed, int lead)
        {
            foreach (var (p, item) in last) if (!ReferenceEquals(composed[lead + p.At].Tag, item.Tag)) return false;
            return true;
        }
        var c = Run(top);
        check.Expect(Order(c) == "Fireball s0 s1 s2 s3" && pf.At == 0 && Invariant(c, 0), $"pin to top splices at 0 ({Order(c)})");
        // Move s2 (index 3) up: becomes a reorder fixed at 2, s1 flows to 3.
        Move(RootKind.Creature, new[] { 2 }, null, 3, 2);
        c = Run(top);
        check.Expect(Order(c) == "Fireball s0 s2 s1 s3" && Invariant(c, 0), $"move up fixes the row, the neighbour flows back ({Order(c)})");
        // Move s0 (index 1) up past the pin (index 0): the pin is persisted, so it takes index 1.
        Move(RootKind.Creature, new[] { 0 }, null, 1, 0);
        c = Run(top);
        check.Expect(Order(c) == "s0 Fireball s2 s1 s3" && pf.At == 1 && Invariant(c, 0), $"move past a persisted row swaps their indices ({Order(c)})");
        // A second pin to top shifts every row of the list down one.
        pm = AddAt(RootKind.Creature, new[] { 6, 2 }, SubToken, 9, "Abilities > Meta", 0);
        c = Run(top);
        check.Expect(Order(c) == "Meta s0 Fireball s2 s1 s3" && pf.At == 2 && Invariant(c, 0), $"second pin lands at 0, the rest shift ({Order(c)})");
        // Unpin the first: rows below shift up.
        Remove(pf);
        c = Run(top);
        check.Expect(Order(c) == "Meta s0 s2 s1 s3" && pm.At == 0 && Invariant(c, 0), $"unpin shifts the rows below up ({Order(c)})");
        // A target where slot 2 is absent: its reorder row is skipped, the rest keep their places.
        var fewer = V(0, 1, 3);
        c = Run(fewer);
        check.Expect(Order(c) == "Meta s0 s1 s3", $"a reorder whose slot is absent is skipped ({Order(c)})");
        // Anchored header at 0, never counted; a garbage At past the end clamps to the last index (s0's reorder holds 1).
        var headed = new CascadeItem[] { new("Header"), new("s0", tag: 0), new("s1", tag: 1) };
        pm.At = 99;
        c = Run(headed);
        check.Expect(Order(c) == "Header s1 s0 Meta" && pm.At == 2 && Invariant(c, 1), $"header anchored, over-range At clamps ({Order(c)})");
        // Two rows claiming one index: the earlier row (lower N) wins, the later takes the next free.
        pm.At = 0;
        var pr = Find(RootKind.Creature, new[] { 2 }, hoist: false)!;
        pr.At = 0;
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta s0 s1 s3" && pr.At == 0 && pm.At == 1, $"a tie pushes the later row forward ({Order(c)})");
        // A far move (CX-694 drop): s3 (index 4) to index 1; the persisted rows in (1..4] shift toward the source, flows fill.
        Move(RootKind.Creature, new[] { 3 }, null, 4, 1);
        c = Run(top);
        check.Expect(Order(c) == "s2 s3 Meta s0 s1" && pm.At == 2 && Invariant(c, 0), $"a far move shifts the persisted rows it crosses ({Order(c)})");
        Move(RootKind.Creature, new[] { 3 }, null, 1, 4);
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta s0 s1 s3" && pm.At == 1 && Invariant(c, 0), $"and back ({Order(c)})");
        // A hoist dropped mid-list lands there; the rows from that index shift down.
        RadialPin pd = AddAt(RootKind.Creature, new[] { 1, 4, 3, 0 }, "fn", 7, "Cast Spell > Fireball", 2);
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta Fireball s0 s1 s3" && pd.At == 2 && pm.At == 1 && Invariant(c, 0), $"a hoist at 2 splices at 2 ({Order(c)})");
        Remove(pd);
        check.Expect(Reset(RootKind.Creature) == 4 && _rows.Count == 0 && Order(Run(top)) == "s0 s1 s2 s3", "reset drops the root's rows, vanilla order returns");
        check.Expect(Find(RootKind.Creature, new[] { 2 }, hoist: false) == null, "find misses after reset");

        // Hide (CX-694): the pin claims no index, replaces the row's reorder, survives the round-trip, and is dropped
        // per list by Unhide. The row filter itself is the service's (ItemsFor); here the bookkeeping.
        Move(RootKind.Creature, new[] { 1 }, null, 1, 0);
        RadialPin ph = Hide(RootKind.Creature, new[] { 1 });
        check.Expect(ph.Hidden && !ph.IsHoist && Find(RootKind.Creature, new[] { 1 }, hoist: false) == ph && _rows.Count == 1, "hide replaces the row's reorder");
        RadialPin pt = AddAt(RootKind.Creature, new[] { 3 }, "", 0, "", 0);
        check.Expect(ph.At == 0 && pt.At == 0, "a hide is not shifted by an insert");
        Remove(pt);
        check.Expect(_rows.Count == 1 && ph.At == 0, "and a hide's removal shifts nothing");
        var hp = Parse(Format(_rows).ToArray());
        check.Expect(hp.Bad == 0 && hp.Rows.Count == 1 && hp.Rows[0].Hidden && hp.Rows[0].Path[0] == 1, "hide round-trips as hide=1");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.fn", "x"), new("p0.at", "0"), new("p0.hide", "1") }).Bad == 1, "a hidden hoist is garbage");
        Hide(RootKind.Creature, new[] { 6, 2 });
        check.Expect(Unhide(RootKind.Creature, Array.Empty<int>()) == 1 && _rows.Count == 1 && _rows[0].Path.Length == 2, "unhide drops the top list's hides only");
        Reset(RootKind.Creature);

        // Folders (CX-694): a folder row and its members are lists of their own; refile / remove / rename / merge;
        // the rows round-trip; a cross-root copy is an ordinary hoist of the other kind.
        RadialPin fa = AddFolder(RootKind.Creature, "Buffs", 0);
        RadialPin m1 = AddAt(RootKind.Creature, new[] { 1, 2 }, "fn", 1, "Cast > Bless", 0, "Buffs");
        RadialPin m2 = AddAt(RootKind.Creature, new[] { 1, 3 }, "fn", 2, "Cast > Aid", 0, "Buffs");
        RadialPin t1 = AddAt(RootKind.Creature, new[] { 6, 1 }, "fn", 3, "Abilities > Rage", 0);
        check.Expect(fa.IsFolder && !fa.IsHoist && fa.ListPath.Length == 0 && m1.SameList(m2) && !m1.SameList(fa) && !m1.SameList(t1) && fa.SameList(t1),
            "a folder sits in the top list; its members share a list of their own");
        check.Expect(m2.At == 0 && m1.At == 1 && t1.At == 0 && fa.At == 1 && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 2
          && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty, "Buffs").Count == 2 && Folders(RootKind.Creature).Count == 1, "members claim indices in the folder, not the top list");
        Refile(m1, "", 0);
        check.Expect(m1.In == "" && m1.At == 0 && t1.At == 1 && fa.At == 2 && m2.At == 0, "refile to the top list shifts the top rows down and the folder's up");
        Refile(m1, "Buffs", 1);
        check.Expect(m1.In == "Buffs" && m1.At == 1 && m2.At == 0 && t1.At == 0 && fa.At == 1, "and back");
        RadialPin r4 = AddAt(RootKind.Creature, new[] { 4 }, "", 0, "Attack", 0, "Buffs");
        check.Expect(r4.In == "Buffs" && !r4.IsHoist && r4.At == 0 && m2.At == 1 && Filed(RootKind.Creature).Count == 1 && Filed(RootKind.Creature)[0] == r4
          && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 2, "a top-level reorder files in a folder: a member there, filed off the top list");
        var fp = Parse(Format(_rows).ToArray());
        check.Expect(fp.Bad == 0 && fp.Rows.Count == 5 && fp.Rows[0].IsFolder && fp.Rows[0].Folder == "Buffs" && fp.Rows[0].Path.Length == 0 && fp.Rows[1].In == "Buffs"
          && fp.Rows[4].In == "Buffs" && !fp.Rows[4].IsHoist && fp.Rows[4].Label == "Attack" && Format(fp.Rows).Count == Format(_rows).Count, "folder and in round-trip, on a filed reorder too");
        Refile(r4, "", 0);
        check.Expect(r4.In == "" && r4.At == 0 && t1.At == 1 && Filed(RootKind.Creature).Count == 0, "a filed reorder refiles to the top list");
        RadialPin h4 = Hide(RootKind.Creature, new[] { 4 });
        Refile(h4, "Buffs", 0);
        check.Expect(!_rows.Contains(r4) && h4.In == "" && Filed(RootKind.Creature).Count == 0, "hiding the slot drops its reorder; a hide never files");
        Unhide(RootKind.Creature, Array.Empty<int>());
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.fn", "x"), new("p0.in", "gone"), new("p0.at", "0") }).Rows[0].In == "",
            "a member of a missing folder is un-filed");
        var sp = Parse(Format(new List<RadialPin> { new() { Root = RootKind.Door, Path = new[] { 0 }, Fn = SelfActions.Prefix + "sheet", Label = "Character sheet", Icon = "ir_charsheet", At = 0 } }).ToArray());
        check.Expect(sp.Bad == 0 && sp.Rows.Count == 1 && sp.Rows[0].IsSelf && sp.Rows[0].Icon == "ir_charsheet" && sp.Rows[0].LeafLabel == "Character sheet" && sp.Rows[0].IsHoist,
            "a self pin with a one-step path and an icon round-trips as a hoist");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "0"), new("p0.fn", "x"), new("p0.at", "0") }).Bad == 1, "a one-step path is still garbage for a walked hoist");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.folder", "x"), new("p0.path", "1"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.in", "x"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1"), new("p0.in", "x"), new("p0.hide", "1"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.at", "0") }).Bad == 1, "a folder with a path, an in on a deeper reorder or a hide, a row with neither: garbage");
        RadialPin fb = AddFolder(RootKind.Creature, "Debuffs", 0);
        Refile(t1, "Debuffs", 0);
        RenameFolder(fa, "Debuffs");
        check.Expect(FindFolder(RootKind.Creature, "Buffs") == null && Folders(RootKind.Creature).Count == 1 && m1.In == "Debuffs" && m2.In == "Debuffs" && t1.In == "Debuffs",
            "rename onto an existing name merges: one folder, every member follows");
        RenameFolder(fb, "All");
        check.Expect(fb.Folder == "All" && m1.In == "All" && FindFolder(RootKind.Creature, "All") == fb, "rename moves the members with the name");
        RenameFolder(fb, "");
        check.Expect(fb.Folder == "All", "an empty rename is nothing");
        check.Expect(RemoveFolder(fb) == 3 && Folders(RootKind.Creature).Count == 0 && m1.In == "" && t1.In == "" && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 3,
            "remove folder returns its members to the top list");
        check.Expect(RemoveFolder(m1) == 0, "removing a non-folder is nothing");
        RadialPin cp = AddAt(RootKind.Placeable, m1.Path, m1.Fn, m1.Param, m1.Label, 0);
        check.Expect(cp.Root == RootKind.Placeable && Find(RootKind.Placeable, m1.Path, hoist: true) == cp && Find(RootKind.Creature, m1.Path, hoist: true) == m1 && !cp.SameList(m1),
            "a copy on another root is that root's hoist at the same path");
        Reset(RootKind.Creature); Reset(RootKind.Placeable);
        check.Expect(_rows.Count == 0, "reset drops folders and members too");

        // Greyed row text.
        var g = Greyed(new RadialPin { Root = RootKind.Creature, Fn = "x", Label = "Cast Spell > Wizard > 3 > Fireball" });
        check.Expect(g.Label == "Fireball" && g.Count == "(Cast Spell > Wizard > 3)" && g.Disabled && g.Tag is RadialRowTag { Valid: false },
            $"greyed row: leaf label, parents as the count, disabled, invalid tag (\"{g.Label}\" \"{g.Count}\")");
        check.Expect(Greyed(new RadialPin { Fn = "x", Label = "" }).Label == "?", "a label-less greyed row still has text");

        // Validation on fake (fn, param) pairs.
        var sub = new RadialSlot(0, SlotKind.Creator, "", "", "", false, 9, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        check.Expect(Validate(new RadialPin { Fn = SubToken, Param = 9 }, sub) && !Validate(new RadialPin { Fn = SubToken, Param = 8 }, sub)
          && !Validate(new RadialPin { Fn = "", Param = 9 }, sub), "a submenu hoist validates on kind + param; a reorder never validates");
        check.Expect(FnToken(sub) == SubToken && FnToken(default) == "", "token: sub for a submenu, empty for an empty slot");
        IntPtr fn = OperatingSystem.IsWindows() ? Base + 0x1000 : SymbolMap.Resolve("malloc", null);
        if (fn != IntPtr.Zero)
        {
            var leaf = new RadialSlot(0, SlotKind.Entry, "", "", "", false, 0x1300007a, IntPtr.Zero, fn, IntPtr.Zero, IntPtr.Zero);
            string tok = FnToken(leaf);
            check.Expect(OperatingSystem.IsWindows() ? tok == "rva:1000" : tok.Length > 0 && SymbolMap.Resolve(tok, null) == fn,
                $"token names the exported fn and the name resolves back to it (\"{tok}\")");
            var pin = new RadialPin { Fn = tok, Param = 0x1300007a };
            check.Expect(Validate(pin, leaf), "a matching (fn, param) validates");
            check.Expect(!Validate(new RadialPin { Fn = tok, Param = 0x1300007b }, leaf), "a different param (another class slot) does not");
            check.Expect(Validate(pin, leaf with { Param = 0x1300007a | TransientBit }) && Validate(new RadialPin { Fn = tok, Param = 0x1300007a | TransientBit }, leaf),
                "the transient bit (spontaneous caster out of uses) is no part of the identity");
            var off = leaf with { Fn = fn + 2 };
            check.Expect(!Validate(pin, off) && (OperatingSystem.IsWindows() || FnToken(off) == ""), "a different fn does not; an inexact address has no name");
            check.Expect(FnToken(leaf with { Fn = fn + 1 }) == "", "an odd fn with no handler object is not persistable");
            var subEntry = sub with { Fn = fn };
            string st = FnToken(subEntry);
            check.Expect(st == SubToken + ":" + tok && Validate(new RadialPin { Fn = st, Param = 9 }, subEntry)
              && !Validate(new RadialPin { Fn = SubToken, Param = 9 }, subEntry) && !Validate(new RadialPin { Fn = st, Param = 9 }, sub)
              && !Validate(new RadialPin { Fn = st, Param = 9 }, subEntry with { Fn = fn + 2 }),
              $"a submenu with an entry carries the entry in its token and validates on both (\"{st}\")");
            check.Expect(!Validate(new RadialPin { Fn = "_ZNoSuchSymbolEv", Param = 0x1300007a }, leaf), "a name that no longer resolves does not");
        }

        // Identity (CX-694): the file's rows split by key on load; a save writes every identity back; key-less
        // rows are adopted by the first identity to load.
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        var file = Format(new List<RadialPin>
        {
            new() { Root = RootKind.Creature, Path = new[] { 1 }, At = 0, Who = "s|a" },
            new() { Root = RootKind.Creature, Path = new[] { 2 }, At = 0, Who = "s|b" },
            new() { Root = RootKind.Creature, Path = new[] { 3 }, At = 1 },
        });
        check.Expect(file.Count == 3 * 3 + 2 && Parse(file.ToArray()).Rows[0].Who == "s|a", "who round-trips, absent when empty");
        var (rl, _) = Parse(file.ToArray());
        check.Expect(!Split("s|z") && _rows.Count == 0, "no rows, nothing adopted");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(rl);
        check.Expect(Split("s|a") && _rows.Count == 2 && _other.Count == 1 && _rows[0].Path[0] == 1 && _rows[1].Path[0] == 3 && _rows[1].Who == "s|a" && _other[0].Who == "s|b",
            "the first identity takes its own rows and adopts the key-less one, in file order");
        Hide(RootKind.Creature, new[] { 5 });
        check.Expect(_rows[2].Who == "s|a", "a new row carries the current key");
        check.Expect(!Split("s|b") && _rows.Count == 1 && _other.Count == 3 && _rows[0].Path[0] == 2, "switching identity swaps the live set, drops nothing");
        Split("s|c");
        check.Expect(_rows.Count == 0 && _other.Count == 4, "an unknown identity starts empty; nothing is adopted twice");
        List<RadialPin> all;
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(Parse(file.ToArray()).Rows);
        check.Expect(!Split("|") && _rows.Count == 0 && _other.Count == 3, "a key with no character adopts nothing");
        check.Expect(Split("s|a") && _rows.Count == 2, "and the next key with a character still adopts");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(Parse(file.ToArray()).Rows); Split("s|a");
        Hide(RootKind.Creature, new[] { 5 });
        all = new List<RadialPin>(_other); all.AddRange(_rows);
        check.Expect(Format(all).Count == 4 * 3 + 4 + 1, "a save writes every identity's rows");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        log.Info(check.Ok ? "RadialPins.SelfCheck: passed" : "RadialPins.SelfCheck: FAILED");
    }
}
