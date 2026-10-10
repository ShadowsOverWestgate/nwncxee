#!/usr/bin/env bash
# A tracked file names no one machine's path: a home directory, a mount point, a personal checkout layout.
# Per-machine paths resolve through tools/paths.py (environment, local.env, stock default). Not scanned:
# docs/adr/ and docs/handoff/ (dated records) and local.env.example (its commented examples).
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE '/home/[a-z_][a-z0-9_.-]*/|/Users/[A-Za-z][^/ ]*/|/mnt/[a-z]+/|[A-Za-z]:\\Users\\|~/(repos|gry|nwn-decomp)/|~/ghidra/' \
  -- . ':!docs/adr' ':!docs/handoff' ':!local.env.example' ':!tools/gates/local-paths.sh')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! local-paths: a machine-specific path in a tracked file (above); resolve it through tools/paths.py" >&2
exit 1
