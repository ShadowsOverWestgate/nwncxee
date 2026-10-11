#!/usr/bin/env python3
"""Emit managed/Alloy/Seam/SeamVtables.g.cs from tools/seam/aurora-seam-vtables.tsv.

The TSV is the measurement (461 rows, both OSes); this turns it into the per-OS slot order the
seam's vtable builder places registered implementations into. Regenerate with
    python3 tools/seam/gen-vtables.py
from the repo root and commit the result; never edit the .g.cs by hand.
"""
import csv, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TSV = os.path.join(ROOT, "tools/seam/aurora-seam-vtables.tsv")
OUT = os.path.join(ROOT, "managed/Alloy/Seam/SeamVtables.g.cs")

THUNK = re.compile(r"^_ZT(?:hn|v0_n)\d+_(.*)$")
DEMANGLED_THUNK = re.compile(r"^(?:non-virtual|virtual) thunk to ")


def canon(mangled):
    """The implementation a slot points at, with any this-adjusting thunk stripped: two slots that
    reach the same implementation share one logical id, so one registration fills both."""
    m = THUNK.match(mangled)
    return "_Z" + m.group(1) if m else mangled


def base_name(dem):
    """`Gob::HitCheck(Vector, ...)` -> `HitCheck(Vector, ...)`: the key that matches a derived class's
    override to the base slot it occupies."""
    d = DEMANGLED_THUNK.sub("", dem)
    i = d.find("::")
    return d[i + 2:] if i >= 0 else d


def win_slot(cell):
    """The Windows index, and whether the slot's Windows home is a different vftable of the class."""
    if not cell or cell == "--":
        return None, False
    s, _, home = cell.partition("@")
    try:
        return int(s), bool(home)
    except ValueError:
        return None, False


rows = []
with open(TSV, newline="") as f:
    for r in csv.DictReader(f, delimiter="\t"):
        rows.append(r)

by_iface = {}
for r in rows:
    by_iface.setdefault(r["iface"], []).append(r)

# ---- logical slots: one per distinct implementation, in first-seen order ----
logical = {}   # canon mangled -> id
lname, lbyval = [], []


def logical_id(r):
    c = canon(r["linux_mangled"])
    if c not in logical:
        logical[c] = len(lname)
        lname.append(DEMANGLED_THUNK.sub("", r["linux_symbol_demangled"]))
        lbyval.append(r["returns_by_value"] != "no")
    return logical[c]


# ---- the six tables ----
# Each entry: (name, linux rows in slot order, windows slot count, offset-to-top)
gob_rows = sorted(by_iface["CAurObject"] + by_iface["Gob-only"], key=lambda r: int(r["slot_index"]))
scene_rows = sorted(by_iface["CAurScene"] + by_iface["Scene-only"], key=lambda r: int(r["slot_index"]))
cam_rows = sorted(by_iface["CAurCamera"] + by_iface["Camera-only"], key=lambda r: int(r["slot_index"]))
str_rows = sorted(by_iface["CAurString"], key=lambda r: int(r["slot_index"]))
sgo_rows = sorted(by_iface["StringGob-only"], key=lambda r: int(r["slot_index"]))

# Gob slot index by override key, for deriving a derived class's primary table.
gob_by_key = {base_name(r["linux_symbol_demangled"]): int(r["slot_index"]) for r in gob_rows}
# the two destructor slots key the same; keep both explicitly
gob_dtor = [int(r["slot_index"]) for r in gob_rows if int(r["slot_index"]) < 2]


def derived_primary(base_rows, own_rows, own_first, extra_rows):
    """A derived class's primary table: the base's slots, with the derived class's overrides patched in,
    plus the derived class's own slots appended. `extra_rows` are rows (from the secondary interface
    table) whose implementation overrides a base slot -- GCC duplicates those into the primary."""
    slots = {int(r["slot_index"]): logical_id(r) for r in base_rows}
    for r in extra_rows:
        k = base_name(r["linux_symbol_demangled"])
        i = int(r["slot_index"])
        if i < 2:                      # the destructor pair
            slots[i] = logical_id(r)
        elif k in gob_by_key:
            slots[gob_by_key[k]] = logical_id(r)
    for r in own_rows:
        slots[int(r["slot_index"])] = logical_id(r)
    n = max(slots) + 1
    return [slots.get(i, -1) for i in range(n)]


def primary(base_rows):
    slots = {int(r["slot_index"]): logical_id(r) for r in base_rows}
    n = max(slots) + 1
    return [slots.get(i, -1) for i in range(n)]


