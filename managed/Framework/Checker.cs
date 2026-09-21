using System.Runtime.CompilerServices;

namespace CX.Framework;

/// <summary>The one self-check assert (CX-697): a failed expectation flips <see cref="Ok"/> and logs
/// "<c>{prefix} FAIL: {what}</c>". The prefix defaults to the calling file's stem up to its first dot plus
/// ".SelfCheck" (so a partial-class file names its class); a suite that prints another name passes it.</summary>
public sealed class Checker
{
    public bool Ok = true;
    private readonly ExtenderLog _log;
    private readonly string _prefix;

    public Checker(ExtenderLog log, string? prefix = null, [CallerFilePath] string file = "")
    {
        _log = log;
        if (prefix == null)
        {
            int start = file.LastIndexOfAny(['/', '\\']) + 1;
            int end = file.IndexOf('.', start);
            prefix = (end < 0 ? file[start..] : file[start..end]) + ".SelfCheck";
        }
        _prefix = prefix;
    }

    public void Expect(bool cond, string what)
    {
        if (cond) return;
        Ok = false;
        _log.Info($"{_prefix} FAIL: {what}");
    }
}
