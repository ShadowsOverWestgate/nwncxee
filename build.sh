#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"
mkdir -p build

if [ ! -f build/libfunchook.so ]; then
  echo ">> building funchook (once)..."
  cmake -S third_party/funchook -B build/funchook \
    -DFUNCHOOK_BUILD_SHARED=ON -DFUNCHOOK_BUILD_STATIC=OFF \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build build/funchook -j >/dev/null
  cp -a build/funchook/libfunchook.so* build/
fi

echo ">> building managed lib (C# NativeAOT -> sowx_managed.so)..."
SOWX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
# Quiet on success, but NEVER swallow a failure: discarding stderr here once hid a compile error behind a
# stale staged .so, and run-demo.sh happily booted the old build.
if ! dotnet publish managed/sowx_managed.csproj -c Release -o build \
     -p:InformationalVersion="$SOWX_VERSION" > build-managed.log 2>&1; then
  echo ">> managed build FAILED:"; grep -E "error|rror" build-managed.log | head -20; exit 1
fi

echo ">> building native bootstrap (libsowx.so)..."
cc -shared -fPIC -O2 -o build/libsowx.so native/bootstrap.c -ldl

echo ">> staging runtime dir (sow-client-extender/)..."
# One ready-to-run/ship folder: the wrapper loads the .so files beside it.
# cp -L on libfunchook resolves the SOVERSION symlink to a real file named
# libfunchook.so -- DllImport("funchook") needs that exact name.
OUT=sow-client-extender
rm -rf "$OUT" && mkdir -p "$OUT"
cp -a build/libsowx.so build/sowx_managed.so "$OUT/"
cp -aL build/libfunchook.so "$OUT/libfunchook.so"
cp packaging/sowx-launch "$OUT/sowx-launch"
cp packaging/README.txt "$OUT/README.txt"
chmod +x "$OUT/sowx-launch"

echo ">> artifacts:"
ls -1 "$OUT"
