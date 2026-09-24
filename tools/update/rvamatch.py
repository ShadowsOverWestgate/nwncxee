#!/usr/bin/env python3
"""Carry RVAs from one build of a binary to the next -- no Ghidra, no disassembler.

    rvamatch.py OLD.exe NEW.exe 0x7a41a0 0xdb2e20 ...

Three kinds of target, told apart by where the RVA lives:
  code    -> function match: shared export name, else the delta of the nearest exported neighbours,
             else the most similar body inside the bracket those neighbours pin in the new build
  vtable  -> MSVC RTTI: vtable[-1] is a Complete Object Locator naming the class; find that name again
  data    -> RIP-relative xrefs from functions matched above; each re-read in the new build is a vote

Every answer carries `how` and `score`. `weak`/`none` is the list a human takes into Ghidra.
"""
import bisect, collections, difflib, functools, struct, sys
import numpy as np
from binfmt import Image

#: Similarity a same-length body needs to take the delta candidate outright.
DELTA_SIM = 0.85
#: Similarity (and lead over the runner-up) an interval winner needs to count as confident.
INTERVAL_SIM, INTERVAL_LEAD = 0.75, 0.08
#: Bytes compared per body; enough to tell functions apart, cheap enough for wide brackets.
CAP = 768
#: Candidates that survive the histogram shortlist to a real similarity score.
SHORTLIST = 8
#: Share of referrer votes the winner needs; a split vote is a human's call.
XREF_SHARE = 0.9
#: Normalized bytes before a displacement that must be found again in a changed referrer.
SITE_CTX = 8
#: Referrers a vote looks at before settling for what it has.
MAX_REFERRERS = 40
#: How far below a data target an xref may land and still vote (array base + index).
DATA_REACH = 64


