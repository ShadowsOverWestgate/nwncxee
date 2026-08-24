#!/usr/bin/env sh
# Proves the extender loads and runs C# inside a foreign process -- using
# /usr/bin/env as a stand-in so no game/X11/GL is needed to see it work.
# (`true` is a shell builtin, so it spawns no process to preload into.)
set -e
cd "$(dirname "$0")"
B="$PWD/sow-client-extender"

[ -f "$B/libsowx.so" ] || { echo "run ./build.sh first"; exit 1; }
LOG="${HOME:-/tmp}/sowx.log"   # ExtenderLog writes here (see ExtenderLog.cs)
# ROTATE, never delete. This is the same file the real client writes, and a client session's log is
# the only record of what happened in a live area -- deleting it means a run-demo after a play session
# destroys the evidence you were about to read. (It has: a large-area diagnosis was mid-flight.)
[ -f "$LOG" ] && mv -f "$LOG" "$LOG.prev"

echo ">> launching /usr/bin/env with the extender preloaded..."
SOWX_MANAGED="$B/sowx_managed.so" LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libsowx.so" /usr/bin/env >/dev/null

echo
echo ">> $LOG (written from C#):"
cat "$LOG"

echo
echo "Attach to the real client with the launcher:"
echo "  NWN_BIN=/path/to/bin/linux-x86/nwmain-linux $B/sowx-launch"
