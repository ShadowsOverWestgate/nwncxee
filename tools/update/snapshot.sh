#!/usr/bin/env bash
# Keep a copy of the client binaries CX is measured against, outside Steam's reach.
#
#   tools/update/snapshot.sh [GAME_BIN]      (default: the Steam install's bin/)
#
# Update day diffs the OLD build against the new one, and Steam overwrites the old one in place with no
# warning. So every build that gets a Framework/Builds/*.tsv also gets a snapshot, taken while it is live:
# $CX_BUILDS/<name>/{linux-x86,win32}/ (default ~/nwn-builds), named by the build file whose key matches.
set -euo pipefail
cd "$(dirname "$0")/../.."
BIN="${1:-$HOME/.local/share/Steam/steamapps/common/Neverwinter Nights/bin}"
DEST="${CX_BUILDS:-$HOME/nwn-builds}"
bid=$(readelf -n "$BIN/linux-x86/nwmain-linux" | awk '/Build ID/ { print $3 }')
name=$(awk -F'\t' -v k="$bid" '$1 == "name" { n = $2 } $1 == "linux" && $2 == k { print n; exit }' managed/Framework/Builds/*.tsv)
[ -n "$name" ] || { echo "build-id $bid is not in any managed/Framework/Builds/*.tsv -- snapshot a KNOWN build"; exit 1; }
out="$DEST/$name"
mkdir -p "$out/linux-x86" "$out/win32"
cp -p "$BIN/linux-x86/nwmain-linux" "$BIN/linux-x86/build.txt" "$out/linux-x86/"
cp -p "$BIN/win32/nwmain.exe" "$out/win32/"
echo "snapshot $name ($bid) -> $out"
