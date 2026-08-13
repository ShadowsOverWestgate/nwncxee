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
dotnet publish managed/sowx_managed.csproj -c Release -o build >/dev/null

echo ">> building native bootstrap (libsowx.so)..."
cc -shared -fPIC -O2 -o build/libsowx.so native/bootstrap.c -ldl

echo ">> artifacts:"
ls -1 build/libsowx.so build/sowx_managed.so build/libfunchook.so
