#!/usr/bin/env python3
"""CX-199 census: engine memory read outside the typed views.

Two rules, per .cs file under managed/ (Framework/ and the generated file excluded -- the hand-written view
partials live there and a view's own body is the one place the offset is read):

1. `ptr +/- EngineLayout.Row` for a Row whose generated view member exists. The row set comes from
   Views.g.cs, so the gate tracks the generator rather than a list kept by hand. A record STRIDE (the
   generator's `this[i]`) and a VTABLE SLOT (whose member indexes an object's vptr, not a bare table) are
   not field members and are left alone.
2. A `not-an-offset:` marker on a line that dereferences `ptr +/- 0x...`. The marker is for constants --
   flag bits, file formats, sizes -- never for a place in engine memory.

Prints "<count> <file>" for tools/gates/ratchet.sh; `-v` lists each offending line instead.
"""
import os, re, sys

VIEWS = 'managed/Framework/Engine/Views.g.cs'
ROW = re.compile(r'EngineLayout\.(\w+)')
# A multiplier is allowed in front: `ptr + i * EngineLayout.Row` walks records the view indexes for you.
ARITH = r'[-+]\s*(?:[A-Za-z_]\w*(?:\.\w+)*\s*\*\s*)?EngineLayout\.(\w+)'
INDEX = r'\w\s*\[\s*EngineLayout\.(\w+)\s*\]'
# A cast-dereference of a pointer plus a hex literal: the shape a layout row exists for.
DEREF = re.compile(r'\*\s*\(\s*\w+\s*\*+\s*\)\s*\(\s*[\w.]+\s*[-+]\s*0[xX][0-9a-fA-F]+')
MARKER = 'not-an-offset:'


def field_rows(text):
    """Rows the generator emits a FIELD member for: not the `this[i]` stride, not a vtable slot."""
    rows = set()
    for line in text.splitlines():
        s = line.lstrip()
        if s.startswith('///') or 'this[int i]' in s or '*(IntPtr*)Ptr + EngineLayout.' in s:
            continue
        rows |= set(ROW.findall(line))
    return rows


def main():
    rows = field_rows(open(VIEWS, encoding='utf-8').read())
    arith, index = re.compile(ARITH), re.compile(INDEX)
    verbose = '-v' in sys.argv
    for root, _, files in os.walk('managed'):
        for name in sorted(files):
            if not name.endswith('.cs') or name.endswith('.g.cs'):
                continue
            path = os.path.join(root, name)
            inside = root.startswith('managed/Framework')
            hits = []
            for n, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
                if MARKER in line and DEREF.search(line):
                    hits.append(f'{path}:{n}: `{MARKER}` on a line that reads engine memory -- '
                                f'the offset belongs in EngineLayout.cs')
                if inside:
                    continue
                for m in list(arith.finditer(line)) + list(index.finditer(line)):
                    if m[1] in rows:
                        hits.append(f'{path}:{n}: EngineLayout.{m[1]} read by hand -- '
                                    f'its view member in Views.g.cs is the read')
            if verbose:
                print('\n'.join(hits)) if hits else None
            elif hits:
                print(len(hits), path)


if __name__ == '__main__':
    main()
