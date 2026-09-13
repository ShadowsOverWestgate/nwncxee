#!/usr/bin/env bash
# Native Windows build of version.dll (#504): MSVC + the Windows SDK on the machine itself, under Git
# Bash inside a VS developer shell (cl, cmake, dotnet on PATH). This is what releases ship;
# build-win.sh is the Linux cross-build, a developer tool only.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build

if [ ! -f build/funchook.lib ] || [ ! -f build/distorm.lib ]; then
  # funchook's Windows page allocator only walks UP from the target; with the NativeAOT GC reservation
  # sitting right above nwmain.exe it leaves rel32 range and the jump truncates. Applied in place.
  if ! git -C third_party/funchook apply --reverse --check ../../native/funchook-win-rel32.patch 2>/dev/null; then
    git -C third_party/funchook apply ../../native/funchook-win-rel32.patch
  fi
  echo ">> building funchook.lib (MSVC)..."
  # Static: it is linked into sowx_managed.dll (DirectPInvoke; see the csproj), not shipped beside it.
  cmake -S third_party/funchook -B build/funchook-win -A x64 \
    -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded \
    -DFUNCHOOK_BUILD_SHARED=OFF -DFUNCHOOK_BUILD_STATIC=ON -DFUNCHOOK_BUILD_TESTS=OFF >/dev/null
  cmake --build build/funchook-win --config Release >/dev/null
  cp build/funchook-win/Release/funchook.lib build/funchook-win/Release/distorm.lib build/
fi

echo ">> compiling the version.dll proxy half (native/bootstrap_win.c -> .obj)..."
# Dash flags: Git Bash rewrites /nologo-style args into C:/Program Files/Git/nologo paths.
cl -nologo -c -O2 -GS- native/bootstrap_win.c -Fobuild/bootstrap_win.obj

# tools/abi/check-win-symbols.py needs llvm-undname and a real nwmain.exe; a CI runner has neither.
echo ">> skipping the symbol check (no nwmain.exe here; build-win.sh runs it on the dev box)" >&2

echo ">> building sowx_managed.dll (NativeAOT win-x64, MSVC link)..."
SOWX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
if ! dotnet publish managed/sowx_managed.csproj -c Release -r win-x64 -o build/win \
     -p:InformationalVersion="$SOWX_VERSION" > build-managed-win.log 2>&1; then
  echo ">> managed win build FAILED:"; grep -E "error" build-managed-win.log | head -20; exit 1
fi

OUT=sow-client-extender-win
rm -rf "$OUT" && mkdir -p "$OUT"
# One file: nwmain.exe imports VERSION.dll and the loader takes the copy beside the exe first.
cp build/win/sowx_managed.dll "$OUT/version.dll"
STAMP=$(grep -oE 'WindowsBuildStamp = 0x[0-9a-f]+' managed/Framework/SymbolMap.cs | grep -oE '0x[0-9a-f]+')
sed "s/@VERSION@/$SOWX_VERSION/g; s/@STAMP@/$STAMP/g" packaging/README-win.txt > "$OUT/README.txt"
packaging/licenses.sh "$OUT/LICENSES.txt"
# Contents, not the folder: the README says "unpack here" and that has to be true.
python -c "import shutil; shutil.make_archive('$OUT', 'zip', '$OUT')"
echo ">> artifacts ($OUT/, $OUT.zip):"; ls -1 "$OUT"
