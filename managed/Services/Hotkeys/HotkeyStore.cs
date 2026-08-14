using System.Security.Cryptography;
using Sowx.Framework;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// The consent list and its persistence -- scoped PER SERVER by a client-minted token. Consent is a
/// capability, not a global switch: on consent the client mints a random token, records {token ->
/// keys}, and hands the token to that server. On a later login the server proves prior consent by
/// presenting that token back; the client looks it up and restores exactly that server's keys. A
/// server that was never granted a token (including one replaying another's key list) has nothing to
/// present, so the armed set stays EMPTY and every key becomes a fresh consent prompt.
/// </summary>
/// <remarks>
/// Effective armed set is per session: empty at startup and every login until a recognised token is
/// presented. Cfg is sowx-hotkeys.cfg in the client's user directory (see <see cref="Path"/>) --
/// [token=&lt;hex&gt;] sections, one SDL keycode per
/// line (SDLK, NOT Windows VK: f=102 not 70, F1=1073741882). Static, not DI: the hot key handler
/// reads Armed directly; swaps publish an immutable snapshot atomically.
/// </remarks>
public static class HotkeyStore
{
    /// <summary>The ACTIVE server's consented keys, or empty when no recognised token is active. Read on the hot path; swapped atomically.</summary>
    public static volatile HashSet<int> Armed = new();

    /// <summary>Bumped on every change to Armed, so HotkeyManagerService rebakes its labels only when it changes.</summary>
    public static volatile int Version;

    // tokenHex -> that server's consented keys. Loaded from the cfg; the active set is one of these.
    private static readonly Dictionary<string, HashSet<int>> _sections = new();
    // The token presented by the current server this session, or null if none/unrecognised-and-unconsented.
    private static string? _activeToken;

    public static string Path => Native.ConfigPath("sowx-hotkeys.cfg");

    private static bool _loaded;

    /// <summary>
    /// Load the cfg on first real use, not at Initialize: our loader runs pre-main, so the engine user
    /// dir (where the cfg lives) isn't resolvable yet -- loading then would read the wrong path and latch
    /// it. Deferred to the first login-path call (Activate/Grant/Set), all post-main. Latches only once
    /// the dir resolves, so a pre-main/headless call stays retryable.
    /// </summary>
    private static void EnsureLoaded(ExtenderLog log)
    {
        if (_loaded || Native.UserDirectory() == null) return;
        Load(log);
        _loaded = true;
    }

