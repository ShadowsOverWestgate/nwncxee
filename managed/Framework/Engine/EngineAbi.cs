using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The engine's <c>List&lt;T&gt;</c> / <c>CExoArrayList&lt;T&gt;</c> head: {element pointer, count}.
/// A bare list head at a global address is not an engine class, so <c>tools/engine/views.py</c> skips its
/// <c>(abi)</c> rows and this hand-written concept view is the one place those two reads live.</summary>
public readonly unsafe struct EngineList(IntPtr ptr)
{
    /// <summary>The list head.</summary>
    public readonly IntPtr Ptr = ptr;
    /// <summary>No list.</summary>
    public bool IsNull => Ptr == IntPtr.Zero;
    /// <inheritdoc cref="EngineLayout.ListItems"/>
    public IntPtr Items { get => *(IntPtr*)(Ptr + EngineLayout.ListItems); set => *(IntPtr*)(Ptr + EngineLayout.ListItems) = value; }
    /// <inheritdoc cref="EngineLayout.ListItemCount"/>
    public int Count { get => *(int*)(Ptr + EngineLayout.ListItemCount); set => *(int*)(Ptr + EngineLayout.ListItemCount) = value; }
    /// <summary>Element <paramref name="i"/> of a list of pointers; no bounds check.</summary>
    public ref IntPtr this[int i] => ref ((IntPtr*)Items)[i];
}

/// <summary>C++ runtime layouts that belong to no engine class -- the other half of what the generator's
/// <c>(abi)</c> rows describe.</summary>
public static class EngineAbi
{
    /// <summary>An exported vtable SYMBOL addresses the type-info header, not the first virtual slot: an
    /// object's first word is the symbol plus <see cref="EngineLayout.VtblSymbolHeader"/>. Zero in, zero out.</summary>
    public static IntPtr Vtbl(IntPtr symbol) =>
        symbol == IntPtr.Zero ? IntPtr.Zero : symbol + EngineLayout.VtblSymbolHeader;
}
