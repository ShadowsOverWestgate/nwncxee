#!/usr/bin/env bash
# Run OffsetDump.java over one program in the nwn-linux Ghidra project.
#
#   tools/ghidra/dump-offsets.sh nwmain-linux out/lin.tsv all
#   tools/ghidra/dump-offsets.sh nwmain.exe   out/win.tsv all addr:1407a41a0 ...
#
# Read-only, no re-analysis. The project must not be open in the Ghidra GUI.
set -euo pipefail
GHIDRA="${GHIDRA:-/opt/ghidra}"
PROJDIR="${GHIDRA_PROJDIR:-$HOME/ghidra}"
PROJ="${GHIDRA_PROJ:-nwn-linux}"
HERE="$(cd "$(dirname "$0")" && pwd)"

prog="${1:?program name (nwmain-linux | nwmain.exe)}"; shift
out="${1:?output tsv path}"; shift

export GHIDRA_HEADLESS_MAXMEM="${GHIDRA_HEADLESS_MAXMEM:-8G}"
"$GHIDRA/support/analyzeHeadless" "$PROJDIR" "$PROJ" \
    -process "$prog" -noanalysis -readOnly \
    -scriptPath "$HERE" -postScript OffsetDump.java "$out" "$@" \
  2>&1 | grep -E 'OffsetDump\.java>|ERROR|error:' || true
