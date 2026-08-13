# sow-client-extender (prototype)

Optional, drop-in extender for the NWN:EE client, with feature logic in **C#**.
Optional by design: attach it and you get extra features; run the stock client
and you play vanilla. New client->server packets the server doesn't recognize
are simply ignored, so an un-extended client loses nothing.

## What this prototype proves

The one genuinely-novel claim: **C# (NativeAOT) can run inside the native game
process.** Everything else (hooking, reading engine structs) is well-trodden.

```
LD_PRELOAD libsowx.so  ->  constructor fires on load  ->  dlopen sowx_managed.so
                                                       ->  call C# export SowxInit()
```

- `native/bootstrap.c` -> `libsowx.so`: the only native code. Gets loaded, hands
  off to C#. The real version installs funchook hooks here.
- `managed/` -> `sowx_managed.so`: C# compiled with NativeAOT to a native shared
  library. `SowxInit` is a `[UnmanagedCallersOnly]` export -- a plain C function
  pointer, which is also exactly what a hook library installs as its target.

## Build & run

```sh
./build.sh      # needs: .NET 10 SDK, clang/gcc, ld.lld, zlib
./run-demo.sh   # preloads into /usr/bin/true -- no game/X11/GL needed
```

Expected: C# reports the host process name (`true`) and pid, and writes
`/tmp/sowx.log`. Reporting the *host's* name proves it ran inside the target,
not inside our shell.

Attach to the real client identically (unstripped, 40k symbols, OpenGL/GLX):

```sh
SOWX_MANAGED=$PWD/build/sowx_managed.so LD_PRELOAD=$PWD/build/libsowx.so ~/nwmain-linux
```

## What it does NOT do yet (deliberate)

- **No hooks.** Next step: link [funchook](https://github.com/kubo/funchook),
  hook one client function (a message-recv, or `glXSwapBuffers`), point it at a
  C# export, confirm it fires and can read a struct by raw pointer (same
  technique the server uses via NWN.Native/Anvil).
- **Linux only.** Windows = proxy DLL or rename-launcher; macOS =
  `DYLD_INSERT_LIBRARIES`, blocked by the signed/notarized app's hardened
  runtime until re-signed (hardest platform, do last).

## Where the real value is

- **Rendering/shadows** engine-internal bugs shaders can't reach -> hook the GL
  path. This is the justification for a native extender (shader overrides handle
  everything shaders *can* touch).
- **Custom packets** (e.g. toolbar hotkeys): capture input client-side, emit a
  payload on an existing message channel the server-side native hook already
  watches. Reuse an opcode; don't invent one.
