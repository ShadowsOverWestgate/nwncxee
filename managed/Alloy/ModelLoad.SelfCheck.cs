using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

internal static unsafe partial class ModelLoad
{
    /// <summary>Off-device, no engine: a real <c>HAK V1.0</c> carrying a tiny BINARY model at restype 2002 is
    /// written to a temp directory, read back through the worker's own location reader, and put through the one
    /// piece of parsing this feature does -- the supermodel name -- plus the three cases that are not a name
    /// (ASCII, <c>"null"</c>, too short) and the extension test the serve gates on.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cx55-" + Environment.ProcessId);
        bool ok = true;
        var h = new Handles();
        try
        {
            Directory.CreateDirectory(dir);
            byte[] mdl = Mdl(0, "TAIL01");
            string hak = Path.Combine(dir, "m.hak");
            File.WriteAllBytes(hak, Hak(mdl, out int off));
            byte[]? got = h.Read(new TexLocation(KindErf, 1, hak, off, mdl.Length, mdl.Length, false, MdlResType),
                                 out ReadOnlySpan<byte> bytes, out _) ? bytes.ToArray() : null;
            ok &= got != null && got.AsSpan().SequenceEqual(mdl);
            ok &= got != null && Super(got) == "TAIL01";

            ok &= Super(Mdl(1, "TAIL01")) == "";      // ASCII: the first dword is not zero
            ok &= Super(Mdl(0, "NULL")) == "";        // the engine's own "no supermodel"
            ok &= Super(Mdl(0, "")) == "";
            ok &= Super(new byte[WrapperBytes + SuperModelAt]) == "";   // too short to carry the field

            ok &= Ext(".mdl") && Ext("mdl") && Ext("MDL") && Ext(".MdL");
            ok &= !Ext(".mtr") && !Ext("txi") && !Ext("mdla") && !Ext("md");
        }
        catch (Exception e)
        {
            ok = false;
            log.Always($"ModelLoad.SelfCheck: threw -- {e.Message}");
        }
        finally
        {
            h.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        log.Info(ok ? "ModelLoad.SelfCheck: passed" : "ModelLoad.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>A model file: the 12-byte wrapper, then a model block whose supermodel field is
    /// <paramref name="super"/>. <paramref name="first"/> non-zero makes it an ASCII file.</summary>
    private static byte[] Mdl(uint first, string super)
    {
        var f = new byte[WrapperBytes + SuperModelAt + SuperModelChars + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(f, first);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), (uint)(f.Length - WrapperBytes));
        System.Text.Encoding.ASCII.GetBytes(super).CopyTo(f, WrapperBytes + SuperModelAt);
        return f;
    }

    /// <summary><see cref="SuperModelOf"/> against a job, as the worker calls it.</summary>
    private static string Super(ReadOnlySpan<byte> raw)
    {
        var j = new LoadJob();
        j.Super[0] = 0;
        SuperModelOf(raw, j);
        int n = 0;
        while (n < ResRefChars && j.Super[n] != 0) n++;
        var s = new byte[n];
        for (int i = 0; i < n; i++) s[i] = j.Super[i];
        return System.Text.Encoding.ASCII.GetString(s);
    }

    private static bool Ext(string ext)
    {
        byte* p = stackalloc byte[8];
        for (int i = 0; i < ext.Length && i < 7; i++) p[i] = (byte)ext[i];
        p[Math.Min(ext.Length, 7)] = 0;
        return IsMdl(p);
    }

    /// <summary>A <c>HAK V1.0</c> file with one resource: the 160-byte ERF header carries the entry count at
    /// <c>+0x10</c> and the resource-list offset at <c>+0x20</c>, and the list entry is {offset, size}.</summary>
    private static byte[] Hak(byte[] a, out int off)
    {
        const int header = 160, entry = 8, count = 0x10, list = 0x20;   // not-an-offset: fixture fields of the ERF header
        off = header + entry;
        var f = new byte[off + a.Length];
        "HAK V1.0"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(count), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(list), header);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(header), (uint)off);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(header + 4), (uint)a.Length);
        a.CopyTo(f, off);
        return f;
    }
}
