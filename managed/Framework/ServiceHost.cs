using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sowx.Framework;

/// <summary>
/// The "hard work" contributors don't touch: discover every IExtenderService,
/// construct it, hand it the shared singletons, initialize, then commit all hooks.
/// </summary>
public static class ServiceHost
{
    // Reflection over our own assembly is trim-safe here: the csproj roots sowx_managed
    // whole (TrimmerRootAssembly), so no service type or its ctor is ever removed.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly is rooted; nothing trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Assembly is rooted; parameterless ctors preserved.")]
    public static void Run()
    {
        var log = new ExtenderLog();
        // Which build produced the lines below. build.sh already stamps this from `git describe`, and a log
        // that does not say it is a log you cannot act on: half a debugging round trip is spent establishing
        // whether the run even contains the fix being discussed.
        log.Info("sowx build " + (Assembly.GetExecutingAssembly()
                     .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                     .InformationalVersion ?? "unknown"));
        var hooks = new HookService(log);

        // OrderBy(Name): Assembly.GetTypes() order is not stable across builds/runtimes,
        // so sort for deterministic init order. Replace with an explicit list if a real
        // init-time dependency ever appears.
        var all = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IExtenderService).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name)
            .Select(t => (IExtenderService)Activator.CreateInstance(t)!)
            .ToList();

        log.Info($"host: {all.Count} service(s) discovered");
        // Shared engine accessors, before any service can reach for one. AurTex is the only real init-time
        // dependency in this host so far -- four services call it and none of them owns it.
        AurTex.Attach(log);
        AurTex.SelfCheck(log);
        CommandRegistry.Global.Log = log;
        CommandRegistry.SelfCheck(log);   // #165: registration/dispatch/enumeration proof, off-device
        // #164: the shared config module's line walker + deferral gate. Proven at the host, so a sixth
        // caller inherits the rule rather than re-deriving it. Run()'s caller IS pre-main, so the gate's
        // "not yet" branch is the one under test here.
        ConfigFile.SelfCheck(log);
        FrameRegistry.SelfCheck(log);   // #170: walk order, post-seal refusal, and the zero-alloc AC
        foreach (IExtenderService service in all)
        {
            try { service.Initialize(log, hooks); }
            catch (Exception e) { log.Info($"host: {service.GetType().Name} init threw: {e.Message}"); }
        }

        // #170: the frame walk must be immutable before the present hook can fire. Sealing here is the
        // happens-before -- a registration after this point is refused, not silently dropped.
        FrameRegistry.Global.Seal();
        foreach (FramePhase p in new[] { FramePhase.Start, FramePhase.PrePresent, FramePhase.PostPresent })
            log.Info($"host: frame {p} -> {string.Join(", ", FrameRegistry.Global.Names(p))}");
        hooks.Commit();
    }
}
