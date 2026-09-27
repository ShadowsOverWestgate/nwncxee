# 0027 — The audio runtime ships beside the extender

Status: accepted (2026-09-27, CX-57).

## Context

CX-57 puts Steam Audio behind the engine's OpenAL. That needs two libraries the extender does not
own: **OpenAL Soft** (LGPL-2.1) as the engine's OpenAL -- pinned, because the engine's own is
whatever the system carries on Linux and 1.20.1 on Windows, and the render needs
`AL_SOFT_callback_buffer` -- and **Steam Audio** (Apache-2.0, Valve ships binaries only). The
Windows install had been one file, `version.dll` (#474); xtul's call for CX-57: licence-clean and
drag-and-drop, not necessarily one file.

## Decision

**Both ship as separate, unmodified files beside the extender; the extender links neither.**

- Linux: `libopenal.so.1` (built from the 1.25.2 release source, libstdc++/libgcc static) and
  `libphonon.so` in `cx-linux-x64/`. `cx-launch` preloads `libopenal.so.1`, which satisfies the
  client's `NEEDED` before any system copy can.
- Windows: `cx_openal.dll` (upstream's own Win64 `soft_oal.dll`, renamed so it cannot collide with
  the game's `soft_oal.dll`) and `phonon.dll` beside `version.dll`. `version.dll`'s attach points
  `nwmain.exe`'s `OpenAL32.dll` imports at `cx_openal.dll` before the game's first instruction --
  the game's files are never touched, so Steam's verify has nothing to undo.
- `tools/deps.py` fetches every archive by URL and sha256; `LICENSES.txt` carries the LGPL, the
  Apache licence and Steam Audio's third-party notices. Relinking obligations do not arise: the
  LGPL library is a replaceable file.
- The native mixer (`native/cxaudio.cpp`) resolves both at run time, so a missing or wrong library
  stands sound down (CX-45: said on screen) instead of stopping the extender from loading.

## Consequences

- The Windows install is four files, not one: still one unpack into `bin/win32`.
- The archives grow by ~95 MB (Steam Audio's binaries are ~40-53 MB per OS).
- Updating either library is a `tools/deps.py` pin change plus a listen; nothing else names a version
  except the device check in `SoundService`, which expects `ALSOFT 1.25.2`.
