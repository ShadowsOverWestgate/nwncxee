using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sowx.Framework;

/// <summary>
/// Every engine-memory constant the extender reads -- struct field offsets and vtable slots -- in one
/// table, keyed by (os, build). <see cref="SymbolMap"/> answers "where is this function"; this answers
/// "where is this field", which the Windows port (#429) made a per-platform question.
/// </summary>
/// <remarks>
/// <para>Three declaration forms, and there is no fourth: <see cref="Diverges"/> (a value per OS),
/// <see cref="Same"/> (measured identical on both) and <see cref="Unresolved"/> (a Linux value, Windows
/// never looked). The point of <c>Same</c> is that "verified identical" and "never looked" stop being the
/// same bare <c>const</c>, and <c>Unresolved</c> gives the migration a completion counter.</para>
/// <para>Resolved once at static init, read afterwards as a plain field -- Alloy reads these in the frame
/// loop, so there is no lookup at the use site. Declarations live here rather than in the file that reads
/// them so that the count is honest: per-file static initializers run on first touch, and a count that
/// depends on what the player has done is not a count.</para>
/// <para>Provenance -- where each Windows value came from -- is <c>docs/engine-layout.md</c>.</para>
/// </remarks>
public static unsafe class EngineLayout
{
    // Declared first on purpose: static field initializers run in textual order, and the table below
    // registers into this list as it resolves.
    private static readonly List<Row> _rows = new();

    // ================= the table =================
    // Each row names the engine class it belongs to: that is the key #431's Ghidra extractor pairs on,
    // and the unit #430 measures (a class diverges in 2-3 contiguous segments, anchored on a function pair).

    /// <summary>CNWCObject + 0x0c: object type byte -- 5 creature, 6 item, 9 placeable, 0xa door.</summary>
    public static readonly int ObjType = Same("CNWCObject", "type", 0x0c);

    /// <summary>CNWCCreature: "the feedback plate is currently shown for me", set at the top of
    /// ShowFeedbackBubbleText (Linux disasm @0x71a81e, Windows @0x80b240).</summary>
    public static readonly int CreaturePlated = Diverges("CNWCCreature", "plated", linux: 0x468, win: 0x448, Expect.Byte01);

    /// <summary>CNWCPlaceable, same flag (Linux disasm @0x7ea8b8, Windows @0x871b60).</summary>
    public static readonly int PlaceablePlated = Diverges("CNWCPlaceable", "plated", linux: 0x218, win: 0x1f8, Expect.Byte01);

    /// <summary>Virtual slot of <c>GetPortrait(unsigned char)</c> on CNWCObject -- MSVC orders the table
    /// differently, so this is a vtable index, not a field (#420).</summary>
    public static readonly int GetPortraitSlot = Diverges("CNWCObject", "vtbl.GetPortrait", linux: 0x1d8, win: 0x1d0, Expect.VtblSlot);

    // ponytail: the 530 Alloy offsets land here one subsystem at a time (#429 section 2). A second tested
    // client build gets its own pinned key plus its own values below; today there is exactly one per OS.

    // ================= build key =================

    /// <summary>The Linux client this table was measured against: <c>.note.gnu.build-id</c> of
    /// <c>nwmain-linux</c>. Build identity from the linker, so a rewrapped Steam/GOG copy of the same build
    /// is the same key -- unlike a content hash.</summary>
    private const string LinuxBuildId = "3e2500a82e0c32008d24c3e74bbff3021da5e1b7";

    /// <summary>The Windows client: CodeView PDB GUID + age from <c>nwmain.exe</c>'s debug directory.
    /// Same build revision as <see cref="LinuxBuildId"/> (<c>bin/*/build.txt</c>), which is the precondition
    /// for any pairing being meaningful at all.</summary>
    private const string WindowsBuildId = "29431F05E0F2461A8BECCFC9E80D8F271";

