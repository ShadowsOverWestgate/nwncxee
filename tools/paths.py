#!/usr/bin/env python3
"""Where this machine keeps the things the tools need, so no script carries one maintainer's paths.

A key resolves from, in order: the environment, `local.env` at the repo root (gitignored; copy
`local.env.example`), then a default that holds on a stock setup (the Steam library, a sibling checkout).
CLI: `python3 tools/paths.py NWN_DIR` prints the path, or exits 1 naming the key to set.
"""
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _local():
    f = ROOT / "local.env"
    out = {}
    if f.is_file():
        for line in f.read_text().splitlines():
            m = re.match(r"\s*(?:export\s+)?([A-Z_][A-Z0-9_]*)\s*=\s*(.*?)\s*$", line)
            if m and not line.lstrip().startswith("#"):
                v = os.path.expanduser(m[2].strip("\"'"))
                out[m[1]] = os.path.normpath(v if os.path.isabs(v) else str(ROOT / v))
    return out


def _steam_nwn():
    """The game in any Steam library: libraryfolders.vdf lists each library's root."""
    for vdf in ("~/.local/share/Steam/steamapps/libraryfolders.vdf", "~/.steam/steam/steamapps/libraryfolders.vdf"):
        p = Path(os.path.expanduser(vdf))
        if not p.is_file():
            continue
        for lib in re.findall(r'"path"\s+"([^"]+)"', p.read_text(errors="replace")):
            game = Path(lib) / "steamapps/common/Neverwinter Nights"
            if (game / "data/nwn_base.key").is_file():
                return str(game)
    return None


def _defaults(key, get):
    sib = ROOT.parent
    if key == "NWN_DIR":
        return _steam_nwn()
    if key == "NWN_BIN":
        d = get("NWN_DIR")
        return d and f"{d}/bin/linux-x86/nwmain-linux"
    if key == "NWN_WIN_EXE":
        d = get("NWN_DIR")
        return d and f"{d}/bin/win32/nwmain.exe"
    if key == "GHIDRA_PROJECT":
        return os.path.expanduser("~/ghidra")
    if key == "NWN_DECOMP":
        return os.path.expanduser("~/nwn-decomp")
    if key == "RELEASE_SIGN_KEY":
        return os.path.expanduser("~/.ssh/cx-release")
    if key == "NEVERBLENDER":
        return str(sib / "neverblender/NeverBlender")
    return None


def get(key):
    """The path for `key`, or None when nothing names one."""
    return os.environ.get(key) or _local().get(key) or _defaults(key, get)


def require(key):
    """The path for `key`, or exit naming the key and where to set it."""
    v = get(key)
    if not v:
        sys.exit(f"!! {key} is not set: export it, or put {key}=<path> in {ROOT / 'local.env'} (see local.env.example)")
    return v


if __name__ == "__main__":
    print(require(sys.argv[1]))
