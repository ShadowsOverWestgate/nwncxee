---
name: server-hooking
description: >-
  Server-side reference ported from sow-codebase; adapt before applying it to the client (see .claude/skills/README.md). Covers everything about hooking NWN engine functions in the SoW codebase via
  NativeHookRegistry and HookPoint. Use this skill whenever working with
  existing hook points, adding subscribers to NativeHookRegistry, discovering
  what functions are available to hook, finding a function's symbol in the
  nwserver-linux binary, writing HookArgs context classes, choosing the right
  HookPoint type, or deciding whether to use Before/After/Modify. Also trigger
  when the user asks about SkipOriginal, hook priority, CallOriginal, or wants
  to intercept any NWN engine behaviour.
---

# SoW Hook System

The SoW hook system has two layers:

- **`NativeHookRegistry`** — a singleton Anvil service that owns one
  `FunctionHook` per intercepted engine function and dispatches calls through
  typed `HookPoint` instances. This is the only place `FunctionHook` and
  `[NativeFunction]` are used.
- **`HookPoint<TArgs>`** — a pub/sub bus that subscribers attach to. Services
  never touch `FunctionHook` directly; they subscribe to hook points on
  `NativeHookRegistry`.

---

## Using an existing hook point

Inject `NativeHookRegistry` and subscribe in the constructor. Unsubscribe in
`Dispose`.

```csharp
[ServiceBinding(typeof(MyService))]
public sealed class MyService : IDisposable
{
    private readonly NativeHookRegistry _hooks;

    public MyService(NativeHookRegistry hooks)
    {
        _hooks = hooks;
        _hooks.AttackRoll.Before(OnAttackRollBefore);
        _hooks.GetArmorClass.Modify(OnGetArmorClass);
    }

    private void OnAttackRollBefore(CombatRoundContext ctx)
    {
        // ctx.Attacker, ctx.Target are CNWSCreature / CNWSObject
    }

    private int OnGetArmorClass(int result, GetArmorClassArgs args)
    {
        return result + 2; // flat +2 AC bonus
    }

    public void Dispose()
    {
        _hooks.AttackRoll.Before -= OnAttackRollBefore;   // unsubscribe
        _hooks.GetArmorClass.Modify -= OnGetArmorClass;
    }
}
```

> All hook callbacks run on **hook threads** — native APIs only. Never call
> Anvil wrappers or NWScript VM methods here. See `server-threading` skill.
>
> **This rule is transitive.** Calling another service from a hook counts as
> calling everything that service touches — a `Lazy<>`, a first-use init, or an
> innocent Anvil property getter (`.Tag`, `.Name`, `.Possessor`) is a script-VM
> builtin underneath, and the VM asserts (`vm->m_nRecursionLevel >= 0`) outside
> VM context. Production hit this on 2026-08-09: a hook called
> `PhysicalCoinService.CopperValuePerCoin`, whose lazy tag resolution ran
> `GetTag`. "It was already resolved at startup" is not a defense — audit every
> callee to the leaves, or better: capture what the hook needs as **plain data**
> (string/int dictionaries) on the main thread at init and read only that
> in-hook. Explicitly hook-safe seams (e.g. `CharacterStateStore.GetIntNative`)
> are the only service calls allowed.

---

## HookPoint types and when to use each

| Type                           | Hook points using it                                                                | Use when                                                          |
| ------------------------------ | ----------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `VoidHookPoint<TArgs>`         | `AttackRoll`, `ResolveDamage`, `Initiative`, …                                      | Engine function returns `void`; you observe or suppress it        |
| `ModifierHookPoint<TArgs>`     | `AttackModifier`, `DamageRoll`, `OffHandAttacks`, `SkillClassSkill`                 | Returns `int`; you chain-modify the value, no Before/After needed |
| `IntModifierHookPoint<TArgs>`  | `GetArmorClass`, `ComputeItemArmorClass`, `GetTotalAttacks`, `ValidateCharacter`, … | Returns `int`; need Before/After _and_ modification               |
| `UintModifierHookPoint<TArgs>` | `ValidateLevelUp`                                                                   | Returns `uint`; need Before/After _and_ modification              |
| `ByteModifierHookPoint<TArgs>` | `GetDEXMod`, `GetSpellFailure`                                                      | Returns `byte`; need Before/After _and_ modification              |

