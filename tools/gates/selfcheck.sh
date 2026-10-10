#!/usr/bin/env bash
# CX-200: a self-check assertion must be able to fail. Refuses, inside a *SelfCheck* file or a SelfCheck
# method, an Expect whose condition is a compile-time constant, one comparing a const to its own kind of
# literal, one whose two operands are the same text, and a hand-written SKIPPED log (a skip goes through
# Checker.Skip, which counts it). `python3 tools/gates/selfcheck.py -v` prints each hit's expression.
cd "$(dirname "$0")/../.."
python3 tools/gates/selfcheck.py
