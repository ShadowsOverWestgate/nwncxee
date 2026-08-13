# CLAUDE.md

Optional, drop-in **client** extender for NWN:EE. Feature logic in C# (NativeAOT);
a thin native bootstrap gets it loaded. Optional by design: run the stock client
and you play vanilla. New client->server packets the server doesn't recognize are
ignored, so an un-extended client loses nothing.

This is the **client** (`nwmain`), not the server. Server hooking lives in
`sow-codebase`; do not conflate the two.

## Build & run

    ./build.sh          # needs: .NET 10 SDK, clang/gcc, ld.lld, zlib, cmake
    ./run-demo.sh       # boots the framework in /usr/bin/true (no game needed)
    ./launch-nwn.sh     # launch the real client with the extender preloaded
    ./sym.sh [-m] <pat> # search client symbols; -m = mangled (for Install)

First build clones+builds the funchook submodule (git submodule --init).

## Architecture

Load path (the "hard work", you never touch it):

    LD_PRELOAD libsowx.so  (native/bootstrap.c: constructor fires on load)
      -> dlopen sowx_managed.so  (C# NativeAOT shared lib)
      -> SowxInit()  ->  ServiceHost.Run()
                           - build DI container
                           - discover every IExtenderService
                           - construct (constructor DI) + Initialize() each
                           - HookService.Commit() installs all hooks at once

Navigation:

    managed/
      Entry.cs                 [UnmanagedCallersOnly] SowxInit -> ServiceHost.Run()
      Framework/               the plumbing -- rarely edited
        IExtenderService.cs      the one contract you implement
        ServiceHost.cs           DI + discovery + lifecycle
        HookService.cs           funchook wrapper: Install(symbol, handler) / Commit()
        ExtenderLog.cs           inject this for logging
      Services/                business logic -- this is where you work
        HeartbeatService.cs      minimal DI example
        FrameTapService.cs       full example: DI + a real hook
    native/bootstrap.c         the only native code
    third_party/funchook       hooking engine (git submodule)

## Adding a service (the whole contributor workflow)

Drop a class in `managed/Services/`. It is discovered and wired automatically --
no registration:

    public sealed class MyService : IExtenderService
    {
        private readonly ExtenderLog _log;
        private readonly HookService _hooks;               // ask for what you need
        public MyService(ExtenderLog log, HookService hooks) { _log = log; _hooks = hooks; }

        public void Initialize()
        {
            // find the symbol:  ./sym.sh -m CNWSMessage::Handle...
            // handler is a pointer to an [UnmanagedCallersOnly] static whose
            // signature matches the target's ABI (member fns take `this` first).
        }
    }

See `FrameTapService` for the hook + trampoline mechanics. Everything else --
loading, DI, install ordering -- is done for you.

## Hard rules

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
**unstripped** ELF (~40k demangled symbols). Ghidra project:
`~/ghidra/nwn-linux.rep`. You hook by name, no offset hunting. Known hook points:

- `CNWSMessage::HandlePlayerToServerMessage(unsigned int, uchar*, uint)` -- client->server
  send path (packet feature). `HandlePlayerToServerDevice` is the device/metrics channel.
- `GLRender::SwapBuffers(SDL_Window*)` -- the frame present (rendering/shadow work).
- `./sym.sh CNWS...` / `./sym.sh Render` to find more.

Renders via OpenGL/GLX/X11 (SDL is static -- symbols appear, no `libSDL` in ldd).

## Status

Framework works end-to-end under NativeAOT (DI + discovery + funchook install
all verified via `run-demo.sh`). `FrameTapService` proves a live hook -- run
`./launch-nwn.sh` and watch for "first frame presented -- hook is LIVE".

Linux only. Windows = proxy DLL or rename-launcher; macOS = `DYLD_INSERT_LIBRARIES`,
blocked by the notarized app's hardened runtime until re-signed (do last).
