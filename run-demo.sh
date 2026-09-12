#!/usr/bin/env sh
# Proves the extender loads and runs C# inside a foreign process -- using a linked-here stub host so no
# game/X11/GL is needed to see it work. The stub carries the client's build-id because #457's rot gate
# refuses an unknown host before discovery; /usr/bin/env is then run as the rot case.
set -e
cd "$(dirname "$0")"
B="$PWD/sow-client-extender"

# Same reason as run-proton.sh: this runs the STAGED artifacts, so without a build it happily
# smoke-tests the previous edit. ~20s when nothing changed; SOWX_NO_BUILD=1 to run what is staged.
[ -n "$SOWX_NO_BUILD" ] || ./build.sh
[ -f "$B/libsowx.so" ] || { echo "run ./build.sh first"; exit 1; }
# ExtenderLog writes to $HOME/sowx.log, which is where a real SESSION's log lives too -- a headless run
# here would truncate the log the last game session left behind, which is the only evidence a live-verified
# ticket has. Give the demo its own HOME so the two can never collide.
DEMOHOME="${TMPDIR:-/tmp}/sowx-demo"
mkdir -p "$DEMOHOME"
LOG="$DEMOHOME/sowx.log"
# ROTATE, never delete. This is the same file the real client writes, and a client session's log is
# the only record of what happened in a live area -- deleting it means a run-demo after a play session
# destroys the evidence you were about to read. (It has: a large-area diagnosis was mid-flight.)
[ -f "$LOG" ] && mv -f "$LOG" "$LOG.prev"

# #457: rot stands the WHOLE extender down, before discovery, keyed on the host's .note.gnu.build-id.
# /usr/bin/env is by definition a wrong build, so a demo that ran in it would prove the refusal and nothing
# else -- and every SelfCheck suite lives inside a service's Initialize. So the demo links a do-nothing host
# carrying the client's build-id and runs in that. The key comes from EngineLayout, never a second copy.
BID=$(grep -oP 'LinuxBuildId = "\K[0-9a-f]+' managed/Framework/EngineLayout.cs)
[ -n "$BID" ] || { echo ">> FAIL: no LinuxBuildId in managed/Framework/EngineLayout.cs"; exit 1; }
STUB="$DEMOHOME/demo-host"
echo 'int main(void){return 0;}' | cc -x c - -o "$STUB" -Wl,--build-id=0x"$BID"

echo ">> launching the demo host with the extender preloaded..."
HOME="$DEMOHOME" SOWX_MANAGED="$B/sowx_managed.so" LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libsowx.so" "$STUB" >/dev/null

echo
echo ">> $LOG (written from C#):"
cat "$LOG"

# The gate: the SelfCheck suites assert real invariants, and until #157 the log could say FAILED
# all day and CI still went green. Six spellings exist across the call sites -- FAILED, FAIL, both
# colon forms, and "had FAILURES" -- so match the suite name and the word loosely rather than
# enumerating them. A log with no SelfCheck line at all is also a failure: the services never ran.
echo
if ! grep -q 'SelfCheck' "$LOG"; then
  echo ">> FAIL: no SelfCheck lines in $LOG -- services never ran"; exit 1
fi
if grep -qE 'SelfCheck.*FAIL' "$LOG"; then
  echo ">> FAIL: a SelfCheck suite failed:"; grep -E 'SelfCheck.*FAIL' "$LOG"; exit 1
fi
echo ">> $(grep -c 'SelfCheck' "$LOG") SelfCheck lines, none failed."

# The other half of #457: the refusal itself, run for real rather than asserted in a unit. /usr/bin/env IS
# the rot case -- a host the offsets were never measured against -- so the gate must discover nothing.
ROTHOME="${TMPDIR:-/tmp}/sowx-demo-rot"
rm -rf "$ROTHOME"; mkdir -p "$ROTHOME"
HOME="$ROTHOME" SOWX_MANAGED="$B/sowx_managed.so" LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libsowx.so" /usr/bin/env >/dev/null
echo
echo ">> rot case ($ROTHOME/sowx.log):"
grep 'host:' "$ROTHOME/sowx.log" || true
if ! grep -q 'standing down WHOLE' "$ROTHOME/sowx.log"; then
  echo ">> FAIL: rot gate did not stand the extender down in a foreign host"; exit 1
fi
if ! grep -q '0 service(s) discovered, 0 hook(s) installed' "$ROTHOME/sowx.log"; then
  echo ">> FAIL: the rot line did not report zero services and zero hooks"; exit 1
fi
echo ">> rot: extender stood down whole, nothing discovered."

echo
echo "Attach to the real client with the launcher:"
echo "  NWN_BIN=/path/to/bin/linux-x86/nwmain-linux $B/sowx-launch"