**Rule of thumb:**

- Want to read context and optionally suppress? → `VoidHookPoint` `.Before` / `.After`
- Want to tweak a return value, no side effects? → `ModifierHookPoint` `.Modify`
- Want both (read context + tweak return value)? → `IntModifierHookPoint` / `UintModifierHookPoint` / `ByteModifierHookPoint`

---

## SkipOriginal

Setting `ctx.SkipOriginal = true` in a **Before** handler tells the registry
dispatcher to skip `CallOriginal` — the engine function never runs for this
call. Every dispatcher checks this flag between `InvokeBefore` and
`CallOriginal`.

```csharp
_hooks.ResolveInitiative.Before(ctx =>
{
    ctx.SkipOriginal = true;   // suppress engine initiative roll entirely
    ctx.Creature.m_nInitiative = 10; // set manually instead
});
```

**After handlers always run regardless of `SkipOriginal`.**

For modifier hook points, `SkipOriginal` causes the dispatcher to use `0` (or
`args.Result` where set) as the base value instead of calling the original.

---

## Priority

Lower numbers run first. Default is `0`.

```csharp
_hooks.GetArmorClass.Before(handler, priority: -10);  // runs before default subscribers
_hooks.GetArmorClass.After(handler,  priority:  10);  // runs after default subscribers
```

Use negative priority to run before other subscribers, positive to run after.
Most subscribers should leave priority at `0`. Reserve extreme values
(`< -100`, `> 100`) for framework-level concerns.

---

## Discovering available hook points

All registered hook points are properties on `NativeHookRegistry`. Check the
property list first — if a hook point already exists for the function you need,
just subscribe to it. Only add a new one to `NativeHookRegistry` if nothing
covers it.

**Current hook points** (as of this writing):

Combat: `AttackRoll`, `AttackModifier`, `SneakAttack`, `DeathAttack`,
`DamageRoll`, `OffHandAttacks`, `GetTotalAttacks`, `InitializeAttackActions`,
`AddCastSpellActions`, `ResolveDamage`, `ResolveMeleeSpecialAttack`, `ResolveRangedSpecialAttack`

Stats/AC: `ComputeItemArmorClass`, `ComputeCreatureArmorClass`, `GetArmorClass`,
`GetTotalDEXBonus`, `GetAttacksPerRound`, `GetTotalACSkillMod`, `GetFlatFooted`,
`GetDEXMod`, `GetSpellFailure`, `GetAbilityInfo`, `ComputeWeight`, `GetWeight`

Level-up/validation: `LevelUpBegin`, `LevelUpMessage`, `SkillClassSkill`,
`ValidateLevelUp`, `ValidateCharacter`, `ValidatePlayerLogin`

Effects: `OnApplyKnockdown`, `OnRemoveKnockdown`

Other: `Initiative`

---

## Finding a function to hook (symbol lookup)

When you need to hook a function not yet in `NativeHookRegistry`, find its
mangled symbol with `nm`. Ask the user to run:

```bash
nm -D /path/to/nwserver-linux | c++filt | grep -i "FunctionName"
```

The binary path varies by OS. Common locations:

- Steam (Linux): `~/.steam/root/steamapps/common/Neverwinter Nights/bin/linux-x86/nwserver-linux`
- Dedicated server: `/opt/nwn/nwserver-linux`

**Reading the output:**

```
0056f3a0 T _ZN12CNWSCreature13GetArmorClassEv
```

- Column 1: address (ignore)
- Column 2: symbol type — `T`/`t` = code (hookable), `D`/`d` = data, `U` = undefined/external
- Column 3: mangled name (Linux) / the `c++filt` output next to it is the demangled form

Only `T` symbols are hookable. `t` (lowercase) is local linkage — may work but
is less reliable.

