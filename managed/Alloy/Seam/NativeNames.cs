using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;

namespace CX.Alloy.Seam;

/// <summary>Model, object and part names as NATIVE, immortal <c>char const*</c>: three slots and
/// <c>CHitInfo+0x68</c> hand a name straight to the game and let it keep the pointer
/// (<c>Gob::GetModelName</c> @0x437270, <c>Gob::GetRandomPart</c> @0x43d4c0), so each distinct name is copied
/// once into native memory and never freed. Cold -- minted on first ask, never called from a hot loop.</summary>
public static unsafe class NativeNames
{
    private static readonly Dictionary<string, nint> _byName = new(StringComparer.Ordinal);
    private static long _bytes;

    /// <summary>Distinct names interned, and the bytes they hold.</summary>
    public static int Count { get { lock (_byName) return _byName.Count; } }
    public static long Bytes { get { lock (_byName) return _bytes; } }

    /// <summary>The one native copy of <paramref name="name"/>, or 0 for a null or empty name -- which is what
    /// the engine answers for a model with no name and a part with no node.</summary>
    public static nint Of(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        lock (_byName)
        {
            if (_byName.TryGetValue(name, out nint p)) return p;
            p = Marshal.StringToCoTaskMemUTF8(name);
            if (p == 0) return 0;
            _byName[name] = p;
            _bytes += name.Length + 1;
            return p;
        }
    }

    /// <summary>The native name of part <paramref name="part"/>'s node, for a slot or a <c>CHitInfo</c> fill.</summary>
    public static nint OfPart(int part)
    {
        if ((uint)part >= (uint)CX.Alloy.Db.SceneDb.MaxParts) return 0;
        ModelDef? m = CX.Alloy.Db.SceneDb.PartModel[part];
        int n = CX.Alloy.Db.SceneDb.PartNode[part];
        return m == null || (uint)n >= (uint)m.Nodes.Length ? 0 : Of(m.Nodes[n].Name);
    }
}