    /// <summary>The running client's build key, whatever it is -- logged even on mismatch, so the stand-down
    /// line can name what was actually loaded.</summary>
    public static string BuildKey => _key ??= OperatingSystem.IsWindows() ? WindowsKey() : LinuxKey();

    /// <summary>Does the running client match the build these offsets were measured against?</summary>
    public static bool KeyOk => string.Equals(BuildKey, Expected, StringComparison.OrdinalIgnoreCase);

    private static string Expected => OperatingSystem.IsWindows() ? WindowsBuildId : LinuxBuildId;

    /// <summary>Offsets still carrying no Windows resolution. Zero is the section-3 finish line. Counted on
    /// BOTH platforms -- the migration is worked on Linux, and a counter that only moves on Windows is not
    /// visible where the work happens. Only the <see cref="Require"/> gate is Windows-only.</summary>
    public static int UnresolvedCount { get; private set; }

    /// <summary>
    /// Unit gate, the <see cref="SymbolMap.Require"/> pattern for layout rather than symbols: a feature that
    /// reads engine memory calls this at the top of <c>Initialize</c> and stands down whole if the answer is
    /// no. Reading a field at an offset measured on a different build is how a port renders garbage instead
    /// of failing.
    /// </summary>
    public static bool Require(ExtenderLog log, string feature)
    {
        if (!KeyOk)
        {
            log.Always($"{feature}: standing down as a unit -- client build {BuildKey} != {Expected} " +
                       "(rot: re-measure, docs/engine-layout.md)");
            return false;
        }
        if (OperatingSystem.IsWindows() && UnresolvedCount > 0)
        {
            log.Always($"{feature}: standing down as a unit -- {UnresolvedCount} engine offset(s) have no " +
                       "Windows resolution (docs/engine-layout.md)");
            return false;
        }
        return true;
    }

