#!/usr/bin/env sh
# Launch the NWN:EE client with the extender preloaded.
# NWN insists its working directory be the platform dir, so we cd there first;
# the LD_PRELOAD/SOWX_MANAGED paths stay absolute so the cd doesn't break them.
#
# If launched detached (from a .desktop/launcher, no terminal), we open one so
# the game log is visible. Run from a terminal and it just runs there.
set -e
cd "$(dirname "$0")"
SELF="$PWD/$(basename "$0")"
B="$PWD/build"

GAME_BIN="${NWN_BIN:-~/gry/steam/Neverwinter Nights/bin/linux-x86/nwmain-linux}"
[ -f "$GAME_BIN" ] || { echo "client not found: $GAME_BIN  (override with NWN_BIN=/path/to/nwmain-linux)"; exit 1; }
[ -f "$B/libsowx.so" ] || { echo "run ./build.sh first"; exit 1; }

# No terminal attached and not already inside one we spawned -> open one.
if [ ! -t 1 ] && [ -z "$SOWX_SPAWNED" ]; then
    term="$TERMINAL"
    [ -n "$term" ] || for t in kitty alacritty foot wezterm konsole gnome-terminal xterm st; do
        command -v "$t" >/dev/null 2>&1 && { term="$t"; break; }
    done
    [ -n "$term" ] || { echo "no terminal emulator found; run $SELF from a terminal" >&2; exit 1; }
    exec "$term" -e sh -c "SOWX_SPAWNED=1 '$SELF' $*; echo; printf '[nwn exited -- press Enter]'; read _"
fi

cd "$(dirname "$GAME_BIN")"
env SOWX_MANAGED="$B/sowx_managed.so" SOWX_KEYDIAG=1 LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libsowx.so" "./$(basename "$GAME_BIN")" "$@"
