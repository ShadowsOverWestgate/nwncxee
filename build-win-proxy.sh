#!/usr/bin/env sh
# Cross-build the Windows proxy `version.dll` from native/bootstrap_win.c on Linux
# using clang-cl + lld-link. Only needs the Windows 10 SDK import libs (kernel32,
# for the loader/thread calls); the CRT is not used (see bootstrap_win.c). MSVC on
# Windows builds the same source with `cl /LD` -- this script is just the Linux path.
set -e
cd "$(dirname "$0")"
mkdir -p build

# Point this at any Windows 10 SDK's um/x64 lib dir. Default = the SDK on the
# mounted Windows partition. Override:  SDK_LIB=/path/to/um/x64 ./build-win-proxy.sh
SDK_LIB="${SDK_LIB:-/mnt/windows/Windows Kits/10/Lib/10.0.19041.0/um/x64}"
[ -f "$SDK_LIB/kernel32.Lib" ] || { echo "!! kernel32.Lib not found under: $SDK_LIB" >&2
  echo "   Set SDK_LIB to a Windows 10 SDK um/x64 dir." >&2; exit 1; }

echo ">> compiling native/bootstrap_win.c (clang-cl, CRT-free)..."
clang-cl -c -O2 -GS- native/bootstrap_win.c -Fo:build/version.obj

echo ">> linking version.dll (lld-link, entry=DllMain, no default libs)..."
lld-link /DLL /NOLOGO /NODEFAULTLIB /ENTRY:DllMain \
  /OUT:build/version.dll build/version.obj \
  "/LIBPATH:$SDK_LIB" kernel32.Lib

echo ">> artifact:"
ls -1 build/version.dll
