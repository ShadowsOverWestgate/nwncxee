using System;
using System.Collections.Generic;

namespace Sowx.Framework;

/// <summary>
/// The slash-command surface (#165; bucket 1 of ADR 0013). Services register their own commands during
/// <see cref="IExtenderService.Initialize"/>; the chat input surface only hands text to
/// <see cref="TryDispatch"/> and echoes whatever comes back, so it no longer knows what any command does.
/// <para>Cold by construction -- one dispatch per submitted line -- so the hot-path allocation rule does not
/// apply here.</para>
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>A command body. Takes the argument text after the name (lowercased, trimmed, may be empty) and returns
    /// the single line to echo back to the player, or null to stay silent.</summary>
    public delegate string? Handler(string args);

    /// <summary>One registered command, as enumeration sees it.</summary>
    /// <param name="Name">Canonical name, with the leading slash.</param>
    /// <param name="Aliases">Extra names that dispatch to the same handler.</param>
    /// <param name="Help">One line describing the command; the listing a /help would print.</param>
    public sealed record Command(string Name, string[] Aliases, string Help, Handler Run);

    /// <summary>The one live registry every service registers into.</summary>
    public static readonly CommandRegistry Global = new();

    private readonly List<Command> _all = new();
    private readonly Dictionary<string, Command> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered command in registration order. Aliases are folded into their command, not
    /// listed separately.</summary>
    public IReadOnlyList<Command> Commands => _all;

    /// <summary>Log for refusals. Set once by the host; a refusal is an init-time collision between two
    /// services, and the command it costs would otherwise just not exist with no diagnostic anywhere.</summary>
    public ExtenderLog? Log;

    /// <summary>Register a command. Names are normalised: a leading slash is added if missing and the whole
    /// name is lowercased. Returns false and changes nothing if the name or any alias is already taken --
    /// first registration wins, so a later service can never silently steal an existing command. A refusal is
    /// logged, so a caller that discards the return value still leaves a trace.</summary>
    public bool Register(string name, string help, Handler run, params string[] aliases)
    {
        string canon = Norm(name);
        string[] alt = Array.ConvertAll(aliases, Norm);
        if (_byName.ContainsKey(canon)) return Refuse(canon, canon);
        foreach (string a in alt) if (_byName.ContainsKey(a)) return Refuse(canon, a);

        var cmd = new Command(canon, alt, help, run);
        _byName[canon] = cmd;
        foreach (string a in alt) _byName[a] = cmd;
        _all.Add(cmd);
        return true;
    }

    /// <summary>Run the command named by the first token of <paramref name="text"/>. Returns false -- with no
    /// side effect -- when the text is not a slash command or names nothing registered, which is what lets the
    /// caller fall through to the channel-switch grammar and the send path.</summary>
    // ponytail: dispatch is first-token only, so an exact-match command now tolerates trailing junk
    // (`/fps off` toggles rather than being sent to the server as chat). Per-command "takes arguments"
    // metadata would preserve the old fall-through exactly; it is not worth a field.
    public bool TryDispatch(string text, out string? reply)
    {
        reply = null;
        if (text.Length < 2 || text[0] != '/') return false;
        // The whole line is lowercased, arguments included -- the if-chain this replaced did the same, and the
        // knob keys depend on it (`/light GAIN 2` must reach OwnedLights as "gain").
        string cmd = text.Trim().ToLowerInvariant();
        int sp = cmd.IndexOf(' ');
        string name = sp < 0 ? cmd : cmd[..sp];
        if (!_byName.TryGetValue(name, out Command? c)) return false;
        reply = c.Run(sp < 0 ? "" : cmd[(sp + 1)..].TrimStart());
        return true;
    }

    private bool Refuse(string canon, string taken)
    {
        Log?.Info($"CommandRegistry: '{canon}' refused -- '{taken}' is already registered by "
                  + $"'{_byName[taken].Name}' (first registration wins)");
        return false;
    }

    private static string Norm(string n) => (n.StartsWith('/') ? n : "/" + n).ToLowerInvariant();

    /// <summary>Registration, lookup, case folding, argument passthrough, duplicate refusal and enumeration,
    /// off-device. Runs against a private instance so nothing it registers reaches the live surface.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CommandRegistry.SelfCheck FAIL: {w}"); } }

        var r = new CommandRegistry();
        string? seen = null;
        E(r.Register("/probe", "probe help", a => { seen = a; return "echo " + a; }, "/pb", "pb2"),
            "a fresh name registers");
        E(r.Register("/silent", "silent help", _ => null), "a second command registers");

        E(r.TryDispatch("/probe", out string? reply), "the canonical name dispatches");
        E(seen == "" && reply == "echo ", "no arguments means an empty argument string");
        E(r.TryDispatch("/pb x", out _) && seen == "x", "an alias dispatches to the same handler");
        E(r.TryDispatch("/pb2 x", out _) && seen == "x", "an alias without a slash is normalised");
        E(r.TryDispatch("/PROBE GAIN 2", out _) && seen == "gain 2",
            "the name is case-insensitive and the arguments arrive lowercased");
        E(r.TryDispatch("/probe  a  b  ", out _) && seen == "a  b",
            "the line is trimmed at the end; inner spacing is the handler's business");
        E(!r.TryDispatch("  /probe", out _), "a leading space means it was never a command (as before)");
        E(r.TryDispatch("/silent", out string? none) && none == null, "a handler may decline to echo");

        seen = null;
        E(!r.TryDispatch("/nosuch", out reply) && reply == null && seen == null,
            "an unknown command is not handled and runs nothing");
        E(!r.TryDispatch("/", out _), "a bare slash is not a command");
        E(!r.TryDispatch("hello", out _), "plain text is not a command");
        E(!r.TryDispatch("/probefoo", out _), "the name must match the whole first token");

        E(!r.Register("/probe", "dupe", _ => "stolen"), "a duplicate name is refused");
        E(!r.Register("/pb", "dupe", _ => "stolen"), "a duplicate alias is refused");
        E(!r.Register("/other", "dupe alias", _ => "stolen", "/probe"), "a new name with a taken alias is refused");
        E(r.TryDispatch("/probe", out reply) && reply == "echo ", "the first registration still owns the name");
        E(!r.TryDispatch("/other", out _), "the refused registration left nothing behind");

        E(r.Commands.Count == 2, "enumeration lists each command once, aliases folded in");
        E(r.Commands[0].Name == "/probe" && r.Commands[0].Help == "probe help", "enumeration carries name + help");
        E(r.Commands[0].Aliases.Length == 2 && r.Commands[0].Aliases[0] == "/pb"
            && r.Commands[0].Aliases[1] == "/pb2", "enumeration carries the normalised aliases");
        E(r.Commands[1].Name == "/silent", "enumeration is in registration order");

        log.Info(ok ? "CommandRegistry.SelfCheck: passed" : "CommandRegistry.SelfCheck: FAILED");
    }
}
