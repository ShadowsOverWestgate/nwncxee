using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Sowx.Framework;

// The "hard work" contributors don't touch: build the DI container, discover
// every IExtenderService, construct them with their dependencies, initialize.
public static class ServiceHost
{
    private static ServiceProvider? _provider;

    public static void Run()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ExtenderLog>();
        services.AddSingleton<HookService>();

        foreach (Type impl in DiscoverServices())
            services.AddSingleton(typeof(IExtenderService), impl);

        _provider = services.BuildServiceProvider();
        var log = _provider.GetRequiredService<ExtenderLog>();

        var all = _provider.GetServices<IExtenderService>().ToList();
        log.Info($"host: {all.Count} service(s) discovered");
        foreach (IExtenderService service in all)
        {
            try { service.Initialize(); }
            catch (Exception e) { log.Info($"host: {service.GetType().Name} init threw: {e.Message}"); }
        }

        _provider.GetRequiredService<HookService>().Commit();
    }

    private static IEnumerable<Type> DiscoverServices() =>
        Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IExtenderService).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false });
}
