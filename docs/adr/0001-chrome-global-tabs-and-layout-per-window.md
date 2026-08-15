# Chrome is global; only tabs and layout are per-window

Multi-window chat keeps appearance ("chrome": font size, colours, animation,
timestamp style) **global** — shared by every window — while making each window own
its tabs, rect, render toggles, input side, lock, and send channel. We picked this
because the render path already drives appearance through process-wide statics (one
glyph atlas, one palette in `WindowRenderer`/`TextRenderer`); making font/colours
per-window would mean N atlases and N palettes and a real refactor of the draw path,
for little user value. Consequence: the **Clone** action copies only look-and-behaviour
(size, toggles, input side, lock), never chrome (already shared) and never tabs.
