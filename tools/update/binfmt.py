"""Just enough PE/ELF reading to list a binary's functions: (rva, size) plus the names it exports.

PE: every non-leaf x64 function has a RUNTIME_FUNCTION in .pdata, so function bounds need no
disassembler. ELF: the symtab (nwmain-linux/nwserver-linux are unstripped). Stdlib only.
"""
import struct


class Image:
    """A loaded image: `read(rva, n)`, `funcs` {rva: size}, `names` {name: rva}, `data_names` {name: rva};
    ELF adds `relocs` {rva: addend}, PE adds `stamp`; `cv` is the build key (build-id / CodeView)."""

    def __init__(self, path):
        self.path = path
        self.raw = open(path, 'rb').read()
        self.funcs, self.names, self.data_names, self.secs = {}, {}, {}, []
        if self.raw[:2] == b'MZ':
            self._pe()
        elif self.raw[:4] == b'\x7fELF':
            self._elf()
        else:
            raise ValueError(f'{path}: neither PE nor ELF')

    def read(self, rva, n):
        for va, size, off in self.secs:
            if va <= rva < va + size:
                return self.raw[off + rva - va: off + min(rva - va + n, size)]
        return b''

    def is_code(self, rva):
        return any(va <= rva < va + size for va, size, _ in self.code)

    # ---- PE ----
    def _pe(self):
        d = self.raw
        lf = struct.unpack_from('<I', d, 0x3c)[0]
        nsec, = struct.unpack_from('<H', d, lf + 6)
        optsz, = struct.unpack_from('<H', d, lf + 20)
        opt = lf + 24
        self.stamp, = struct.unpack_from('<I', d, lf + 8)
        dirs = [struct.unpack_from('<II', d, opt + 112 + 8 * i) for i in range(16)]
        sh = opt + optsz
        self.code = []
        for i in range(nsec):
            name, vsize, va, rawsz, rawoff = struct.unpack_from('<8sIIII', d, sh + 40 * i)
            chars, = struct.unpack_from('<I', d, sh + 40 * i + 36)
            self.secs.append((va, min(vsize, rawsz) if rawsz else 0, rawoff))
            if chars & 0x20:                                   # IMAGE_SCN_CNT_CODE
                self.code.append((va, vsize, rawoff))
        prva, psz = dirs[3]                                    # exception directory = .pdata
        pd = self.read(prva, psz)
        for i in range(0, len(pd) - 11, 12):
            b, e, _ = struct.unpack_from('<III', pd, i)
            # chained unwind entries repeat a function's tail; keep the first (the entry point)
            if b and e > b and b not in self.funcs:
                self.funcs[b] = e - b
        erva, esz = dirs[0]
        if erva:
            ed = self.read(erva, 40)
            _, _, _, _, _, _, nfun, nname, afun, aname, aord = struct.unpack_from('<IIHHIIIIIII', ed)
            fns = struct.unpack('<%dI' % nfun, self.read(afun, 4 * nfun))
            nms = struct.unpack('<%dI' % nname, self.read(aname, 4 * nname))
            ords = struct.unpack('<%dH' % nname, self.read(aord, 2 * nname))
            for nrva, o in zip(nms, ords):
                nm = self.read(nrva, 512).split(b'\0', 1)[0].decode('latin1')
                (self.names if self.is_code(fns[o]) else self.data_names)[nm] = fns[o]
        self.cv = self._codeview(dirs[6])

    def _codeview(self, dbg):
        rva, sz = dbg
        for i in range(0, sz, 28):
            e = self.read(rva + i, 28)
            if len(e) < 28 or struct.unpack_from('<I', e, 12)[0] != 2:
                continue
            cv = self.read(struct.unpack_from('<I', e, 20)[0], 24)
            if cv[:4] != b'RSDS':
                continue
            a, b, c = struct.unpack_from('<IHH', cv, 4)
            return f'{a:08X}{b:04X}{c:04X}' + cv[12:20].hex().upper() + str(struct.unpack_from('<I', cv, 20)[0])
        return None

    # ---- ELF ----
    def _elf(self):
        d = self.raw
        shoff, = struct.unpack_from('<Q', d, 0x28)
        shentsize, shnum, shstrndx = struct.unpack_from('<HHH', d, 0x3a)
        shs = [struct.unpack_from('<IIQQQQIIQQ', d, shoff + i * shentsize) for i in range(shnum)]
        self.code = []
        for s in shs:
            _, typ, flags, addr, off, size = s[:6]
            if flags & 2 and typ != 8:                         # SHF_ALLOC, not NOBITS
                self.secs.append((addr, size, off))
            if flags & 4:                                      # SHF_EXECINSTR
                self.code.append((addr, size, off))
        self.stamp = None
        self.cv = None
        for s in shs:
            if s[1] == 7:                                      # SHT_NOTE
                n = d[s[4]:s[4] + s[5]]
                if len(n) > 16 and struct.unpack_from('<I', n, 8)[0] == 3:
                    namesz, descsz = struct.unpack_from('<II', n)
                    p = 12 + ((namesz + 3) & ~3)
                    self.cv = n[p:p + descsz].hex()
        self.relocs = {}
        for s in shs:
            if s[1] != 4:                                      # SHT_RELA
                continue
            sym, strtab = shs[s[6]], shs[shs[s[6]][6]]
            for i in range(0, s[5], 24):
                off, info, add = struct.unpack_from('<QQq', d, s[4] + i)
                typ, si = info & 0xffffffff, info >> 32
                if typ == 8:                                   # R_X86_64_RELATIVE: the addend is the target
                    self.relocs[off] = add
                elif typ == 1 and si:                          # R_X86_64_64: an imported/pure-virtual slot
                    nm, = struct.unpack_from('<I', d, sym[4] + si * 24)
                    self.relocs[off] = d[strtab[4] + nm: d.index(b'\0', strtab[4] + nm)].decode('latin1')
        for s in shs:
            if s[1] not in (2, 11):                            # SHT_SYMTAB / SHT_DYNSYM
                continue
            strtab = shs[s[6]]
            for i in range(0, s[5], 24):
                nm, info, _, shndx, val, size = struct.unpack_from('<IBBHQQ', d, s[4] + i)
                if not val or shndx == 0:
                    continue
                name = d[strtab[4] + nm: d.index(b'\0', strtab[4] + nm)].decode('latin1')
                if info & 0xf == 2 and size:                   # STT_FUNC
                    self.funcs.setdefault(val, size)
                    self.names.setdefault(name, val)
                elif info & 0xf == 1:                          # STT_OBJECT
                    self.data_names.setdefault(name, val)
