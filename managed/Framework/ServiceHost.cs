using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CX.Framework;

/// <summary>
/// The "hard work" contributors don't touch: discover every IExtenderService,
/// construct it, hand it the shared singletons, initialize, then commit all hooks.
/// </summary>
public static class ServiceHost
{
    // Reflection over our own assembly is trim-safe here: the csproj roots cx_managed
    // whole (TrimmerRootAssembly), so no service type or its ctor is ever removed.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly is rooted; nothing trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Assembly is rooted; parameterless ctors preserved.")]
    public static void Run()
    {
        var log = new ExtenderLog();
        // Which build produced the lines below. build.sh already stamps this from `git describe`, and a log
        // that does not say it is a log you cannot act on: half a debugging round trip is spent establishing
        // whether the run even contains the fix being discussed.
        log.Info("NWNCX:EE build " + (Assembly.GetExecutingAssembly()
                     .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
                     .InformationalVersion ?? "unknown"));
        log.Info($"host: GC server={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode} cpus={Environment.ProcessorCount}");
        // #457: the one rot gate, and the whole extender is behind it -- both platforms, ahead of DISCOVERY
        // rather than ahead of Initialize, because constructing a service runs its type initializer and a
        // type initializer is enough to latch engine state. Nothing constructed, nothing hooked, so no
        // feature can half-fire against a client these offsets were never measured against. The build key is
        // the linker's build identity, the only lock that exists on both platforms (EngineLayout).
        if (!EngineLayout.KeyOk)
        {
            string rot = $"client build changed, extender standing down WHOLE -- including the Alloy renderer. " +
                         $"Observed build {EngineLayout.BuildKey}, expected {EngineLayout.ExpectedKey}. " +
                         "The game runs vanilla (re-measure: docs/engine-layout.md).";
            log.Always("host: " + rot);
            log.Always("host: 0 service(s) discovered, 0 hook(s) installed");
            SymbolMap.RotNotice("NWNCX:EE: " + rot);   // Windows-only: a version.dll proxy has no log the player reads
            ExtenderLog.Flush();   // Always only enqueues, and nothing else in this process will drain it
            return;
        }

        // ADR 0013 bucket 2, and this is as early as it goes: ahead of DISCOVERY, not just ahead of
        // Initialize. Constructing a service runs its type initializer, and GameGate latches `lockchat` in
        // one -- so a load after this line would pin the opt-out to its default and no-op it silently.
        Settings.Load(log);
        var hooks = new HookService(log);   // after Load: it reads hookdebug

        // OrderBy(Name): Assembly.GetTypes() order is not stable across builds/runtimes,
        // so sort for deterministic init order. Replace with an explicit list if a real
        // init-time dependency ever appears.
        var all = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IExtenderService).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name)
            .Select(t => (IExtenderService)Activator.CreateInstance(t)!)
            .ToList();

        // #456: nothing is filtered here any more. Alloy was the last Linux-only namespace and it came off
        // with the stand-down lift; its Windows refusal, if any, is EngineLayout.Require inside AlloyGate.
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
        ExtenderLog.SelfCheck(log);   // the disabled log evaluates nothing, the enabled one reaches the file
        Settings.SelfCheck(log);   // #183: parse degrade, range rejection, and that /knobs lists only real keys
        KnobSurface.Register();
        EngineLayout.SelfCheck(log);   // #432: the three resolution forms, the counter, and a probe that fires
        EngineLayout.Register();
        SymbolMap.Register();
        FrameRegistry.SelfCheck(log);   // #170: walk order, post-seal refusal, and the zero-alloc AC
        CX.Ui.AtlasSheet.SelfCheck(log);   // #469: pack-and-grow, ceiling refusal, face lookup -- no GL, no stbtt
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
        ExtenderLog.Flush();   // boot lines on disk before the host returns: the demo exits right after
    }
}
