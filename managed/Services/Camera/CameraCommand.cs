using System;
using System.Collections.Generic;
using System.Text;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// The one <c>/camera</c> command, shared by every camera ticket of map #535. <see cref="CommandRegistry"/>
/// is first-registration-wins, so two services that each wanted <c>/camera</c> would leave one of them
/// silently commandless; instead each registers its subcommands here and the bare <c>/camera</c> report is
/// every registered reporter in turn. Cold: one dispatch per typed line.
/// </summary>
public static class CameraCommand
{
    private sealed record Sub(string Name, string Help, CommandRegistry.Handler Run);

    private static readonly List<Sub> _subs = new();
    private static readonly List<Func<string?>> _reports = new();
    private static bool _registered;

    /// <summary>Own one subcommand: <c>/camera &lt;name&gt; [args]</c> reaches <paramref name="run"/> with the
    /// text after the name. First registration of a name wins, like the registry it fronts.</summary>
    public static void Register(string name, string help, CommandRegistry.Handler run)
    {
        EnsureRegistered();
        for (int i = 0; i < _subs.Count; i++) if (_subs[i].Name == name) return;
        _subs.Add(new Sub(name, help, run));
    }

    /// <summary>Contribute a line (or null for nothing this session) to the bare <c>/camera</c> report.</summary>
    public static void Report(Func<string?> supplier) { EnsureRegistered(); _reports.Add(supplier); }

    private static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        CommandRegistry.Global.Register("/camera", "report or tune the camera layer; /camera help lists the subcommands", Dispatch, "/cam");
    }

    private static string? Dispatch(string args)
    {
        if (args.Length == 0)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _reports.Count; i++)
            {
                string? line = _reports[i]();
                if (line != null) sb.Append(sb.Length > 0 ? "\n" : "").Append(line);
            }
            return sb.Length > 0 ? sb.ToString() : "[camera] nothing registered";
        }
        int sp = args.IndexOf(' ');
        string name = sp < 0 ? args : args[..sp];
        string rest = sp < 0 ? "" : args[(sp + 1)..].TrimStart();
        if (name == "help")
        {
            var sb = new StringBuilder("[camera] subcommands:");
            for (int i = 0; i < _subs.Count; i++) sb.Append("\n  ").Append(_subs[i].Name).Append(" -- ").Append(_subs[i].Help);
            return sb.ToString();
        }
        for (int i = 0; i < _subs.Count; i++)
            if (_subs[i].Name == name) return _subs[i].Run(rest);
        return $"[camera] unknown subcommand '{name}'; /camera help";
    }
}
