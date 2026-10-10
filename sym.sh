#!/usr/bin/env sh
# Search the client's symbols -- the recurring "what do I hook" task.
#   ./sym.sh CNWSMessage         # demangled, readable
#   ./sym.sh -m GLRender::SwapBuffers   # mangled, to paste into HookService.Install
# The binary: NWN_BIN, else tools/paths.py (local.env, then your Steam library)
BIN="${NWN_BIN:-$(python3 "$(dirname "$0")/tools/paths.py" NWN_BIN)}"
DEMANGLE=-C
if [ "$1" = "-m" ]; then DEMANGLE=; shift; fi
[ -f "$BIN" ] || { echo "client not found: $BIN  (set NWN_BIN)"; exit 1; }
[ -n "$1" ] || { echo "usage: $0 [-m] <pattern>"; exit 1; }
nm $DEMANGLE --defined-only "$BIN" | grep -i "$1"
