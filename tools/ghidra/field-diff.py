#!/usr/bin/env python3
"""Per-class Linux/Windows field-displacement diff, with per-pair sanity filters and a
pairing-free corroboration (the raw offset sets touched by all of a class's functions)."""
import sys, collections

def load(path):
    out = collections.defaultdict(list)
    for line in open(path):
        if not line.startswith('FN\t'):
            continue
        p = line.rstrip('\n').split('\t')
        cls, nm, entry = p[1], p[2], p[3]
        acc = p[7].strip() if len(p) > 7 else ''
        items = None
        if acc and acc != 'ERR' and not acc.startswith('EXC'):
            items = []
            for t in acc.split():
                o, rest = t.split(':')
                items.append((int(o, 16), int(rest[:-1]), rest[-1]))
        out[(cls, nm)].append(dict(entry=entry, acc=items))
    return out

lin, win = load(sys.argv[1]), load(sys.argv[2])
classes = sorted({c for c, _ in lin} & {c for c, _ in win})

def allsets(d, cls):
    s = collections.Counter()
    for (c, nm), fns in d.items():
        if c != cls: continue
        for f in fns:
            for o, sz, rw in (f['acc'] or []):
                s[o] += 1
    return s

for cls in classes:
    per_off = collections.defaultdict(collections.Counter)   # lin off -> Counter(delta)
    witness = collections.defaultdict(set)
    used, skip = 0, collections.Counter()
    for (c, nm) in sorted(lin):
        if c != cls or (c, nm) not in win: continue
        L, W = lin[(c, nm)], win[(c, nm)]
        if len(L) != 1 or len(W) != 1: skip['ambiguous-name'] += 1; continue
        la, wa = L[0]['acc'], W[0]['acc']
        if la is None or wa is None: skip['decomp-fail'] += 1; continue
        if not la or not wa: skip['no-access'] += 1; continue
        if len(la) != len(wa): skip['length-mismatch'] += 1; continue
        if [x[1:] for x in la] != [x[1:] for x in wa]: skip['shape-mismatch'] += 1; continue
        # induced map must be a function and order-preserving
        mp = {}
        bad = False
        for (lo, _, _), (wo, _, _) in zip(la, wa):
            if mp.get(lo, wo) != wo: bad = True; break
            mp[lo] = wo
        if bad: skip['inconsistent-map'] += 1; continue
        ks = sorted(mp)
        vs = [mp[k] for k in ks]
        if any(vs[i] >= vs[i+1] for i in range(len(vs)-1)): skip['non-monotonic'] += 1; continue
        used += 1
        for k in ks:
            per_off[k][mp[k]-k] += 1
            witness[(k, mp[k]-k)].add(nm)

    ls, ws = allsets(lin, cls), allsets(win, cls)
    only_l = sorted(set(ls) - set(ws)); only_w = sorted(set(ws) - set(ls))
    print(f"\n===== {cls}")
    print(f"  paired+validated fns: {used}   skipped: {dict(skip)}")
    conf = {o: c for o, c in per_off.items() if len(c) > 1}
    cons = {o: next(iter(c)) for o, c in per_off.items() if len(c) == 1}
    hist = collections.Counter(cons.values())
    print(f"  distinct linux offsets mapped: {len(per_off)}  conflicted: {len(conf)}")
    print(f"  delta histogram: " + ", ".join(f"{d:+#x}:{n}" for d, n in sorted(hist.items())))
    seq = []
    for o in sorted(cons):
        d = cons[o]
        seq.append(f"{o:#x}{'' if d==0 else format(d,'+#x')}[{sum(per_off[o].values())}]")
    for i in range(0, len(seq), 7):
        print("     " + " ".join(seq[i:i+7]))
    if conf:
        print("  conflicted:", {hex(o): {hex(d): n for d, n in c.items()} for o, c in sorted(conf.items())})
    print(f"  set corroboration: |lin|={len(ls)} |win|={len(ws)} shared={len(set(ls)&set(ws))}")
    if only_l: print("    lin-only:", " ".join(hex(o) for o in only_l[:25]), "..." if len(only_l)>25 else "")
    if only_w: print("    win-only:", " ".join(hex(o) for o in only_w[:25]), "..." if len(only_w)>25 else "")
