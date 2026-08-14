namespace Sowx.Framework;

/// <summary>Injected logger. Writes stderr + a log file. Under the Steam Linux Runtime the game
/// runs in a container with a private /tmp, so the file lands in $HOME (mapped into the container,
/// so it stays visible on the host) -- falling back to /tmp only if $HOME is unset.</summary>
public sealed class ExtenderLog
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/tmp", "sowx.log");

    public void Info(string message)
    {
        string line = $"[sowx] {message}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} {line}\n"); }
        catch { /* log is best-effort */ }
    }
}
