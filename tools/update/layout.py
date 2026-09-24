"""Which EngineLayout rows survive a client update untouched, and which need a human with Ghidra.

A row's value is evidence left in machine code: the field displacement inside the functions that read or
write it. So a row CARRIES when every one of its canary functions has the same body in both builds --
relink-moved displacements masked (rvamatch.norm), field displacements left in. The canaries:

  * every member function of the row's class -- the constructor alone catches an inserted field, since it
    touches the fields after it at new displacements;
  * every function the row's own XML doc names as its anchor (`Class::Method`, or a bare `<c>Name</c>`
    that resolves to exactly one function) -- that is how plain structs with no methods get a canary;
  * for a `vtbl.*` row, the class's vtable itself, compared slot by slot by the name each slot points at.

Linux only: the unstripped ELF names every function. A Windows value is carried when its Linux row is
and, for classes MSVC exports, those exported bodies are unchanged too.
"""
import collections, re, struct
from rvamatch import same

#: How far a moved field is looked for around its old displacement.
NEAR = 0x40

DECL = re.compile(r'public static readonly int (\w+) =\s*Field\(\s*"([^"]+)",\s*"([^"]+)"')
QUAL = re.compile(r'\b([A-Za-z_]\w*)::(~?[A-Za-z_]\w*)')
BARE = re.compile(r'<c>([A-Za-z_]\w*)</c>')
INHERIT = re.compile(r'<inheritdoc cref="(\w+)"')


def rows(src):
    """(row name, class, field, doc) per EngineLayout declaration. A row's doc is its own `///` lines plus the
    `//` section comment above its group (up to the previous blank line) -- that is where a group anchored
    on one function says so once. inheritdoc is resolved."""
    out, section, doc, last, lines, i = [], [], [], '', src.split('\n'), 0
    while i < len(lines):
        t = lines[i].strip()
        if not t:
            section, doc, last = [], [], ''
        elif t.startswith('///'):
            doc.append(t); last = '///'
        elif t.startswith('//'):
            section = section + [t] if last == '//' else [t]
            last = '//'
        elif t.startswith('public static readonly int'):
            j = i
            while ';' not in lines[j] and j + 1 < len(lines):
                j += 1
            m = DECL.search(' '.join(l.strip() for l in lines[i:j + 1]))
            if m:
                out.append((m[1], m[2], m[3], '\n'.join(doc + section)))
            doc, last, i = [], 'row', j
        else:
            doc, last = [], ''
        i += 1
    docs = {r[0]: r[3] for r in out}
    res = []
    for name, cls, field, doc in out:
        seen = set()
        while (m := INHERIT.search(doc)) and m[1] in docs and m[1] not in seen:
            seen.add(m[1])
            doc = doc.replace(m[0], '') + docs[m[1]]
        res.append((name, cls, field, doc))
    return res


def itanium(sym):
    """('Class', 'method') for `_ZN[K]...<len>Class<len|C1|D1..>...E`, None for anything templated or
    free. Enough to group members by class; not a demangler."""
    if not sym.startswith('_ZN'):
        return None
    p, parts = 3, []
    while p < len(sym) and sym[p] in 'rVK':
        p += 1
    while p < len(sym):
        c = sym[p]
        if c.isdigit():
            q = p
            while sym[q].isdigit():
                q += 1
            n = int(sym[p:q])
            parts.append(sym[q:q + n])
            p = q + n
        elif sym[p:p + 2] in ('C1', 'C2', 'C3', 'D0', 'D1', 'D2'):
            parts.append(sym[p:p + 2])
            p += 2
        elif c == 'E':
            break
        else:
            return None
    return (parts[-2], parts[-1]) if len(parts) >= 2 else None


def free(sym):
    m = re.match(r'_Z(\d+)', sym)
    return sym[len(m[0]):len(m[0]) + int(m[1])] if m else None


def msvc(sym):
    """('Class', 'method') for an MSVC member export `?m@C@@...`, `??0C@@` ctor, `??1C@@` dtor."""
    m = re.match(r'\?\?([01])(\w+)@@', sym)
    if m:
        return m[2], 'C1' if m[1] == '0' else 'D1'
    m = re.match(r'\?(\w+)@(\w+)@@', sym)
    return (m[2], m[1]) if m else None


class Index:
    """One image's functions grouped by class and by name; bodies normalized on demand."""

    def __init__(self, img, parse):
        self.img, self.members, self.byname = img, {}, {}
        for sym, rva in img.names.items():
            cm = parse(sym)
            if cm:
                self.members.setdefault(cm[0], set()).add(sym)
                self.byname.setdefault(f'{cm[0]}::{cm[1]}', set()).add(sym)
                self.byname.setdefault(cm[1], set()).add(sym)
            elif parse is itanium and (f := free(sym)):
                self.byname.setdefault(f, set()).add(sym)

    def body(self, sym):
        """The raw bytes as linked; compare two through `rvamatch.same`."""
        rva = self.img.names.get(sym)
        size = self.img.funcs.get(rva, 0) if rva is not None else 0
        return self.img.read(rva, size) if size else None


def unchanged(a, b, sym):
    x, y = a.body(sym), b.body(sym)
    return x is not None and y is not None and same(x, y)


def vtable(img, cls):
    """Linux vtable of `cls` as the symbol name each slot points at, or None. Slots are relocations
    (the ELF is PIE), so the addend is the target."""
    addr = img.data_names.get(f'_ZTV{len(cls)}{cls}')
    if addr is None:
        return None
    rev = {}
    for s, r in img.names.items():
        rev.setdefault(r, s)
    out, off = [], addr + 16                        # offset-to-top, typeinfo, then slot 0
    while off in img.relocs:                        # the next vtable's offset-to-top is a plain 0
        t = img.relocs[off]
        out.append(t if isinstance(t, str) else rev.get(t, hex(t)))
        off += 8
    return out