def win_order(base_rows, extra=(), overrides=()):
    """The Windows table, in Windows slot order.

    A slot whose Windows home is a DIFFERENT vftable of the class (`12@0xd82f18`) is absent from the
    class's PRIMARY table -- MSVC does not duplicate a secondary-base override into it. But when we are
    building that other vftable itself (Camera's `Gob` table, a StringGob's `CAurString` table), those same
    rows ARE its contents, at the index the suffix names. `overrides` carries them: rows whose Windows home
    is elsewhere, placed at their own index, replacing whatever the base had there."""
    out = {}
    for r in list(base_rows) + list(extra):
        w, elsewhere = win_slot(r["windows_slot"])
        if w is None or elsewhere:
            continue
        out[w] = logical_id(r)
    for r in overrides:
        w, elsewhere = win_slot(r["windows_slot"])
        if w is None:
            continue
        # Only a row whose Windows home IS this other vftable, plus the destructor pair (slot 0, which the
        # derived class always overrides). A row with a bare index is a NEW primary virtual -- Camera's
        # SetFadePlanes at Windows 32 -- and belongs to the primary table alone.
        if elsewhere or int(r["slot_index"]) < 2:
            out[w] = logical_id(r)
    n = max(out) + 1 if out else 0
    return [out.get(i, -1) for i in range(n)]


def win_patched(order, extra_rows):
    """A derived primary's Windows table with the overrides `derived_primary` patches in on Linux. An override
    of a virtual BOTH bases declare sits in the primary under MSVC too (nwmain.exe's StringGob vftable @0xd81440
    holds StringGob::GetExternalData at 30); only an override of a secondary-only virtual is absent from it."""
    out = list(order)
    for r in extra_rows:
        k = base_name(r["linux_symbol_demangled"])
        if int(r["slot_index"]) < 2 or k not in gob_by_key:
            continue
        w, _ = win_slot(next(g for g in gob_rows if int(g["slot_index"]) == gob_by_key[k])["windows_slot"])
        if w is not None and w < len(out):
            out[w] = logical_id(r)
    return out


tables = []
# Gob: single inheritance, pairs 1:1.
tables.append(("Gob", primary(gob_rows), win_order(gob_rows), 0))
tables.append(("Scene", primary(scene_rows), win_order(scene_rows), 0))
# Camera primary: Camera-only 33-40 are its own slots; seven of the eight are Windows-resident only in
# Camera's Gob vftable, which win_order drops.
tables.append(("Camera", primary(cam_rows), win_order(cam_rows), 0))
# StringGob primary: Gob's layout with StringGob's overrides (read off the CAurString table's thunks)
# and StringGob's own 49 appended.
tables.append(("StringGob", derived_primary(gob_rows, sgo_rows, 94, str_rows),
               win_patched(win_order(gob_rows, sgo_rows), str_rows), 0))
# CAurString: the secondary sub-object table at +0x4c0.
tables.append(("CAurString", primary(str_rows), win_order(str_rows), -0x4c0))
# Camera's secondary Gob table at +8: Gob's layout with Camera's overrides patched in.
cam_only = by_iface["Camera-only"] + [r for r in by_iface["CAurCamera"] if int(r["slot_index"]) < 2]
# Camera's secondary Gob table: on Linux, Gob's layout with Camera's overrides patched in; on Windows, the
# same -- and this IS the vftable the `@0xd82f18` suffix names, so those rows are placed here rather than
# dropped. Without them Camera's Gob table on Windows is byte-identical to a plain Gob's.
tables.append(("CameraGob", derived_primary(gob_rows, [], 94, cam_only),
               win_order(gob_rows, overrides=cam_only), -8))

def fnv(tabs):
    h = 1469598103934665603
    for nm, lin, win, off in tabs:
        for v in lin:
            h ^= (v + 2) & 0xFFFFFFFFFFFFFFFF; h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
        for v in win:
            h ^= (v + 2) & 0xFFFFFFFFFFFFFFFF; h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
        h ^= (off + 4096) & 0xFFFFFFFFFFFFFFFF; h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h

digest = fnv(tables)
names = ",\n        ".join('"%s"' % n for n in lname)
byval = ", ".join("true" if b else "false" for b in lbyval)


def arr(xs):
    return ", ".join(str(x) for x in xs)


blocks = []
for nm, lin, win, off in tables:
    blocks.append(f"    private static readonly short[] _lin{nm} = {{ {arr(lin)} }};")
    blocks.append(f"    private static readonly short[] _win{nm} = {{ {arr(win)} }};")

cases_lin = "\n".join(f"        SeamTable.{nm} => _lin{nm}," for nm, _, _, _ in tables)
cases_win = "\n".join(f"        SeamTable.{nm} => _win{nm}," for nm, _, _, _ in tables)
offs = ", ".join(str(off) for _, _, _, off in tables)

