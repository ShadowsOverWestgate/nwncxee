#!/usr/bin/env bash
# Intent: tools/no-decompiled.sh goes red on a Ghidra listing, on a tracked
# decompiled/ path, and on a listing a pushed range added and later deleted, and
# stays green on prose that names a symbol, an address and a constant. Without this, a pattern slip turns the tripwire quietly green.
set -euo pipefail

check="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/no-decompiled.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cd "$work"
git init -q
git config user.email contract@example.invalid
git config user.name contract
git config commit.gpgsign false

cat >notes.md <<'MD'
`CNWSCreature::BroadcastSpellCast` at `0x46c2d0` rolls a d20 against the
spell's level plus 15, and FUN_0059fe60 holds the constant 0x14.
MD
git add .
if ! bash "$check" "$work" >/dev/null; then echo "FAIL: prose went red" >&2; exit 1; fi

printf 'u%s12 = RollDice(par%s_1, 1, 0x14);\n' Var am >listing.md
git add . && if bash "$check" "$work" >/dev/null 2>&1; then echo "FAIL: a listing passed" >&2; exit 1; fi
git rm -q --cached listing.md && rm listing.md

mkdir decompiled && echo 'reads fine' >decompiled/Notes.txt
git add . && if bash "$check" "$work" >/dev/null 2>&1; then echo "FAIL: a decompiled/ path passed" >&2; exit 1; fi

# A listing committed and then deleted is gone from the tree but not from the push.
git rm -rqf decompiled && git commit -qm base
before="$(git rev-parse HEAD)"
printf 'u%s12 = RollDice(par%s_1, 1, 0x14);\n' Var am >leak.md && git add leak.md && git commit -qm leak
git rm -q leak.md && git commit -qm "delete the leak"
if bash "$check" "$work" "$before" HEAD >/dev/null 2>&1; then echo "FAIL: a deleted listing in the pushed range passed" >&2; exit 1; fi
bash "$check" "$work" >/dev/null || { echo "FAIL: the clean tree went red" >&2; exit 1; }

echo "no-decompiled-contract: OK"
