# CLAUDE.md

Optional, drop-in **client** extender for NWN:EE. Feature logic in C# (NativeAOT);
a thin native bootstrap gets it loaded. Optional by design: run the stock client
and you play vanilla. New client->server packets the server doesn't recognize are
ignored, so an un-extended client loses nothing.

This is the **client** (`nwmain`), not the server. Server hooking lives in
`sow-codebase`; do not conflate the two.

## Agent skills

### Issue tracker

Issues live on the self-hosted Gitea at `git.westgate.pw` via the `tea` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root (created lazily). See `docs/agents/domain.md`.

## Build & run

    ./build.sh          # needs: .NET 10 SDK, clang/gcc, ld.lld, zlib, cmake
    ./run-demo.sh       # boots the framework in /usr/bin/env (no game needed)
    ./sym.sh [-m] <pat> # search client symbols; -m = mangled (for Install)

`build.sh` stages a runtime folder `sow-client-extender/` (launcher + 3 `.so`).
Launch the real client:  `NWN_BIN=/path/to/nwmain-linux ./sow-client-extender/sowx-launch`
(Steam: put `"<abs>/sowx-launch" %command%` in the game Launch Options.)

First build clones+builds the funchook submodule (git submodule --init).

## Architecture

Load path (the "hard work", you never touch it):

    LD_PRELOAD libsowx.so  (native/bootstrap.c: constructor fires on load)
      -> dlopen sowx_managed.so  (C# NativeAOT shared lib)
      -> SowxInit()  ->  ServiceHost.Run()
                           - discover every IExtenderService (reflection, sorted by name)
                           - construct each + Initialize(log, hooks)
                           - HookService.Commit() installs all hooks at once

Navigation:

    managed/
      Entry.cs                 [UnmanagedCallersOnly] SowxInit -> ServiceHost.Run()
      Framework/               the plumbing -- rarely edited
        IExtenderService.cs      the one contract you implement
        ServiceHost.cs           discovery + lifecycle
        HookService.cs           funchook wrapper: Install(symbol, handler) / Commit();
                                 Chain(symbol, handler, priority) for shared symbols (#88)
        ExtenderLog.cs           inject this for logging
      Services/                business logic -- this is where you work
    docs/hook-pattern.md       example: a real hook + trampoline
    native/bootstrap.c         the only native code
    third_party/funchook       hooking engine (git submodule)

## Adding a service (the whole contributor workflow)

Drop a class in `managed/Services/`. It is discovered and wired automatically --
no registration:

    public sealed class MyService : IExtenderService
    {
        // [UnmanagedCallersOnly] handlers must be static, so hooked services keep
        // their state static and stash `log` in a static field in Initialize.
        private static ExtenderLog? _log;

        public void Initialize(ExtenderLog log, HookService hooks)
        {
            _log = log;
            // find the symbol:  ./sym.sh -m CNWSMessage::Handle...
            // handler is a pointer to an [UnmanagedCallersOnly] static whose
            // signature matches the target's ABI (member fns take `this` first).
            hooks.Install(SymbolMap.Something, /* &OnSomething */ default);
        }
    }

See `docs/hook-pattern.md` for the hook + trampoline mechanics. Everything else --
loading, discovery, install ordering -- is done for you.

## Comment rule

Every declaration that needs explaining gets a concise XML `<summary>` (one--three
lines). No prose banners. Inside method bodies, no comments EXCEPT a single terse
line where omitting it is a footgun -- a magic offset/opcode, a non-obvious ABI
requirement, or an ordering hazard. Prefer promoting a magic number to a named
`const` with an XML doc over an inline comment. `ponytail:` markers are allowed and
name a known ceiling + its upgrade path.

## Hard rules

- **NEVER GUESS.** Always verify with a Ghidra decompilation.
- **Hook with funchook, NOT NativeHookRegistry.** The ported `sow-hooking` skill
  describes the server's registry, which does not exist here. Take its RE mindset
  and Before/After/SkipOriginal semantics, not its C# API.
- **Hot hooks allocate nothing.** NativeAOT GC runs on whatever thread the hook
  fired on; the render/main loop stutters if it pauses there. Input/network hooks
  are cold -- fine. Frame/GL hooks are hot -- be careful.
- **Reuse an existing message opcode; don't invent one.** That is what keeps the
  extender optional (unknown packets are ignored server-side).
- **Shaders for what shaders reach; extender for what they can't.** Lighting/color
  = `.shd` overrides via hak/NWSync (see `sow-topdata`). Shadows, render bugs,
  fixed-function state = engine-internal, needs a GL hook.

## The binary is your map

`~/gry/steam/Neverwinter Nights/bin/linux-x86/nwmain-linux` is an
**unstripped** ELF (~24k dynamically-exported functions). You hook by name, no
offset hunting. Known hook points:

- `CNWSMessage::HandlePlayerToServerMessage(unsigned int, uchar*, uint)` -- client->server
  send path (packet feature). `HandlePlayerToServerDevice` is the device/metrics channel.
- `GLRender::SwapBuffers(SDL_Window*)` -- the frame present (rendering/shadow work).
- `./sym.sh CNWS...` / `./sym.sh Render` to find more.

Renders via OpenGL/GLX/X11 (SDL is static -- symbols appear, no `libSDL` in ldd).

## Reverse engineering (Ghidra)

Two tools, two jobs:

- **`./sym.sh` (nm)** -- fast path for *addresses and signatures*. "What's the
  symbol? Does it exist? What's its ABI?" This is enough for most hooks.
- **Ghidra** -- for *logic and layout*: decompiled function bodies, struct field
  offsets, call graphs, xrefs. Reach for it when you need to know what a function
  *does* or where a field sits in a struct (raw-pointer reads).

The client is already imported and analyzed:

- Project: `~/ghidra/nwn-linux.rep`  (client / `nwmain`; the *only* Ghidra project)
- GUI: `ghidra`   Headless/scripted: `/opt/ghidra/support/analyzeHeadless`
- There is no checked-in decompiled C++ dump -- the binary + this project are the
  source of truth. State uncertainty from decompilation honestly; confirm against
  runtime behavior when it matters.

## Sibling repos

- **`~/repos/sow-codebase`** -- the SoW **server** extender (Anvil + native hooks
  via `NativeHookRegistry`). Your reference for: the *server end* of any custom
  packet (where the payload is received/ignored), hooking semantics
  (Before/After/SkipOriginal), and shared engine knowledge -- `CNWSMessage` and
  friends are the *same classes* on client and server, so its struct/field notes
  transfer. Do NOT copy its hook mechanism; the client uses funchook. Its
  `AGENTS.md`/`DOCTRINE.md` are server law, not ours.
- **`~/repos/sow-topdata`** -- shaders (`.shd`), 2DA, TLK as data. The
  graphics-via-data path: anything shaders can express ships here, no extender.
- `.claude/skills/` -- ported server skills (hooking, threading, native
  conventions) with a README on what adapts to the client.

## Status

Framework works end-to-end under NativeAOT (DI + discovery + funchook install
all verified via `run-demo.sh`). A live hook proves out via `sowx-launch` --
watch for "first frame presented -- hook is LIVE" (now emitted by `UiHostService`
in `managed/Ui/`, the frame host that owns the `SwapBuffers` present, computes the
in-game gate + UI-scale sync, and drives the `UiHost` window registry's draw
roll-call each frame; #62). Chat renders as one registered `Window` whose
content-draw slot runs the chatbox. The minimal hook-pattern example now lives in
`docs/hook-pattern.md` (#158); under the `SOWX_LOCKCHAT` opt-out nothing hooks the
present at all and the client renders vanilla.

Linux only. Windows = proxy DLL or rename-launcher; macOS = `DYLD_INSERT_LIBRARIES`,
blocked by the notarized app's hardened runtime until re-signed (do last).
