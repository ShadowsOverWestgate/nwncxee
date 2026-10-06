#!/usr/bin/env bash
# Refuses decompiled engine code anywhere in the tracked tree (sow-platform ADR-0064).
#
# Committing decompiler or disassembler output is not legal for us, so no
# repository holds it, in any form. Symbol names, addresses, engine constants and
# prose about what the engine does are fine; a listing is kept on your own
# machine, outside the checkout.
#
# This is a tripwire for the usual signatures: Ghidra's generated names, IDA's
# types, objdump's line shape, and any path named decompiled/. A listing it
# misses is still forbidden. Scans the whole tree in one git grep, so it costs
# about a second.
#
# Usage: tools/no-decompiled.sh [repo-root] [base tip]
# With no range it checks the tree. With a range it checks every line and path the
# commits in base..tip add, so a listing committed and then deleted still fails:
# the pre-push hook passes each pushed range, because the blob travels either way.
# An empty base checks every commit of tip that no remote branch holds yet.
set -euo pipefail
cd "${1:-$(git rev-parse --show-toplevel)}"
base="${2-}" tip="${3-}"

signatures='WARNING: Decompi[l]|\b[iulbcp]Var[0-9]+\b|\blocal_[0-9a-f]{2,}\b|\bparam_[0-9]+\b|\bextraout_[a-z]|\b_[DQ]WORD\b|\bLODWORD\(|->field_0x[0-9a-f]|\bundefined[1248]\b|^\s*[0-9a-f]{5,}:\s+([0-9a-f]{2} ){2,}'
# The patterns are written so this file never matches them itself, and the
# contract builds its fixtures the same way; neither needs to be skipped.

found=0
report() {
  [[ -n "$2" ]] || return 0
  echo "no-decompiled: $1 (sow-platform ADR-0064):" >&2
  printf '%s\n' "$2" | sed 's/^/  /' >&2
  found=1
}
if [[ -z "$tip" ]]; then
  report "a path named decompiled/ is tracked" "$(git ls-files | grep -E '(^|/)decompiled/' || true)"
  report "decompiler output in the tree" "$(git grep -nIE "$signatures" -- . || true)"
else
  if [[ -n "$base" ]]; then range=("$base..$tip"); else range=("$tip" --not --remotes); fi
  report "a pushed commit adds a decompiled/ path" \
    "$(git log --format= --name-only --diff-filter=A "${range[@]}" | grep -E '(^|/)decompiled/' || true)"
  report "a pushed commit adds decompiler output" \
    "$(git log -p --format= "${range[@]}" -- . |
      grep -E '^\+' | grep -vE '^\+\+\+ ' | sed 's/^+//' | grep -E "$signatures" || true)"
fi
[[ $found -eq 0 ]] && echo "no-decompiled: OK"
exit "$found"
