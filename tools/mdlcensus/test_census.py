#!/usr/bin/env python3
"""Smallest check that fails if the ASCII scanner or the binary path breaks."""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
import census

ASCII = """newmodel t
classification Tile
node dummy t
endnode
node trimesh box1
  bitmap plc_wood06_gray
  render 1
  shadow 0
  tilefade 2
  verts 3
endnode
newanim walk t
node trimesh box1
  render 0
endnode
"""
model, nodes = census.scan(ASCII)
assert model["classification"] == "Tile"
assert [n["type"] for n in nodes] == ["dummy", "trimesh"], nodes
assert nodes[1]["tilefade"] == "2" and nodes[1]["render"] == "1"
text, binary = census.to_ascii(ASCII.encode())
assert not binary and text == ASCII
for _, _, data in census.erf_entries(os.path.expanduser("~/.local/share/Neverwinter Nights/hak/sow_appr_01.hak")):
    text, binary = census.to_ascii(data)
    assert census.scan(text)[1], "first hak model produced no nodes"
    break
print("ok")
