using System;
using System.Globalization;

namespace Sowx.Framework;

/// <summary>
/// The one place a <c>SOWX_*</c> tuning knob is read (ADR 0013). Replaces four byte-identical private
/// <c>Env</c> parsers and the cross-subsystem borrowing they caused -- render and probe code was calling
/// into a shadow pass to parse a string.
/// </summary>
/// <remarks>
/// Env vars are not a bucket in ADR 0013: every knob is destined for a chat command, the config file, or
/// deletion. This module is the consolidation step, not the end state -- it exists so the triage has one
/// call site shape to rewrite rather than seventy-eight.
/// </remarks>
public static class Tunables
{
    /// <summary>
    /// Invariant-culture float override, or <paramref name="fallback"/>. Invariant parsing is not cosmetic:
    /// a dot-decimal default parsed under a comma-decimal desktop locale made every float knob silently
    /// ignored. Negatives are rejected as malformed -- inherited from all four parsers this replaces, and
    /// every knob today is a magnitude.
    /// </summary>
    public static float Float(string name, float fallback)
        => float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
                          CultureInfo.InvariantCulture, out float v) && v >= 0f ? v : fallback;

    /// <summary>Invariant-culture int override, or <paramref name="fallback"/>. Negatives rejected, as <see cref="Float"/>.</summary>
    public static int Int(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : fallback;

    /// <summary>True when the knob is exactly <c>"1"</c>, false when exactly <c>"0"</c>, else <paramref name="fallback"/>.</summary>
    public static bool Bool(string name, bool fallback)
        => Environment.GetEnvironmentVariable(name) switch { "1" => true, "0" => false, _ => fallback };

    /// <summary>The raw knob, or <paramref name="fallback"/> when unset or empty.</summary>
    public static string Str(string name, string fallback)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }
}
