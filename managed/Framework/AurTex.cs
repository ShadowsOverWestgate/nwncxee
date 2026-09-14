using System;

namespace CX.Framework;

/// <summary>
/// A safe front door to <c>AurTextureGetTID</c>. A <c>CAurTexture*</c> is BORROWED -- the engine frees it on
/// an area transition or a module unload -- and <c>AurTextureGetTID</c> @0x48e080 opens by dereferencing the
/// object's vtable and calling through slot 0x18. Hand it a freed pointer and the call lands wherever the
/// reused heap now says, which is a SIGSEGV rather than a wrong texture.
/// <para>The rule the chat portraits learned (#33) was "never cache the pointer, re-fetch every frame". The
/// Alloy pipeline already obeys it -- every consumer calls <c>Material::GetTexture</c> per frame -- and it is
/// not enough, because <c>Material::GetTexture</c> @0x4c5810 is a plain FIELD READ (<c>this + slot*0x10</c>,
/// or the SharedMaterial's table at <c>this+0xd0</c>). It returns whatever pointer the material was left
/// holding, live or not. A per-frame re-fetch of a stale field is still a stale pointer.</para>
/// <para>So the pointer is checked instead of trusted. The engine has exactly TWO texture vtables --
/// <c>_ZTV11CAurTexture</c> and <c>_ZTV16CAurTextureBasic</c> -- so "is this object still a texture" is two
/// compares against the values those symbols put in a live object's first word. A freed-and-reused object
/// has a heap pointer there (confirmed on the core dump behind this fix: the faulting object's vtable slot
/// held 0x55ae2000d7a8, a heap address, against 2237 live CAurTexture and 309 CAurTextureBasic objects in
/// the same image all carrying the right one).</para>
/// <para>ponytail: a whitelist of two, not a mapped-range test. A range test would also admit a freed object
/// reused as some OTHER engine class, which is a wrong-vtable call rather than a wild one -- quieter, and no
/// cheaper to check.</para>
/// </summary>
public static unsafe class AurTex
{
    private static delegate* unmanaged<IntPtr, int> _getTid;
    /// <summary>The two values a live texture's first word may hold: each class's vtable symbol plus the
    /// 0x10 the Itanium ABI puts before the function pointers.</summary>
    private static IntPtr _vtbl, _vtblBasic;
    private static delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _aurGet;
    private static long _refused;

    /// <summary>Resolved and usable. False leaves every caller where it was before its texture resolved --
    /// no texture id, so no Alloy draw for that part.</summary>
    public static bool Ready { get; private set; }

    /// <summary>How many dangling texture pointers have been refused this session. Nonzero is not an error
    /// to chase in this code -- it is the engine handing out a field it has already freed -- but a number
    /// that climbs every frame means something upstream is holding a dead material, not a dead texture.
    /// </summary>
    public static long Refused => _refused;

    public static void Attach(ExtenderLog log)
    {
        if (Ready) return;
        _getTid = (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.AurTextureGetTID);
        _aurGet = (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)Native.Sym(SymbolMap.AurTextureGet);
        IntPtr v = Native.Sym(SymbolMap.AurTextureVtbl), b = Native.Sym(SymbolMap.AurTextureBasicVtbl);
        if (_getTid == null || v == IntPtr.Zero || b == IntPtr.Zero)
        {
            log.Info("AurTex: AurTextureGetTID or a texture vtable unresolved -- texture ids unavailable");
            return;
        }
        // Itanium ABI: the vtable SYMBOL points at the RTTI pair; an object's first word is symbol + 0x10.
        // MSVC's vftable symbol is the first word itself (#419).
        int skew = OperatingSystem.IsWindows() ? 0 : 0x10;
        _vtbl = v + skew; _vtblBasic = b + skew;
        Ready = true;
    }

    /// <summary>Is this pointer still a texture? The whole guard, split out from <see cref="Tid"/> so it
    /// can be exercised without an engine to call into -- see <see cref="SelfCheck"/>. Two loads and two
    /// compares; a refusal is counted here, where it happens.</summary>
    private static bool Live(IntPtr tex)
    {
        if (!Ready || tex == IntPtr.Zero) return false;
        IntPtr vt = *(IntPtr*)tex;
        if (vt == _vtbl || vt == _vtblBasic) return true;
        _refused++;
        return false;
    }

    /// <summary>This texture's GL id, or 0 if the pointer is null or no longer a texture. Hot -- called per
    /// Alloy part per frame.</summary>
    public static int Tid(IntPtr tex) => Live(tex) ? _getTid(tex) : 0;
    /// <summary>The guard alone, for a caller about to read the object through an engine call other than
    /// <c>AurTextureGetTID</c> (#331's controller lookup).</summary>
    public static bool IsLive(IntPtr tex) => Live(tex);

