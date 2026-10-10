#!/usr/bin/env bash
# Writes LICENSES.txt ($1) from the repo's LICENSE plus the verbatim notices of every
# third-party library linked into the shipped binaries.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
{
  echo "NWNCX:EE -- licenses"
  echo "==============================="
  echo
  echo "The extender itself is MIT licensed. It links funchook (GPLv2+ with a linking"
  echo "exception), diStorm3 (BSD 3-clause) and xatlas (MIT). It ships, as separate"
  echo "unmodified libraries, OpenAL Soft 1.25.2 (LGPL-2.1: libopenal.so.1 / cx_openal.dll,"
  echo "source at https://github.com/kcat/openal-soft/releases/tag/1.25.2 -- replace the file"
  echo "with any compatible build of your own) and Steam Audio 4.8.1 (Apache-2.0:"
  echo "libphonon.so / phonon.dll, https://github.com/ValveSoftware/steam-audio). Their"
  echo "notices follow in full."
  echo
  echo "----- NWNCX:EE (MIT) -----"; echo
  cat "$here/LICENSE"; echo
  echo "----- funchook (https://github.com/kubo/funchook) -----"; echo
  cat "$here/third_party/funchook/LICENSE"; echo
  echo "----- diStorm3 (https://github.com/gdabah/distorm) -----"; echo
  cat "$here/third_party/funchook/distorm/COPYING"; echo
  echo "----- xatlas (https://github.com/jpcy/xatlas) -----"; echo
  cat "$here/native/xatlas/LICENSE"; echo
  echo "----- OpenAL Soft (https://github.com/kcat/openal-soft) -----"; echo
  cat "$here/build/deps/openal-soft-1.25.2/COPYING"; echo
  echo "OpenAL Soft carries these inside it:"; echo
  cat "$here/build/deps/openal-soft-1.25.2/LICENSE-pffft"; echo
  cat "$here/build/deps/openal-soft-1.25.2/BSD-3Clause"; echo
  cat "$here/build/deps/openal-soft-1.25.2/fmt-11.2.0/LICENSE"; echo
  echo "----- Steam Audio (https://github.com/ValveSoftware/steam-audio) -----"; echo
  cat "$here/third_party/steamaudio/LICENSE.md"; echo
  cat "$here/third_party/steamaudio/THIRDPARTY.md"
} > "$1"
