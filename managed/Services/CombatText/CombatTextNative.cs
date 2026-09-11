using System;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services.CombatText;

/// <summary>
/// The raw client reads this feature needs on top of <see cref="Sowx.Services.Nameplate.NameplateNative"/>
/// (which already owns the object, camera and screen-anchor accessors): the two critical-hit strings, read
/// once out of the TLK so the crit banner can be recognised by its text.
/// </summary>
public static unsafe class CombatTextNative
{
    /// <summary>The font resref the engine asks for when the floaty text is a DAMAGE NUMBER. Every combat-text
    /// banner passes an empty resref instead. Read off the two call sites in
    /// <c>CNWCMessage::HandleServerToPlayerCCMessage</c> (gh 0x88ecf9 / 0x89069f).</summary>
    public const string DamageFont = "fnt_floaty_smal";

    /// <summary>The attack-result strrefs the client turns into a crit banner: attack result 3 (gh 0x891411)
    /// and 10 (gh 0x890dxx). Both are formatted as <c>*%s*</c> before they reach
    /// <c>ShowFloatyText</c>, which is why <see cref="IsCritBanner"/> compares against that wrapping.</summary>
    private static readonly uint[] CritStrRefs = { 0x107b6, 0x6ea };

    /// <summary>Scratch big enough for a CExoString with room to spare -- see the repo memory on the 17-byte
    /// CResRef sret smashing a 16-byte buffer's canary.</summary>
    private const int ExoScratch = 32;

    private static readonly IntPtr _tlkSym = Native.Sym(SymbolMap.TlkTable);
    private static readonly delegate* unmanaged<IntPtr, IntPtr, uint, void> _getSimple =
        (delegate* unmanaged<IntPtr, IntPtr, uint, void>)Native.Sym(SymbolMap.TlkGetSimpleString);
    private static readonly delegate* unmanaged<IntPtr, void> _exoDtor =
        (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ExoStringDtor);

    private static string[]? _crit;

    /// <summary>Attempts left at resolving the crit strings. A retry is right (the TLK may not have answered
    /// on the first banner of a session, and a module can open an alternate TLK), but an UNBOUNDED one turns
    /// every ordinary banner into two sret calls and a list allocation for the rest of the session when the
    /// TLK will simply never answer.</summary>
    private static int _tries = MaxTries;
    private const int MaxTries = 8;

    /// <summary>Whether <paramref name="text"/> is one of the client's critical-hit banners. Resolved lazily
    /// on the first banner of a session: our loader runs before <c>main</c>, so the TLK is not open at
    /// Initialize. A TLK that will not answer leaves the list empty and nothing is ever a crit -- the feature
    /// degrades to "all banners are ordinary", never to a wrong one.
    /// <para>COLD: one strcmp per banner, on the message thread.</para></summary>
    public static bool IsCritBanner(string text)
    {
        string[] crit;
        if (_crit is { Length: > 0 } c) crit = c;
        else if (_tries > 0) { _tries--; crit = _crit = ResolveCrit(); }
        else crit = Array.Empty<string>();
        for (int i = 0; i < crit.Length; i++)
            if (string.Equals(crit[i], text, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Drop the cached strings so the next banner re-reads the TLK. The TLK is reloaded on a
    /// language change and an alternate TLK is opened per module, so a session that never re-resolves would
    /// stop recognising crits after a module swap.</summary>
    public static void Forget() { _crit = null; _tries = MaxTries; }

    /// <summary>True once the TLK has actually answered -- reported by <c>/combattext</c> so an unrecognised
    /// crit is diagnosable without a second RE session.</summary>
    public static bool CritResolved => _crit is { Length: > 0 };

    private static string[] ResolveCrit()
    {
        if (_getSimple == null || _tlkSym == IntPtr.Zero) return Array.Empty<string>();
        IntPtr tlk = *(IntPtr*)_tlkSym;
        if (tlk == IntPtr.Zero) return Array.Empty<string>();

        var found = new System.Collections.Generic.List<string>(CritStrRefs.Length);
        foreach (uint strref in CritStrRefs)
        {
            string s = SimpleString(tlk, strref);
            if (s.Length != 0) found.Add("*" + s + "*");   // the engine's own CExoString::Format("*%s*", ...)
        }
        return found.ToArray();
    }

    /// <summary>One TLK fetch through the sret ABI, with the caller-owned buffer freed. Empty on any failure.</summary>
    private static string SimpleString(IntPtr tlk, uint strref)
    {
        byte* exo = stackalloc byte[ExoScratch];
        for (int i = 0; i < ExoScratch; i++) exo[i] = 0;   // the callee frees a non-null buf: never hand it garbage
        // sret: Itanium puts the hidden buffer FIRST, Win64 puts it after `this`.
        if (OperatingSystem.IsWindows()) _getSimple(tlk, (IntPtr)exo, strref); else _getSimple((IntPtr)exo, tlk, strref);

        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        string s = "";
        if (buf != null && len != 0 && len <= 1024)
        {
            var sb = new StringBuilder((int)len);
            for (uint i = 0; i < len; i++) sb.Append((char)buf[i]);   // Latin1, as every CExoString read here is
            s = sb.ToString();
        }
        if (_exoDtor != null) _exoDtor((IntPtr)exo);
        return s;
    }

    /// <summary>Whether a font resref names the damage-number font. The engine passes the resref by value in
    /// a <c>CExoString</c>; a null or empty one is a banner. Pure -- self-checked.</summary>
    public static bool IsDamageFont(string font) =>
        string.Equals(font, DamageFont, StringComparison.OrdinalIgnoreCase);

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = IsDamageFont(DamageFont) && IsDamageFont("FNT_FLOATY_SMAL")
                  && !IsDamageFont("") && !IsDamageFont("fnt_galahad14");
        log.Info(ok ? "CombatTextNative.SelfCheck: passed" : "CombatTextNative.SelfCheck: FAILED");
    }
}
