using System;
using System.Collections.Generic;
using System.Text;

namespace Sowx.Framework;

/// <summary>
/// <c>/knobs</c>: print what is tunable (#183). ADR 0013's last consequence — before the two buckets
/// existed nothing could enumerate what the extender could be told to do, because an environment variable
/// leaves no trace of itself anywhere.
/// </summary>
/// <remarks>
/// Reads the two buckets rather than a third list of its own: <see cref="CommandRegistry.Commands"/> is
/// bucket 1 and <see cref="Settings.All"/> is bucket 2. A knob that is not in one of them does not exist,
/// which is the property the ADR wanted and a hand-maintained catalogue would quietly lose.
/// </remarks>
public static class KnobSurface
{
    /// <summary>Join the command surface. Once, from the host, after <see cref="Settings.Load"/>.
    /// <para>Aliased <c>/cxhelp</c> and deliberately NOT <c>/help</c>. Registration happens before every
    /// service, so first-registration-wins would hand <c>/help</c> to the client for good -- and a handled
    /// command never reaches the send path, so a server's own <c>/help</c> would become unreachable with no
    /// way out. Plenty of servers use it, SoW among them. The prefixed name is discoverable without claiming
    /// a name that is not ours.</para></summary>
    public static void Register() =>
        CommandRegistry.Global.Register("/knobs", "list the commands and config settings this build has",
                                        Report, "/cxhelp");

    /// <summary>#234: the third thing <c>/knobs</c> has to be able to say -- a knob of the CLIENT's that
    /// this build TAKES AWAY. It is in neither bucket by construction, and a hand-written line here would
    /// be the catalogue the class exists to avoid, so the layer that removes the knob registers a supplier
    /// instead and the framework names nothing above it (ADR 0014). Returning null means "nothing to say
    /// this session", which is how a supplier reports live state rather than a fact.</summary>
    public static void Note(Func<string?> supplier) => _notes.Add(supplier);

    private static readonly List<Func<string?>> _notes = new();

    /// <summary>Both buckets, one line each. Cold — one dispatch per typed line.</summary>
    private static string Report(string args)
    {
        var sb = new StringBuilder();
        var cmds = CommandRegistry.Global.Commands;
        sb.Append($"[knobs] {cmds.Count} command(s):");
        for (int i = 0; i < cmds.Count; i++)
        {
            sb.Append("\n  ").Append(cmds[i].Name);
            if (cmds[i].Aliases.Length > 0) sb.Append(" (").Append(string.Join(", ", cmds[i].Aliases)).Append(')');
            sb.Append(" -- ").Append(cmds[i].Help);
        }
        var cfg = Settings.All;
        sb.Append($"\n[knobs] {cfg.Count} setting(s) in {Settings.Path}:");
        for (int i = 0; i < cfg.Count; i++)
            sb.Append($"\n  {cfg[i].Key}={cfg[i].Value}")
              .Append(cfg[i].Value == cfg[i].Default ? " (default)" : $" (default {cfg[i].Default})");
        for (int i = 0; i < _notes.Count; i++)
        {
            string? note = _notes[i]();
            if (note != null) sb.Append("\n[knobs] ").Append(note);
        }
        return sb.ToString();
    }
}
