#!/usr/bin/env sh
# Launch the NWN:EE client with the extender preloaded.
# NWN insists its working directory be the platform dir, so we cd there first;
# the LD_PRELOAD/SOWX_MANAGED paths stay absolute so the cd doesn't break them.
set -e
cd "$(dirname "$0")"
B="$PWD/build"

GAME_BIN="${NWN_BIN:-~/gry/steam/Neverwinter Nights/bin/linux-x86/nwmain-linux}"
[ -f "$GAME_BIN" ] || { echo "client not found: $GAME_BIN  (override with NWN_BIN=/path/to/nwmain-linux)"; exit 1; }
[ -f "$B/libsowx.so" ] || { echo "run ./build.sh first"; exit 1; }

cd "$(dirname "$GAME_BIN")"
exec env SOWX_MANAGED="$B/sowx_managed.so" LD_PRELOAD="$B/libsowx.so" "./$(basename "$GAME_BIN")" "$@"
