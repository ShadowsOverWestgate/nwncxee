#!/usr/bin/env bash
# Claude Code PostToolUse hook (Edit|Write): runs the gates the moment a file in this repo changes, so a
# violation surfaces as feedback (exit 2 = stderr shown to Claude) rather than at the end of a session.
f=$(jq -r '.tool_input.file_path // empty')
case "$f" in "$(cd "$(dirname "$0")/../.." && pwd)"/*) ;; *) exit 0 ;; esac
out=$(bash "$(dirname "$0")/run.sh" 2>&1) && exit 0
echo "$out" >&2
exit 2
