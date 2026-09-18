#!/usr/bin/env bash
# Writes LICENSES.txt ($1) from the repo's LICENSE plus the verbatim notices of every
# third-party library linked into the shipped binaries.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
{
  echo "SoW client extender -- licenses"
  echo "==============================="
  echo
  echo "The extender itself is MIT licensed. It links funchook (GPLv2+ with a linking"
  echo "exception), diStorm3 (BSD 3-clause) and xatlas (MIT). Their notices follow in full."
  echo
  echo "----- NWNCX:EE (MIT) -----"; echo
  cat "$here/LICENSE"; echo
  echo "----- funchook (https://github.com/kubo/funchook) -----"; echo
  cat "$here/third_party/funchook/LICENSE"; echo
  echo "----- diStorm3 (https://github.com/gdabah/distorm) -----"; echo
  cat "$here/third_party/funchook/distorm/COPYING"; echo
  echo "----- xatlas (https://github.com/jpcy/xatlas) -----"; echo
  cat "$here/native/xatlas/LICENSE"
} > "$1"
