---
name: server-threading
description: >-
  Server-side reference ported from sow-codebase; adapt before applying it to the client (see .claude/skills/README.md). Covers thread safety, offloading work off the main game thread, and async
  patterns for the SoW NWN:EE server codebase. Use this skill whenever writing
  or reviewing any code that involves async/await, Task, NwTask, background
  work, database access, timers, scheduled callbacks, hook context, or any
  situation where an operation might block or delay the NWN server's main
  thread. Trigger also when the user mentions lag, server performance, player
  experience, tick rate, or "slow operations". Prefer this skill over general
  C# async knowledge — SoW has domain-specific threading rules that override
  standard .NET conventions.
---

# SoW Threading Guide

The NWN server runs player-facing game logic on a single VM thread. Every
millisecond that thread is busy is a millisecond players are waiting. The
golden rule:

> **Keep the VM thread fast. Move everything slow off it.**

"Slow" means: database queries, file I/O, HTTP calls, heavy computation,
anything with unpredictable latency. Even a 5 ms database hit that fires for
every player on every heartbeat is 500 ms of cumulative stall on a 100-player
server.

---

## Thread Landscape

| Thread             | What runs here                                       | Rules                                               |
| ------------------ | ---------------------------------------------------- | --------------------------------------------------- |
| **VM thread**      | NWScript, Anvil events, NUI, game state reads/writes | Never block. Never do I/O.                          |
| **Hook threads**   | NativeHookRegistry Before/After callbacks            | Native APIs only. No Anvil wrappers. No VM calls.   |
| **Worker threads** | `Task.Run`, `NwTask`, `Offload`, DB, HTTP            | No direct game state writes without switching back. |

Mixing these up is the #1 source of crashes and threading bugs in this codebase.

---

## Decision Tree: Where Does My Code Run?

```
Am I inside a NativeHookRegistry callback?
├── YES → Native APIs only. Use async/await freely for off-thread work.
│         Never call Anvil wrappers. Never touch NWScript VM.
│         To write game state: capture uint IDs now, schedule a VM callback.
└── NO → Am I inside an Anvil event handler?
         ├── YES → Anvil wrappers are safe here.
         │         For slow work: use Offload (collect → offload → switch back).
         └── NO → Am I on a worker/background thread?
                  └── YES → await NwTask.SwitchToMainThread() before any
                            Anvil wrapper or game state write.
```

---

## Pattern 1: Offload (VM event context)

Use when you start on the VM thread (e.g. an Anvil event handler) and need
to do something slow before writing a result back.

**Prefer the `Offload` helper** (`Core.Helpers.Offload`) — it enforces the
collect→compute→switch pipeline and handles the `SwitchToMainThread` for you:

```csharp
private async void OnPlayerRest(ModuleEvents.OnPlayerRest args)
{
    var healAmount = await Offload
        .Collect(() => new { args.Player.m_oidPCObject, args.Player.Constitution })
        .Compute(data => CalculateHeal(data.Constitution))          // background thread
        .Validate(result => NwObject.FindById<NwPlayer>(data.m_oidPCObject)?.IsValid) // back on VM thread
        .OnInvalid(() => 0)
        .RunAsync();

    var player = NwObject.FindById<NwPlayer>(args.Player.m_oidPCObject);
    player?.LoginCreature?.ApplyEffect(..., healAmount);
}
```

`Offload` stages:

1. **`.Collect(...)`** — runs immediately on the VM thread; snapshot all game state needed.
2. **`.Compute(...)`** — runs on a background thread; use only the snapshot, never touch NWN APIs.
3. **`.Validate(...)`** _(optional)_ — runs back on the VM thread; check objects are still valid.
4. **`.OnInvalid(...)`** _(optional)_ — fallback value if validation fails.
5. **`.RunAsync()`** — executes the pipeline; awaitable.

Use the void overload (`.Compute(Action<TData>)`) when you don't need a return value.

**Fall back to manual `NwTask` only when `Offload` is too rigid** (e.g. multiple
async stages, conditional branching mid-pipeline):

```csharp
private async void OnLogin(ModuleEvents.OnClientEnter args)
{
    uint playerId = args.Player.m_oidPCObject;
    string cdKey = args.Player.CDKey;                    // collect on VM thread

    var record = await _db.QueryAsync<PlayerRecord>(cdKey); // worker thread

    await NwTask.SwitchToMainThread();                   // back to VM thread
    NwObject.FindById<NwPlayer>(playerId)?.Apply(record);
}
```

