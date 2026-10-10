using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Services.Radial;

/// <summary>A PLT icon layer CX resolves itself. Such a layer is normally the engine's -- <c>AurTextureFind(pltName,
/// scheme)</c> answers the texture <c>Material::ReplaceTexturePLT</c> @0x4c7a10 composited under that (resref, scheme)
/// pair -- but the engine's render layer is never constructed, so that find always misses. CX does what the world does
/// for a PLT creature: the PAIR texture from the registry once per resref, the palette lookup in the fragment stage.</summary>
internal static partial class RadialIconsPlt
{
    /// <summary>Pair textures acquired for an icon resref. One reference each, for the session: an item icon
    /// is a handful of resrefs and the table is capped, so this is a cache and never a leak that grows with
    /// play.</summary>
    private static readonly Dictionary<string, TexId> _byResref = new(16, StringComparer.OrdinalIgnoreCase);
    private const int MaxCached = 256;
    private static int _said;
    private static ExtenderLog? _log;

    /// <summary>Layers CX resolved this session, and asks the registry for that are not resident yet.</summary>
    internal static long Resolved, Pending;

    internal static void Install(ExtenderLog log) => _log = log;

    /// <summary>The pair texture's GL name for <paramref name="resref"/>, or 0 while it loads (the layer is
    /// skipped and appears a frame later, exactly as a not-yet-built engine texture is).</summary>
    internal static uint Pair(string resref)
    {
        if (string.IsNullOrEmpty(resref)) return 0;
        if (!_byResref.TryGetValue(resref, out TexId id))
        {
            if (_byResref.Count >= MaxCached)
            {
                if (_said++ == 0)
                    _log?.Always($"RadialIcons: more than {MaxCached} PLT icon resrefs -- the rest draw nothing "
                               + "(the cache is a ceiling, and this says so rather than acquiring per frame)");
                return 0;
            }
            id = TexRegistry.Acquire(resref, TexKind.Plt);
            if (id.IsNull) return 0;
            _byResref[resref] = id;
        }
        if (TexRegistry.TryResident(id, out uint name, out _)) { Resolved++; return name; }
        Pending++;
        return 0;
    }

    /// <summary>The master palette, 0 before it exists.</summary>
    internal static uint Palette => PltPalettes.Texture;

    /// <summary>Every texture this holds goes back; the registry's references are ours.</summary>
    internal static void Forget()
    {
        foreach (TexId id in _byResref.Values) TexRegistry.Release(id);
        _byResref.Clear();
    }
}
