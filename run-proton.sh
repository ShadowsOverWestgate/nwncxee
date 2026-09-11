#!/usr/bin/env sh
# The Windows hook-sanity loop (#48): run bin/win32/nwmain.exe under Proton with the cross-built DLLs
# dropped beside it, in a throwaway prefix under build/. Proves loading, symbol resolution and hooks
# on the real nwmain.exe; NOT a release gate -- that is a native Windows tester. Never touches Steam's
# Proton setting or the depot. Log: build/proton-pfx/pfx/drive_c/users/steamuser/Documents/Neverwinter Nights/sowx.log
set -e
cd "$(dirname "$0")"
GAME="${NWN_WIN:-~/gry/steam/Neverwinter Nights/bin/win32}"
PROTON="${PROTON:-$HOME/.steam/root/steamapps/common/Proton - Experimental/proton}"
[ -f "$GAME/nwmain.exe" ] || { echo "!! no nwmain.exe under $GAME (set NWN_WIN)" >&2; exit 1; }
[ -f "$PROTON" ] || { echo "!! no proton at $PROTON (set PROTON)" >&2; exit 1; }
cp sow-client-extender-win/*.dll "$GAME/"
PFX="$PWD/build/proton-pfx"; mkdir -p "$PFX"
# The engine picks "Neverwinter Nights.0" (its .N instance suffix) under Proton, so seed the cfg in both.
DOCS="$PFX/pfx/drive_c/users/steamuser/Documents/Neverwinter Nights"
for d in "$DOCS" "$DOCS.0"; do mkdir -p "$d"; [ -f "$d/sowx.cfg" ] || printf 'logs=1\n' > "$d/sowx.cfg"; done
rm -f "$DOCS/sowx.log"
# Wine prefers its builtin version.dll; n,b makes the proxy beside nwmain.exe win.
cd "$GAME" && STEAM_COMPAT_DATA_PATH="$PFX" STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.steam/root" \
  WINEDLLOVERRIDES="version=n,b" exec "$PROTON" run "$GAME/nwmain.exe" "$@"
