#!/usr/bin/env python3
"""Read appearance.2da (stock and content) and print the rows a player character can be.

    ./app2da.py            # both files, PC rows
    ./app2da.py --all      # every row that has WALKDIST/RUNDIST
"""
import os, sys

STOCK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "extract", "appearance.2da")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import paths  # noqa: E402  tools/paths.py: env, local.env, then the Steam library
CONTENT = paths.require("CONTENT_APPEARANCE_2DA")


def read2da(path):
    with open(path, "r", encoding="latin1") as f:
        lines = [l.rstrip("\n") for l in f]
    # line 0 = "2DA V2.0", line 1 blank (or default), line 2 = header
    hi = next(i for i, l in enumerate(lines) if l.strip() and i >= 2)
    hdr = lines[hi].split()
    rows = {}
    for l in lines[hi + 1:]:
        t = tokenize(l)
        if not t:
            continue
        rows[t[0]] = dict(zip(hdr, t[1:]))
    return hdr, rows


def tokenize(line):
    """2DA tokens: whitespace separated, "..." quoted."""
    out, i, n = [], 0, len(line)
    while i < n:
        while i < n and line[i] in " \t":
            i += 1
        if i >= n:
            break
        if line[i] == '"':
            j = line.find('"', i + 1)
            out.append(line[i + 1:j if j > 0 else n])
            i = (j + 1) if j > 0 else n
        else:
            j = i
            while j < n and line[j] not in " \t":
                j += 1
            out.append(line[i:j])
            i = j
    return out


PC_LABELS = ["Dwarf", "Elf", "Gnome", "Halfling", "Half_Elf", "Half_Orc", "Human"]

COLS = ["LABEL", "NAME", "RACE", "MODELTYPE", "MOVERATE", "WALKDIST", "RUNDIST", "HEIGHT",
        "PERSPACE", "SIZECATEGORY", "FOOTSTEPTYPE"]

if __name__ == "__main__":
    allrows = "--all" in sys.argv
    for tag, path in (("stock", STOCK), ("content", CONTENT)):
        if not os.path.exists(path):
            print(f"# {tag}: missing {path}")
            continue
        hdr, rows = read2da(path)
        print(f"# {tag}: {path} ({len(rows)} rows)")
        print("\t".join(["ROW"] + COLS))
        for rid, r in rows.items():
            lab = r.get("LABEL", "")
            if not allrows and lab not in PC_LABELS:
                continue
            print("\t".join([rid] + [r.get(c, "") for c in COLS]))
        print()