    /// <summary>Join <c>/knobs</c>. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
        $"engine layout: {_rows.Count} offset(s), {UnresolvedCount} unresolved; " +
        $"build {BuildKey}{(KeyOk ? "" : $" != expected {Expected} -- ROT")}");

    // ================= probes =================

    /// <summary>What a resolved offset can assert about live memory before anything renders off it.</summary>
    private enum Expect
    {
        /// <summary>Nothing this offset can cheaply claim.</summary>
        None,
        /// <summary>A boolean field: the dword is 0 or 1. A wrong offset lands in a pointer or a float.</summary>
        Byte01,
        /// <summary>A vtable slot: <c>*(*obj + off)</c> is a code address inside the client image.</summary>
        VtblSlot,
    }

    /// <summary>
    /// Validate this class's resolved offsets against a live instance, once, and stand the caller down
    /// loudly rather than let it render off a wrong offset. Call at the first sighting of a pointer of that
    /// class, behind the caller's own one-shot latch -- a name lookup does not belong in a frame loop.
    /// </summary>
    /// <returns>false if any probe failed -- the caller should treat that as rot.</returns>
    public static bool Probe(string cls, IntPtr obj, ExtenderLog? log)
    {
        if (obj == IntPtr.Zero) return true;
        _ = BuildKey;   // the key readers are also what fill the image range VtblSlot compares against
        bool ok = true;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            if (r.Class != cls || r.Expect == Expect.None) continue;
            if (Holds(r, obj)) continue;
            log?.Always($"layout: probe FAILED -- {r.Class}.{r.Field} at +0x{r.Value:x} " +
                        $"(build {BuildKey}); re-measure, docs/engine-layout.md");
            ok = false;
        }
        return ok;
    }

    private static bool Holds(Row r, IntPtr obj)
    {
        switch (r.Expect)
        {
            case Expect.Byte01:
                int v = *(int*)(obj + r.Value);
                return v == 0 || v == 1;
            case Expect.VtblSlot:
                IntPtr vtbl = *(IntPtr*)obj;
                if (vtbl == IntPtr.Zero) return true;   // no instance to judge, not a failed probe
                IntPtr fn = *(IntPtr*)(vtbl + r.Value);
                return (ulong)fn >= _imageLo && (ulong)fn < _imageHi;
            default:
                return true;
        }
    }

    // ================= plumbing =================

    private readonly record struct Row(string Class, string Field, int Value, Expect Expect);

    private static string? _key;

    private static int Diverges(string cls, string field, int linux, int win, Expect probe = Expect.None)
        => Add(cls, field, OperatingSystem.IsWindows() ? win : linux, probe);

    private static int Same(string cls, string field, int off, Expect probe = Expect.None)
        => Add(cls, field, off, probe);

    /// <summary>A Linux value whose Windows counterpart nobody has measured. Usable on Linux; on Windows
    /// <see cref="Require"/> refuses while any exist, so it can never reach a read there.</summary>
    private static int Unresolved(string cls, string field, int linux, Expect probe = Expect.None)
    {
        UnresolvedCount++;
        return Add(cls, field, linux, probe);
    }

    private static int Add(string cls, string field, int value, Expect probe)
    {
        _rows.Add(new Row(cls, field, value, probe));
        return value;
    }

    // ---- build key readers ----

    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();
    private static ulong _imageLo, _imageHi;

    /// <summary>PE CodeView GUID + age, read from the loaded image: e_lfanew -> DataDirectory[6] ->
    /// IMAGE_DEBUG_DIRECTORY entries -> the type-2 (CODEVIEW) one -> "RSDS" + GUID + age.</summary>
    private static string WindowsKey()
    {
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);
        byte* opt = b + lfanew + 24;
        _imageLo = (ulong)Base;
        _imageHi = _imageLo + *(uint*)(opt + 56);            // SizeOfImage (PE32+)
        uint dbgRva = *(uint*)(opt + 112 + 6 * 8), dbgSize = *(uint*)(opt + 112 + 6 * 8 + 4);
        for (uint i = 0; i + 28 <= dbgSize; i += 28)
        {
            byte* e = b + dbgRva + i;
            if (*(uint*)(e + 12) != 2) continue;             // IMAGE_DEBUG_TYPE_CODEVIEW
            byte* cv = b + *(uint*)(e + 20);                 // AddressOfRawData
            if (cv[0] != 'R' || cv[1] != 'S' || cv[2] != 'D' || cv[3] != 'S') continue;
            var sb = new StringBuilder(33);
            sb.Append((*(uint*)(cv + 4)).ToString("X8"))
              .Append((*(ushort*)(cv + 8)).ToString("X4"))
              .Append((*(ushort*)(cv + 10)).ToString("X4"));
            for (int k = 12; k < 20; k++) sb.Append(cv[k].ToString("X2"));
            sb.Append(*(uint*)(cv + 20));                    // age
            return sb.ToString();
        }
        return "no-codeview";
    }

    /// <summary>ELF NT_GNU_BUILD_ID of the running program. Read from <c>/proc/self/exe</c> -- under
    /// LD_PRELOAD that is the client, and <see cref="Base"/> is a dlopen handle rather than a load address,
    /// so the in-memory shortcut the Windows reader takes is not available here.</summary>
    private static string LinuxKey()
    {
        try
        {
            ImageRangeFromMaps();
            using var fs = File.OpenRead("/proc/self/exe");
            var hdr = new byte[64];
            if (fs.Read(hdr, 0, 64) != 64) return "no-elf-header";
            long phoff = BitConverter.ToInt64(hdr, 0x20);
            int phentsize = BitConverter.ToUInt16(hdr, 0x36), phnum = BitConverter.ToUInt16(hdr, 0x38);
            var ph = new byte[phentsize];
            for (int i = 0; i < phnum; i++)
            {
                fs.Position = phoff + (long)i * phentsize;
                if (fs.Read(ph, 0, phentsize) != phentsize) break;
                if (BitConverter.ToUInt32(ph, 0) != 4) continue;                 // PT_NOTE
                long off = BitConverter.ToInt64(ph, 0x08), size = BitConverter.ToInt64(ph, 0x20);
                var notes = new byte[size];
                fs.Position = off;
                if (fs.Read(notes, 0, notes.Length) != notes.Length) continue;
                for (int p = 0; p + 12 <= notes.Length; )
                {
                    int namesz = BitConverter.ToInt32(notes, p);
                    int descsz = BitConverter.ToInt32(notes, p + 4);
                    int type = BitConverter.ToInt32(notes, p + 8);
                    int name = p + 12, desc = name + ((namesz + 3) & ~3);
                    if (type == 3 && namesz == 4 && notes[name] == 'G' && notes[name + 1] == 'N')
                        return Convert.ToHexString(notes, desc, descsz).ToLowerInvariant();
                    p = desc + ((descsz + 3) & ~3);
                }
            }
            return "no-build-id";
        }
        catch (Exception e) { return "unreadable:" + e.GetType().Name; }
    }

    /// <summary>Image extent of the main program, for the vtable-slot probe. From <c>/proc/self/maps</c>:
    /// the lines whose path is the exe.</summary>
    private static void ImageRangeFromMaps()
    {
        string exe = File.ResolveLinkTarget("/proc/self/exe", true)?.FullName ?? "";
        if (exe.Length == 0) return;
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            if (!line.EndsWith(exe, StringComparison.Ordinal)) continue;
            int dash = line.IndexOf('-'), sp = line.IndexOf(' ');
            if (dash < 0 || sp < dash) continue;
            ulong lo = Convert.ToUInt64(line[..dash], 16), hi = Convert.ToUInt64(line[(dash + 1)..sp], 16);
            if (_imageLo == 0 || lo < _imageLo) _imageLo = lo;
            if (hi > _imageHi) _imageHi = hi;
        }
    }

    /// <summary>
    /// #432: the one runnable check. Proves the three forms register and count, that the build key reads,
    /// and -- the part that matters -- that a deliberately wrong offset makes a probe FAIL rather than pass
    /// quietly. Off-device: the probe runs against a hand-built fake object, not the client.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        int before = _rows.Count, unresolvedBefore = UnresolvedCount;
        int same = Same("SelfCheck", "same", 0x10);
        int div = Diverges("SelfCheck", "div", linux: 0x20, win: 0x24);
        int unk = Unresolved("SelfCheck", "unk", 0x30);
        bool win = OperatingSystem.IsWindows();
        if (same != 0x10 || div != (win ? 0x24 : 0x20) || unk != 0x30) { log.Always("layout selfcheck FAIL: resolution"); return; }
        if (_rows.Count != before + 3) { log.Always("layout selfcheck FAIL: registration"); return; }
        if (UnresolvedCount != unresolvedBefore + 1) { log.Always("layout selfcheck FAIL: counter"); return; }

        // A probe that cannot fail is not a probe: +0x40 holds 2, which no boolean does.
        long* fake = stackalloc long[16];
        for (int i = 0; i < 16; i++) fake[i] = 0;
        *(int*)((byte*)fake + 0x38) = 1;
        *(int*)((byte*)fake + 0x40) = 2;
        _rows.Add(new Row("SelfCheckProbe", "good", 0x38, Expect.Byte01));
        if (!Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: good probe rejected"); return; }
        _rows.Add(new Row("SelfCheckProbe", "bad", 0x40, Expect.Byte01));
        if (Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: bad probe accepted"); return; }

        _rows.RemoveRange(before, _rows.Count - before);
        UnresolvedCount = unresolvedBefore;
        log.Info($"layout selfcheck ok: {_rows.Count} offset(s), {UnresolvedCount} unresolved, build {BuildKey}");
    }
}
