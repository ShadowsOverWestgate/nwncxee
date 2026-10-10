using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>
/// <c>/knobs</c>: print what is tunable -- the registry walk (<see cref="KnobGroup.Walk"/>, one line per declared
/// knob) and then the Note suppliers, so a knob in neither does not exist. It is the <c>knobs</c> group's
/// diagnostic status line, with no row and no <see cref="KnobGroup.PanelStatus"/>, so it is a chat surface only
/// (ADR 0030); the panel reaches the same text through General's About copy row.
/// </summary>
public static class KnobSurface
{
    /// <summary>Declare the group, whose <see cref="KnobGroup.Status"/> is <see cref="Report"/>. Once, from the
    /// host, after <see cref="Settings.Load"/> and after <see cref="General"/> has declared, so it walks third.
    /// Deliberately NOT <c>/help</c>: a handled command never reaches the send path, so claiming that name would
    /// make a server's own <c>/help</c> unreachable with no way out.</summary>
    public static void Register()
    {
        KnobGroup g = KnobGroup.Declare("knobs", "General", "Debug info",
            header: "Every knob this build has, with its value, and what the framework found at boot.");
        g.Status = Report;
    }

    /// <summary>The other thing <c>/knobs</c> has to say: a knob of the CLIENT's that this build TAKES AWAY. Such a
    /// knob is in no registry, so the layer that removes it registers a supplier and the framework names nothing
    /// above it (ADR 0014). A supplier returning null means "nothing to say this session".</summary>
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
