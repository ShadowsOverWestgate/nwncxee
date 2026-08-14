namespace Sowx.Framework;

/// <summary>
/// Implement this and the host discovers, constructs, and initializes it
/// automatically. The only contract a contributor needs.
/// </summary>
public interface IExtenderService
{
    /// <summary>Wire hooks and state here. Runs once, before hooks are committed.</summary>
    void Initialize(ExtenderLog log, HookService hooks);
}