---

## Pattern 2: Hook Context → Scheduled Callback

Use when you're inside a hook and need to trigger a VM-thread action (floaty
text, applying effects, firing events).

```csharp
// WRONG — Anvil wrapper called from hook thread. Will crash or corrupt state.
private void OnAttackHook(...)
{
    attacker.FloatingTextString("Hit!"); // ❌ VM call on hook thread
}

// CORRECT — capture the ID, fire-and-forget onto the VM thread.
private void OnAttackHook(CNWSCreature* attacker, ...)
{
    uint attackerId = attacker->m_idSelf; // capture primitive, not pointer

    _schedulerService.SafeSchedule(() =>
    {
        var obj = NwObject.FindById<NwCreature>(attackerId);
        obj?.FloatingTextString("Hit!");    // ✅ safe on VM thread
    });
}
```

**Never capture native pointers into scheduled lambdas.** The pointer may be
freed by the time the callback runs. Always capture `uint` object IDs and
re-resolve inside the callback.

---

## Pattern 3: Cross-Hook State

When two hooks need to share transient data within the same engine call chain
(e.g. a Before hook sets a flag the After hook reads), use `[ThreadStatic]`.

```csharp
[ThreadStatic]
private static bool _wasSpecialAttack;

// Before hook sets the flag
private static Hook.Result BeforeAttack(CNWSCreature* attacker, ...)
{
    _wasSpecialAttack = IsSpecialAttack(attacker);
    return Hook.Result.Continue;
}

// After hook reads it
private static void AfterAttack(CNWSCreature* attacker, ...)
{
    if (_wasSpecialAttack) { ... }
    _wasSpecialAttack = false; // always reset
}
```

`[ThreadStatic]` means each thread gets its own copy — no locking needed, no
cross-thread contamination. Always reset the value in the After hook or in a
finally block.

---

## Pattern 4: Database Access

Database calls are I/O and must never block the VM thread.

```csharp
// WRONG
private void OnLogin(ModuleEvents.OnClientEnter args)
{
    var record = _db.Query<PlayerRecord>(args.Player.CDKey); // ❌ blocks VM thread
}

// CORRECT
private async void OnLogin(ModuleEvents.OnClientEnter args)
{
    uint playerId = args.Player.m_oidPCObject;
    string cdKey = args.Player.CDKey;         // collect on VM thread

    var record = await _db.QueryAsync<PlayerRecord>(cdKey); // off-thread

    await NwTask.SwitchToMainThread();         // back to VM thread to apply
    NwObject.FindById<NwPlayer>(playerId)?.DoSomething(record);
}
```

If the database service uses `AsyncDatabase`, it already handles the threading
internally — but you still need to `SwitchToMainThread` before writing game
state with the result.

---

## Pattern 5: Measuring VM Thread Impact

All non-trivial paths that touch the VM thread must be measured. Declare a
Prometheus histogram on your service and time the synchronous body with it.
`Core/src/Services/MetricsService/FrameMetricsService.cs` is the reference.

```csharp
private static readonly Histogram RebuildDuration = Metrics.CreateHistogram(
    "nwn_state_snapshot_rebuild_seconds",
    "Main-loop time spent rebuilding the published server state document.",
    new HistogramConfiguration
    {
        Buckets = Histogram.ExponentialBuckets(0.000005, 2, 12),
    });

using (RebuildDuration.NewTimer())
{
    // code that runs on the VM thread
}
```

Pick buckets around the duration you expect; the default set starts at 5 ms and
is far too coarse for anything on this thread. Name the metric
`nwn_<thing>_<unit>` and declare it once in a `static readonly` field, as every
existing metrics service does — never per call site or per instance.

To assert on a metric in a test, build the collector against an isolated
registry (`Metrics.NewCustomRegistry()`) and inject it, so the assertion does
not depend on what else ran first. `StateSnapshotRebuilderTests` does this.

If a path conditionally goes off-thread, measure only the synchronous portion.
This data flows into Prometheus/Grafana — spikes here are player-visible lag.

**There is no `MainThreadMetrics` class.** Earlier revisions of this skill and
of `server-native-conventions` told you to call one, with two different APIs. It has
never existed in this codebase; anything written against it will not compile.

