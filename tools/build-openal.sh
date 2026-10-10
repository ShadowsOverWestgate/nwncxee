#!/usr/bin/env bash
# Builds OpenAL Soft's libopenal.so.1 from its release source: tools/build-openal.sh <source dir> <work dir> <out file>
# build.sh calls it, and so does flake.nix's openal package, so CI's copy and the shipped one use the same flags.
# GCC, not whatever `c++` is: clang 21 fails OpenAL Soft's own -Werror=function-effects against libstdc++ 15.
# libstdc++ and libgcc are linked static, because a player's machine may not have them.
set -euo pipefail
src=$1 work=$2 out=$3
cmake -S "$src" -B "$work" -DCMAKE_C_COMPILER=gcc -DCMAKE_CXX_COMPILER=g++ \
  -DCMAKE_BUILD_TYPE=Release -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF \
  -DALSOFT_INSTALL=OFF -DALSOFT_ENABLE_MODULES=OFF -DALSOFT_UPDATE_BUILD_VERSION=OFF \
  -DALSOFT_REQUIRE_PIPEWIRE=ON -DALSOFT_REQUIRE_PULSEAUDIO=ON -DALSOFT_REQUIRE_ALSA=ON \
  -DALSOFT_STATIC_STDCXX=ON -DALSOFT_STATIC_LIBGCC=ON
# Bare `-j` is unbounded under make: OpenAL Soft then forks every TU at once and OOMs the host.
cmake --build "$work" -j"${JOBS:-$(nproc)}"
mkdir -p "$(dirname "$out")"
cp -L "$work/libopenal.so.1" "$out"