class Side:
    """One build: function starts, bodies, a code byte array for xref scans."""

    def __init__(self, img):
        self.img = img
        s = set(img.funcs) | set(img.names.values())
        self.code = []
        for va, size, off in img.code:
            arr = np.frombuffer(img.raw, np.uint8, min(size, len(img.raw) - off), off)
            self.code.append((va, arr))
            if img.raw[:2] == b'MZ':
                # MSVC pads between functions with int3 and aligns entries to 16: that is where leaf
                # functions (no .pdata row) start
                here, prev = arr[16::16], arr[15::16]
                idx = np.nonzero((here != 0xCC) & (prev[:len(here)] == 0xCC))[0] * 16 + 16
                s.update(va + int(i) for i in idx)
        self.starts = sorted(s)

    def raw(self, rva, cap=1 << 20):
        """The function's bytes as linked, trailing int3 padding dropped."""
        if rva in self.img.funcs:
            end = rva + self.img.funcs[rva]
        else:
            i = bisect.bisect_right(self.starts, rva)
            end = self.starts[i] if i < len(self.starts) else rva + cap
        b = self.img.read(rva, min(end - rva, cap))
        return b.rstrip(b'\xcc') or b

    @functools.lru_cache(maxsize=None)
    def body(self, rva):
        """What similarity compares: the first CAP bytes, relink-moved displacements zeroed."""
        return norm(self.raw(rva, CAP))

    @functools.lru_cache(maxsize=None)
    def hist(self, rva):
        return np.bincount(np.frombuffer(self.body(rva), np.uint8), minlength=256)

    def rank(self, lo, hi, mine, mhist, want):
        """Starts in [lo, hi] best first as (sim, -distance to want, start): a length gate and a byte
        histogram shortlist, then the real similarity on the few survivors."""
        j0, j1 = bisect.bisect_left(self.starts, lo), bisect.bisect_right(self.starts, hi)
        n = len(mine)
        pre = []
        for c in self.starts[j0:j1]:
            m = len(self.body(c))
            if 0.6 * n <= m <= 1.6 * n + 8:
                pre.append((int(np.minimum(mhist, self.hist(c)).sum()) / max(n, m), c))
        pre.sort(reverse=True)
        return sorted(((sim(mine, self.body(c)), -abs(c - want), c) for _, c in pre[:SHORTLIST]), reverse=True)

    def index(self):
        """Every 4-byte window in code read as a RIP-relative disp32, kept when its target lands inside
        the image: (targets, sites), sorted by target. Built once; a lookup is a binary search."""
        if not hasattr(self, '_index'):
            hi = max(va + size for va, size, _ in self.img.secs)
            ts, ss = [], []
            for va, arr in self.code:
                for j in range(4):
                    n = (len(arr) - j) // 4
                    d = arr[j:j + 4 * n].view('<i4').astype(np.int64)
                    pos = va + j + 4 * np.arange(n, dtype=np.int64)
                    t = pos + 4 + d
                    keep = (t >= 0) & (t < hi)
                    ts.append(t[keep]); ss.append(pos[keep])
            t, st = np.concatenate(ts), np.concatenate(ss)
            o = np.argsort(t, kind='stable')
            self._index = (t[o], st[o])
        return self._index

    def containing(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        return self.starts[i] if i >= 0 else None


#: Opcodes whose ModRM may be RIP-relative (mov/lea/cmp/add/sub/call-indirect/...): a relink moves the
#: disp32 behind them, so it is not evidence about which function this is.
_RIPOPS = frozenset(b'\x8b\x8d\x89\x3b\x39\x03\x2b\x33\x0b\x23\x85\xff\xc7\x80\x81\x83\xf6\xf7\x10\x11\x28\x29\x2e\x2f\x58\x59\x5c\x5e\x54\x57\xc6')


_RIPARR = np.frombuffer(bytes(_RIPOPS), np.uint8)


def moved(b):
    """Bool mask of the bytes a relink rewrites: rel32 after call/jmp/jcc and disp32 after a RIP-relative
    ModRM. No decoder -- a byte that only looks like an opcode masks a few bytes too many, and not the
    same ones in both builds; compare through `same`, which takes the union."""
    a = np.frombuffer(b, np.uint8)
    n = len(a)
    keep = np.ones(n + 6, bool)
    if n >= 2:
        nxt = np.append(a[1:], 0)
        call = (a == 0xE8) | (a == 0xE9)
        jcc = (a == 0x0F) & (nxt >= 0x80) & (nxt <= 0x8F)
        rip = np.isin(a, _RIPARR) & ((nxt & 0xC7) == 0x05)
        for src, first in ((call, 1), (jcc, 2), (rip, 2)):
            idx = np.nonzero(src)[0] + first
            for k in range(4):
                keep[idx + k] = False
    return ~keep[:n]


def norm(b):
    """The body with every relink-moved byte zeroed (one build's mask: for similarity, not equality)."""
    a = np.frombuffer(b, np.uint8)
    return (a * ~moved(b)).astype(np.uint8).tobytes()


def same(x, y):
    """Byte-identical outside what either build's relink could have moved."""
    if len(x) != len(y):
        return False
    if x == y:
        return True
    m = moved(x) | moved(y)
    ax, ay = np.frombuffer(x, np.uint8), np.frombuffer(y, np.uint8)
    return bool(np.all((ax == ay) | m))


def sim(x, y):
    if not x or not y:
        return 0.0
    if len(x) == len(y):
        p = float(np.mean(np.frombuffer(x, np.uint8) == np.frombuffer(y, np.uint8)))
        if p >= 0.9:
            return p
    else:
        p = 0.0
    # an inserted instruction misaligns every byte after it; the CAP makes big bodies equal-length too
    return max(p, difflib.SequenceMatcher(None, x, y, autojunk=False).ratio())


class Matcher:
    def __init__(self, old, new, hide=()):
        self.a, self.b = Side(Image(old) if isinstance(old, str) else old), Side(Image(new) if isinstance(new, str) else new)
        hide = set(hide)
        ra = collections.defaultdict(set)
        for n, r in self.a.img.names.items():
            if n in self.b.img.names and n not in hide:
                ra[r].add(self.b.img.names[n])
        # an old address whose names land in more than one new place (ICF folding undone) anchors nothing
        pairs = sorted((r, next(iter(v))) for r, v in ra.items() if len(v) == 1)
        self.anc_a = [p[0] for p in pairs]
        self.anc_b = [p[1] for p in pairs]
        self.byname = dict(pairs)
        self.claimed = set(self.anc_b)
        self.base_a = self.a.img.base if hasattr(self.a.img, 'base') else 0x140000000
        self.base_b = self.b.img.base if hasattr(self.b.img, 'base') else 0x140000000

    # ---- code ----
    def func(self, r, deep=True):
        """Body similarity first; if that is not confident and `deep`, the callers vote."""
        got = self._body_match(r)
        if not deep or got[1] not in ('weak', 'none'):
            return got
        v = self.xref(r, 0)
        return v if v[1] == 'xref' else got

    def _body_match(self, r):
        if r in self.byname:
            return self.byname[r], 'name', 1.0
        i = bisect.bisect_left(self.anc_a, r)
        if i == 0 or i == len(self.anc_a):
            return self._global(r)
        lo_a, hi_a, lo_b, hi_b = self.anc_a[i - 1], self.anc_a[i], self.anc_b[i - 1], self.anc_b[i]
        mine = self.a.body(r)
        dlo, dhi = lo_b - lo_a, hi_b - hi_a
        if dlo == dhi:
            s = sim(mine, self.b.body(r + dlo))
            if s >= DELTA_SIM:
                return r + dlo, 'delta', s
        if lo_b >= hi_b:
            return self._global(r)
        scored = self.b.rank(lo_b + 1, hi_b - 1, mine, self.a.hist(r), r + dlo)
        if not scored:
            return None, 'none', 0.0
        best, second = scored[0][0], scored[1][0] if len(scored) > 1 else 0.0
        how = 'interval' if best >= INTERVAL_SIM and best - second >= INTERVAL_LEAD else 'weak'
        # a match must be mutual, or near-twin bodies (template thunks, lambda impls) hand each other's
        # addresses out with a confident score
        c = scored[0][2]
        back = self.a.rank(lo_a + 1, hi_a - 1, self.b.body(c), self.b.hist(c), c - dlo)
        if how == 'interval' and (not back or back[0][2] != r):
            how = 'weak'
        return c, how, best

    def _global(self, r):
        """No bracket (past the first or last anchor, or anchors out of order): whole image, never
        confident on its own -- a human confirms."""
        scored = self.b.rank(0, 1 << 40, self.a.body(r), self.a.hist(r), r)
        if not scored:
            return None, 'none', 0.0
        return scored[0][2], 'weak', scored[0][0]

    # ---- vtables ----
    def _col(self, img, base, vt):
        q = img.read(vt - 8, 8)
        if len(q) < 8:
            return None
        col = struct.unpack('<Q', q)[0] - base
        c = img.read(col, 24)
        if len(c) < 24:
            return None
        sig, off, cd, td, _, me = struct.unpack('<6I', c)
        if sig != 1 or me != col:
            return None
        name = img.read(td + 16, 256).split(b'\0', 1)[0]
        return name, off, cd

    def vtable(self, r):
        got = self._col(self.a.img, self.base_a, r)
        if not got:
            return None
        name, off, cd = got
        img, raw = self.b.img, self.b.img.raw
        hits = []
        p = raw.find(b'\0' + name + b'\0')
        while p >= 0:
            td = self._rva(img, p + 1) - 16
            for col in self._find_u32(img, td, 12):
                c = img.read(col, 24)
                sig, o, d, _, _, me = struct.unpack('<6I', c)
                if sig == 1 and me == col and o == off and d == cd:
                    for q in self._find_u64(img, self.base_b + col):
                        hits.append(q + 8)
            p = raw.find(b'\0' + name + b'\0', p + 1)
        if len(hits) == 1:
            return hits[0], 'rtti', 1.0
        return (hits[0] if hits else None), ('weak' if hits else 'none'), 0.0

    @staticmethod
    def _rva(img, fileoff):
        for va, size, off in img.secs:
            if off <= fileoff < off + size:
                return va + fileoff - off
        return None

    def _find_u32(self, img, val, at):
        needle, out = struct.pack('<I', val), []
        p = img.raw.find(needle)
        while p >= 0:
            r = self._rva(img, p - at)
            if r is not None:
                out.append(r)
            p = img.raw.find(needle, p + 1)
        return out

    def _find_u64(self, img, val):
        needle, out = struct.pack('<Q', val), []
        p = img.raw.find(needle)
        while p >= 0:
            r = self._rva(img, p)
            if r is not None:
                out.append(r)
            p = img.raw.find(needle, p + 1)
        return out

    # ---- data ----
    def xrefs(self, side, lo, hi):
        """(site, k, target): a disp32 at `site` whose RIP-relative target falls in [lo, hi]; the
        instruction may carry k immediate bytes after the displacement."""
        t0, sites = side.index()
        a, b = np.searchsorted(t0, lo - 4), np.searchsorted(t0, hi, 'right')
        out = []
        for t, site in zip(t0[a:b].tolist(), sites[a:b].tolist()):
            for k in (0, 1, 2, 4):
                if lo <= t + k <= hi:
                    out.append((site, k, t + k))
        return out

    def xref(self, r, reach=DATA_REACH):
        """Vote by referrers: every xref to r (or up to `reach` below it, an array base) from a function
        matched confidently is re-read at the same instruction in the new build. For data this is the only
        route; for code it survives the target's own body being rewritten."""
        refs = []
        for site, k, t in self.xrefs(self.a, r - reach, r):
            fa = self.a.containing(site)
            if fa is not None and fa != r:
                refs.append((fa not in self.byname, t != r, site, k, t, fa))
        # referrers matched by name are free and certain: they vote first, and a popular global (thousands
        # of referrers) stops as soon as agreement is plain
        refs.sort()
        exact = any(not off for _, off, *_ in refs)
        if exact:
            refs = [x for x in refs if not x[1]]   # base+offset reach is for when nothing names r itself
        code = self.a.img.is_code(r)
        claimed = self.claimed if code else ()
        votes, voters, certain, tried = collections.Counter(), collections.defaultdict(set), set(), 0
        for _, _, site, k, t, fa in refs:
            if tried >= MAX_REFERRERS or (votes and votes.most_common(1)[0][1] >= 6
                                          and votes.most_common(1)[0][1] == sum(votes.values())):
                break
            tried += 1
            fb, how, s = self.func(fa, deep=False)
            if fb is None or how in ('weak', 'none'):
                continue
            ib = self._site(fa, fb, site - fa)
            if ib is None:
                continue
            dnew = struct.unpack('<i', self.b.img.read(fb + ib, 4))[0]
            got = fb + ib + 4 + k + dnew + (r - t)
            if got in claimed:
                continue   # the mapping is one-to-one: an address another old function owns by name is not r's
            votes[got] += 1
            voters[got].add(fa)
            if how == 'name' and same(self.a.raw(fa), self.b.raw(fb)):
                certain.add(got)
        if not votes:
            return None, 'none', 0.0
        (best, n), = votes.most_common(1)
        share = n / sum(votes.values())
        # independent agreement: two sites in one referrer are one opinion -- unless that referrer is
        # matched by name and byte-identical, where the site maps exactly and the address is just re-read
        sure = exact and share >= XREF_SHARE and (len(voters[best]) >= 2 or best in certain)
        if sure and code:
            sure = best in set(self.b.starts[bisect.bisect_left(self.b.starts, best):][:1])
        return best, ('xref' if sure else 'weak'), share

    def _site(self, fa, fb, i):
        """Where the displacement at old offset i of fa sits in fb. An unchanged referrer maps it exactly; a
        changed one needs the normalized bytes before it found again, close to where they were."""
        xa, xb = self.a.raw(fa), self.b.raw(fb)
        if same(xa, xb):
            return i
        na, nb = norm(xa), norm(xb)
        ctx = na[max(0, i - SITE_CTX):i]
        if len(ctx) < SITE_CTX:
            return None
        cands, p = [], nb.find(ctx)
        while p >= 0:
            cands.append(p + len(ctx))
            p = nb.find(ctx, p + 1)
        reach = max(64, len(xa) // 4)
        cands = [c for c in cands if abs(c - i) <= reach and c + 4 <= len(xb)]
        return min(cands, key=lambda c: abs(c - i)) if len(cands) == 1 else None

    def match(self, r):
        if self.a.img.is_code(r):
            return self.func(r)
        return self.vtable(r) or self.xref(r)


def main(argv):
    m = Matcher(argv[1], argv[2])
    for s in argv[3:]:
        r = int(s, 16)
        nr, how, score = m.match(r)
        print(f'{r:#x}\t{nr:#x}\t{how}\t{score:.2f}' if nr is not None else f'{r:#x}\t-\t{how}\t{score:.2f}')


if __name__ == '__main__':
    main(sys.argv)