---

## Common Mistakes

### Mistake: `Task.Delay` on the VM thread

```csharp
// WRONG — blocks the VM thread for 1 second
await Task.Delay(1000);

// CORRECT
await NwTask.Delay(TimeSpan.FromSeconds(1));
```

Always use `NwTask` equivalents over raw `Task` when one exists.

### Mistake: Managed `DelayCommand` in hook context

`NwScript.DelayCommand` and Anvil's `DelayCommand` both require the VM thread.
Calling them from a hook will cause a threading error. Schedule via `SchedulerService` instead (as a last resort). In general, if you think you are forced to use SchedulerService, first check if what you want to do is possible with native APIs.

### Mistake: Locking on the VM thread

`lock` and `Monitor.Wait` block the calling thread. If you lock on the VM thread
waiting for a worker thread to release, you stall the entire game. Use
lock-free structures (`ConcurrentDictionary`, `Interlocked`) or ensure locks
are only ever taken on worker threads.

### Mistake: Holding game object references across await points

```csharp
var creature = args.Player.LoginCreature; // NwCreature reference
await SomeSlowWork();                     // VM thread yields here
creature.ApplyEffect(...);                // ❌ creature may be invalid/GC'd
```

Re-resolve via `NwObject.FindById<T>(id)` after every `await`.

For destructive economy-style steps (Destroy, StackSize decrement, AcquireItem),
use `LiveObjectResolver` (`Services/EconomyService/Transactions/`) —
`ResolveConsumableItem(itemId, possessorId, quantity)`
and `ResolveControlledCreature(player, creatureId)` re-resolve on the main thread and return
`null` unless the object is still the same live one that was validated before the await.
The decision table lives in the pure `EconomyTransactionGuard` (unit-tested). Capture the
character id with `CharacterIdentity.TryGetFromPlayer` *before* the first await and use the
`*ByCharacterIdAsync` database methods for refunds/rollbacks, so compensation still works
after the player disconnects.

---

## Quick Reference

| I need to...                  | Use                                              |
| ----------------------------- | ------------------------------------------------ |
| Run slow work off VM thread   | `await NwTask.Run(() => ...)`                    |
| Write game state after async  | `await NwTask.SwitchToMainThread()`              |
| Delay without blocking        | `await NwTask.Delay(...)`                        |
| Share state between hooks     | `[ThreadStatic]` field                           |
| Schedule VM call from hook    | `Task.Run` + `await NwTask.SwitchToMainThread()` |
| Measure VM thread cost        | `Metrics.CreateHistogram(...)` + `.NewTimer()`   |
| Force VM thread (last resort) | `SchedulerService`                               |

---

## Performance Targets

With 100 simultaneous players, every VM-thread operation in a per-player
heartbeat runs 100 times per tick. Rule of thumb:

- **< 0.1 ms** per call: fine on VM thread
- **0.1 – 1 ms**: consider offloading, definitely measure
- **> 1 ms** (or any I/O): must offload, no exceptions

If a service's histogram shows consistent spikes above 5 ms, that service needs
profiling.

---

## Testing VM-thread code

"It needs a live module and an Anvil service container" is not a reason to leave
logic untested — it describes the shape of the code. Split the service in two:

- a **thin shell** that owns the Anvil surface (the `SafeScheduler` loop, the
  `await NwTask.Delay`, the `NwModule.Instance` reads, the injected Anvil
  services) and holds no decisions;
- a **plain-C# core** holding every decision, taking a seam for anything
  engine-bound, and constructible in a test with no Anvil type present.

Test the core exhaustively. Worked examples in this codebase:
`StateSnapshotService` / `StateSnapshotRebuilder`, and `SettingsService` /
`ConfigOverrideApplier`.

Prefer a **synchronous, non-`async` core method**. "This completes inside one
frame" then becomes compiler-enforced instead of a claim in a doc comment — an
`async` core can yield mid-way and split its work across frames.

Assert that an engine-bound seam was *not called* where that is the point, not
merely that the result was zero. Skipping the player walk while the module is
still loading is a behaviour; a zero count is only its symptom.

What genuinely cannot be tested is an Anvil framework guarantee — for example
that `NwTask.Delay` resumes on the main loop. Gather evidence for those
(decompile and cite it in the class doc); do not let them cover our own logic.
