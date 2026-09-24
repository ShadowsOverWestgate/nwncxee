using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Engine;

/// <summary>An MdlNode's name is an inline <c>char[]</c> (<see cref="EngineLayout.MdlNodeName"/>), so the row
/// hands out its first byte and the string it spells is a concept on the view.</summary>
public readonly unsafe ref partial struct MdlNode
{
    /// <summary>The node name as a managed string, or null when it does not decode. ALLOCATES -- diagnostics
    /// and load-time lookups only, never the walk.</summary>
    public string? NameText => Marshal.PtrToStringAnsi(Ptr + EngineLayout.MdlNodeName);
}
