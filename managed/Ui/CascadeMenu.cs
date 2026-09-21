using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>One row of a cascade level: a label, an optional engine-texture icon (resref, "" for none, or an owner-
/// minted <see cref="IconHandle"/> for an icon the owner resolves itself -- CX-693's composite item icons), a
/// uses-left count ("" for none), a submenu mark, and the owner's handle. The owner builds and owns the arrays.</summary>
public struct CascadeItem
{
    public string Label;
    public string Icon;
    /// <summary>Opaque to the widget: non-zero means the row has an icon the owner draws from this handle (reported
    /// back as <see cref="IconQuad.Handle"/>) instead of from <see cref="Icon"/>.</summary>
    public IntPtr IconHandle;
    public bool Disabled;
    public bool HasChildren;
    public string Count;
    public object? Tag;

    public CascadeItem(string label, string icon = "", bool disabled = false, bool hasChildren = false, string count = "", object? tag = null)
    { Label = label; Icon = icon; Disabled = disabled; HasChildren = hasChildren; Count = count; Tag = tag; }
}

public enum CascadeActionKind { None, Activate, RequestChildren, Context, CloseAll, Drop }

/// <summary>What an input did: nothing; a leaf picked (on the release of an undragged left press); a submenu row
/// wants its children (the owner materializes them and calls <see cref="CascadeMenu.OpenChild"/>); a right-button
/// press on a row; the menu closed itself; or a row was dragged and dropped (CX-694: <see cref="Level"/>/<see cref="Row"/>
/// the source, <see cref="ToLevel"/>/<see cref="ToIndex"/> the gap it landed in -- 0..n, before row ToIndex).</summary>
public readonly struct CascadeAction
{
    public readonly CascadeActionKind Kind;
    public readonly int Level, Row, ToLevel, ToIndex;
    private CascadeAction(CascadeActionKind kind, int level, int row, int toLevel = -1, int toIndex = -1)
    { Kind = kind; Level = level; Row = row; ToLevel = toLevel; ToIndex = toIndex; }
    public static readonly CascadeAction None = default;
    public static readonly CascadeAction CloseAll = new(CascadeActionKind.CloseAll, -1, -1);
    public static CascadeAction Activate(int level, int row) => new(CascadeActionKind.Activate, level, row);
    public static CascadeAction RequestChildren(int level, int row) => new(CascadeActionKind.RequestChildren, level, row);
    public static CascadeAction Context(int level, int row) => new(CascadeActionKind.Context, level, row);
    public static CascadeAction Drop(int level, int row, int toLevel, int toIndex) => new(CascadeActionKind.Drop, level, row, toLevel, toIndex);
}

/// <summary>A visible icon square for the owner's texture pass (the widget draws no textures itself).</summary>
public readonly struct IconQuad
{
    public readonly string Resref;
    /// <summary>The row's <see cref="CascadeItem.IconHandle"/>, carried through untouched; zero = draw <see cref="Resref"/>.</summary>
    public readonly IntPtr Handle;
    public readonly float X, Y, Size, Alpha;
    public IconQuad(string resref, float x, float y, float size, float alpha, IntPtr handle = default)
    { Resref = resref; Handle = handle; X = x; Y = y; Size = size; Alpha = alpha; }
}

/// <summary>The cascade's look for one draw (CX-694): the fills as RGBA, the text colour (RGB + alpha, as
/// <see cref="UiText.Draw"/> takes them), and the open animation (<see cref="AnimMs"/> 0 = none) against the
/// owner's clock.</summary>
public struct CascadeStyle
{
    public (float r, float g, float b, float a) Bg, Frame, Hover;
    public uint TextRgb;
    public float TextAlpha;
    /// <summary>Whole-menu multiplier on every alpha.</summary>
    public float Alpha;
    public int AnimMs;
    public long NowMs;

    /// <summary>A knob swatch (RRGGBBAA) as the fill tuple.</summary>
    public static (float r, float g, float b, float a) Rgba(uint c)
        => (((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);

    /// <summary>The widget's own defaults: the settings-panel palette, no animation.</summary>
    public static CascadeStyle Default => new()
    {
        Bg = Rgba(0x05060afa), Frame = Rgba(0x8c8c8cff), Hover = Rgba(0x38669eff), TextRgb = 0xd8dee9, TextAlpha = 1f, Alpha = 1f,
    };
}

/// <summary>One open list of the cascade: its rows, its viewport (<see cref="List"/> is the rect), the hovered
/// row, the parent row it hangs off (-1 at the root), and when it first drew (-1 until then).</summary>
public sealed class CascadeLevel
{
    public long OpenedMs = -1;
    public CascadeItem[] Items = Array.Empty<CascadeItem>();
    public readonly ScrollColumn List = new();
    public int Hover = -1;
    public int ParentRow = -1;
    /// <summary>Any row carries an icon, so the icon column is laid out.</summary>
    public bool HasIcons;
    /// <summary>Submenu mark codepoint: U+203A, or '&gt;' when the face lacks it.</summary>
    public int Mark = '>';
    /// <summary>Drill mode (<see cref="CascadeMenu.Drill"/>): the level carries a Back band above its rows, labelled
    /// with the parent row it came from; the band's height is the row height.</summary>
    public bool HasBack;
    public string BackLabel = "";
    public bool BackHover;
    /// <summary>A drag from another level may drop into this one (the host sets it on a level it owns the rows of,
    /// e.g. a folder list); the root always accepts outside drill mode.</summary>
    public bool DropTarget;
    public float X => List.X;
    public float Y => List.Y;
    public float W => List.W;
    public float H => List.H;
    /// <summary>Top of the whole level: the Back band's when it has one, else the rows'.</summary>
    public float Top(float itemH) => HasBack ? List.Y - itemH : List.Y;
    public bool InBack(float mx, float my, float itemH) => HasBack && mx >= List.X && mx < List.X + List.W && my >= List.Y - itemH && my < List.Y;
}

/// <summary>
/// A nested dropdown with an icon column (CX-690/CX-691): a root list opened at a point and a cascade of child
/// lists, each opened beside the parent row that owns it, flipped to the parent's left edge at the viewport's
/// right edge and clamped into the viewport. Children are lazy: an input that opens a submenu row yields
/// <see cref="CascadeActionKind.RequestChildren"/> and the owner answers with <see cref="OpenChild"/>. Pure:
/// pixels and keys in, actions out; icons are reported as rects (<see cref="Icons"/>) for the owner's texture
/// pass. The widget closes itself only on <see cref="CascadeActionKind.CloseAll"/>; after Activate/Context the
/// owner decides. A left press arms a row drag (CX-694): past <see cref="DragPx"/> the row follows the pointer
/// and a gap in its own level or the root is the drop target (<see cref="DropLevel"/>/<see cref="DropIndex"/>); the release yields
/// <see cref="CascadeActionKind.Drop"/>, or Activate for an undragged press released on the same leaf. Hover, the
/// dwell and level closes hold while dragging. Zero-alloc in <see cref="Draw"/> and <see cref="Tick"/>.
/// <para><see cref="Drill"/> mode: a child replaces its parent in place instead of opening beside it, under a Back
/// band that returns to the parent (its rows, scroll and hover kept); only the deepest level shows, takes input
/// and reports icons. Submenu rows open on the click's release, never on the dwell (the pointer would land on the
/// new list and descend again), and a drag drops in the row's own list only.</para>
/// </summary>
public sealed class CascadeMenu
{
    /// <summary>Rows a level shows at most before it scrolls.</summary>
    public const int MaxVisible = 12;
    /// <summary>Nesting ceiling; a deeper <see cref="OpenChild"/> is refused. ponytail: vanilla's radial is 3 deep;
    /// the level pool and the icon buffer size off this one const, so raising it is the whole upgrade.</summary>
    public const int MaxDepth = 8;
    /// <summary>Default hover dwell before a submenu row asks for its children.</summary>
    public const long DefaultDwellMs = 250;
    /// <summary>Hover dwell before a submenu row asks for its children; 0 = the first Tick after the hover.</summary>
    public long DwellMs = DefaultDwellMs;
    /// <summary>Alpha multiplier of a disabled row.</summary>
    public const float DisabledAlpha = 0.4f;
    /// <summary>Alpha multipliers of the row being dragged and of its ghost label at the pointer.</summary>
    public const float DragSourceAlpha = 0.5f, GhostAlpha = 0.85f;
    /// <summary>Half-thickness of the drop-gap line, in design px.</summary>
    private const float GapLinePx = 1f;
    private const int LeftButton = 1;
    /// <summary>How far a list slides as it fades in: the root from above, a child from its parent's edge.</summary>
    public static float SlidePx => 8f * Metrics.Scale;
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyKpEnter = 0x40000058;
    /// <summary>Back mark: U+2039, or '&lt;' when the face lacks it.</summary>
    private const int BackMark = 0x2039;
    private const int RightButton = 3;

    public static float Pad => 4f * Metrics.Scale;
    /// <summary>Pointer travel from a left press that turns it into a row drag.</summary>
    public static float DragPx => 4f * Metrics.Scale;
    /// <summary>The dragged row's ghost label sits this far right of the pointer.</summary>
    private static float GhostDx => 12f * Metrics.Scale;
    /// <summary>Widest level; longer labels clip.</summary>
    public static float MaxW => 360f * Metrics.Scale;
    /// <summary>Inset of the icon square inside its row-height cell.</summary>
    public static float IconInset => 2f * Metrics.Scale;
    /// <summary>Narrowest level, in row heights, so a list of one-letter labels still reads as a menu.</summary>
    private const float MinWRows = 4f;

    private readonly CascadeLevel[] _lv = new CascadeLevel[MaxDepth];
    private readonly IconQuad[] _icons = new IconQuad[MaxDepth * (MaxVisible + 1)];
    private int _iconN;
    private GlyphAtlas? _atlas;
    private float _vpW, _vpH, _mx, _my;
    private int _dwellLevel = -1, _dwellRow = -1;
    private long _dwellSince = -1;
    private bool _dwellDone;
    private int _pressLevel = -1, _pressRow = -1;
    private float _pressX, _pressY;

    /// <summary>Off, a left press never becomes a row drag (the owner's layout is locked); a click still activates.</summary>
    public bool DragEnabled = true;
    /// <summary>Drill-down mode: see the class summary. Set before <see cref="OpenAt"/>.</summary>
    public bool Drill;
    /// <summary>A row drag is in progress (not the scroll thumb: that is <see cref="Dragging"/>).</summary>
    public bool RowDrag { get; private set; }
    /// <summary>The drop target while <see cref="RowDrag"/>: the level under the pointer (-1 = none, a release
    /// cancels) and the gap index in it, 0..n.</summary>
    public int DropLevel { get; private set; } = -1;
    public int DropIndex { get; private set; }

    public CascadeMenu() { for (int i = 0; i < MaxDepth; i++) _lv[i] = new CascadeLevel(); }

    /// <summary>Open levels; 0 when closed.</summary>
    public int Depth { get; private set; }
    public bool Open => Depth > 0;
    /// <summary>Row height, set at <see cref="OpenAt"/>: the face's line height plus <see cref="Pad"/> each side.</summary>
    public float ItemH { get; private set; }
    public CascadeLevel Level(int i) => _lv[i];
    /// <summary>Icon squares fully inside their level band as of the last <see cref="Draw"/>.</summary>
    public ReadOnlySpan<IconQuad> Icons => new(_icons, 0, _iconN);
    public bool Dragging { get { for (int i = 0; i < Depth; i++) if (_lv[i].List.Dragging) return true; return false; } }

    /// <summary>Open the root at a point (the cursor), clamped into the viewport; the face sizes every level.</summary>
    public void OpenAt(CascadeItem[] items, float x, float y, GlyphAtlas atlas, float viewportW, float viewportH)
    {
        _atlas = atlas; _vpW = viewportW; _vpH = viewportH;
        ItemH = atlas.LineHeight + 2f * Pad;
        long opened = Depth > 0 ? _lv[0].OpenedMs : -1;   // a re-layout of an open root does not re-animate
        Depth = 0;
        Place(_lv[0], Layout(_lv[0], items), x, y, -1);
        _lv[0].OpenedMs = opened;
        Depth = 1;
        ResetDwell();
    }

    /// <summary>The owner's answer to a RequestChildren: open <paramref name="items"/> beside row
    /// <paramref name="row"/> of <paramref name="level"/>, closing every deeper level first (a sibling's child
    /// closes when another opens). False when refused (closed, bad row, or at <see cref="MaxDepth"/>).</summary>
    public bool OpenChild(int level, int row, CascadeItem[] items)
    {
        if (!Open || (uint)level >= (uint)Depth || level + 1 >= MaxDepth || (uint)row >= (uint)_lv[level].Items.Length) return false;
        long opened = level + 1 < Depth && _lv[level + 1].ParentRow == row ? _lv[level + 1].OpenedMs : -1;
        CloseTo(level);
        var parent = _lv[level];
        parent.Hover = row;
        var child = _lv[level + 1];
        float w = Layout(child, items);
        if (Drill)
        {
            string back = parent.Items[row].Label ?? "";
            float bw = Pad + (_atlas!.TryGet(BackMarkOf(_atlas), out Glyph bg) ? bg.Advance : 0f) + Pad + UiText.Measure(_atlas, back) + Pad;
            if (bw > w) w = bw > MaxW ? MaxW : bw;
            Place(child, w, parent.X, parent.Top(ItemH), row, back);
        }
        else
        {
            float x = parent.X + parent.W;
            if (x + w > _vpW) x = parent.X - w;
            Place(child, w, x, RowTop(level, row), row);
        }
        child.OpenedMs = opened;
        Depth = level + 2;
        ResetDwell();
        return true;
    }

    /// <summary>Place a laid-out level with its top-left at the point, clamped into the viewport; a
    /// <paramref name="back"/> label puts a Back band above the rows.</summary>
    private void Place(CascadeLevel lv, float w, float x, float y, int parentRow, string? back = null)
    {
        int n = lv.Items.Length;
        float h = (n < MaxVisible ? n : MaxVisible) * ItemH, band = back != null ? ItemH : 0f;
        lv.List.Reset();
        lv.List.SetRect(Window.ClampAxis(x, w, _vpW), Window.ClampAxis(y, h + band, _vpH) + band, w, h);
        lv.List.ContentH = n * ItemH;
        lv.Hover = -1; lv.ParentRow = parentRow; lv.OpenedMs = -1;
        lv.HasBack = back != null; lv.BackLabel = back ?? ""; lv.BackHover = false; lv.DropTarget = false;
    }

    private static int BackMarkOf(GlyphAtlas atlas) => atlas.TryGet(BackMark, out _) ? BackMark : '<';

    private static bool HasIcon(in CascadeItem it) => it.IconHandle != IntPtr.Zero || !string.IsNullOrEmpty(it.Icon);

    /// <summary>Measure a level: icon column, widest label + count + mark, the gutter when it scrolls. Cold.</summary>
    private float Layout(CascadeLevel lv, CascadeItem[] items)
    {
        var atlas = _atlas!;
        lv.Items = items;
        lv.HasIcons = false;
        for (int i = 0; i < items.Length; i++) if (HasIcon(in items[i])) { lv.HasIcons = true; break; }
        lv.Mark = atlas.TryGet(0x203A, out Glyph mg) ? 0x203A : '>';
        float markW = atlas.TryGet(lv.Mark, out mg) ? mg.Advance : 0f;
        float w = 0f, iconCol = lv.HasIcons ? ItemH : 0f;
        for (int i = 0; i < items.Length; i++)
        {
            ref CascadeItem it = ref items[i];
            float rw = iconCol + Pad + UiText.Measure(atlas, it.Label ?? "") + Pad;
            if (!string.IsNullOrEmpty(it.Count)) rw += UiText.Measure(atlas, it.Count) + Pad;
            if (it.HasChildren) rw += markW + Pad;
            if (rw > w) w = rw;
        }
        if (items.Length > MaxVisible) w += ScrollColumn.BarW;
        if (w < MinWRows * ItemH) w = MinWRows * ItemH;
        return w > MaxW ? MaxW : w;
    }

    /// <summary>Keep levels 0..<paramref name="level"/>, close the rest.</summary>
    public void CloseTo(int level)
    {
        int keep = level + 1;
        if (keep < 0) keep = 0;
        for (int i = keep; i < Depth; i++) { _lv[i].List.MouseUp(); _lv[i].Hover = -1; _lv[i].BackHover = false; _lv[i].Items = Array.Empty<CascadeItem>(); }
        if (keep < Depth) Depth = keep;
        if (_dwellLevel >= Depth) ResetDwell();
    }

    public void Close() { CloseTo(-1); _iconN = 0; ResetDwell(); ResetPress(); }

    private void ResetPress() { _pressLevel = -1; _pressRow = -1; RowDrag = false; DropLevel = -1; }

    private void ResetDwell() { _dwellLevel = -1; _dwellRow = -1; _dwellSince = -1; _dwellDone = false; }

    /// <summary>Screen top of a row (may lie outside its level while scrolled).</summary>
    public float RowTop(int level, int row) => _lv[level].List.ToScreen(row * ItemH);

    public bool Contains(float mx, float my) => LevelAt(mx, my) >= 0;

    /// <summary>Deepest open level under the point, or -1. In drill mode only the deepest level (its Back band
    /// included) is under anything.</summary>
    public int LevelAt(float mx, float my)
    {
        if (!Open) return -1;
        if (Drill) { var top = _lv[Depth - 1]; return top.List.Contains(mx, my) || top.InBack(mx, my, ItemH) ? Depth - 1 : -1; }
        for (int i = Depth - 1; i >= 0; i--) if (_lv[i].List.Contains(mx, my)) return i;
        return -1;
    }

    /// <summary>The deepest level's Back band is under the point (drill mode).</summary>
    private bool BackAt(float mx, float my) => Drill && Open && _lv[Depth - 1].InBack(mx, my, ItemH);

    /// <summary>Row of <paramref name="level"/> under the point (the gutter excluded), or -1.</summary>
    public int ItemAt(int level, float mx, float my)
    {
        var lv = _lv[level];
        if (!lv.List.Contains(mx, my) || lv.List.OverBar(mx, my)) return -1;
        int i = (int)(lv.List.ToContent(my) / ItemH);
        return (uint)i < (uint)lv.Items.Length ? i : -1;
    }

    /// <summary>Motion: a thumb drag, a row drag (armed by a left press, started past <see cref="DragPx"/>: the drop
    /// gap tracks the pointer, nothing else moves), else hover in the level under the pointer (deeper hovers clear,
    /// shallower ones keep, so the row owning an open child stays lit). True when something to redraw changed.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!Open) return false;
        _mx = mx; _my = my;
        for (int i = 0; i < Depth; i++) if (_lv[i].List.MouseMove(mx, my)) return true;
        if (_pressLevel >= 0 && _pressRow != BackRow && !RowDrag && DragEnabled && (MathF.Abs(mx - _pressX) > DragPx || MathF.Abs(my - _pressY) > DragPx)) RowDrag = true;
        if (RowDrag)
        {
            int at = LevelAt(mx, my);
            DropLevel = at == _pressLevel || (at == 0 && !Drill) || (at > 0 && _lv[at].DropTarget) ? at : -1;   // a drop lands in the row's own list, the top one, or a level that takes drops
            if (DropLevel >= 0)
            {
                var dl = _lv[DropLevel];
                int gap = (int)MathF.Round(dl.List.ToContent(my) / ItemH);
                DropIndex = gap < 0 ? 0 : gap > dl.Items.Length ? dl.Items.Length : gap;
            }
            return true;
        }
        int l = LevelAt(mx, my);
        if (l < 0) { ResetDwell(); return SetBackHover(false); }
        int row = ItemAt(l, mx, my);
        bool changed = SetBackHover(BackAt(mx, my));
        for (int i = l + 1; i < Depth; i++) if (_lv[i].Hover != -1) { _lv[i].Hover = -1; changed = true; }
        if (_lv[l].Hover != row) { _lv[l].Hover = row; changed = true; }
        if (l != _dwellLevel || row != _dwellRow) { _dwellLevel = l; _dwellRow = row; _dwellSince = -1; _dwellDone = false; }
        return changed;
    }

    private bool SetBackHover(bool on)
    {
        if (!Open) return false;
        var top = _lv[Depth - 1];
        if (top.BackHover == on) return false;
        top.BackHover = on;
        return true;
    }

    /// <summary>Release: the thumb lets go; a row drag drops into the gap under the pointer (None when over no
    /// level); an undragged left press released on the leaf it pressed activates it (in drill mode a submenu row
    /// released the same way asks for its children, and the Back band goes up a level).</summary>
    public CascadeAction MouseUp()
    {
        for (int i = 0; i < Depth; i++) _lv[i].List.MouseUp();
        if (!Open || _pressLevel < 0) return CascadeAction.None;
        int l = _pressLevel, row = _pressRow;
        bool drag = RowDrag;
        int toL = DropLevel, toI = DropIndex;
        ResetPress();
        if (drag) return toL >= 0 ? CascadeAction.Drop(l, row, toL, toI) : CascadeAction.None;
        if (row == BackRow) { if (l == Depth - 1 && BackAt(_mx, _my)) CloseTo(l - 1); return CascadeAction.None; }
        if (l >= Depth || (uint)row >= (uint)_lv[l].Items.Length || LevelAt(_mx, _my) != l || ItemAt(l, _mx, _my) != row) return CascadeAction.None;
        ref CascadeItem it = ref _lv[l].Items[row];
        if (it.Disabled) return CascadeAction.None;
        if (it.HasChildren) return Drill ? CascadeAction.RequestChildren(l, row) : CascadeAction.None;
        return CascadeAction.Activate(l, row);
    }

    /// <summary>The pressed-row value that stands for the Back band (never a row index).</summary>
    private const int BackRow = -2;

    /// <summary>Wheel scrolls the level under the last pointer position (else the deepest) one row per notch.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        int l = LevelAt(_mx, _my);
        if (l < 0) l = Depth - 1;
        bool moved = _lv[l].List.MouseWheel(dy, ItemH);
        _lv[l].Hover = -1;   // the rows moved under a still pointer; the next motion re-hovers
        if (_dwellLevel == l) ResetDwell();
        return moved;
    }

    /// <summary>Press: outside every level closes (CloseAll); the gutter scrolls; a row yields Context for the
    /// right button, RequestChildren for an enabled submenu row, else None -- a left press arms the row for a
    /// drag, and a leaf activates on its release (<see cref="MouseUp"/>). A press on a row other than the one
    /// owning an open child closes that child at once -- unless the child is a <see cref="CascadeLevel.DropTarget"/>,
    /// which the press may be about to drag into (a leaf release or another submenu still replaces it).</summary>
    public CascadeAction MouseDown(float mx, float my, int button)
    {
        if (!Open) return CascadeAction.None;
        _mx = mx; _my = my;
        int l = LevelAt(mx, my);
        if (l < 0) { Close(); return CascadeAction.CloseAll; }
        var lv = _lv[l];
        if (lv.List.MouseDown(mx, my)) return CascadeAction.None;
        if (BackAt(mx, my))
        {
            if (button == LeftButton) { _pressLevel = l; _pressRow = BackRow; _pressX = mx; _pressY = my; RowDrag = false; DropLevel = -1; }
            return CascadeAction.None;
        }
        int row = ItemAt(l, mx, my);
        if (row < 0) return CascadeAction.None;
        _dwellDone = true;
        bool childHere = l + 1 < Depth && _lv[l + 1].ParentRow == row;
        if (l + 1 < Depth && !childHere && !_lv[l + 1].DropTarget) CloseTo(l);   // a drop-target child stays for the drag this press may start
        if (button == RightButton) return CascadeAction.Context(l, row);
        if (button == LeftButton) { _pressLevel = l; _pressRow = row; _pressX = mx; _pressY = my; RowDrag = false; DropLevel = -1; }
        ref CascadeItem it = ref lv.Items[row];
        if (it.Disabled || !it.HasChildren || Drill) return CascadeAction.None;
        return childHere ? CascadeAction.None : CascadeAction.RequestChildren(l, row);
    }

    /// <summary>Per-frame poll with the owner's clock: once the hovered row has rested <see cref="DwellMs"/> (0 = at once)
    /// (timed from the first Tick after the hover changed), a sibling child closes and a submenu row asks for
    /// its children, once. Zero-alloc.</summary>
    public CascadeAction Tick(long nowMs)
    {
        if (!Open || Drill || RowDrag || _dwellDone || _dwellLevel < 0 || _dwellRow < 0) return CascadeAction.None;
        if (_dwellSince < 0) { _dwellSince = nowMs; if (DwellMs > 0) return CascadeAction.None; }
        if (nowMs - _dwellSince < DwellMs) return CascadeAction.None;
        _dwellDone = true;
        int l = _dwellLevel, row = _dwellRow;
        if (l >= Depth || row >= _lv[l].Items.Length) return CascadeAction.None;
        if (l + 1 < Depth && _lv[l + 1].ParentRow == row) return CascadeAction.None;
        ref CascadeItem it = ref _lv[l].Items[row];
        bool opens = !it.Disabled && it.HasChildren;
        if (l + 1 < Depth && _lv[l + 1].DropTarget && !opens) return CascadeAction.None;   // resting on a leaf beside a drop target keeps it for the drag
        if (l + 1 < Depth) CloseTo(l);
        return opens ? CascadeAction.RequestChildren(l, row) : CascadeAction.None;
    }

    /// <summary>Keys act on the deepest level: Up/Down move the hover (wrapping, scrolled into view);
    /// Right/Enter open a submenu row or activate a leaf; Left closes one level; Esc closes one level, or the
    /// menu at the root (CloseAll). While a row drags, Esc cancels the drag and every other key is inert.</summary>
    public CascadeAction Key(uint sdlKey)
    {
        if (!Open) return CascadeAction.None;
        if (RowDrag) { if (sdlKey == KeyEscape) ResetPress(); return CascadeAction.None; }
        int l = Depth - 1;
        var lv = _lv[l];
        int n = lv.Items.Length;
        switch (sdlKey)
        {
            case KeyDown:
            case KeyUp:
                if (n == 0) return CascadeAction.None;
                int h = lv.Hover;
                if (sdlKey == KeyDown) h = h < 0 || h + 1 >= n ? 0 : h + 1;
                else h = h <= 0 ? n - 1 : h - 1;
                lv.Hover = h;
                float top = h * ItemH;
                if (top < lv.List.Offset) lv.List.Offset = top;
                else if (top + ItemH > lv.List.Offset + lv.List.H) lv.List.Offset = top + ItemH - lv.List.H;
                lv.List.Clamp();
                ResetDwell();
                return CascadeAction.None;
            case KeyRight:
            case KeyReturn:
            case KeyKpEnter:
                if ((uint)lv.Hover >= (uint)n) return CascadeAction.None;
                ref CascadeItem it = ref lv.Items[lv.Hover];
                if (it.Disabled) return CascadeAction.None;
                return it.HasChildren ? CascadeAction.RequestChildren(l, lv.Hover) : CascadeAction.Activate(l, lv.Hover);
            case KeyLeft:
                if (l > 0) CloseTo(l - 1);
                return CascadeAction.None;
            case KeyEscape:
                if (l > 0) { CloseTo(l - 1); return CascadeAction.None; }
                Close();
                return CascadeAction.CloseAll;
        }
        return CascadeAction.None;
    }

    /// <summary>A 1 px frame just inside the rect, as four strips: a filled rect under the bg would tint a
    /// translucent bg with the frame colour.</summary>
    private static void Outline(VertexBatch b, float x0, float y0, float x1, float y1, (float r, float g, float b, float a) c, float alpha)
    {
        float a = c.a * alpha;
        b.Rect(x0, y0, x1, y0 + 1f, c.r, c.g, c.b, a); b.Rect(x0, y1 - 1f, x1, y1, c.r, c.g, c.b, a);
        b.Rect(x0, y0 + 1f, x0 + 1f, y1 - 1f, c.r, c.g, c.b, a); b.Rect(x1 - 1f, y0 + 1f, x1, y1 - 1f, c.r, c.g, c.b, a);
    }

    /// <summary>Draw every open level, deepest last: body, frame, the owning-row and hover fills, icons noted for
    /// the owner (<see cref="Icons"/>), labels, counts, submenu marks (clipped to the level) and the scrollbar. A
    /// level fades and slides in over <see cref="CascadeStyle.AnimMs"/> from its first draw (the drawn rect moves,
    /// the hit rect never does); the drop gap and the dragged row's ghost draw on top.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop, in CascadeStyle st)
    {
        _iconN = 0;
        if (!Open) return;
        var bg = st.Bg; var active = st.Hover; var frame = st.Frame;
        uint textRgb = st.TextRgb;
        float tr = ((textRgb >> 16) & 0xFF) / 255f, tg = ((textRgb >> 8) & 0xFF) / 255f, tb = (textRgb & 0xFF) / 255f;
        float vpad = (ItemH - atlas.LineHeight) / 2f, inset = IconInset, pad = Pad;
        for (int i = Drill ? Depth - 1 : 0; i < Depth; i++)
        {
            var lv = _lv[i];
            if (lv.OpenedMs < 0) lv.OpenedMs = st.NowMs;
            float e = st.AnimMs <= 0 ? 1f : (st.NowMs - lv.OpenedMs) / (float)st.AnimMs;
            e = e >= 1f ? 1f : e <= 0f ? 0f : 1f - (1f - e) * (1f - e);
            float alpha = st.Alpha * e, ox = i == 0 ? 0f : (Drill ? 1f : -1f) * (1f - e) * SlidePx, oy = i == 0 ? -(1f - e) * SlidePx : 0f;
            float x0 = lv.X + ox, y0 = lv.Y + oy, x1 = x0 + lv.W, y1 = y0 + lv.H, band = lv.HasBack ? ItemH : 0f;
            b.Rect(x0, y0 - band, x1, y1, bg.r, bg.g, bg.b, bg.a * alpha);
            Outline(b, x0 - 1f, y0 - band - 1f, x1 + 1f, y1 + 1f, frame, alpha);
            float fillR = x1 - (lv.List.Overflows ? ScrollColumn.BarW : 0f), textClip = fillR - pad;
            float iconCol = lv.HasIcons ? ItemH : 0f;
            bool hasMark = atlas.TryGet(lv.Mark, out Glyph mg);
            float markW = hasMark ? mg.Advance : 0f;
            if (lv.HasBack)
            {
                if (lv.BackHover) b.Rect(x0, y0 - band, x1, y0, active.r, active.g, active.b, active.a * 0.55f * alpha);
                float bl = y0 - band + baseDrop + vpad, a = st.TextAlpha * alpha, bx = x0 + pad;
                if (atlas.TryGet(BackMarkOf(atlas), out Glyph bm)) { b.Glyph(bm, bx, bl, tr, tg, tb, a); bx += bm.Advance + pad; }
                UiText.Draw(b, atlas, lv.BackLabel, bx, bl, textRgb, textClip, a);
                b.Rect(x0, y0 - 1f, x1, y0, frame.r, frame.g, frame.b, frame.a * alpha);
            }
            int owning = i + 1 < Depth ? _lv[i + 1].ParentRow : -1;
            b.ClipY(y0, y1);
            for (int r = 0; r < lv.Items.Length; r++)
            {
                float top = lv.List.ToScreen(r * ItemH) + oy;
                if (top + ItemH <= y0 || top >= y1) continue;
                ref CascadeItem it = ref lv.Items[r];
                if (r == owning) b.Rect(x0, top, fillR, top + ItemH, active.r, active.g, active.b, active.a * alpha);
                else if (r == lv.Hover) b.Rect(x0, top, fillR, top + ItemH, active.r, active.g, active.b, active.a * 0.55f * alpha);
                float a = (it.Disabled ? DisabledAlpha : 1f) * st.TextAlpha * alpha;
                if (RowDrag && i == _pressLevel && r == _pressRow) a *= DragSourceAlpha;
                // Icons draw unclipped in the owner's pass, so only a square wholly inside the band is reported.
                if (iconCol > 0f && HasIcon(in it) && top >= y0 && top + ItemH <= y1 && _iconN < _icons.Length)
                    _icons[_iconN++] = new IconQuad(it.Icon, x0 + inset, top + inset, ItemH - 2f * inset, a, it.IconHandle);
                float baseline = top + baseDrop + vpad, right = textClip;
                if (it.HasChildren && hasMark) { right -= markW; b.Glyph(mg, right, baseline, tr, tg, tb, a); right -= pad; }
                float labelX = x0 + iconCol + pad;
                if (!string.IsNullOrEmpty(it.Count))
                {
                    float cw = UiText.Measure(atlas, it.Count);
                    if (cw + pad <= right - labelX - UiText.Measure(atlas, it.Label ?? ""))   // the count never eats the label: dropped when it would
                    {
                        right -= cw;
                        UiText.Draw(b, atlas, it.Count, right, baseline, textRgb, textClip, a);
                        right -= pad;
                    }
                }
                if (it.Label != null) UiText.Draw(b, atlas, it.Label, labelX, baseline, textRgb, right, a);
            }
            if (RowDrag && i == DropLevel)
            {
                float gy = lv.List.ToScreen(DropIndex * ItemH) + oy, line = GapLinePx * Metrics.Scale;
                if (gy >= y0 - line && gy <= y1 + line) b.Rect(x0, gy - line, fillR, gy + line, tr, tg, tb, st.TextAlpha * alpha);
            }
            b.ClearClip();
            lv.List.Draw(b, active);
        }
        if (RowDrag && _pressLevel < Depth && (uint)_pressRow < (uint)_lv[_pressLevel].Items.Length)
        {
            string? ghost = _lv[_pressLevel].Items[_pressRow].Label;
            if (ghost != null) UiText.Draw(b, atlas, ghost, _mx + GhostDx, _my + baseDrop - atlas.LineHeight / 2f, textRgb, _vpW, GhostAlpha * st.TextAlpha * st.Alpha);
        }
    }

    /// <summary>Off-device proof of the CX-690/CX-691 contract: root placement + clamp, hit-test, hover, wrapping
    /// key navigation, lazy children opened at the parent's right edge (flipped at the viewport edge, clamped
    /// vertically), a 30-row child scrolling to its tail, sibling replacement, Left/Esc unwind, outside press,
    /// disabled rows, the right-button context, the hover dwell, icon rects, a zero-alloc draw, the row drag and
    /// the open animation.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CascadeMenu.SelfCheck FAIL: {w}"); } }

        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = '0'; ch <= '9'; ch++) atlas.SetTestGlyph(ch, 6f);
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        atlas.SetTestGlyph('>', 6f);
        const float vpW = 1920f, vpH = 1080f;

        var root = new CascadeItem[8];
        for (int i = 0; i < 8; i++) root[i] = new CascadeItem("row" + i);
        root[1].Icon = "ir_x";
        root[2].HasChildren = true;
        root[3].Count = "3";
        root[5].Disabled = true;
        root[6].Disabled = true; root[6].HasChildren = true;
        root[7].HasChildren = true;
        var child = new CascadeItem[30];
        for (int i = 0; i < 30; i++) child[i] = new CascadeItem("c" + i);
        child[0].HasChildren = true;
        var grand = new[] { new CascadeItem("g0", "ir_g"), new CascadeItem("g1") { IconHandle = (IntPtr)0x7f01 } };

        var m = new CascadeMenu();
        E(!m.Open && m.MouseDown(0f, 0f, 1).Kind == CascadeActionKind.None && m.Key(KeyDown).Kind == CascadeActionKind.None
          && m.Tick(0).Kind == CascadeActionKind.None && !m.MouseMove(0f, 0f) && !m.MouseWheel(1), "closed: nothing consumed");

        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        float ih = m.ItemH;
        var l0 = m.Level(0);
        E(m.Open && m.Depth == 1 && l0.X == 100f && l0.Y == 100f && l0.H == 8 * ih && !l0.List.Overflows, "8 rows open at the point, no scroll");
        E(ih == atlas.LineHeight + 2f * Pad && l0.HasIcons && l0.Mark == '>' && l0.W > ih + 30f && l0.W <= MaxW, "row height from the face, icon column, '>' fallback mark, measured width");
        E(m.Contains(110f, 101f) && !m.Contains(99f, 101f) && m.LevelAt(110f, 101f) == 0, "contains + level hit-test");
        E(m.ItemAt(0, 110f, 100f + ih * 2f + 1f) == 2 && m.RowTop(0, 2) == 100f + 2f * ih, "row hit-test and row top");
        E(m.MouseMove(110f, 100f + ih * 2f + 1f) && l0.Hover == 2 && !m.MouseMove(111f, 100f + ih * 2f + 1f), "hover tracks the row; same row reports no change");

        // Keys: Down/Up move the hover and wrap; Enter/Right on a leaf activate.
        E(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 3, "Down moves the hover");
        m.Key(KeyUp); m.Key(KeyUp); m.Key(KeyUp);
        E(l0.Hover == 0 && m.Key(KeyUp).Kind == CascadeActionKind.None && l0.Hover == 7, "Up wraps from the first row to the last");
        E(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 0, "Down wraps from the last row to the first");
        m.Key(KeyDown); m.Key(KeyDown); m.Key(KeyDown); m.Key(KeyDown);
        var act = m.Key(KeyReturn);
        E(l0.Hover == 4 && act.Kind == CascadeActionKind.Activate && act.Level == 0 && act.Row == 4 && m.Open, "Enter on a leaf activates it and leaves the menu open");
        E(m.Key(KeyRight).Kind == CascadeActionKind.Activate && m.Key(KeyKpEnter).Kind == CascadeActionKind.Activate, "Right / keypad Enter activate a leaf too");
        E(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Open && m.Depth == 1, "Left at the root does nothing");

        // Submenu row: a press asks for children; OpenChild places them at the right edge, top on the row.
        var req = m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1);
        E(req.Kind == CascadeActionKind.RequestChildren && req.Level == 0 && req.Row == 2 && m.Open, "press on a submenu row requests its children");
        E(m.MouseUp().Kind == CascadeActionKind.None, "its release is nothing");
        E(m.OpenChild(0, 2, child) && m.Depth == 2, "OpenChild opens level 1");
        var l1 = m.Level(1);
        E(l1.X == l0.X + l0.W && l1.Y == m.RowTop(0, 2) && l1.ParentRow == 2 && l0.Hover == 2, "the child hangs off the parent's right edge, top on its row");
        E(l1.H == MaxVisible * ih && l1.List.Overflows && l1.List.MaxOffset == (30 - MaxVisible) * ih, "30 rows cap at MaxVisible and scroll");
        E(m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 2 && m.MouseUp().Kind == CascadeActionKind.None, "pressing the owning row again keeps its child");
        E(!m.OpenChild(0, 9, child) && !m.OpenChild(3, 0, child), "bad row / level refused");

        // Wheel in the child reaches the tail; keys scroll the hover into view.
        E(m.MouseMove(l1.X + 10f, l1.Y + 1f) && l1.Hover == 0 && l0.Hover == 2, "hover in the child leaves the parent's owning row lit");
        int notches = 0;
        while (m.MouseWheel(-1)) notches++;
        E(notches == 30 - MaxVisible && l1.List.Offset == l1.List.MaxOffset && l1.Hover == -1, $"wheel scrolls one row per notch to the tail ({notches})");
        E(m.ItemAt(1, l1.X + 10f, l1.Y + l1.H - 1f) == 29, "the last row sits at the child's bottom");
        E(m.Key(KeyDown).Kind == CascadeActionKind.None && l1.Hover == 0 && l1.List.Offset == 0f, "Down from no hover picks row 0 and scrolls it into view");
        E(m.Key(KeyUp).Kind == CascadeActionKind.None && l1.Hover == 29 && l1.List.Offset == l1.List.MaxOffset, "Up wraps to the tail and scrolls to it");
        E(m.Key(KeyReturn).Kind == CascadeActionKind.Activate && m.Key(KeyReturn).Level == 1, "Enter in the child activates at level 1");
        E(m.ItemAt(1, l1.X + l1.W - 2f, l1.Y + 5f) == -1, "the gutter is not a row");
        E(l1.List.ThumbRect(out float tt, out _) && m.MouseDown(l1.X + l1.W - 2f, tt + 1f, 1).Kind == CascadeActionKind.None && m.Dragging, "thumb press grabs, picks nothing");
        m.MouseMove(l1.X + l1.W - 2f, tt + 1f - l1.H);
        E(m.MouseUp().Kind == CascadeActionKind.None && !m.Dragging && l1.List.Offset == 0f, "thumb dragged to the top shows the head");

        // Grandchild, then a sibling at the root replaces the whole subtree.
        l1.Hover = 0;
        E(m.Key(KeyRight).Kind == CascadeActionKind.RequestChildren && m.OpenChild(1, 0, grand) && m.Depth == 3, "Right on a submenu row + OpenChild nests a third level");
        E(m.Level(2).X == l1.X + l1.W && m.Level(2).Y == m.RowTop(1, 0), "the grandchild hangs off the child");
        E(m.OpenChild(0, 7, child) && m.Depth == 2 && l1.ParentRow == 7 && l1.Y == m.RowTop(0, 7) && l0.Hover == 7, "opening a sibling closes the first child and its subtree");
        E(m.OpenChild(1, 0, grand) && m.Depth == 3, "re-nest");
        E(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Depth == 2, "Left closes the deepest level");
        E(m.Key(KeyEscape).Kind == CascadeActionKind.None && m.Depth == 1, "Esc closes one level");
        E(m.Key(KeyEscape).Kind == CascadeActionKind.CloseAll && !m.Open && m.Depth == 0, "Esc at the root closes all");

        // Press on a different parent row closes the child at once; press outside closes all.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.OpenChild(0, 2, child);
        E(m.MouseDown(110f, m.RowTop(0, 3) + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 1, "a press on another parent row closes the child, activates nothing yet");
        var rel = m.MouseUp();
        E(rel.Kind == CascadeActionKind.Activate && rel.Level == 0 && rel.Row == 3 && m.Open, "the release on the same leaf activates it");
        E(m.MouseUp().Kind == CascadeActionKind.None, "a second release is nothing");
        m.MouseDown(110f, m.RowTop(0, 3) + 1f, 1);
        m.MouseMove(110f + DragPx - 1f, m.RowTop(0, 3) + DragPx);
        E(!m.RowDrag && m.MouseUp().Kind == CascadeActionKind.Activate, "jitter under DragPx is still a click");
        m.OpenChild(0, 2, child);
        E(m.MouseDown(5f, 5f, 1).Kind == CascadeActionKind.CloseAll && !m.Open, "press outside every level closes all");

        // Row drag (CX-694): armed by a left press, started past DragPx, the gap under the pointer is the target,
        // hover/dwell/closes hold, the release drops; Esc or a release off every level cancels.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.OpenChild(0, 2, child);
        float r4 = m.RowTop(0, 4) + 1f;
        E(m.MouseDown(110f, r4, 1).Kind == CascadeActionKind.None && !m.RowDrag && m.Depth == 1, "a left press arms, does not drag; the child closed");
        E(m.MouseMove(110f + DragPx, r4) && !m.RowDrag && m.MouseMove(110f + DragPx + 1f, r4) && m.RowDrag, "the drag starts one past DragPx");
        E(m.DropLevel == 0 && m.DropIndex == 4 && m.MouseMove(110f, m.RowTop(0, 1) + ih / 2f + 1f) && m.DropIndex == 2, "the gap rounds to the nearest row edge");
        E(l0.Hover == 4 && m.Tick(100).Kind == CascadeActionKind.None && m.Tick(100 + DefaultDwellMs).Kind == CascadeActionKind.None, "hover and the dwell hold while dragging");
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        E(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 4, "keys are inert while dragging");
        E(m.MouseMove(110f, m.RowTop(0, 0) - 5f) && m.DropLevel == -1 && m.MouseMove(110f, m.RowTop(0, 7) + ih - 1f) && m.DropIndex == 8, "off the level = no target; below the last row = gap n");
        var drop = m.MouseUp();
        E(drop.Kind == CascadeActionKind.Drop && drop.Level == 0 && drop.Row == 4 && drop.ToLevel == 0 && drop.ToIndex == 8 && !m.RowDrag && m.Open, "the release drops into the gap");
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        E(m.RowDrag && m.MouseMove(5f, 5f) && m.DropLevel == -1 && m.MouseUp().Kind == CascadeActionKind.None && !m.RowDrag, "a release off every level cancels");
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        E(m.RowDrag && m.Key(KeyEscape).Kind == CascadeActionKind.None && !m.RowDrag && m.Open && m.MouseUp().Kind == CascadeActionKind.None, "Esc cancels the drag, the menu stays");
        m.OpenChild(0, 2, child);
        m.MouseDown(l1.X + 10f, m.RowTop(1, 3) + 1f, 1); m.MouseMove(l1.X + 10f, m.RowTop(1, 3) + 2f * DragPx);
        E(m.RowDrag && m.Depth == 2 && m.MouseMove(110f, m.RowTop(0, 1) + 1f) && m.Depth == 2 && m.DropLevel == 0 && m.DropIndex == 1, "dragging from the child over the root keeps the child open, targets the root gap");
        E(m.OpenChild(1, 0, grand) && m.MouseMove(m.Level(2).X + 5f, m.Level(2).Y + 1f) && m.DropLevel == -1 && m.MouseMove(l1.X + 5f, l1.Y + 1f) && m.DropLevel == 1, "a level that is neither the source nor the root is no target");
        m.Level(2).DropTarget = true;
        E(m.MouseMove(m.Level(2).X + 5f, m.Level(2).Y + 1f) && m.DropLevel == 2 && m.DropIndex == 0, "a level flagged as a drop target takes a drop from another level");
        E(m.OpenChild(1, 0, grand) && !m.Level(2).DropTarget, "re-opening a level clears the flag");
        m.MouseMove(110f, m.RowTop(0, 1) + 1f);
        drop = m.MouseUp();
        E(drop.Kind == CascadeActionKind.Drop && drop.Level == 1 && drop.Row == 3 && drop.ToLevel == 0 && drop.ToIndex == 1, "a cross-level drop reports both ends");
        m.CloseTo(0);
        m.OpenChild(0, 2, child);
        m.Level(1).DropTarget = true;
        m.MouseDown(110f, r4, 1);
        E(m.Depth == 2 && m.RowDrag == false && m.MouseMove(110f, r4 + 2f * DragPx) && m.RowDrag && m.MouseMove(l1.X + 5f, l1.Y + 1f) && m.DropLevel == 1,
            "a press on another row keeps a drop-target child open, so the drag can land in it");
        drop = m.MouseUp();
        E(drop.Kind == CascadeActionKind.Drop && drop.Level == 0 && drop.Row == 4 && drop.ToLevel == 1, "and the drop reports the child as its end");
        m.CloseTo(0); m.OpenChild(0, 2, child);
        m.MouseDown(110f, r4, 1);
        E(m.Depth == 1, "without the flag the press still closes a sibling's child");
        m.MouseUp();
        m.OpenChild(0, 2, child);
        m.Level(1).DropTarget = true;
        m.DwellMs = 0;
        E(m.MouseMove(110f, r4) && m.Tick(1).Kind == CascadeActionKind.None && m.Depth == 2, "resting on a leaf beside a drop-target child keeps it open");
        E(m.MouseMove(110f, m.RowTop(0, 7) + 1f) && m.Tick(2).Kind == CascadeActionKind.RequestChildren && m.Depth == 1, "resting on another submenu row still replaces it");
        m.DwellMs = DefaultDwellMs;
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        m.Close();
        E(!m.RowDrag && m.DropLevel == -1, "Close drops the drag");
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.DragEnabled = false;
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        E(!m.RowDrag && m.MouseUp().Kind == CascadeActionKind.Activate, "drag disabled: a moved press never drags, the release on the row still activates");
        m.DragEnabled = true;
        m.Close();

        // Disabled rows hover but never activate; the right button gives Context on any row.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        E(m.MouseMove(110f, m.RowTop(0, 5) + 1f) && l0.Hover == 5, "a disabled row hovers");
        E(m.Key(KeyReturn).Kind == CascadeActionKind.None && m.Key(KeyRight).Kind == CascadeActionKind.None
          && m.MouseDown(110f, m.RowTop(0, 5) + 1f, 1).Kind == CascadeActionKind.None && m.MouseUp().Kind == CascadeActionKind.None && m.Open, "a disabled leaf never activates");
        m.MouseMove(110f, m.RowTop(0, 6) + 1f);
        E(m.Key(KeyReturn).Kind == CascadeActionKind.None && m.MouseDown(110f, m.RowTop(0, 6) + 1f, 1).Kind == CascadeActionKind.None, "a disabled submenu row never requests children");
        m.MouseUp();
        var ctx = m.MouseDown(110f, m.RowTop(0, 3) + 1f, 3);
        E(ctx.Kind == CascadeActionKind.Context && ctx.Level == 0 && ctx.Row == 3 && m.Open, "right button on a row is Context, menu stays");
        E(m.MouseDown(110f, m.RowTop(0, 5) + 1f, 3).Kind == CascadeActionKind.Context, "Context on a disabled row too");

        // Dwell: timed from the first Tick after the hover changed, fires once, re-arms on a new row.
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        E(m.Tick(1000).Kind == CascadeActionKind.None && m.Tick(1000 + DefaultDwellMs - 1).Kind == CascadeActionKind.None, "under the dwell: nothing");
        m.MouseMove(111f, m.RowTop(0, 2) + 1f);
        var dw = m.Tick(1000 + DefaultDwellMs);
        E(dw.Kind == CascadeActionKind.RequestChildren && dw.Level == 0 && dw.Row == 2, "at the dwell the hovered submenu row asks for its children");
        E(m.Tick(1000 + DefaultDwellMs + 1).Kind == CascadeActionKind.None && m.Tick(9999).Kind == CascadeActionKind.None, "the request fires once");
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        m.Tick(1500);
        m.MouseMove(5f, 5f);
        E(m.Tick(1500 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "leaving every level disarms the dwell");
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        m.OpenChild(0, 2, child);
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        E(m.Tick(2000).Kind == CascadeActionKind.None && m.Depth == 2, "hovering another submenu row keeps the child until the dwell");
        dw = m.Tick(2000 + DefaultDwellMs);
        E(dw.Kind == CascadeActionKind.RequestChildren && dw.Row == 7 && m.Depth == 1, "at the dwell the sibling closes and the new row asks");
        m.OpenChild(0, 7, child);
        m.MouseMove(110f, m.RowTop(0, 4) + 1f);
        m.Tick(3000);
        E(m.Tick(3000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "dwelling on a leaf closes the sibling child, asks nothing");
        m.OpenChild(0, 7, child);
        m.MouseMove(l1.X + 10f, l1.Y + ih + 1f);
        m.Tick(4000);
        E(m.Tick(4000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 2, "dwelling on a leaf inside the child keeps it");
        m.MouseMove(l1.X + 10f, l1.Y + 1f);
        m.Tick(4500);
        dw = m.Tick(4500 + DefaultDwellMs);
        E(dw.Kind == CascadeActionKind.RequestChildren && dw.Level == 1 && dw.Row == 0 && m.Depth == 2, "dwelling on a submenu row inside the child asks at level 1");
        m.MouseMove(110f, m.RowTop(0, 6) + 1f);
        m.Tick(5000);
        E(m.Tick(5000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "a disabled submenu row closes the sibling but asks nothing");
        m.OpenChild(0, 7, child);
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        m.Tick(6000);
        E(m.Tick(6000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 2, "dwelling on the owning row asks nothing");
        m.CloseTo(0);
        m.DwellMs = 0;
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        dw = m.Tick(7000);
        E(dw.Kind == CascadeActionKind.RequestChildren && dw.Row == 2 && m.Tick(7000).Kind == CascadeActionKind.None, "dwell 0: the first Tick after the hover asks, once");
        m.DwellMs = DefaultDwellMs;
        m.Close();

        // Edges: the root clamps into the viewport; a child past the right edge flips left; a tall child clamps up.
        m.OpenAt(root, 1900f, 1070f, atlas, vpW, vpH);
        E(l0.X == vpW - l0.W && l0.Y == vpH - l0.H, "the root clamps into the viewport");
        E(m.OpenChild(0, 2, child) && l1.X == l0.X - l1.W && l1.Y == vpH - l1.H, "a child past the right edge flips to the parent's left; a tall child clamps up");
        m.OpenAt(root, 100f, 1000f, atlas, vpW, vpH);
        E(l0.Y == vpH - 8 * ih && m.OpenChild(0, 7, child) && l1.X == l0.X + l0.W && l1.Y == Window.ClampAxis(m.RowTop(0, 7), l1.H, vpH) && l1.Y + l1.H <= vpH, "a child off the row near the bottom clamps into view");
        m.Close();

        // Drill mode: a child replaces the parent in place under a Back band; only the deepest level is live.
        VertexBatch? b2 = null;
        atlas.SetTestGlyph('<', 6f);
        m.Drill = true;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        E(!l0.HasBack && m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1).Kind == CascadeActionKind.None, "drill: a press on a submenu row asks nothing yet");
        var dr = m.MouseUp();
        E(dr.Kind == CascadeActionKind.RequestChildren && dr.Row == 2, "drill: the release asks for its children");
        E(m.OpenChild(0, 2, child) && m.Depth == 2 && l1.HasBack && l1.BackLabel == "row2" && l1.X == l0.X && l1.Y == l0.Y + ih && l1.Top(ih) == l0.Y,
            "drill: the child sits where the parent was, its Back band on the parent's top row");
        E(m.LevelAt(110f, l0.Y + 1f) == 1 && m.ItemAt(1, 110f, l0.Y + 1f) == -1 && m.Contains(110f, l0.Y + 1f), "drill: the band belongs to the child, not to a row; the hidden parent is under nothing");
        E(m.MouseMove(110f, l0.Y + 1f) && l1.BackHover && l1.Hover == -1 && m.MouseMove(110f, l1.Y + 1f) && !l1.BackHover && l1.Hover == 0, "drill: the band hovers as one, rows hover as rows");
        m.MouseMove(110f, l1.Y + 1f);
        E(m.Tick(100).Kind == CascadeActionKind.None && m.Tick(100 + DefaultDwellMs).Kind == CascadeActionKind.None, "drill: no dwell descent");
        E(m.MouseDown(110f, l0.Y + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 2, "drill: a press on Back waits for the release");
        E(m.MouseMove(110f + 30f, l0.Y + 1f) && !m.RowDrag, "drill: Back never drags");
        E(m.MouseUp().Kind == CascadeActionKind.None && m.Depth == 1 && l0.Hover == 2, "drill: releasing on Back returns to the parent, its owning row still lit");
        m.OpenChild(0, 2, child);
        m.MouseDown(110f, l1.Y + 1f, 1);
        m.MouseMove(110f, l1.Y + ih * 3f + 1f);
        E(m.RowDrag && m.DropLevel == 1, "drill: a row drags within its own list");
        m.MouseMove(110f, 5f);
        E(m.DropLevel == -1, "drill: nowhere else is a target");
        m.MouseUp();
        var dst = CascadeStyle.Default;
        m.Draw(b2 ??= new VertexBatch(atlas.SolidU, atlas.SolidV), atlas, 15f, in dst);
        E(m.Icons.Length == 0, "drill: the hidden root's icon is not reported");
        E(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Depth == 1 && m.Key(KeyEscape).Kind == CascadeActionKind.CloseAll, "drill: Left goes up, Esc at the root closes");
        m.Drill = false;

        // Icons: reported for rows with icons only, inside the row's square; draw allocates nothing.
        var b = b2 ?? new VertexBatch(atlas.SolidU, atlas.SolidV);
        var st = CascadeStyle.Default;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.Draw(b, atlas, 15f, in st);
        E(m.Icons.Length == 1 && m.Icons[0].Resref == "ir_x" && m.Icons[0].X == l0.X + IconInset && m.Icons[0].Y == m.RowTop(0, 1) + IconInset
          && m.Icons[0].Size == ih - 2f * IconInset && m.Icons[0].Alpha == 1f, "one icon rect, on row 1, inset in its square");
        // Frame: the bg quad first, then four 1 px strips outside it -- nothing opaque paints under a translucent bg.
        float[] fv = b.ToArray();
        const int QuadFloats = 6 * VertexBatch.Floats;
        bool strips = fv[0] == l0.X && fv[1] == l0.Y && fv[7] == st.Bg.a;
        for (int q = 1; q <= 4 && strips; q++)
        {
            float qx0 = fv[q * QuadFloats], qy0 = fv[q * QuadFloats + 1], qx1 = fv[q * QuadFloats + 2 * VertexBatch.Floats], qy1 = fv[q * QuadFloats + 2 * VertexBatch.Floats + 1];
            strips = (qx1 - qx0 == 1f || qy1 - qy0 == 1f) && (qx1 <= l0.X || qx0 >= l0.X + l0.W || qy1 <= l0.Y || qy0 >= l0.Y + l0.H);
        }
        E(strips, "the frame is four strips outside the bg, drawn after it");
        m.OpenChild(0, 2, child); m.OpenChild(1, 0, grand);
        m.MouseMove(m.Level(2).X + 10f, m.Level(2).Y + 1f);
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(m.Icons.Length == 3 && m.Icons[1].Resref == "ir_g" && m.Icons[1].Handle == IntPtr.Zero && !m.Level(1).HasIcons, "icon rects across levels; a level without icons has no column");
        E(m.Icons[2].Handle == (IntPtr)0x7f01 && m.Icons[2].Resref == "" && m.Icons[2].Y == m.RowTop(2, 1) + IconInset, "a handle-only row is an icon row and its quad carries the handle");
        E(b.VertexCount > 0, "the draw emitted geometry");
        var wide = new[] { new CascadeItem("leaf", count: new string('x', 200)) };
        m.OpenAt(wide, 100f, 100f, atlas, vpW, vpH);
        b.Reset(atlas.SolidU, atlas.SolidV); m.Draw(b, atlas, 15f, in st);
        int withCount = b.VertexCount;
        m.OpenAt(new[] { new CascadeItem("leaf") }, 100f, 100f, atlas, vpW, vpH);
        b.Reset(atlas.SolidU, atlas.SolidV); m.Draw(b, atlas, 15f, in st);
        E(withCount == b.VertexCount, $"a count wider than the room beside the label is dropped, the label still draws ({withCount} vs {b.VertexCount}, w={m.Level(0).W})");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); m.Draw(b, atlas, 15f, in st); m.Tick(7000 + i); }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        E(delta == 0, $"100 three-level draws + ticks allocate nothing ({delta} bytes)");
        m.Close();
        E(!m.Open && m.Icons.Length == 0 && !m.Contains(110f, 110f), "closed: nothing reported, nothing hit");

        // Open animation (CX-694): a level stamps its first draw, fades/slides over AnimMs (the icon quads carry the
        // eased alpha and offset, the hit rect does not move), a re-layout of an open level keeps its stamp.
        st.AnimMs = 100; st.NowMs = 5000;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        E(l0.OpenedMs == -1, "unstamped before the first draw");
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(l0.OpenedMs == 5000 && m.Icons.Length == 1 && m.Icons[0].Alpha == 0f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset - SlidePx, "the first draw stamps the level: alpha 0, slid up by SlidePx");
        E(m.ItemAt(0, 110f, m.RowTop(0, 1) + 1f) == 1, "the hit rect does not slide");
        st.NowMs = 5050; b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(m.Icons[0].Alpha > 0.7f && m.Icons[0].Alpha < 0.8f && m.Icons[0].Y > m.RowTop(0, 1) + IconInset - SlidePx && m.Icons[0].Y < m.RowTop(0, 1) + IconInset, "half-way: eased-out alpha, part-way slid");
        st.NowMs = 5100; b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(m.Icons[0].Alpha == 1f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset, "at AnimMs: settled");
        m.OpenChild(0, 2, child);
        E(l1.OpenedMs == -1, "a new child is unstamped");
        st.NowMs = 5200; b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(l1.OpenedMs == 5200 && l0.OpenedMs == 5000, "the child stamps at its own first draw, the root keeps its stamp");
        m.OpenChild(0, 2, child);
        E(l1.OpenedMs == 5200, "re-opening the same child in place keeps its stamp");
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        E(l0.OpenedMs == 5000, "re-laying the open root keeps its stamp");
        m.OpenChild(0, 7, child);
        E(l1.OpenedMs == -1, "a sibling child is a fresh open");
        st.AnimMs = 0; m.Close(); m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        E(m.Icons[0].Alpha == 1f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset, "AnimMs 0 draws settled at once");
        m.Close();

        log.Info(ok ? "CascadeMenu.SelfCheck: passed" : "CascadeMenu.SelfCheck: FAILED");
    }
}
