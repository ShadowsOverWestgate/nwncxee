using Sowx.Framework;

namespace Sowx.Services;

// The consent list and its persistence. The client does NOT map keys to toolbar
// slots -- it does not know the server's toolbar layout. It only "arms" a set of
// keys the player consents to have sent; the server decides what each does.
//
// The armed set is normally chosen in the server-authored NUI and pushed to us
// (HotkeyServerLinkService), then cached in a client-side file. That file is also
// a manual override the player may edit, and it is loaded on startup so armed keys
// work before the NUI is ever opened. Either way it is the effective consent: only
// keys in this set are ever observed or sent (see HotkeyInputService).
//
//   ~/.config/sowx-hotkeys.cfg   one VK-style keycode per line (# starts a comment)
//   (e.g. F1 = 112 ... F12 = 123)
//
// Static, not DI: the hot key handler is a static [UnmanagedCallersOnly] and reads
// Armed directly. Load()/Save() republish an immutable snapshot atomically.
public static class HotkeyStore
{
    public static volatile HashSet<int> Armed = new();

    public static string Path =>
        System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? "/tmp", ".config", "sowx-hotkeys.cfg");

    public static void Load(ExtenderLog log)
    {
        var set = new HashSet<int>();
        try
        {
            if (File.Exists(Path))
                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line[..hash];
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (int.TryParse(line, out int vk)) set.Add(vk);
                }
        }
        catch (Exception e) { log.Info($"HotkeyStore: load failed: {e.Message}"); }

        Armed = set;   // atomic publish; readers see old or new, both immutable
        log.Info($"HotkeyStore: {set.Count} key(s) armed, from {Path}");
    }

    public static void Save(ExtenderLog log)
    {
        try
        {
            var snapshot = Armed;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var lines = new List<string> { "# sowx hotkeys -- armed keys (one VK code per line)" };
            foreach (int vk in snapshot) lines.Add(vk.ToString());
            File.WriteAllLines(Path, lines);
        }
        catch (Exception e) { log.Info($"HotkeyStore: save failed: {e.Message}"); }
    }
}
