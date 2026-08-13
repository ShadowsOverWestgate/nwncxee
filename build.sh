#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"

echo ">> building managed lib (C# NativeAOT -> sowx_managed.so)..."
dotnet publish managed/sowx_managed.csproj -c Release -o build >/dev/null

echo ">> building native bootstrap (libsowx.so)..."
cc -shared -fPIC -O2 -o build/libsowx.so native/bootstrap.c -ldl

echo ">> artifacts:"
ls -1 build/*.so
