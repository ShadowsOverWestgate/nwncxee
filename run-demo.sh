#!/usr/bin/env sh
# Proves the extender loads and runs C# inside a foreign process -- using
# /usr/bin/true as a stand-in so no game/X11/GL is needed to see it work.
set -e
cd "$(dirname "$0")"
B="$PWD/build"

[ -f "$B/libsowx.so" ] || { echo "run ./build.sh first"; exit 1; }
rm -f /tmp/sowx.log

echo ">> launching /usr/bin/true with the extender preloaded..."
SOWX_MANAGED="$B/sowx_managed.so" LD_PRELOAD="$B/libsowx.so" /usr/bin/true

echo
echo ">> /tmp/sowx.log (written from C#):"
cat /tmp/sowx.log

echo
echo "Attach to the real client the same way:"
echo "  SOWX_MANAGED=$B/sowx_managed.so LD_PRELOAD=$B/libsowx.so ~/nwmain-linux"
