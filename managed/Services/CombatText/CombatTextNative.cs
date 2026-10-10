using System;
using CX.Framework;

namespace CX.Services.CombatText;

/// <summary>
/// The raw client reads this feature needs on top of <see cref="CX.Services.Nameplate.NameplateNative"/>
/// (which already owns the object, camera and screen-anchor accessors).
/// </summary>
public static class CombatTextNative
{
    /// <summary>The font resref the engine asks for when the floaty text is a DAMAGE NUMBER. Every combat-text
    /// banner passes an empty resref instead. Read off the two call sites in
    /// <c>CNWCMessage::HandleServerToPlayerCCMessage</c> (gh 0x88ecf9 / 0x89069f).</summary>
    public const string DamageFont = "fnt_floaty_smal";

    /// <summary>Whether a font resref names the damage-number font. The engine passes the resref by value in
    /// a <c>CExoString</c>; a null or empty one is a banner. Pure -- self-checked.</summary>
    public static bool IsDamageFont(string font) =>
        string.Equals(font, DamageFont, StringComparison.OrdinalIgnoreCase);

    public static void SelfCheck(ExtenderLog log)
    {
        // The resref is the engine's own, read off HandleServerToPlayerCCMessage -- written out here so a
        // change to DamageFont fails rather than redefining what the check means.
        bool ok = IsDamageFont("fnt_floaty_smal") && IsDamageFont("FNT_FLOATY_SMAL")
                  && !IsDamageFont("") && !IsDamageFont("fnt_galahad14");
        log.Info(ok ? "CombatTextNative.SelfCheck: passed" : "CombatTextNative.SelfCheck: FAILED");
    }
}
