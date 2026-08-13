namespace Sowx.Framework;

// Inject this instead of reimplementing logging. Writes stderr + /tmp/sowx.log.
public sealed class ExtenderLog
{
    public void Info(string message)
    {
        string line = $"[sowx] {message}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText("/tmp/sowx.log", $"{DateTime.Now:O} {line}\n"); }
        catch { /* log is best-effort */ }
    }
}
