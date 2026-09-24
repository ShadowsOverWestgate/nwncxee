#!/usr/bin/env python3
"""Score rvamatch against ground truth: two builds of the same PE/ELF, names hidden from the matcher.

    backtest.py OLD NEW [--classes CNWSCreature,CNWSItem] [--sample 400] [--data 60]

`--classes` hides every export of those classes at once -- the realistic case, since CX's RVA targets
are the client-only classes MSVC never exported, so their neighbourhoods have no anchors at all.
A wrong answer the matcher called confident is the only number that must stay at zero.
"""
import argparse, collections, random
from binfmt import Image
from rvamatch import Matcher

ap = argparse.ArgumentParser()
ap.add_argument('old'); ap.add_argument('new')
ap.add_argument('--classes', default='')
ap.add_argument('--sample', type=int, default=400)
ap.add_argument('--data', type=int, default=60)
ap.add_argument('--seed', type=int, default=1)
a = ap.parse_args()
A, B = Image(a.old), Image(a.new)
rng = random.Random(a.seed)
common = sorted(n for n in A.names if n in B.names)
by_rva = collections.Counter(A.names[n] for n in common)
common = [n for n in common if by_rva[A.names[n]] == 1]          # skip ICF-folded names: ambiguous truth
classes = [c for c in a.classes.split(',') if c]
if classes:
    hidden = [n for n in common if any(f'@{c}@@' in n or f'N{len(c)}{c}' in n for c in classes)]
else:
    hidden = rng.sample(common, min(a.sample, len(common)))
targets = rng.sample(hidden, min(a.sample, len(hidden)))
m = Matcher(A, B, hide=hidden)
CONF = {'name', 'delta', 'interval', 'global', 'rtti', 'xref'}
tally = collections.Counter()
for n in targets:
    got, how, s = m.func(A.names[n])
    ok = got == B.names[n]
    tally[(how in CONF, ok)] += 1
    if how in CONF and not ok:
        print(f'CONFIDENT WRONG {how} {s:.2f} {n}')
print(f'code: {len(targets)} targets, {len(hidden)} hidden of {len(common)} exports')
print(f'  confident right {tally[(True, True)]}  confident WRONG {tally[(True, False)]}  '
      f'weak right {tally[(False, True)]}  weak wrong {tally[(False, False)]}')

dcommon = [n for n in A.data_names if n in B.data_names and not n.startswith('??_')]
dt = collections.Counter()
for n in rng.sample(dcommon, min(a.data, len(dcommon))):
    got, how, s = m.xref(A.data_names[n])
    ok = got == B.data_names[n]
    dt[(how in CONF, ok)] += 1
    if how in CONF and not ok:
        print(f'CONFIDENT WRONG data {how} {s:.2f} {n}')
print(f'data: confident right {dt[(True, True)]}  confident WRONG {dt[(True, False)]}  '
      f'weak/none right {dt[(False, True)]}  weak/none wrong {dt[(False, False)]}')
