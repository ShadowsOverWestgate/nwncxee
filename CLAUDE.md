# CLAUDE.md

Optional, drop-in **client** extender for NWN:EE. Feature logic in C# (NativeAOT);
a thin native bootstrap gets it loaded. Optional by design: run the stock client
and you play vanilla. New client->server packets the server doesn't recognize are
ignored, so an un-extended client loses nothing.

This is the **client** (`nwmain`), not the server. Server hooking lives in
`sow-codebase`; do not conflate the two.

## Build & run

    ./build.sh        # needs: .NET 10 SDK, clang/gcc, ld.lld, zlib
    ./run-demo.sh     # proves C# runs inside a foreign process (no game needed)
    ./launch-nwn.sh   # launch the real client with the extender preloaded
    ./sym.sh <pat>    # search the client's demangled symbols -- "what do I hook"

## Architecture

    LD_PRELOAD libsowx.so  (native/bootstrap.c: constructor fires on load)
      -> dlopen sowx_managed.so  (managed/: C# NativeAOT shared lib)
      -> call C# export SowxInit()  ([UnmanagedCallersOnly] == C fn pointer)

The bootstrap is the *only* native code. Real features = funchook installs hooks
whose targets are C# `[UnmanagedCallersOnly]` exports.

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

Linux only. Windows = proxy DLL or rename-launcher; macOS = `DYLD_INSERT_LIBRARIES`,
blocked by the notarized app's hardened runtime until re-signed (do last).
No hooks yet -- next step is funchook + one named function.