#: Opcodes (last byte) of the loads/stores/compares/arithmetic that take a memory operand: a ModRM is
#: only read after one of these, which is what keeps a decoder-less scan from counting every byte.
MEMOPS = frozenset(b'\x8b\x89\x8d\x88\x8a\xc7\xc6\xb6\xb7\xbe\xbf\x63\x3b\x39\x3a\x38\x83\x81\x80'
                   b'\xf6\xf7\x85\x84\x03\x01\x2b\x29\x33\x31\x0b\x09\x23\x21\xff\xfe\xd9\xdd'
                   b'\x10\x11\x28\x29\x2e\x2f\x58\x59\x5c\x5e\x54\x57\x6e\x7e\xd6\x5a\x2a\x2c\x6f\x7f')


def disps(body, base=None):
    """Counter of every value addressed as register+displacement (ModRM mod 01/10, SIB included) after a
    memory opcode; `base` keeps only that base register (7 = rdi, `this` on SysV)."""
    out = collections.Counter()
    n = len(body)
    for i in range(1, n - 1):
        m = body[i]
        mod, rm = m >> 6, m & 7
        if mod == 0 or mod == 3 or body[i - 1] not in MEMOPS or (base is not None and rm != base):
            continue
        j = i + 1 + (rm == 4)
        if mod == 1 and j < n:
            out[struct.unpack_from('<b', body, j)[0]] += 1
        elif mod == 2 and j + 4 <= n:
            out[struct.unpack_from('<i', body, j)[0]] += 1
    return out


def anchors(row, lin):
    """The functions the row's doc names -- where its value was read off, so where it must still appear."""
    name, cls, field, doc = row
    syms = set()
    for c, m in QUAL.findall(doc):
        syms |= lin.byname.get(f'{c}::{m}', set())
    for b in BARE.findall(doc):
        hit = lin.byname.get(b, set())
        if len(hit) == 1:
            syms |= hit
    return syms


def survives(a, b, syms, v):
    """(verdict, notes) from displacement counts of v in the anchors: 'likely' when every anchor that
    addressed v still does so as often, 'changed' (with nearby values that gained what v lost) otherwise."""
    seen, lost, gained = 0, [], collections.Counter()
    for s in sorted(syms):
        x, y = a.body(s), b.body(s)
        if x is None or y is None:
            continue
        cx, cy = disps(x), disps(y)
        if not cx[v]:
            continue
        seen += 1
        if cy[v] != cx[v]:
            lost.append(f'{s}: +{v:#x} addressed {cx[v]}x -> {cy[v]}x')
            for d in cy:
                if d != v and abs(d - v) <= NEAR and cy[d] > cx[d]:
                    gained[d] += cy[d] - cx[d]
    if not seen:
        return None, []
    if not lost:
        return 'likely', [f'+{v:#x} addressed as often as before in {seen} anchor(s)']
    guess = ', '.join(f'{d:#x}' for d, _ in gained.most_common(3))
    return 'changed', lost[:2] + ([f'candidates: {guess}'] if guess else [])


def canaries(row, lin):
    """The functions (Linux symbols) whose bodies vouch for this row."""
    name, cls, field, doc = row
    syms = set(lin.members.get(cls, ()))
    for c, m in QUAL.findall(doc):
        syms |= lin.byname.get(f'{c}::{m}', set())
    for b in BARE.findall(doc):
        hit = lin.byname.get(b, set())
        if len(hit) == 1:
            syms |= hit
    return syms


def check(src, values, old_lin, new_lin, old_win=None, new_win=None):
    """Per row: (row, verdict, notes); `values` is the old build file's {(cls, field): (linux, windows)}. carried = canaries byte-identical; likely = canaries changed but the
    row's displacement is addressed as often as before in its anchors; changed / blind = a human's."""
    a, b = Index(old_lin, itanium), Index(new_lin, itanium)
    wa = Index(old_win, msvc) if old_win else None
    wb = Index(new_win, msvc) if new_win else None
    vt_cache = {}
    out = []
    for row in rows(src):
        name, cls, field, doc = row
        lin_v, win_v = values.get((cls, field), (None, None))
        if cls == 'SelfCheck':
            continue
        if cls == '(abi)':
            out.append((row, 'carried', ['compiler ABI constant, not engine layout']))
            continue
        notes = []
        if field.startswith('vtbl.'):
            if cls not in vt_cache:
                vt_cache[cls] = (vtable(old_lin, cls), vtable(new_lin, cls))
            va, vb = vt_cache[cls]
            if va is not None and va != vb:
                notes.append(f'vtable {cls} changed ({len(va)} -> {len(vb or [])} slots)')
        syms = canaries(row, a)
        if not syms and (not field.startswith('vtbl.') or vt_cache[cls][0] is None):
            out.append((row, 'blind', ['no member functions and no anchor named in its doc']))
            continue
        moved = sorted(s for s in syms if not unchanged(a, b, s))
        if moved and not notes and not field.startswith('vtbl.'):
            tier, why = (survives(a, b, anchors(row, a) or set(moved), lin_v) if lin_v is not None
                         else (None, []))
            if tier:
                out.append((row, tier, why + [f'{len(moved)} canary bodies changed']))
                continue
        notes += [f'body changed: {s}' for s in moved]
        if wa and wb and win_v is not None:
            for s in sorted(wa.members.get(cls, ())):
                if wa.body(s) is not None and not unchanged(wa, wb, s):
                    notes.append(f'windows body changed: {s}')
        out.append((row, 'changed' if notes else 'carried', notes))
    return out
