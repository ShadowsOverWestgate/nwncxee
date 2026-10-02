---
name: server-native-conventions
description: >-
  Server-side reference ported from sow-codebase; adapt before applying it to the client (see .claude/skills/README.md). Use when writing, reviewing, or debugging any NWN:EE server code using NWN.Anvil, NWNX:EE, or native CNWSCreature/CNWSItem APIs. Triggers on: service bindings, feat scripts, special attacks, function hooks, NUI windows, native API access, threading/multithreading, memory safety, chat commands, or any mention of NwModule/NwPlayer/NwCreature.
---

# NWN Anvil Conventions (SoW)

Everything below is enforced at runtime or not at all. Finish with a green
integration run — load the `integration-testing` skill before you report done.

## Project Structure

- **Core** – Main plugin: services, helpers, feat scripts, hooks
- **ChatCommands** – Chat command handlers (`-cleanup`, `-clock`, etc.)
- **AsyncDatabase** – Database access
- **Settings** – Configuration service
- **IntegrationTests** – Tests running inside the NWN server

Source root: `src/`. Build: `dotnet build` in `src/`.

## Anvil Services

- Use `[ServiceBinding(typeof(ServiceName))]` on service classes
- Use `[ServiceBindingOptions(BindingPriority.X)]` when load order matters (e.g. `BindingPriority.Highest` for SettingsService)
- Implement `IDisposable` when subscribing to events; unsubscribe in `Dispose()`
- For startup-critical services: implement `IReadinessAware` and call `readinessService.RegisterService(this)`

```csharp
[ServiceBinding(typeof(MyService))]
public class MyService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public MyService()
    {
        NwModule.Instance.OnSomeEvent += OnSomeEvent;
    }

    public void Dispose() => NwModule.Instance.OnSomeEvent -= OnSomeEvent;
}
```

## Logging

Use NLog:

```csharp
private static readonly Logger Log = LogManager.GetCurrentClassLogger();
Log.Info("message");
Log.Warn("warning");
Log.Error(ex, "error");
```

If you're trying to debug something now, rather than give a permanent log about behavior, you should use `Log.Info`.

## NWN API Types

- **Anvil wrappers**: `NwModule`, `NwPlayer`, `NwArea`, `NwCreature`, `NwGameObject`, `NwItem`
- **Native API** `CNWSCreature`, `CNWSItem`

Almost all Anvil wrappers can be cast directly into Native API equivalents (e.g. `(CNWSCreature)creature!`). Those that cannot must use the below pattern:

```csharp
private static readonly CServerExoApp _server;


static CharacterSheetUIHandler()
{
    _server = NWNXLib.AppManager().m_pServerExoApp ?? throw new InvalidOperationException("Server ExoApp was null");
}

// ...

CNWSCreature creature = _server.GetGameObject(args.Player.m_oidPCObject).AsNWSCreature();
```

In general, using Native API is more desirable than Anvil wrappers for several reasons:

1. Native API, while harder to use, can be used from any thread
2. Anvil wrappers generally utilize NWScript VM thread, which makes asynchronous code tedious/risky to write
3. Native APIs give us access to things that Anvil wrappers can't reach, or Anvil developers haven't built a wrapping for yet.
4. Performance gain is debatable, but native APIs are definitely more flexible.

When deciding when to use an Anvil wrapper or a native API, consider the following table:

| Context            | Use                         |
| ------------------ | --------------------------- |
| Function hook      | Native APIs only            |
| Built-in event     | Anvil wrappers preferred    |
| NUI window         | Anvil wrappers required     |
| Async/offload work | NwTask + SwitchToMainThread |

## Area iteration goes through AreaSpatialQuery

**Any iteration over the contents of an area goes through `Core.Helpers.AreaSpatialQuery`.** `AreaIterationRuleTests` fails the build with the file and line when a hand-rolled one appears. The Anvil iterators also share one engine-global cursor, so destroying, spawning or subscribing inside such a loop is a live invalidation hazard the helper's buffer removes.

```csharp
// WRONG: VM iterator, global cursor, filters after wrapping
foreach (var door in area.FindObjectsOfTypeInArea<NwDoor>())

// RIGHT: native filter first, into a buffer you own
List<NwDoor> doors = [];
AreaSpatialQuery.FindObjectsInArea(area, SpatialTypes.Doors, doors);
```

The shapes it replaces, the four call-site rules, and how to mark a deliberate exception: `## Area Iteration` in `docs/developer-conventions.md`. Read it before your first conversion.

## Addressing players (DM avatar trap)

**Never enumerate players by mapping area creatures through `NwCreature.ControllingPlayer`** — it is `null` for DM avatars, so DMs silently drop out of the loop. This shipped a bug where every mid-session sky-uniform send skipped DMs and looked like an engine limitation (weeks lost to a phantom "shader latch"). Iterate the player list instead:

```csharp
// WRONG: yields nothing for a DM alone in the area
area.FindObjectsOfTypeInArea<NwCreature>().Select(c => c.ControllingPlayer).Where(p => p != null)

// RIGHT: covers PCs, possessed creatures, and DM avatars
AreaSpatialQuery.FindPlayersInArea(area, players);
```

Same trap in event handlers: `if (e.EnteringObject is NwCreature { ControllingPlayer: { } player })` skips DMs. Resolve via `NwModule.Instance.Players.FirstOrDefault(p => p.ControlledCreature == creature)`.

## Shader uniforms are live (no engine latch)