src = f'''// GENERATED by tools/seam/gen-vtables.py from tools/seam/aurora-seam-vtables.tsv -- do not edit.
// {len(rows)} measured rows -> {len(lname)} logical slots across {len(tables)} tables.
using System;

namespace CX.Alloy.Seam;

/// <summary>The measured slot order of every vtable the seam builds, per OS (CX-120). A LOGICAL slot is one
/// implementation: two vtable slots that reach the same implementation -- a <c>CAurString</c> thunk and the
/// <c>StringGob</c> primary slot behind it -- share one logical id, so one registration fills both. The Linux
/// order is the canonical identity; the Windows order carries the three measured cross-OS rules (the collapsed
/// destructor pair, MSVC's reversed adjacent overloads, and the secondary-base overrides MSVC does not
/// duplicate into the primary vtable, which are absent from a Windows table here).</summary>
public static class SeamVtables
{{
    /// <summary>How many distinct implementations the seam's tables name.</summary>
    public const int LogicalCount = {len(lname)};

    private static readonly string[] _names =
    {{
        {names}
    }};

    private static readonly bool[] _byValue = {{ {byval} }};

    /// <summary>Byte adjustment from a table's own <c>this</c> to the shell base: 0 for a primary table,
    /// -8 for Camera's <c>Gob</c> sub-object, -0x4c0 for a text shell's <c>CAurString</c> sub-object. Written
    /// ahead of the table as the Itanium offset-to-top word, so a slot implementation normalises any interface
    /// pointer it is handed without a this-adjusting thunk per slot.</summary>
    private static readonly int[] _offsetToTop = {{ {offs} }};

{chr(10).join(blocks)}

    /// <summary>The demangled engine implementation a logical slot stands for -- the name a poison slot and
    /// every refusal line print.</summary>
    public static string Name(int logical) => (uint)logical < (uint)_names.Length ? _names[logical] : "?";

    /// <summary>True when the slot returns a class by value, i.e. its declaration differs per OS
    /// (SysV xmm0:xmm1 with no hidden buffer, Win64 a buffer in rdx).</summary>
    public static bool ByValue(int logical) => (uint)logical < (uint)_byValue.Length && _byValue[logical];

    /// <summary>The Linux slot order of <paramref name="t"/>: index = Linux slot, value = logical id, -1 for
    /// a slot the measurement does not fill.</summary>
    public static ReadOnlySpan<short> Linux(SeamTable t) => t switch
    {{
{cases_lin}
        _ => default,
    }};

    /// <summary>The Windows slot order of <paramref name="t"/>: index = Windows slot, value = logical id.</summary>
    public static ReadOnlySpan<short> Windows(SeamTable t) => t switch
    {{
{cases_win}
        _ => default,
    }};

    /// <summary>A digest of the measurement the tables were generated from: the per-table Linux and Windows
    /// logical-id sequences, hashed. The self-check compares it against the same hash computed from the
    /// tables at runtime, so a hand-edited <c>.g.cs</c> or a regenerated TSV that nobody re-ran the generator
    /// against fails the boot check instead of mis-dispatching one slot in the game.</summary>
    public const ulong Digest = {digest}UL;   // not-an-offset: a content hash of the generated tables

    /// <summary>The same hash, computed from the tables as they stand.</summary>
    public static ulong Hash()
    {{
        ulong h = 1469598103934665603UL;
        foreach (SeamTable t in Enum.GetValues<SeamTable>())
        {{
            foreach (short v in Linux(t)) {{ h ^= (ulong)(v + 2); h *= 1099511628211UL; }}
            foreach (short v in Windows(t)) {{ h ^= (ulong)(v + 2); h *= 1099511628211UL; }}
            h ^= (ulong)(OffsetToTop(t) + 4096); h *= 1099511628211UL;
        }}
        return h;
    }}

    /// <summary>The slot order this OS's game code calls through.</summary>
    public static ReadOnlySpan<short> Order(SeamTable t)
        => OperatingSystem.IsWindows() ? Windows(t) : Linux(t);

    public static int OffsetToTop(SeamTable t) => _offsetToTop[(int)t];

    /// <summary>The logical slot at Linux index <paramref name="linuxSlot"/> of <paramref name="t"/>, or -1.</summary>
    public static int Logical(SeamTable t, int linuxSlot)
    {{
        ReadOnlySpan<short> lin = Linux(t);
        return (uint)linuxSlot < (uint)lin.Length ? lin[linuxSlot] : -1;
    }}
}}
'''

with open(OUT, "w") as f:
    f.write(src)

print("wrote", OUT)
for nm, lin, win, off in tables:
    print(f"  {nm:11s} linux {len(lin):3d}  windows {len(win):3d}  offsetToTop {off}  "
          f"unfilled linux {sum(1 for x in lin if x < 0)} windows {sum(1 for x in win if x < 0)}")
print("  logical slots:", len(lname))
