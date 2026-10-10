#!/usr/bin/env bash
# Shrink-only baseline: stdin is "<count> <file>" lines (the current census), $1 the baseline in the same form.
# Fails when a file's count exceeds its baseline (absent = 0). `--update` as $2 rewrites the baseline, but only
# downwards: it refuses while any file is over. A file at 0 drops out of the baseline.
base=$1 cur=$(mktemp); trap 'rm -f "$cur"' EXIT
awk '$1 > 0' > "$cur"
over=$(awk 'FILENAME == ARGV[1] { b[$2]=$1; next } $1 > (b[$2]+0) { printf "  %s: %d (baseline %d)\n", $2, $1, b[$2]+0 }' \
  "${base}" "$cur" 2>/dev/null || awk '{ printf "  %s: %d (baseline 0)\n", $2, $1 }' "$cur")
if [ -n "$over" ]; then
  echo "!! $(basename "$base" .baseline): over the baseline ($base may only shrink):" >&2
  echo "$over" >&2
  exit 1
fi
if [ "$2" = --update ]; then sort -k2 "$cur" > "$base"; fi
exit 0
