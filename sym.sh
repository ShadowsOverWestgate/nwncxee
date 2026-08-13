#!/usr/bin/env sh
# Search the client's symbols -- the recurring "what do I hook" task.
#   ./sym.sh CNWSMessage         # demangled, readable
#   ./sym.sh -m GLRender::SwapBuffers   # mangled, to paste into HookService.Install
# Override the binary with NWN_BIN=/path/to/nwmain-linux
BIN="${NWN_BIN:-~/gry/steam/Neverwinter Nights/bin/linux-x86/nwmain-linux}"
DEMANGLE=-C
if [ "$1" = "-m" ]; then DEMANGLE=; shift; fi
[ -f "$BIN" ] || { echo "client not found: $BIN  (set NWN_BIN)"; exit 1; }
[ -n "$1" ] || { echo "usage: $0 [-m] <pattern>"; exit 1; }
nm $DEMANGLE --defined-only "$BIN" | grep -i "$1"
