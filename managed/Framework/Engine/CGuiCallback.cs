using System;
using CX.Framework;

namespace CX.Engine;

public readonly unsafe partial struct CGuiCallback
{
    /// <summary>A slot the menu's creator actually filled. An unset one's object word is the record's OWN
    /// address -- the sentinel <c>HandleMenuAction</c> tests before falling through to
    /// <c>HandleUninitializedCallback</c>.</summary>
    public bool IsSet => Obj != Ptr;

    /// <summary>The Itanium member-fn pair's this-adjust: the word right after <c>fn</c>. An MSVC pair is
    /// <c>{obj, fn}</c> with no adjust word at all, so there it reads 0 and a write is dropped rather than
    /// running off the end of the record.</summary>
    public IntPtr Adj
    {
        get => OperatingSystem.IsWindows() ? IntPtr.Zero : *AdjWord;
        set { if (!OperatingSystem.IsWindows()) *AdjWord = value; }
    }

    private IntPtr* AdjWord => (IntPtr*)(Ptr + EngineLayout.CallbackFn) + 1;
}
