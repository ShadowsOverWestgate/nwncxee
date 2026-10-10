#!/usr/bin/env python3
"""Print a class's vtable, slot by slot, from BOTH client binaries -- no Ghidra.

Both builds carry the symbol table a vtable needs to be read by name: the Linux ELF
exports `_ZTV<class>` and every virtual it points at, and nwmain.exe name-exports its
virtuals (though not the vftable itself, which is found by scanning .rdata for a run of
pointers into that class's virtuals). That makes a vtable slot a MEASUREMENT rather than
a guess -- which is what EngineLayout rows for `vtbl.*` need (#433, docs/engine-abi.md).

    tools/abi/vtable-slots.py Part                  # both platforms, paired
    tools/abi/vtable-slots.py CAurTexture --n 30

Env: NWN_BIN / NWN_WIN_EXE (tools/paths.py) name the binaries.
"""
import argparse, os, pathlib, re, struct, subprocess, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import paths  # noqa: E402  tools/paths.py: env, local.env, then the Steam library
LINUX = paths.require("NWN_BIN")
WIN = paths.require("NWN_WIN_EXE")


def undname(names):
    if not names:
        return {}
    p = subprocess.run(["llvm-undname"] + list(names), capture_output=True, text=True)
    ls = [l for l in p.stdout.splitlines() if l.strip()]
    return dict(zip(ls[0::2], ls[1::2]))


# ---------------- Linux (ELF) ----------------

def linux_vtable(cls, n):
    d = pathlib.Path(LINUX).read_bytes()
    shoff = struct.unpack_from("<Q", d, 0x28)[0]
    shent, shnum = struct.unpack_from("<HH", d, 0x3A)
    secs = []
    for i in range(shnum):
        b = shoff + i * shent
        addr, off, size = (struct.unpack_from("<Q", d, b + o)[0] for o in (16, 24, 32))
        secs.append((addr, size, off))

    def foff(a):
        for addr, size, off in secs:
            if addr and addr <= a < addr + size:
                return off + (a - addr)

    named, raw = {}, {}
    for flag, sink in (("-C", named), ("", raw)):
        cmd = ["nm", "--defined-only"] + ([flag] if flag else []) + [LINUX]
        for l in subprocess.run(cmd, capture_output=True, text=True).stdout.splitlines():
            p = l.split(" ", 2)
            if len(p) == 3:
                (sink.setdefault(int(p[0], 16), p[2]) if flag else sink.setdefault(p[2], int(p[0], 16)))
    sym = f"_ZTV{len(cls)}{cls}"
    if sym not in raw:
        return None, f"no {sym} in the ELF"
    # +0x10 skips Itanium's offset-to-top + typeinfo prologue: slot 0 is the first entry
    # the vptr addresses, which is where the two destructor slots (D1, D0) live.
    o = foff(raw[sym] + 0x10)
    return [(i * 8, named.get(struct.unpack_from("<Q", d, o + 8 * i)[0])) for i in range(n)], None


# ---------------- Windows (PE) ----------------

def win_vtable(cls, n):
    d = pathlib.Path(WIN).read_bytes()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    imgbase = struct.unpack_from("<Q", d, opt + 24)[0]
    exp_rva = struct.unpack_from("<I", d, opt + 112)[0]
    secs = []
    for i in range(nsec):
        b = opt + optsz + i * 40
        name = d[b:b + 8].rstrip(b"\0").decode()
        vsz, va, rsz, ro = struct.unpack_from("<IIII", d, b + 8)
        secs.append((va, vsz, ro, rsz, name))

    def off(rva):
        for va, vsz, ro, rsz, _ in secs:
            if va <= rva < va + max(vsz, rsz):
                return ro + (rva - va)

    e = off(exp_rva)
    nnam = struct.unpack_from("<I", d, e + 24)[0]
    afun, anam, aord = struct.unpack_from("<III", d, e + 28)
    fo, no, oo = off(afun), off(anam), off(aord)
    rva2name = {}
    for i in range(nnam):
        o = off(struct.unpack_from("<I", d, no + 4 * i)[0])
        nm = d[o:d.index(b"\0", o)].decode("utf-8", "replace")
        fr = struct.unpack_from("<I", d, fo + 4 * struct.unpack_from("<H", d, oo + 2 * i)[0])[0]
        rva2name.setdefault(fr, nm)

    rd = next(s for s in secs if s[4] == ".rdata")

    def rva_at(fileoff):
        for va, vsz, ro, rsz, _ in secs:
            if ro <= fileoff < ro + rsz:
                return va + (fileoff - ro)

    def by_rtti():
        """MSVC keeps a class's name in its RTTI whether or not anything is exported, so this reaches
        the client-GUI classes the export scan cannot: type name -> TypeDescriptor -> complete object
        locator -> the word that stores it, and the vftable starts 8 bytes after."""
        i = d.find((".?AV%s@@" % cls).encode())
        if i < 0:
            return None
        td = rva_at(i - 16)                                   # vfptr + spare precede the name
        pat, start = struct.pack("<I", td), 0
        while True:
            j = d.find(pat, start)
            if j < 0:
                return None
            start = j + 1
            col = j - 12                                      # pTypeDescriptor is the COL's 4th dword
            if col < 0 or struct.unpack_from("<I", d, col)[0] != 1:
                continue                                      # signature 1 = 64-bit COL
            p8, k = struct.pack("<Q", imgbase + rva_at(col)), 0
            k = d.find(p8)
            if k >= 0:
                return rva_at(k) + 8

    # a virtual's mangled name carries its access+virtual code: [EMU]E[AB]A
    own = {r for r, nm in rva2name.items() if re.match(rf"^\?\w+@{re.escape(cls)}@@[EMU]E[AB]A", nm)}
    if not own:
        v = by_rtti()
        if v is None:
            return None, f"{cls}: no exported virtuals and no RTTI type name in the PE"
        o = off(v)
        rows = [(i * 8, rva2name.get(struct.unpack_from("<Q", d, o + 8 * i)[0] - imgbase)) for i in range(n)]
        t = undname([nm for _, nm in rows if nm])
        return [(x, t.get(nm, nm)) for x, nm in rows], f"vftable rva 0x{v:x}, found by RTTI (names unavailable)"
    text = next(s for s in secs if s[4] == ".text")
    words = struct.unpack_from("<%dQ" % (rd[1] // 8), d, rd[2])
    rvas = [(w - imgbase) if w > imgbase else 0 for w in words]
    code = lambda r: text[0] <= r < text[0] + text[1]
    hit = next((i for i, r in enumerate(rvas) if r in own), None)
    if hit is None:
        return None, f"{cls} virtuals are exported but no vftable references them"
    s = hit
    while s > 0 and code(rvas[s - 1]):
        s -= 1
    rows = [(i * 8, rva2name.get(rvas[s + i])) for i in range(n)]
    t = undname([nm for _, nm in rows if nm])
    return [(o, t.get(nm, nm)) for o, nm in rows], f"vftable rva 0x{rd[0] + s * 8:x}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cls")
    ap.add_argument("--n", type=int, default=32)
    a = ap.parse_args()
    for label, fn in (("linux", linux_vtable), ("windows", win_vtable)):
        rows, note = fn(a.cls, a.n)
        print(f"=== {a.cls} / {label}" + (f"  ({note})" if note else ""))
        if rows is None:
            print(f"  -- {note}")
            continue
        for o, nm in rows:
            print(f"  +0x{o:03x}  {nm or '(not exported)'}")


if __name__ == "__main__":
    main()
