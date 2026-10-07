using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace CX.MdlCheck;

/// <summary>A minimal, engine-free KEY/BIF reader: enough of the container to walk every model in the base
/// game off-device. <c>managed/Alloy/ResLocate.cs</c> cannot be reused -- it asks the live
/// <c>CExoResMan</c> for a key entry -- so the two header layouts are read here directly.
/// <para>Layouts verified against <c>data/nwn_base.key</c> and <c>data/models_01.bif</c>: a KEY V1 file
/// table entry is 12 bytes (size, name offset, name length, drives) with the names packed NUL-separated
/// after the table, a key entry is 22 bytes (resref[16], restype, resid) and a BIF variable-resource entry
/// is 16 bytes (id, offset, size, restype). A resid's top 12 bits are the file-table index and its low 20
/// bits the index into that BIF's variable table; the BIF's own <c>id</c> word is
/// <c>(index &lt;&lt; 20) | index</c> and is ignored.</para></summary>
internal sealed class KeyBif : IDisposable
{
    /// <summary>One resource: which BIF and where in it.</summary>
    internal readonly struct Entry
    {
        public Entry(int bif, long offset, int size) { Bif = bif; Offset = offset; Size = size; }
        public readonly int Bif;
        public readonly long Offset;
        public readonly int Size;
    }

    private const int KeyFileEntryBytes = 12, KeyEntryBytes = 22, BifEntryBytes = 16;
    private const int ResRefChars = 16;

    private readonly string _root;
    private readonly string[] _bifNames;
    private readonly FileStream?[] _bifs;
    private readonly Dictionary<(string Name, ushort Type), Entry> _byName = new();
    private readonly List<(string Name, ushort Type)> _order = new();

    internal IReadOnlyList<(string Name, ushort Type)> Resources => _order;

    internal KeyBif(string keyPath)
    {
        _root = Path.GetDirectoryName(Path.GetFullPath(keyPath))!;
        // The KEY sits in data/, and its BIF names are relative to the install root one level up.
        string installRoot = Path.GetDirectoryName(_root) ?? _root;
        byte[] key = File.ReadAllBytes(keyPath);
        if (key.Length < 0x40 || System.Text.Encoding.ASCII.GetString(key, 0, 4) != "KEY ")
            throw new InvalidDataException($"{keyPath}: not a KEY file");
        int bifCount = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(8));
        int keyCount = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(12));
        int fileTable = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(16));
        int keyTable = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(20));

        _bifNames = new string[bifCount];
        _bifs = new FileStream?[bifCount];
        for (int i = 0; i < bifCount; i++)
        {
            int at = fileTable + i * KeyFileEntryBytes;
            int nameAt = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(at + 4));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(at + 8));
            string name = System.Text.Encoding.ASCII.GetString(key, nameAt, nameLen).TrimEnd('\0');
            _bifNames[i] = Path.Combine(installRoot, name.Replace('\\', '/'));
        }

        for (int i = 0; i < keyCount; i++)
        {
            int at = keyTable + i * KeyEntryBytes;
            string name = Cstr(key.AsSpan(at, ResRefChars));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(at + ResRefChars));
            uint resid = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(at + ResRefChars + 2));
            var k = (name, type);
            if (_byName.ContainsKey(k)) continue;
            _byName[k] = new Entry((int)(resid >> 20), resid & 0xFFFFF, 0);
            _order.Add(k);
        }
    }

    private static string Cstr(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(n < 0 ? s : s[..n]).ToLowerInvariant();
    }

    /// <summary>The variable-resource table of one BIF, loaded on first use.</summary>
    private readonly Dictionary<int, (long Offset, int Size)[]> _tables = new();

    private (long Offset, int Size)[] Table(int bif)
    {
        if (_tables.TryGetValue(bif, out var t)) return t;
        string path = _bifNames[bif];
        if (!File.Exists(path)) { t = Array.Empty<(long, int)>(); _tables[bif] = t; return t; }
        using FileStream fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[20];
        fs.ReadExactly(head);
        int varCount = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
        int varAt = BinaryPrimitives.ReadInt32LittleEndian(head[16..]);
        byte[] tab = new byte[(long)varCount * BifEntryBytes <= int.MaxValue ? varCount * BifEntryBytes : 0];
        fs.Position = varAt;
        fs.ReadExactly(tab);
        t = new (long, int)[varCount];
        for (int i = 0; i < varCount; i++)
        {
            int at = i * BifEntryBytes;
            t[i] = (BinaryPrimitives.ReadUInt32LittleEndian(tab.AsSpan(at + 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(tab.AsSpan(at + 8)));
        }
        _tables[bif] = t;
        return t;
    }

    /// <summary>The bytes of one resource, or null when the container is missing or the index is out of range.</summary>
    internal byte[]? Read(string name, ushort type)
    {
        if (!_byName.TryGetValue((name, type), out Entry e)) return null;
        var t = Table(e.Bif);
        if (e.Offset >= t.Length) return null;
        (long off, int size) = t[(int)e.Offset];
        FileStream? fs = _bifs[e.Bif];
        if (fs == null)
        {
            if (!File.Exists(_bifNames[e.Bif])) return null;
            fs = _bifs[e.Bif] = File.OpenRead(_bifNames[e.Bif]);
        }
        byte[] data = new byte[size];
        fs.Position = off;
        fs.ReadExactly(data, 0, size);
        return data;
    }

    public void Dispose()
    {
        foreach (FileStream? fs in _bifs) fs?.Dispose();
    }
}
