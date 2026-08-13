# sow-client-extender

Optional, drop-in extender for the NWN:EE client, with feature logic in **C#**.
Attach it for extra features; run the stock client and you play vanilla. New
client->server packets the server doesn't recognize are ignored, so an
un-extended client loses nothing.

Contributors write **services**; the framework does loading, DI, and hooking --
the same deal Anvil gives server developers. See `CLAUDE.md` for architecture,
navigation, and how to add a service.

## Build & run

```sh
./build.sh      # needs: .NET 10 SDK, clang/gcc, ld.lld, zlib, cmake
./run-demo.sh   # boots the framework in /usr/bin/true -- no game needed
./launch-nwn.sh # launch the real client with the extender preloaded
```

`run-demo.sh` shows the host boot the DI container, discover services, and
install hooks. Against the real client, `FrameTapService` logs
`first frame presented -- hook is LIVE`.

Clone with submodules (funchook): `git clone --recursive`, or
`git submodule update --init` after.

## Status

Framework verified end-to-end under NativeAOT (DI + discovery + funchook).
Linux only for now; Windows/macOS loaders are the next port (see `CLAUDE.md`).
