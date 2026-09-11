#!/usr/bin/env sh
# Cross-build the Windows leg on Linux (#48 M1): funchook.dll + sowx_managed.dll + version.dll.
# Needs clang-cl, lld-link, llvm-lib, cmake, the .NET SDK, and a Windows 10 SDK + MSVC lib/include
# tree (default: the mounted Windows partition). Nothing here is supported by Microsoft; it works
# because ILC emits COFF on any host and lld-link links it.
set -e
cd "$(dirname "$0")"
mkdir -p build

SDK="${SDK:-/mnt/windows/Windows Kits/10}"
SDKVER="${SDKVER:-10.0.19041.0}"
MSVC="${MSVC:-/mnt/windows/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC/14.35.32215}"
[ -f "$SDK/Lib/$SDKVER/um/x64/kernel32.Lib" ] || { echo "!! no Windows SDK under $SDK (set SDK/SDKVER)" >&2; exit 1; }
[ -f "$MSVC/lib/x64/libvcruntime.lib" ] || { echo "!! no MSVC tools under $MSVC (set MSVC)" >&2; exit 1; }

# Windows resolves headers and libs case-insensitively; the NTFS mount does not. Headers go through a
# clang VFS overlay with case-sensitive:false (the SDK includes itself in mixed case, so a renamed
# mirror is not enough); libs get a lowercase+UPPERCASE symlink mirror for lld-link and the
# /DEFAULTLIB:LIBCMT directives MSVC objects carry.
W=build/winsdk
if [ ! -d "$W" ]; then
  echo ">> mirroring Windows SDK + MSVC libs into $W, writing the include overlay..."
  mkdir -p "$W/lib"
  for d in "$SDK/Lib/$SDKVER/um/x64" "$SDK/Lib/$SDKVER/ucrt/x64" "$MSVC/lib/x64"; do
    find "$d" -maxdepth 1 -type f -iname '*.lib' | while read -r f; do
      b=$(basename "$f"); b="${b%.*}"
      ln -sf "$f" "$W/lib/$(echo "$b" | tr 'A-Z' 'a-z').lib"
      ln -sf "$f" "$W/lib/$(echo "$b" | tr 'a-z' 'A-Z').lib"
    done
  done
  # Space-free directory symlinks: the paths ride through CMake flag strings unquoted.
  mkdir -p "$W/inc"
  ln -sfn "$SDK/Include/$SDKVER/um" "$W/inc/um"; ln -sfn "$SDK/Include/$SDKVER/shared" "$W/inc/shared"
  ln -sfn "$SDK/Include/$SDKVER/ucrt" "$W/inc/ucrt"; ln -sfn "$MSVC/include" "$W/inc/msvc"
  python3 - "$W/vfs.yaml" "$(cd "$W" && pwd)/inc" um shared ucrt msvc <<'PY'
import os, sys, json
out, base, names = sys.argv[1], sys.argv[2], sys.argv[3:]
roots = [{"name": os.path.join(base, n), "type": "directory", "contents": [
    {"name": f, "type": "file", "external-contents": os.path.join(base, n, f)}
    for f in os.listdir(os.path.join(base, n)) if os.path.isfile(os.path.join(base, n, f))]} for n in names]
json.dump({"version": 0, "case-sensitive": "false", "roots": roots}, open(out, "w"))
PY
fi
WABS=$(cd "$W" && pwd)

if [ ! -f build/funchook.dll ]; then
  # funchook's Windows page allocator only walks UP from the target; with the NativeAOT GC reservation
  # sitting right above nwmain.exe (Wine) it leaves rel32 range and the jump truncates. Applied in place,
  # idempotent; the submodule stays dirty on purpose.
  if git -C third_party/funchook apply --check ../../native/funchook-win-rel32.patch 2>/dev/null; then
    git -C third_party/funchook apply ../../native/funchook-win-rel32.patch
  fi
  echo ">> building funchook.dll (clang-cl cross)..."
  cmake -S third_party/funchook -B build/funchook-win -DCMAKE_TOOLCHAIN_FILE="$PWD/native/win-toolchain.cmake" \
    -DWINSDK="$WABS" -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DFUNCHOOK_BUILD_SHARED=ON -DFUNCHOOK_BUILD_STATIC=OFF \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build build/funchook-win -j >/dev/null
  cp build/funchook-win/funchook.dll build/
fi

echo ">> building sowx_managed.dll (NativeAOT win-x64, lld-link)..."
SOWX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
# CppLinker must be a global property: the ILCompiler targets reset it to `link` otherwise.
if ! dotnet publish managed/sowx_managed.csproj -c Release -r win-x64 -o build/win \
     -p:CppLinker=lld-link -p:CppLibCreator=llvm-lib -p:WinLib="$WABS/lib" \
     -p:InformationalVersion="$SOWX_VERSION" > build-managed-win.log 2>&1; then
  echo ">> managed win build FAILED:"; grep -E "error" build-managed-win.log | head -20; exit 1
fi

echo ">> building version.dll proxy..."
SDK_LIB="$WABS/lib" ./build-win-proxy.sh >/dev/null

OUT=sow-client-extender-win
rm -rf "$OUT" && mkdir -p "$OUT"
cp build/version.dll build/funchook.dll build/win/sowx_managed.dll "$OUT/"
echo ">> artifacts:"; ls -1 "$OUT"
