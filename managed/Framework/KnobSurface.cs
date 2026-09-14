using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>
/// <c>/knobs</c>: print what is tunable (#183). Spec §4.1: the <c>knobs</c> group (General → Debug info),
/// zero knobs, whose status line is this report; the generated <c>/knobs</c> handler prints it, gated like
/// every command.
/// </summary>
/// <remarks>
/// The registry walk (ADR 0025: <see cref="KnobGroup.Walk"/>, one line per declared knob), then the Note
/// suppliers. A knob that is in neither does not exist, which is the property the ADR wanted and a
/// hand-maintained catalogue would quietly lose.
/// </remarks>
public static class KnobSurface
{
    /// <summary>Declare the group, whose <see cref="KnobGroup.Status"/> is <see cref="Report"/>. Once, from the
    /// host, after <see cref="Settings.Load"/> and after <see cref="General"/> has declared, so it walks third.
    /// <para>Deliberately NOT <c>/help</c>. Registration happens before every service, so first-registration-wins
    /// would hand <c>/help</c> to the client for good -- and a handled command never reaches the send path, so a
    /// server's own <c>/help</c> would become unreachable with no way out. Plenty of servers use it, SoW among
    /// them. The prefixed name is discoverable without claiming a name that is not ours.</para></summary>
    public static void Register()
    {
        KnobGroup g = KnobGroup.Declare("knobs", "General", "Debug info",
            header: "Every knob this build has, with its value, and what the framework found at boot.");
        g.Status = Report;
    }

    /// <summary>#234: the other thing <c>/knobs</c> has to be able to say -- a knob of the CLIENT's that
    /// this build TAKES AWAY. It is not in the registry by construction, and a hand-written line here would
    /// be the catalogue the class exists to avoid, so the layer that removes the knob registers a supplier
    /// instead and the framework names nothing above it (ADR 0014). Returning null means "nothing to say
    /// this session", which is how a supplier reports live state rather than a fact.</summary>
    public static void Note(Func<string?> supplier) => _notes.Add(supplier);

    private static readonly List<Func<string?>> _notes = new();

    /// <summary>The walk, then the notes. Cold — one dispatch per typed line. The generated handler prefixes
    /// the first line with <c>[knobs]</c>.</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        sb.Append(Product.Name).Append(' ').Append(Product.Version).Append(" -- the extender's knobs\n");
        KnobGroup.Walk(sb);
        for (int i = 0; i < _notes.Count; i++)
        {
            string? note = _notes[i]();
            if (note != null) sb.Append("\n[knobs] ").Append(note);
        }
        return sb.ToString();
    }
}
