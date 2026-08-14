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
./run-demo.sh   # boots the framework in /usr/bin/env -- no game needed
```

`build.sh` stages a ready-to-run `sow-client-extender/` folder: the launcher
plus the three `.so` files. `run-demo.sh` shows the host boot, discover
services, and install hooks. Against the real client, `ChatService` logs
`first frame presented -- hook is LIVE`.

Clone with submodules (funchook): `git clone --recursive`, or
`git submodule update --init` after.

## Install & play

Grab `sowx-linux-x64.tar.gz` from the releases page (or build it yourself, see
above).

**Steam (one line, same for everyone):**

1. Find the game folder: in Steam, right-click *Neverwinter Nights* ->
   Manage -> Browse local files.
2. Unpack the tarball into `bin/linux-x86/` there. You now have a
   `bin/linux-x86/sow-client-extender/` folder.
3. Right-click *Neverwinter Nights* -> Properties -> General -> Launch
   Options, and paste this exactly (no editing -- it is a path relative to the
   `bin/linux-x86` folder, which Steam uses as the working directory):

```
./sow-client-extender/sowx-launch %command%
```

Launch from Steam as normal. To uninstall, clear that line.

**No Steam:** point the launcher at the client binary:

```sh
NWN_BIN="/path/to/bin/linux-x86/nwmain-linux" ./sowx-launch
```

The extender is optional: run the stock client and you play vanilla. Linux
only for now. See `sow-client-extender/README.txt` in the release for the same
steps offline.

Did it work? In game you get a native chatbox (bottom-left). The extender logs
to `~/sowx.log` -- look for `window render installed` and `using font`. Under
the Steam Linux Runtime the game runs in a container with a private `/tmp`, so
the log deliberately lands in `$HOME`, not `/tmp`.

## Status

Framework verified end-to-end under NativeAOT (DI + discovery + funchook).
Linux only for now; Windows/macOS loaders are the next port (see `CLAUDE.md`).
