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

# #456: the Windows symbol gate runs BEFORE the build, not as a report nobody reads. It checks every Nm row
# names a real export, every At row lands in the image, and -- the clause the #456 crash bought -- that every
# instance member returning a class by value has had its (this, sret) argument order decided at the call site.
if command -v llvm-undname >/dev/null 2>&1 && [ -f "${NWN_WIN_EXE:-~/gry/steam/Neverwinter Nights/bin/win32/nwmain.exe}" ]; then
  echo ">> checking Windows symbol rows..."
  ./tools/abi/check-win-symbols.py || { echo "!! symbol rows are wrong -- fix them before shipping a DLL" >&2; exit 1; }
else
  echo ">> skipping the symbol check (no llvm-undname, or no nwmain.exe to check against)" >&2
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
# The UI font travels with the DLL (#419): Windows ships no font with the gear/arrow/pin glyphs. Bitstream Vera licence.
FONT=$(ls /usr/share/fonts/TTF/DejaVuSans.ttf /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf 2>/dev/null | head -1)
[ -n "$FONT" ] && cp "$FONT" "$OUT/" || echo "!! DejaVuSans.ttf not found; Windows falls back to Segoe UI Symbol / Arial" >&2
STAMP=$(grep -oE 'WindowsBuildStamp = 0x[0-9a-f]+' managed/Framework/SymbolMap.cs | grep -oE '0x[0-9a-f]+')
cat > "$OUT/README.txt" <<EOF
Sowx client extender for Windows -- $SOWX_VERSION, pinned to nwmain.exe build $STAMP

INSTALL
  1. Copy every file in this folder next to nwmain.exe:
       <Steam>\\steamapps\\common\\Neverwinter Nights\\bin\\win32\\
     (version.dll is the loader; funchook.dll, sowx_managed.dll and DejaVuSans.ttf ride beside it.)
  2. Create sowx.cfg NEXT TO nwn.ini -- usually Documents\\Neverwinter Nights\\, but if the game uses an
     instance folder (Neverwinter Nights.0) put it there, wherever nwn.ini actually is. One line:
       logs=1
     Do this BEFORE the first launch: without it the log is silent after boot.
  3. Launch the game normally (Steam or nwmain.exe). No launch options needed.

WHAT WORKING LOOKS LIKE
  Documents\\Neverwinter Nights\\sowx.log (always the unsuffixed folder) contains
    UiHostService: first frame presented -- hook is LIVE
  and one "armed" / "hooks installed" line per feature (Chat, Nameplate, CombatText, Dialog).
  A message box "Sowx: client build changed, extender standing down" means Beamdog patched the
  client since build $STAMP; the game runs vanilla and the extender needs re-deriving.

UNINSTALL
  Delete version.dll from bin\\win32\\ (the other files are inert without it).

REPORTING
  Send back sowx.log (and funchook.log if you added hookdebug=1 to sowx.cfg), plus what you did
  right before anything went wrong. An unsigned 9 MB DLL may trip Defender: restore it and add an
  exclusion for the bin\\win32 folder.
EOF
python3 -c "import shutil; shutil.make_archive('$OUT', 'zip', '.', '$OUT')"
echo ">> artifacts ($OUT/, sow-client-extender-win.zip):"; ls -1 "$OUT"