    public static void Load(ExtenderLog log)
    {
        _sections.Clear();
        int legacy = 0;
        try
        {
            if (File.Exists(Path))
            {
                HashSet<int>? cur = null;   // null until the first [token=] header
                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line[..hash];
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("[token=") && line.EndsWith("]"))
                    {
                        string tok = line[7..^1].Trim().ToLowerInvariant();
                        cur = tok.Length > 0 ? (_sections[tok] = new HashSet<int>()) : null;
                    }
                    else if (int.TryParse(line, out int vk))
                    {
                        if (cur != null) cur.Add(vk);
                        else legacy++;   // a bare keycode before any token section == old global format
                    }
                }
            }
        }
        catch (Exception e) { log.Info($"HotkeyStore: load failed: {e.Message}"); }

        // Startup publishes EMPTY -- nothing is armed until a login presents a recognised token.
        _activeToken = null;
        Armed = new HashSet<int>();
        Version++;

        if (legacy > 0)
            log.Info($"HotkeyStore: dropped {legacy} legacy global armed key(s) -- consent is now per-server; " +
                     "reconnect to each server to re-consent (they re-push their key set on login)");
        log.Info($"HotkeyStore: {_sections.Count} server section(s) loaded from {Path}; none active until a token is presented");
    }

    /// <summary>A new login: forget the previous server's consent. Armed goes empty (fail-closed) until a recognised token is presented.</summary>
    public static void BeginSession()
    {
        _activeToken = null;
        Armed = new HashSet<int>();
        Version++;
    }

    /// <summary>
    /// The server presented `tokenHex` (proof of prior consent). If recognised, restore that server's
    /// consented keys; otherwise the token becomes the active one to consent UNDER (so a later Grant is
    /// stored against the server's own token) but arms nothing yet. Returns true if already known.
    /// </summary>
    public static bool Activate(string tokenHex, ExtenderLog log)
    {
        EnsureLoaded(log);
        string tok = tokenHex.Trim().ToLowerInvariant();
        _activeToken = tok;
        bool known = _sections.TryGetValue(tok, out HashSet<int>? keys);
        Armed = known ? new HashSet<int>(keys!) : new HashSet<int>();
        Version++;
        log.Info(known
            ? $"HotkeyStore: token recognised -- restored {Armed.Count} consented key(s)"
            : "HotkeyStore: token not recognised -- armed set empty, awaiting fresh consent");
        return known;
    }

    /// <summary>
    /// Add player-consented keys to the ACTIVE server's set and persist. Only called after an explicit
    /// OK in the consent modal. If the server presented no token this session, mint one so the consent
    /// has a receipt to hand back. Returns the active token (the caller sends it to the server).
    /// </summary>
    public static string Grant(HashSet<int> keys, ExtenderLog log)
    {
        EnsureLoaded(log);
        if (_activeToken == null)
        {
            _activeToken = Mint();
            log.Info("HotkeyStore: no server token this session -- minted a fresh consent token");
        }
        if (!_sections.TryGetValue(_activeToken, out HashSet<int>? set))
            set = _sections[_activeToken] = new HashSet<int>();
        set.UnionWith(keys);

        Armed = new HashSet<int>(set);
        Version++;
        Save(log);
        log.Info($"HotkeyStore: player armed {keys.Count} new key(s) for this server; {set.Count} total");
        return _activeToken;
    }

    /// <summary>
    /// Replace the ACTIVE server's set wholesale and persist. Used for silent removals and the
    /// manager's unbind/revoke-all. No active token -> no-op.
    /// </summary>
    public static void Set(HashSet<int> keys, ExtenderLog log)
    {
        EnsureLoaded(log);
        if (_activeToken == null) return;
        _sections[_activeToken] = new HashSet<int>(keys);
        Armed = new HashSet<int>(keys);
        Version++;
        Save(log);
    }

    /// <summary>A fresh, crypto-random 128-bit token as 32 lowercase hex chars.</summary>
    private static string Mint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// One runnable check on the leak-critical invariant: a token the client never granted must restore
    /// NOTHING (so a malicious server presenting a random token can't arm keys), and the default posture
    /// is empty. Run once at startup; ends fail-closed.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        string ghost = new string('f', 32);                       // a token no server was ever granted
        bool unknownEmpty = !Activate(ghost, log) && Armed.Count == 0;   // unknown -> empty
        BeginSession();                                           // reset to the real startup posture
        bool freshEmpty = Armed.Count == 0 && _activeToken == null;
        bool tokenOk = Mint().Length == 32;
        log.Info(unknownEmpty && freshEmpty && tokenOk
            ? "HotkeyStore: fail-closed self-check passed"
            : "HotkeyStore: FAIL-CLOSED SELF-CHECK FAILED -- consent default is unsafe");
    }

    public static void Save(ExtenderLog log)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var lines = new List<string>
            {
                "# sowx hotkeys -- per-server consent. One [token=<hex>] section per server;",
                "# one SDL keycode per line under it. Deleting a section revokes that server's consent.",
            };
            foreach (var (tok, keys) in _sections)
            {
                lines.Add("");
                lines.Add($"[token={tok}]");
                foreach (int code in keys) lines.Add(code.ToString());
            }
            File.WriteAllLines(Path, lines);
        }
        catch (Exception e) { log.Info($"HotkeyStore: save failed: {e.Message}"); }
    }
}
