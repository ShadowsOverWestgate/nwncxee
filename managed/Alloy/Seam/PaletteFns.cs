using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>CX-120 seam-obj: the object-free <c>Aur*</c> palette setters, routed onto CX-117's palette store
/// (<c>docs/alloy-db-texture-mode.md</c>). They take no engine object, so there is nothing to facade -- the
/// seam just answers them with CX's own state instead of the engine's GL texture.</summary>
public sealed unsafe class PaletteFns : ISeamProvider
{
    private static bool _createPending;
    private static long _setFailures;

    /// <summary>A deferred <c>AurCreatePaletteTexture</c> waiting for Alloy's bracket; the frame host asks.</summary>
    public static bool CreatePending => _createPending;

    public void Register(SeamRegistry r)
    {
        r.ReplaceEngine(SymbolMap.AurSetPalette, (void*)(delegate* unmanaged<int, nint, int>)&AurSetPalette);
        r.ReplaceEngine(SymbolMap.AurClearPalette, (void*)(delegate* unmanaged<void>)&AurClearPalette);
        r.ReplaceEngine(SymbolMap.AurCreatePaletteTexture, (void*)(delegate* unmanaged<void>)&AurCreatePaletteTexture);
    }

    /// <summary><c>AurSetPalette(int slot, char const* resref)</c> @0x485190 -- returns int. Synchronous on the
    /// game thread, which is where CX-117 wants it: the palette rows go into a CPU buffer and the texture is
    /// built later, in the bracket.</summary>
    [UnmanagedCallersOnly]
    private static int AurSetPalette(int slot, nint resref)
    {
        string name = resref == 0 ? "" : Marshal.PtrToStringUTF8(resref) ?? "";
        if (PltPalettes.SetPalette(slot, name)) { _createPending = true; return 1; }
        _setFailures++;
        if ((_setFailures & (_setFailures - 1)) == 0)
            Facade.Log?.Always($"seam: AurSetPalette({slot}, \"{name}\") could not be honoured ({_setFailures} so far)");
        return 0;
    }

    /// <summary><c>AurClearPalette()</c> @0x485090: drop the palette texture. The CPU rows survive, and the
    /// next <c>SetPalette</c> rebuilds it -- which is what the engine's own pairing does.</summary>
    [UnmanagedCallersOnly]
    private static void AurClearPalette()
    {
        PltPalettes.ContextLost();
        _createPending = false;
    }

    /// <summary><c>AurCreatePaletteTexture()</c> @0x4874d0: the engine built a GL texture inline. CX's build
    /// has to run inside Alloy's bracket, so the call only RECORDS that one is due; <see cref="Pump"/> does it
    /// at the frame. Deferring is not a silent drop -- the pending flag is the whole mechanism, and a frame
    /// that never comes leaves it set and visible.</summary>
    [UnmanagedCallersOnly]
    private static void AurCreatePaletteTexture() => _createPending = true;

    /// <summary>Run a deferred palette build. Called from Alloy's bracket, where a GL call is legal.</summary>
    public static void Pump()
    {
        if (!_createPending) return;
        _createPending = false;
        PltPalettes.Create();
    }
}
