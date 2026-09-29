"""The repo's version-bound tables as the tools see them: build files, SymbolMap's symbols, EngineLayout's
declarations. One reader of each format, shared by newbuild.py, layout.py and tools/abi/check-win-symbols.py.
"""
import glob, os, re

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
FW = os.path.join(ROOT, 'managed', 'Framework')


def hexval(s):
    return None if s == '-' else int(s, 16)


def read_build(path):
    """A build file: name/linux/windows/stamp, offsets {(cls, field): (linux, windows)}, rvas {mangled: rva},
    lvas {static name: linux nm address}, and the trailing `# comment` of each line kept for rewriting (row /
    constant names)."""
    b = dict(path=path, offsets={}, rvas={}, lvas={}, notes={})
    for raw in open(path):
        body, _, note = raw.partition('#')
        f = [x.strip() for x in body.split('\t') if x.strip()]
        if not f:
            continue
        if f[0] in ('name', 'linux', 'windows', 'stamp'):
            b[f[0]] = f[1]
        elif f[0] == 'offset':
            b['offsets'][(f[1], f[2])] = (hexval(f[3]), hexval(f[4]))
            b['notes'][(f[1], f[2])] = note.strip()
        elif f[0] == 'rva':
            b['rvas'][f[1]] = int(f[2], 16)
            b['notes'][f[1]] = note.strip()
        elif f[0] == 'lva':
            b['lvas'][f[1]] = int(f[2], 16)
    return b


def read_builds():
    return [read_build(p) for p in sorted(glob.glob(os.path.join(FW, 'Builds', '*.tsv')))]


def symbols():
    """(consts {Name: linux symbol}, win [(Name, 'Nm'|'At', msvc-or-None)]) from SymbolMap.cs. Every
    declarator counts: `public const string A = "a", B = "b";` declares two."""
    sm = open(os.path.join(FW, 'SymbolMap.cs')).read()
    consts = {}
    for stmt in re.findall(r'public const string\s+([^;]*);', sm):
        consts.update(re.findall(r'(\w+)\s*=\s*"([^"]+)"', stmt))
    win = [(c, k, v.strip('"') or None)
           for c, k, v in re.findall(r'\[(\w+)\]\s*=\s*(Nm|At)\(("[^"]*")?\)', sm)]
    return consts, win


def fmt(v):
    return '-' if v is None else f'{v:#x}'
