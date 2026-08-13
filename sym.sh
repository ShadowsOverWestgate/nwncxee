#!/usr/bin/env sh
# Search the client's (demangled) symbols -- the recurring "what do I hook" task.
#   ./sym.sh CNWSMessage        # find message-layer methods
#   ./sym.sh SwapBuffers        # find the GL present path
# Override the binary with NWN_BIN=/path/to/nwmain-linux
BIN="${NWN_BIN:-~/gry/steam/Neverwinter Nights/bin/linux-x86/nwmain-linux}"
[ -f "$BIN" ] || { echo "client not found: $BIN  (set NWN_BIN)"; exit 1; }
[ -n "$1" ] || { echo "usage: $0 <pattern>"; exit 1; }
nm -C --defined-only "$BIN" | grep -i "$1"
