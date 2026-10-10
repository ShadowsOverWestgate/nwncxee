using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>A material's texture slots are an inline array of <c>SafePointer</c> records (the pointer, then a
/// refcount int), <see cref="EngineLayout.MatSlotStride"/> apart -- an INDEX into an inline array, which no
/// field row describes, so the accessor is a concept on the view.</summary>
public readonly unsafe ref partial struct Material
{
    /// <summary>Slot <paramref name="i"/> of the Material's OWN table, which starts at the object itself (the
    /// constructor writes 0x00/0x10/../0x60). Borrowed: a <c>CAurTexture*</c> dies at the next area
    /// transition, so it is re-fetched, never cached.</summary>
    public CAurTexture Slot(int i) => new(*(IntPtr*)(Ptr + i * EngineLayout.MatSlotStride));
}

/// <inheritdoc cref="Material"/>
public readonly unsafe ref partial struct SharedMaterial
{
    /// <summary>Slot <paramref name="i"/> of the SharedMaterial's own eleven, which begin at
    /// <see cref="EngineLayout.SharedSlot0"/> and win over the Material's three.</summary>
    public CAurTexture Slot(int i) => new(*(IntPtr*)(Ptr + EngineLayout.SharedSlot0 + i * EngineLayout.MatSlotStride));
}