**Interpreting the demangled signature:**

```
CNWSCreature::GetArmorClass()           → void* pCreature (the `this` pointer)
CNWSCreatureStats::GetDEXMod(int)       → void* pStats, int bUseArmourPenalty
CNWSItem::ComputeArmorClass()           → void* pItem
```

The first parameter of every non-static member function is always the implicit
`this` pointer, typed as `void*` in the delegate.

---

## Adding a new hook point to NativeHookRegistry

Only do this after confirming the symbol exists and no existing hook point
covers it.

`NativeHookRegistry` is a partial class split across domain files in
`src/Core/src/Hooks/`: `NativeHookRegistry.cs` holds the guards, constructor
(all `RequestHook` registrations) and `Dispose`; each
`NativeHookRegistry.<Domain>.cs` (Combat, CombatRound, Spellcasting, Skills,
Perception, Stats, Progression, Effects, Feats, Items, Messages) holds that
domain's hook point properties, native delegates, `FunctionHook` fields and
dispatchers. Put steps 1-4 and 6 in the matching domain file; steps 5 and 7
(register + dispose) go in `NativeHookRegistry.cs`.

The pattern, derived from existing hooks:

### 1. Declare the Args class

```csharp
public sealed class MyFunctionArgs : HookArgs
{
    public CNWSCreature Creature { get; init; } = null!;
    public int SomeParam { get; init; }
    public int Result { get; set; }   // only needed for modifier hook points
}
```

### 2. Choose the HookPoint type and declare the property

```csharp
// In NativeHookRegistry:
public IntModifierHookPoint<MyFunctionArgs> MyFunction { get; } = new();
```

### 3. Declare the native delegate

```csharp
[NativeFunction("_ZN12CNWSCreature10MyFunctionEi",   // Linux mangled
                "?MyFunction@CNWSCreature@@QEAAHI@Z")] // Windows mangled
private delegate int MyFunctionNative(void* pCreature, int someParam);
```

The `[NativeFunction]` attribute takes `(linuxSymbol, windowsSymbol)`. If you
only have the Linux symbol, leave the Windows string empty — the server only
runs on Linux.

### 4. Declare the FunctionHook field

```csharp
private readonly FunctionHook<MyFunctionNative> _myFunctionHook;
```

### 5. Register in the constructor

```csharp
_myFunctionHook = hookService.RequestHook<MyFunctionNative>(
    OnMyFunction, HookOrder.Late);
```

Use `HookOrder.Late` unless you have a specific reason not to. `HookOrder.Final - 1`
is used for hooks that must fully own the call (e.g. initiative, sneak attack).

### 6. Write the dispatcher

Follow the pattern from existing dispatchers exactly — null-check, build args,
InvokeBefore, conditional CallOriginal, InvokeModifiers, InvokeAfter:

```csharp
private int OnMyFunction(void* pCreature, int someParam)
{
    CNWSCreature? creature = CNWSCreature.FromPointer(pCreature);
    if (creature == null)
        return _myFunctionHook.CallOriginal(pCreature, someParam);

    var args = new MyFunctionArgs { Creature = creature, SomeParam = someParam };

    MyFunction.InvokeBefore(args);

    int result = args.SkipOriginal
        ? args.Result
        : _myFunctionHook.CallOriginal(pCreature, someParam);

    result = MyFunction.InvokeModifiers(result, args);
    args.Result = result;

    MyFunction.InvokeAfter(args);

    return result;
}
```

### 7. Dispose

Add `_myFunctionHook.Dispose();` to the `Dispose()` method.

---

## Combat context threading

`NativeHookRegistry` stores current combat state in a `[ThreadStatic]` field:

```csharp
public static CombatRoundContext? CurrentCombatContext => _currentCombat;
```

This is set for the duration of `OnResolveAttackRoll` and cleared after.
Subscribers to other combat hooks (e.g. `SneakAttack`, `DamageRoll`) fired
within the same attack resolution can read it to get the outer combat context.
Do not hold a reference to it beyond the hook callback.
