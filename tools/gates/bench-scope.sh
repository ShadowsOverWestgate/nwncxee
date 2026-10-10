#!/usr/bin/env bash
# CX-197: /alloy bench's ablation ladder keeps its own footprint. Its state and its rung tables live in its two
# files (managed/Alloy/Ablation.cs, managed/Alloy/AlloyRenderer.Bench.cs); a pass holds one program field and no
# bench-only one. The ladder may be NAMED only at the sanctioned call sites below -- the context-loss and
# module-change edges, the blended accumulation's scope and the issue loop's draw cap.
cd "$(dirname "$0")/../.."

own='managed/Alloy/(Ablation|AlloyRenderer\.Bench)\.cs'
sites='managed/Alloy/(Ablation|AlloyRenderer\.Bench|AlloyRenderer|AlloyRenderer\.Frame|Issue|Programs)\.cs'
# the ladder's own state, and the shapes it used to leave in a pass
inner='AblateProgram|BlendAblate|_progFull|_progAbl|_progBench|_ablating|_blendAbl|_blendProg|_blendSkip'
inner="$inner"'|_blendOne|_drawCap|LitDefines|LitRung|_litProgs|_litSkip|_litCap|\bAblate\('

rc=0
bad=$(grep -rnE "$inner" --include='*.cs' managed | grep -vE "^$own:") || true
if [ -n "$bad" ]; then
  echo "!! bench-scope: the ablation ladder's own state outside its files (one program field per pass):" >&2
  echo "$bad" | sed 's/^/  /' >&2
  rc=1
fi

bad=$(grep -rnE '\bAblation\b' --include='*.cs' managed | grep -vE "^$sites:") || true
if [ -n "$bad" ]; then
  echo "!! bench-scope: the bench ladder named outside its sanctioned call sites (add it to tools/gates/bench-scope.sh only with a reason):" >&2
  echo "$bad" | sed 's/^/  /' >&2
  rc=1
fi
exit $rc
