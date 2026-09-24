#!/usr/bin/env python3
"""Score layout.py's verdicts against ground truth mined from two unstripped ELFs of the same program.

    layout_backtest.py OLD.elf NEW.elf

Ground truth: one-field getters (`mov eax,[rdi+X]; ret` and kin) present in both builds by name -- the
displacement each reads IS the field offset, old and new. Each becomes a synthetic row whose canaries are
its class's other members (the getter itself is hidden). The only number that must be zero is MOVED rows
called carried/likely: that is a feature reading a stale offset.
"""
import collections, sys
from binfmt import Image
import layout
from rvamatch import same


def getter_disp(body):
    """The displacement a tiny `this`-relative load-and-return reads, or None."""
    if body is None or not (3 <= len(body) <= 24) or body[-1] != 0xC3:
        return None
    d = layout.disps(body[:-1], base=7)
    if len(d) != 1:
        return None
    (v, n), = d.items()
    return v if n == 1 and v > 0 else None


A, B = Image(sys.argv[1]), Image(sys.argv[2])
a, b = layout.Index(A, layout.itanium), layout.Index(B, layout.itanium)
tally, wrong = collections.Counter(), []
for cls, mem in a.members.items():
    for g in mem:
        vo, vn = getter_disp(a.body(g)), getter_disp(b.body(g))
        if vo is None or vn is None:
            continue
        canary = mem - {g}
        if not canary:
            continue
        moved = sorted(s for s in canary if not layout.unchanged(a, b, s))
        if not moved:
            verdict = 'carried'
        else:
            verdict, notes = layout.survives(a, b, set(moved), vo)
            verdict = verdict or 'blind'
            if verdict == 'changed' and vn != vo:
                cands = next((n for n in notes if n.startswith('candidates: ')), '')
                tally['cand hit' if f'{vn:#x}' in cands.split(': ', 1)[-1].split(', ') else 'cand miss'] += 1
        truth = 'moved' if vn != vo else 'same'
        tally[(truth, verdict)] += 1
        if truth == 'moved' and verdict in ('carried', 'likely'):
            wrong.append((g, hex(vo), hex(vn), verdict))
for t in ('same', 'moved'):
    print(t, {v: tally[(t, v)] for v in ('carried', 'likely', 'changed', 'blind')})
print('suggested value right:', tally['cand hit'], 'of', tally['cand hit'] + tally['cand miss'], 'moved+changed rows')
print('STALE (moved but called carried/likely):', len(wrong))
for w in wrong[:10]:
    print('  ', *w)
