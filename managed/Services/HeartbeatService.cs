using Sowx.Framework;

namespace Sowx.Services;

// Simplest possible service: proves discovery + constructor DI with no native
// code. Delete once real services exist.
public sealed class HeartbeatService : IExtenderService
{
    private readonly ExtenderLog _log;

    public HeartbeatService(ExtenderLog log) => _log = log;

    public void Initialize() => _log.Info("HeartbeatService: initialized (DI works)");
}