    /// <summary>A resman texture's GL id by RESREF -- the resolve and the guard in one call, for the handful
    /// of engine textures an owned pass wants by name rather than off a material (#339's <c>solid_noise</c>,
    /// which is the wave field vanilla's water shader samples). Re-resolves every call and caches NOTHING:
    /// the pointer is borrowed and the engine frees it on an area transition. Cold-ish -- once per pass, not
    /// per part -- and allocates nothing (the name is marshalled on the stack).</summary>
    public static int TidByName(string resref) => Tid(FindByName(resref));

    /// <summary>...and the CAurTexture the resman answered with, which is what a caller needs to ask the
    /// object its own name (#456: Windows and Linux resolve <c>solid_noise</c> to different-looking
    /// textures, and only the object can say whether that is a different RESOURCE or a different upload).
    /// Borrowed, never cached -- the engine frees it on an area transition.</summary>
    public static IntPtr FindByName(string resref)
    {
        if (!Ready || _aurGet == null || resref.Length == 0) return IntPtr.Zero;
        byte* name = stackalloc byte[17];   // 16 resref bytes + NUL
        int n = resref.Length < 16 ? resref.Length : 16;
        for (int i = 0; i < n; i++) name[i] = (byte)resref[i];
        name[n] = 0;
        return _aurGet(name, IntPtr.Zero, null, null, 0, 0);
    }

    /// <summary>Is this pointer a live <c>CAurTextureBasic</c> specifically? <see cref="Live"/> admits both
    /// texture classes, which is right for <c>AurTextureGetTID</c> and wrong for anything that reads a
    /// BASIC field off the object -- the per-cell GL name array at <c>+0x50</c> and the resolved name at
    /// <c>+0xa8</c> exist on the subclass only. #259 reads both at the upload funnel, so it asks the
    /// narrower question. Cold: once per engine texture upload.</summary>
    public static bool IsBasic(IntPtr tex) => Ready && tex != IntPtr.Zero && *(IntPtr*)tex == _vtblBasic;

    /// <summary>Off-device: the guard, in both directions, with SYNTHETIC vtables -- so it runs identically
    /// on a machine with no client, which is where a silent inversion would otherwise go unnoticed until it
    /// admitted a freed pointer on someone's live session. The real vtable ADDRESSES cannot be asserted here
    /// (they belong to a client this build may not be running against); what can, and what matters, is that
    /// an exact match is admitted, that anything else is refused and counted, and that a null and an unarmed
    /// AurTex are both refused without dereferencing anything.
    /// <para><see cref="Live"/> rather than <see cref="Tid"/> deliberately: a match must be admissible to
    /// test at all, and admitting one through Tid would call an engine function that is not there.</para>
    /// </summary>
    public static bool SelfCheck(ExtenderLog log)
    {
        bool wasReady = Ready; IntPtr wv = _vtbl, wb = _vtblBasic;
        long before = _refused;
        // Saving Ready is not the same as clearing it, and the two assertions below are ABOUT the unarmed
        // state. On a live client Ready is already true, so the "unarmed" probe was really an armed probe
        // against the client's own vtables: refused AND counted, which made `_refused == before` false and
        // this check fail on every live run since it was written. It passed everywhere it was ever watched
        // (headless, where Ready is false), which is how it stayed that way.
        Ready = false;
        bool ok = !Live(IntPtr.Zero);                              // null, before anything is armed
        IntPtr* obj = stackalloc IntPtr[1];
        IntPtr* fakeVtbl = stackalloc IntPtr[2];
        *obj = (IntPtr)fakeVtbl;
        ok &= !Live((IntPtr)obj) && _refused == before;             // unarmed: refused, and not counted

        Ready = true; _vtbl = (IntPtr)fakeVtbl; _vtblBasic = (IntPtr)(fakeVtbl + 1);
        ok &= Live((IntPtr)obj) && _refused == before;               // exact match on the first vtable
        *obj = (IntPtr)(fakeVtbl + 1);
        ok &= Live((IntPtr)obj) && _refused == before;               // ...and on the second
        *obj = (IntPtr)0x1234;                                       // a freed object's clobbered first word
        ok &= !Live((IntPtr)obj) && _refused == before + 1;          // refused AND counted
        ok &= !Live(IntPtr.Zero) && _refused == before + 1;          // null still costs no dereference

        Ready = wasReady; _vtbl = wv; _vtblBasic = wb;
        _refused = before;                                           // the check's own refusal is not a session's
        log.Info(ok ? $"AurTex.SelfCheck: passed (armed={wasReady})" : "AurTex.SelfCheck: FAILED");
        return ok;
    }
}