`SetShaderUniform*` reaches connected clients **immediately, mid-session** — decomp-verified: the client packet handler writes a global uniform table and every shader program re-reads it on the next bind (dirty-seq). There is **no** stored login table, no scene latch, and nothing client-side ever reverts the values. The engine stores nothing server-side either; "applies at login" only ever means *our* `OnClientEnter` handler re-sent it.

If a uniform appears stale or "reverts", the cause is server-side in our code: the send never happened (see DM avatar trap above), a wrong slot index (`SHADER_UNIFORM_1` == slot 0), or another service re-sending old values. Do not add re-assert timers or self-heal loops to fight it — find the missing/stale send. Prefer the player-addressed raw packet `CNWSMessage.SendServerToPlayerSetShaderUniform_*(player, ...)` when the recipient may be a DM or possessing; the script-level call resolves the client from a creature object id.

## Feat Scripts

- Location: `Core/src/Services/FeatScriptService/Scripts/`
- Implement `IFeatScriptProvider`, use `FeatScriptBuilder`
- Generate skeleton: `./featgen.sh -i [FEAT_ID] -n "[FEAT NAME]"` (from `src/`)

```csharp
[ServiceBinding(typeof(IFeatScriptProvider))]
public class MyFeat : IFeatScriptProvider
{
    public IFeatScript GetScript() => new FeatScriptBuilder()
        .ForFeat(featId)
        .Build();
}
```

## Special Attacks

- Use `AddsSpecialAttackOnUse(specialAttackId)` on `FeatScriptBuilder`
- Check chain: `.WithCheck().Requires(...).AttackerScore(...).DefenderScore(...).OnLanded(...).OnResisted(...).Done()`
- Helpers: `DisarmHelper`, `ToppleHelper`, etc. in `Core/src/Helpers/SpecialAttacks/`
- Reference: `Disarm.cs`, `BullRush.cs`, `Charge.cs`

## Chat Commands

- Extend `Command` base class
- Register in `ChatCommandsService` constructor: `_commands.Add("name", new MyCommand(...))`
- Commands use `-` prefix (e.g. `-cleanup`)
- Reference: `CleanupCommand.cs`, `ClockCommand.cs`

## Service Injection

Anvil injects services via constructor. Add dependencies as constructor parameters; no manual registration needed for `[ServiceBinding]` types.

## Multithreading

This codebase must always make a heavy use of multithreading while also adhering to best thread-safety practices. Whenever it's possible, use asynchronous code to ensure that the NWN server threads are unobstructed from handling player information.

When you are in NWScript VM context, you can utilize `Offload` class to run code in a model of Collect->Offload->Switch to main VM thread. The `Offload` class is unneeded in function hook context; there you can just utilize regular async/await behaviors.

The application is meant to handle up to a hundred of players simultaneously. Every choice you make must keep in mind performance and stability.

Before using `Task` objects, first check if `NwTask` has an equivalent; it's better to use `NwTask` due to domain-specific thread handling. In general, before running a VM thread code you must first use `await NwTask.SwitchToMainThread()`. You can also use `SchedulerService.SafeSchedule` to force a piece of code to run on the VM thread (optionally with a delay), however that is discouraged and is considered a last-resort option.

## Memory safety

NWScript VM context (and thus Anvil wrappers) are considered fully memory safe. However, when working with native APIs, you must keep in mind a "double free" problem.

If you have to run a native function that requires an object as an input, you must never use a C# constructor. Instead, you must follow this pattern:

```csharp
IntPtr ptr = NWNXLibPINVOKE.new_CScriptEvent();

var ev = CScriptEvent.FromPointer(ptr, false);
ev.m_nType = SCRIPT_EVENT_TYPE_ON_PLAYER_LEVEL_UP;

_server.m_pcExoAppInternal.m_pServerAIMaster.AddEventAbsoluteTime(
        0,
        0,
        creature.m_idSelf,
        module.m_idSelf,
        AI_MASTER_EVENT_SIGNAL_EVENT,
        (void*)ptr
);
```

This pattern forces the object to be created by the NWN server, so it's memory is managed by NWN server rather than our .NET runtime. In addition, you should never hold a raw `CNWSCreature*` across async boundaries.

## Testability

We work fully TDD: write the test first, and never ship logic uncovered.

"It needs a live server / an Anvil service container" is not an exemption — it
describes the shape of the code, not a limit of the test suite. Split the
service into a thin Anvil-bound shell that only touches the engine, and a
plain-C# core that holds every decision behind seams. Test the core
exhaustively. See `StateSnapshotService` / `StateSnapshotRebuilder` for a worked
example, and the "Testing VM-thread code" section of the `server-threading` skill
for the full rule.

Since our code is tracked in Prometheus+Grafana, measure any non-trivial path
that runs on the VM thread. Declare a histogram once and time the synchronous
body with it, following
`Core/src/Services/MetricsService/FrameMetricsService.cs`:

```csharp
private static readonly Histogram ClientLeaveDuration = Metrics.CreateHistogram(
    "nwn_afk_client_leave_seconds",
    "Main-loop time spent tearing down AFK tracking for a leaving player.",
    new HistogramConfiguration
    {
        Buckets = Histogram.ExponentialBuckets(0.000005, 2, 12),
    });

using (ClientLeaveDuration.NewTimer())
{
    if (eventData.Player != null)
    {
        _trackedPlayers.TryRemove(eventData.Player, out _);
    }
}
```

**There is no `MainThreadMetrics` class.** Earlier revisions of this skill and of
`server-threading` told you to call one, with two different APIs. It has never
existed in this codebase; anything written against it will not compile.

## Function Hooks

- Use [ThreadStatic] for cross-hook state
- Never capture native pointers into lambdas — capture uint IDs and re-resolve
- Never call VM-dependent methods on hook threads
