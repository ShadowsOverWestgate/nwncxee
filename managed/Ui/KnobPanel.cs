using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;
using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>
/// The settings panel (docs/spec/unified-settings.md §3, #588): one centred window of three independently
/// scrolling columns -- categories, the chosen category's submenus (one <see cref="KnobGroup"/> each,
/// declaration order), the chosen submenu's rows under their section headers -- with a header (title, close).
/// A row is the knob's label with its help wrapped beneath and the control right-aligned: toggle, slider with
/// an inline value box that applies per motion, dropdown, text, swatch, or an action button whose press runs
/// <see cref="KnobButton.Press"/>. Every edit is a registry set (instant apply; <see cref="KnobFile"/>
/// debounces the write). Pure: pixels and keys in, sets and geometry out, no native refs, so it self-checks
/// off-device. Layout runs cold on the input thread and publishes an immutable row list; <see cref="Emit"/>
/// walks it every frame and allocates nothing -- a row's value text is cached and re-formatted only when its
/// knob moved.
/// <para>Semantics: the master is the boxed first row; an off master -- the group's own, or any ancestor's
/// through <c>parent:</c> -- greys every value row to 42 % and makes it inert, while action and link rows stay
/// live; an off ancestor also lays a warning strip above the rows. Advanced rows sit behind a collapsed
/// disclosure, compact, with the <c>(?)</c> marker and a <see cref="Tooltip"/> where they have help. A blue
/// dot marks an off-default row and a submenu holding one; <c>Reset to defaults</c> in the column head is
/// <see cref="KnobGroup.ResetRows"/>. A Restart row wears the ↻ badge, amber once its value left the boot one.
/// The status line and the header note lead the rows; a link row copies on press and flashes "copied"; a
/// confirming button fires on its second press within <see cref="KnobButton.ConfirmSec"/>; a swatch expands to
/// R/G/B/A channel rows in place (#589).</para>
/// <para>Faces: everything is measured in <see cref="Em"/>, the panel's own body face height (design
/// <see cref="FacePx"/> × the game's GUI scale), never <c>ui.font</c>; help, section and path text use the
/// <see cref="SmallPx"/> face of the same scale.</para>
/// <para>Row source: by default the registry (the three columns above). A consumer that sets <see cref="Source"/>
/// supplies its own <see cref="Categories"/> and, per relayout, its own rows through <see cref="Publish"/> --
/// rows with no <see cref="Row.K"/>, carrying <see cref="Row.Act"/> + extras the consumer alone interprets --
/// and takes the edits back through <see cref="RowPressed"/>, <see cref="ApplyValue"/> and
/// <see cref="CommitLabel"/>; <see cref="SubFrac"/> 0 drops the submenu column. The chat window's per-window
/// settings are that consumer (CX-696).</para>
/// </summary>
public sealed partial class KnobPanel
{
    public static readonly string[] KnobCategories = { "General", "UI", "Graphics", "Camera", "Hotkeys" };
    public const string ResetText = "Reset to defaults";
    public const string AdvancedClosed = "▸ Advanced", AdvancedOpen = "▾ Advanced";
    public const string BadgeText = "↻ restart";
    public const string ConfirmText = "Click again to confirm";
    public const string CopiedText = "copied";
    /// <summary>Design face heights before the GUI scale: the body face, and the small face help / sections / the
    /// submenu path draw in (the prototype's 0.82 em).</summary>
    public const float FacePx = 20f, SmallPx = 16f;
    /// <summary>Window size as a fraction of the viewport, and the nav column widths as fractions of it;
    /// <see cref="SubFrac"/> 0 = no submenu column (a consumer's two-column panel).</summary>
    public float WFrac = 0.70f, HFrac = 0.75f, CatFrac = 0.18f, SubFrac = 0.22f;
    /// <summary>Rows one wheel notch scrolls a column by, in em.</summary>
    public const float WheelEm = 3.3f;
    /// <summary>Opacity of a value row under an off master.</summary>
    public const float OffAlpha = 0.42f;
    /// <summary>How long a link row shows "copied" after a press.</summary>
    public const float CopiedSec = 1.5f;

    /// <summary>Time source for the confirm window and the copied flash; the self-check substitutes a fake clock.
    /// Stopwatch ticks.</summary>
    internal static Func<long> Clock = Stopwatch.GetTimestamp;
    /// <summary>The clipboard: SDL on both platforms through <see cref="SelectableText.Copy"/>; the self-check
    /// substitutes a recorder.</summary>
    internal static Func<string, bool> CopyText = SelectableText.Copy;
    /// <summary>A group's parent master; the self-check's throwaway groups are not in the registry, so it
    /// resolves them itself.</summary>
    internal Func<KnobGroup, Knob<bool>?> ResolveParent = g => g.Parent;

    // ---- rows ----
    public enum RowKind { Note, Section, Knob, Status, Strip, Advanced, Channel }

    /// <summary>One laid-out line of the knob column: a section header, a note (the group's header text), the
    /// status line, the parent-off strip, the Advanced disclosure, a control row (a knob's, or a consumer's
    /// with <see cref="K"/> null), or one channel of an expanded swatch. Geometry is in list-content px (the
    /// column's scroll offset and origin are added at draw and hit-test); the value state is a cache the draw
    /// thread refreshes when the knob moved, or the consumer's own draw state.</summary>
    public sealed class Row
    {
        public RowKind Kind;
        public Knob? K;
        /// <summary>The control a <see cref="RowKind.Knob"/> / <see cref="RowKind.Channel"/> row draws and
        /// hit-tests: the knob's kind, or what the consumer set.</summary>
        public KnobKind Ctl;
        /// <summary>The label (a knob's, or the consumer's), section / note text, a button's caption, or a channel's letter.</summary>
        public string Text = "";
        /// <summary>The label word-wrapped to the label column at layout (a button carries its caption in
        /// <see cref="Text"/> instead).</summary>
        public string[] Label = Array.Empty<string>();
        /// <summary>Help (or a status / strip / link address), word-wrapped at layout.</summary>
        public string[] Help = Array.Empty<string>();
        /// <summary>A consumer row's help before wrapping; "" = none.</summary>
        public string HelpText = "";
        // A consumer row's identity + extras: the panel never interprets them beyond Min/Max/Choices.
        public SetAct Act;
        public string Key = "";
        public uint Bit;
        public float Min, Max;
        public string[]? Choices;
        public float Top, H;
        /// <summary>Control width, laid out once so the label clip is known.</summary>
        public float CtlW;
        /// <summary>The boxed master row.</summary>
        public bool Master;
        /// <summary>An Advanced-tier row: compact, its help behind the marker.</summary>
        public bool Adv;
        /// <summary>The <c>(?)</c> marker (an Advanced row with help) and the ↻ badge (a Restart row).</summary>
        public bool HasMark, Restart;
        /// <summary>Width reserved after the label's first line for the marker and the badge.</summary>
        public float ExtraW;
        /// <summary>A swatch channel row: 0 R, 1 G, 2 B, 3 A; -1 otherwise.</summary>
        public int Channel = -1;
        // The value cache: what the draw shows without formatting anything per frame.
        public string Value = "";
        public int Selected = -1;
        public float Norm;
        public bool On;
        public uint Rgba;
        public byte Byte;
        public bool OffDefault, Moved;
        private bool _seeded;
        private bool _cb; private int _ci; private float _cf; private string? _cs; private uint _cu;
        /// <summary>Set by a panel edit of a knob whose T the cache cannot compare (a Custom knob).</summary>
        public bool Dirty;

        /// <summary>Bring the cache up to the knob's live value. Alloc-free unless the value moved (then one Format).</summary>
        public void Refresh()
        {
            if (K == null) return;
            switch (K)
            {
                case Knob<bool> kb:
                    if (_seeded && kb.V == _cb && !Dirty) return;
                    _cb = kb.V; On = kb.V; break;
                case Knob<float> kf:
                    if (_seeded && kf.V == _cf && !Dirty) return;
                    _cf = kf.V; Norm = kf.Max > kf.Min ? (kf.V - kf.Min) / (kf.Max - kf.Min) : 0f; break;
                case Knob<int> ki:
                    if (_seeded && ki.V == _ci && !Dirty) return;
                    _ci = ki.V; Norm = ki.Max > ki.Min ? (ki.V - ki.Min) / (float)(ki.Max - ki.Min) : 0f; break;
                case Knob<string> ks:
                    if (_seeded && ReferenceEquals(ks.V, _cs) && !Dirty) return;
                    _cs = ks.V; break;
                case Knob<uint> ku:
                    if (_seeded && ku.V == _cu && !Dirty) return;
                    _cu = ku.V; Rgba = ku.V; break;
                default:
                    if (_seeded && !Dirty) return;
                    break;
            }
            _seeded = true; Dirty = false;
            OffDefault = !K.IsDefault; Moved = K.BootMoved;
            if (Channel >= 0)
            {
                Byte = (byte)(Rgba >> (8 * (3 - Channel)));
                Value = ByteText[Byte]; Norm = Byte / 255f; Selected = -1;
                return;
            }
            Value = K.Format();
            Selected = K.ChoiceIndex;
        }
    }

    /// <summary>Every channel value's text, so a channel row never formats at draw.</summary>
    private static readonly string[] ByteText = BuildByteText();
    private static string[] BuildByteText()
    {
        var t = new string[256];
        for (int i = 0; i < 256; i++) t[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return t;
    }
    private static readonly string[] ChannelNames = { "R", "G", "B", "A" };

    // ---- state ----
    public bool Open { get; private set; }
    public float X, Y, W, H;
    /// <summary>The viewport the panel was laid out for.</summary>
    public float VpW, VpH;
    /// <summary>The body face height in px: every metric is a multiple of it.</summary>
    public float Em { get; private set; } = FacePx;
    /// <summary>The faces the layout measured with; the host re-fetches them by atlas epoch.</summary>
    public GlyphAtlas? Body, Small;
    /// <summary>Title-bar caption.</summary>
    public string Title = "CX settings";
    /// <summary>A button in the header, left of the close ×; "" = none. Its press runs <see cref="HeaderActionPressed"/>.</summary>
    public string HeaderAction = "";
    public Action? HeaderActionPressed;
    /// <summary>A modal panel owns the press that opened it and the off-panel press that closes it (the host's
    /// release bookkeeping fires); the main panel leaves an off-panel press to whatever is under it.</summary>
    public bool Modal;

    /// <summary>The category column's entries: the registry's, or the consumer's.</summary>
    public string[] Categories = KnobCategories;
    public int Category;
    public int Sub = -1;
    /// <summary>The Advanced disclosure; closes when the submenu changes.</summary>
    public bool AdvOpen;
    private KnobGroup[] _subs = Array.Empty<KnobGroup>();
    /// <summary>The laid-out rows; the draw walks these, hit-testing reads them. A layout swaps in a fresh list
    /// (never mutates in place), so a reader on another thread iterates a stable snapshot.</summary>
    private volatile List<Row> _rows = new();
    private string _khTitle = "", _khPath = "", _statusText = "";
    private Knob<bool>? _own;
    private Knob<bool>[] _ancestors = Array.Empty<Knob<bool>>();
    private bool _stripLaid, _resettable;
    private Knob? _expanded;
    private Row? _confirm; private long _confirmAt;
    private Row? _copied; private long _copiedAt;
    public IReadOnlyList<KnobGroup> Subs => _subs;
    public List<Row> Rows => _rows;

    // ---- consumer contract (rows from a Source instead of the registry) ----

    /// <summary>Rebuild the rows from the consumer's model, ending in <see cref="Publish"/>. Set, it replaces
    /// the registry layout: after every category press and row edit so the change shows next frame.</summary>
    public Action? Source;
    /// <summary>A category entry was pressed. The consumer sets <see cref="Category"/> (unset: the pressed
    /// index) and mutates its model; the panel then re-lays out.</summary>
    public Action<int>? CategoryPicked;
    /// <summary>A press landed on a consumer row that is not a slider or a text box (a toggle, a swatch, a
    /// button's box, or a dropdown after its pick with <see cref="Row.Selected"/> set): the consumer mutates
    /// its model.</summary>
    public Action<Row>? RowPressed;
    /// <summary>Write a raw value for a consumer slider / channel row (in the row's Min..Max units) onto the
    /// consumer's model, and mirror it back onto the row's <see cref="Row.Norm"/>.</summary>
    public Action<Row, float>? ApplyValue;
    /// <summary>Commit a consumer text row's edit (never empty; the consumer decides what it means).</summary>
    public Action<Row, string>? CommitLabel;

    public readonly ScrollColumn Cats = new(), SubCol = new(), List = new();
    public readonly Dropdown Drop = new();
    public readonly Tooltip Tip = new();

    private bool _moving; private float _mgx, _mgy;
    private Row? _grab;
    private Row? _editRow; private string _editText = "";
    private int _hoverCat = -1, _hoverSub = -1;
    private Row? _hoverRow;
    private float _lastMx, _lastMy;
    private bool _pressed;

    // ---- em metrics ----
    private float HeaderH => 2.7f * Em;
    private float Pad => 1.2f * Em;
    private float NavPadX => 1f * Em;
    private float NavH => BodyLh + 1.2f * Em;
    /// <summary>The row column's head: the title line, plus the path line when there are submenus.</summary>
    private float KHeadH => BodyLh + (SubFrac > 0f ? SmallLh : 0f) + 1.2f * Em;
    private float RowMinH => 3.3f * Em;
    /// <summary>A row without help lines (or an Advanced row) is compact.</summary>
    private float RowPlainH => 2.4f * Em;
    private float ChanH => 2f * Em;
    private float RowPadY => 0.4f * Em;
    private float SecH => SmallLh + 1.3f * Em;
    private float BodyLh => Body?.LineHeight ?? Em * 1.2f;
    private float SmallLh => Small?.LineHeight ?? Em;
    private float CloseW => 1.7f * Em;
    private float HeaderBtnH => 1.7f * Em;
    private float HeaderBtnW => (Body != null ? UiText.Measure(Body, HeaderAction) : 6f * Em) + 1.8f * Em;
    private float ToggleW => 2.6f * Em;
    private float ToggleH => 1.3f * Em;
    private float TrackW => 8f * Em;
    private float ValueW => 3.6f * Em;
    private float UnitW => 1.4f * Em;
    private float TextW => 11f * Em;
    private float SwatchW => 2.4f * Em;
    private float HexW => 5f * Em;
    private float CtlH => 1.6f * Em;
    private float Gap => 0.6f * Em;
    private float MasterPadX => 0.9f * Em;
    private float MasterTop => 0.3f * Em;
    private float MasterBottom => 0.6f * Em;
    private float DotR => 0.2f * Em;
    private float ChanIndent => 1.4f * Em;
    private float BadgeW => (Small != null ? UiText.Measure(Small, BadgeText) : 4f * Em) + 0.9f * Em;
    private float ResetW => (Small != null ? UiText.Measure(Small, ResetText) : 7f * Em) + 1.4f * Em;

    // ---- open / close / place ----

    /// <summary>Open centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport with the faces the host
    /// baked at <paramref name="em"/>. Keeps the last category / submenu selection.</summary>
    public void OpenAt(float vpW, float vpH, float em, GlyphAtlas? body, GlyphAtlas? small)
    {
        Open = true; _pressed = Modal; _grab = null; _moving = false; _editRow = null; _editText = "";
        _confirm = null; _copied = null;
        Drop.Close(); Tip.Leave();
        Measure(vpW, vpH, em, body, small);
        X = MathF.Round((vpW - W) / 2f); Y = MathF.Round((vpH - H) / 2f);
        Place();
        Relayout();
    }

    /// <summary>Re-measure for a new viewport or face (the host calls it when either changed while open).</summary>
    public void Rebase(float vpW, float vpH, float em, GlyphAtlas? body, GlyphAtlas? small)
    {
        Measure(vpW, vpH, em, body, small);
        X = Window.ClampAxis(X, W, vpW); Y = Window.ClampAxis(Y, H, vpH);
        Place();
        Relayout();
    }

    private void Measure(float vpW, float vpH, float em, GlyphAtlas? body, GlyphAtlas? small)
    {
        VpW = vpW; VpH = vpH; Em = em; Body = body; Small = small;
        W = MathF.Round(vpW * WFrac); H = MathF.Round(vpH * HFrac);
    }

    public void Close()
    {
        Open = false; _pressed = false; _grab = null; _moving = false; _editRow = null; _editText = "";
        _confirm = null; _copied = null; _hoverRow = null;
        Drop.Close(); Tip.Leave(); Cats.MouseUp(); SubCol.MouseUp(); List.MouseUp();
    }

    /// <summary>Seat the columns under the header for the current X/Y.</summary>
    private void Place()
    {
        float top = Y + HeaderH, h = H - HeaderH;
        float cw = MathF.Round(W * CatFrac), sw = SubFrac > 0f ? MathF.Round(W * SubFrac) : 0f;
        Cats.SetRect(X, top, cw, h);
        SubCol.SetRect(X + cw, top, sw, h);
        List.SetRect(X + cw + sw, top + KHeadH, W - cw - sw, h - KHeadH);
    }

    public bool Contains(float mx, float my)
        => Open && ((mx >= X && mx < X + W && my >= Y && my < Y + H) || Drop.Contains(mx, my));

    // ---- layout (cold) ----

    /// <summary>Rebuild the submenu list for the category from the registry, then the rows -- or, with a
    /// <see cref="Source"/>, the consumer's rows.</summary>
    public void Relayout()
    {
        if (Source != null) { Source(); return; }
        var subs = new List<KnobGroup>();
        foreach (KnobGroup g in KnobGroup.All)
            if (string.Equals(g.Category, Categories[Category], StringComparison.OrdinalIgnoreCase)) subs.Add(g);
        _subs = subs.ToArray();
        LayoutRows();
    }

    /// <summary>A submenu change: the disclosure closes and the expanded swatch folds.</summary>
    private void SelectSub(int s)
    {
        Sub = s; AdvOpen = false; _expanded = null; List.Reset();
    }

    /// <summary>Lay out the chosen submenu's rows for the current geometry and faces, then publish the list:
    /// the strip when an ancestor master is off, the header note, the status line, the User rows (master boxed
    /// first) with their sections and any expanded swatch's channels, then the Advanced disclosure and, open,
    /// the Advanced rows with their own sections.</summary>
    internal void LayoutRows()
    {
        if (Source != null) { Source(); return; }
        if (Sub >= _subs.Length) Sub = _subs.Length - 1;
        if (Sub < 0 && _subs.Length > 0) Sub = 0;
        Cats.ContentH = 0.4f * Em + Categories.Length * NavH;
        SubCol.ContentH = 0.4f * Em + _subs.Length * NavH;
        Cats.Clamp(); SubCol.Clamp();

        var rows = new List<Row>();
        float y = 0.4f * Em;
        float noteW = ListInnerW();
        KnobGroup? g0 = Sub >= 0 ? _subs[Sub] : null;
        _khTitle = g0?.Submenu ?? Categories[Category];
        _khPath = g0 == null ? "" : Categories[Category] + " » " + g0.Submenu;
        _own = g0?.Master; _resettable = false; _statusText = ""; _stripLaid = false;
        _ancestors = Array.Empty<Knob<bool>>();
        if (g0 == null)
            AddNote(rows, ref y, "Nothing here yet.", noteW);
        else
        {
            var anc = new List<Knob<bool>>();
            for (Knob<bool>? p = ResolveParent(g0); p != null && anc.Count < 8; p = ResolveParent(p.Group)) anc.Add(p);
            _ancestors = anc.ToArray();
            Knob<bool>? off = FirstOffAncestor();
            if (off != null)
            {
                _stripLaid = true;
                string restart = off.Effect == Effect.Restart ? " (restart)" : "";
                AddLines(rows, ref y, RowKind.Strip,
                         $"{off.Label} is off — these settings apply once it is enabled in {off.Group.Category} » {off.Group.Submenu}{restart}.",
                         noteW - 1.6f * Em, 0.6f * Em + 1f * Em);
            }
            if (g0.Header.Length > 0) AddNote(rows, ref y, g0.Header, noteW);
            _statusText = g0.Status?.Invoke() ?? "";
            if (_statusText.Length > 0) AddLines(rows, ref y, RowKind.Status, _statusText, noteW - 0.6f * Em, 0.8f * Em);
            string section = "";
            int adv = 0;
            foreach (Knob k in g0.Knobs)
            {
                if (k.Tier == Tier.Advanced) { adv++; continue; }
                if (k.Tier != Tier.User) continue;
                if (k.Kind != KnobKind.Button && k.Kind != KnobKind.Link) _resettable = true;
                AddSection(rows, ref y, k, ref section);
                AddKnobRow(rows, ref y, k, k == g0.Master, false);
            }
            if (adv > 0)
            {
                _resettable = true;
                rows.Add(new Row { Kind = RowKind.Advanced, Text = AdvOpen ? AdvancedOpen : AdvancedClosed, Top = y, H = 0.8f * Em + 1.2f * Em + SmallLh });
                y += rows[rows.Count - 1].H;
                if (AdvOpen)
                {
                    section = "";
                    foreach (Knob k in g0.Knobs)
                    {
                        if (k.Tier != Tier.Advanced) continue;
                        AddSection(rows, ref y, k, ref section);
                        AddKnobRow(rows, ref y, k, false, true);
                    }
                }
            }
        }
        Publish(rows, y);
    }

    /// <summary>Lay a consumer's rows out top to bottom for the current faces (control widths, wrapped label
    /// and help, row heights, a slider's value text) and publish the list; the consumer's <see cref="Source"/>
    /// ends here. A <see cref="RowKind.Section"/> row is a header; a <see cref="RowKind.Channel"/> row is a
    /// compact 0..255 slider; every other row is a control of its <see cref="Row.Ctl"/>.</summary>
    public void Publish(List<Row> rows)
    {
        Cats.ContentH = 0.4f * Em + Categories.Length * NavH;
        Cats.Clamp();
        _khTitle = (uint)Category < (uint)Categories.Length ? Categories[Category] : ""; _khPath = "";
        float y = 0.4f * Em;
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            r.Top = y;
            if (r.Kind == RowKind.Section) r.H = SecH;
            else if (r.Kind == RowKind.Channel) { r.Ctl = KnobKind.Slider; r.H = ChanH; r.CtlW = TrackW + Gap + ValueW; r.Value = FormatValue(r); }
            else
            {
                r.Kind = RowKind.Knob;
                r.CtlW = ControlW(r);
                float labelW = LabelW(r);
                if (r.Ctl != KnobKind.Button) r.Label = Wrap(Body, r.Text, labelW);
                r.Help = r.HelpText.Length > 0 ? Wrap(Small, r.HelpText, labelW) : Array.Empty<string>();
                r.H = RowH(r, false);
                if (r.Ctl == KnobKind.Slider) r.Value = FormatValue(r);
            }
            y += r.H;
        }
        Publish(rows, y);
    }

    /// <summary>Swap the laid-out list in, dropping every half-owned row interaction.</summary>
    private void Publish(List<Row> rows, float contentY)
    {
        List.ContentH = contentY + 1.5f * Em;
        List.Clamp();
        _rows = rows;
        _grab = null; _editRow = null; _hoverRow = null; _confirm = null;
        Tip.Leave();
    }

    /// <summary>A control row's height: its label and help lines with padding, at least the plain or the
    /// helped minimum (an Advanced row is always compact).</summary>
    private float RowH(Row r, bool adv)
    {
        float h = 2f * RowPadY + (r.Label.Length > 0 ? r.Label.Length : 1) * BodyLh
                  + (r.Help.Length > 0 ? 0.1f * Em + r.Help.Length * SmallLh : 0f);
        float min = adv || r.Help.Length == 0 ? RowPlainH : RowMinH;
        return h < min ? min : h;
    }

    /// <summary>A consumer slider / channel row's value-box text (a channel reads as a 0..255 integer).</summary>
    private static string FormatValue(Row r)
    {
        float v = r.Min + r.Norm * (r.Max - r.Min);
        return r.Channel >= 0
            ? ((int)(v + 0.5f)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void AddSection(List<Row> rows, ref float y, Knob k, ref string section)
    {
        if (k.Section == section) return;
        section = k.Section;
        if (section.Length == 0) return;
        rows.Add(new Row { Kind = RowKind.Section, Text = section.ToUpperInvariant(), Top = y, H = SecH });
        y += SecH;
    }

    private void AddKnobRow(List<Row> rows, ref float y, Knob k, bool master, bool adv)
    {
        var r = new Row { Kind = RowKind.Knob, K = k, Ctl = k.Kind, Text = k.Label, Top = y, Master = master, Adv = adv };
        r.HasMark = adv && k.Help.Length > 0;
        r.Restart = k.Effect == Effect.Restart && k.Kind != KnobKind.Button && k.Kind != KnobKind.Link;
        r.CtlW = ControlW(r);
        r.ExtraW = (r.HasMark && Small != null ? 0.4f * Em + Tooltip.MarkerW(Small) : 0f) + (r.Restart ? 0.5f * Em + BadgeW : 0f);
        float labelW = LabelW(r);
        if (k.Kind != KnobKind.Button) r.Label = Wrap(Body, k.Label, labelW);
        if (k is KnobLink link) r.Help = Wrap(Small, link.Url, labelW + r.ExtraW);
        else r.Help = !adv && k.Help.Length > 0 ? Wrap(Small, k.Help, labelW + r.ExtraW) : Array.Empty<string>();
        r.H = RowH(r, adv) + (master ? MasterTop + MasterBottom : 0f);
        r.Refresh();
        y += r.H;
        rows.Add(r);
        if (k.Kind == KnobKind.Swatch && ReferenceEquals(k, _expanded))
            for (int ch = 0; ch < 4; ch++)
            {
                var c = new Row { Kind = RowKind.Channel, K = k, Ctl = KnobKind.Slider, Channel = ch, Text = ChannelNames[ch], Top = y, H = ChanH, CtlW = TrackW + Gap + ValueW };
                c.Refresh();
                y += ChanH;
                rows.Add(c);
            }
    }

    /// <summary>The width a row's label wraps to: the column less the control, the gap, the master inset and the
    /// marker / badge reserve.</summary>
    internal float LabelW(Row r)
    {
        float w = ListInnerW() - r.CtlW - Gap - r.ExtraW - (r.Master ? 2f * MasterPadX : 0f);
        return w < 4f * Em ? 4f * Em : w;
    }

    private void AddNote(List<Row> rows, ref float y, string text, float w)
    {
        var r = new Row { Kind = RowKind.Note, Text = text, Top = y, Help = Wrap(Small, text, w) };
        r.H = 0.6f * Em + r.Help.Length * SmallLh;
        rows.Add(r);
        y += r.H;
    }

    /// <summary>A multi-line element (status, strip): each <c>\n</c> line wrapped in the small face, all lines
    /// in one row of <paramref name="chrome"/> extra height.</summary>
    private void AddLines(List<Row> rows, ref float y, RowKind kind, string text, float w, float chrome)
    {
        var lines = new List<string>();
        foreach (string line in text.Split('\n'))
            lines.AddRange(Wrap(Small, line.Length > 0 ? line : " ", w));
        var r = new Row { Kind = kind, Text = text, Top = y, Help = lines.ToArray() };
        r.H = chrome + r.Help.Length * SmallLh;
        rows.Add(r);
        y += r.H;
    }

    /// <summary>The knob column's inner width: side pads and the gutter off.</summary>
    private float ListInnerW() => List.W - 2f * Pad - ScrollColumn.BarW;

    /// <summary>Control width per kind, at layout: the label clip and the row's hit zones derive from it.</summary>
    private float ControlW(Row r)
    {
        Knob? k = r.K;
        switch (r.Ctl)
        {
            case KnobKind.Toggle: return ToggleW;
            case KnobKind.Slider:
                return TrackW + Gap + ValueW + (UnitOf(k).Length > 0 ? 0.3f * Em + UnitW : 0f);
            case KnobKind.Dropdown:
            {
                float w = 0f;
                string[] choices = ChoicesOf(r);
                if (Body != null) for (int i = 0; i < choices.Length; i++) { float cw = UiText.Measure(Body, choices[i]); if (cw > w) w = cw; }
                w += 2.6f * Em;
                float min = 9f * Em, max = 14f * Em;
                return w < min ? min : w > max ? max : w;
            }
            // A consumer swatch is the colour box alone; a knob's carries the hex field beside it.
            case KnobKind.Swatch: return k != null ? SwatchW + 0.4f * Em + HexW : SwatchW;
            case KnobKind.Button:
            {
                float w = Body != null ? UiText.Measure(Body, r.Text) : 6f * Em;
                if (k is KnobButton { Confirm: true } && Body != null) { float cw = UiText.Measure(Body, ConfirmText); if (cw > w) w = cw; }
                return w + 1.8f * Em;
            }
            case KnobKind.Link: return Small != null ? UiText.Measure(Small, CopiedText) + 0.6f * Em : 4f * Em;
            default: return TextW;
        }
    }

    private static string UnitOf(Knob? k) => k switch { Knob<float> f => f.Unit, Knob<int> i => i.Unit, _ => "" };

    /// <summary>A Dropdown row's labels: the knob's whatever its T (the typed field is on <see cref="Knob{T}"/>),
    /// or the consumer's array.</summary>
    private static string[] ChoicesOf(Row r) => r.K switch
    {
        Knob<int> i => i.Choices, Knob<float> f => f.Choices, Knob<string> s => s.Choices, Knob<bool> b => b.Choices,
        Knob<uint> u => u.Choices, null => r.Choices ?? Array.Empty<string>(), _ => Array.Empty<string>(),
    };

    /// <summary>Greedy word wrap in <paramref name="face"/> (cold: substrings are allocated once per layout).</summary>
    internal static string[] Wrap(GlyphAtlas? face, string s, float maxW)
    {
        if (face == null) return new[] { s };
        var lines = new List<string>();
        string[] words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float space = face.SpaceAdvance > 0f ? face.SpaceAdvance : UiText.Measure(face, " ");
        int start = 0; float lineW = 0f;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            float w = UiText.Measure(face, words[i]);
            if (i > start && lineW + space + w > maxW)
            {
                lines.Add(sb.ToString()); sb.Clear(); start = i; lineW = 0f;
            }
            if (sb.Length > 0) { sb.Append(' '); lineW += space; }
            sb.Append(words[i]); lineW += w;
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines.ToArray();
    }

    // ---- master state (read per frame: fields only) ----

    private Knob<bool>? FirstOffAncestor()
    {
        for (int i = 0; i < _ancestors.Length; i++) if (!_ancestors[i].V) return _ancestors[i];
        return null;
    }

    /// <summary>The group's value rows are inert: its own master or any ancestor's is off.</summary>
    public bool MasterOff => (_own != null && !_own.V) || FirstOffAncestor() != null;

    /// <summary>A row drawn at <see cref="OffAlpha"/> and ignoring presses: a <see cref="Knob.Disabled"/> row
    /// always, a button whose own <see cref="KnobButton.Enabled"/> says no, else a value row (never the master,
    /// an action or a link) while <see cref="MasterOff"/>.</summary>
    public bool Greyed(Row r)
    {
        if (r.Kind == RowKind.Channel) return MasterOff;
        if (r.Kind != RowKind.Knob || r.Master || r.K == null) return false;
        if (r.K.Disabled) return true;
        if (r.K.Kind == KnobKind.Button) return r.K is KnobButton { Enabled: { } en } && !en();
        if (r.K.Kind == KnobKind.Link) return false;
        return MasterOff;
    }

    /// <summary>A submenu holding an off-default User or Advanced row (the nav dot). Per frame: a field compare
    /// per knob, nothing allocated.</summary>
    public static bool SubDirty(KnobGroup g)
    {
        IReadOnlyList<Knob> ks = g.Knobs;
        for (int i = 0; i < ks.Count; i++)
        {
            Knob k = ks[i];
            if ((k.Tier == Tier.User || k.Tier == Tier.Advanced) && !k.IsDefault) return true;
        }
        return false;
    }

    private bool ConfirmArmed(Row r) => ReferenceEquals(_confirm, r) && Clock() - _confirmAt < (long)(KnobButton.ConfirmSec * Stopwatch.Frequency);
    private bool CopiedShown(Row r) => ReferenceEquals(_copied, r) && Clock() - _copiedAt < (long)(CopiedSec * Stopwatch.Frequency);

    // ---- geometry shared by draw and hit-test ----

    private float NavTop(ScrollColumn col, int i) => col.ToScreen(0.4f * Em + i * NavH);

    private int NavAt(ScrollColumn col, int count, float mx, float my)
    {
        if (!col.Contains(mx, my) || col.OverBar(mx, my)) return -1;
        int i = (int)((col.ToContent(my) - 0.4f * Em) / NavH);
        return i >= 0 && i < count ? i : -1;
    }

    /// <summary>Screen y a row's top is drawn at.</summary>
    public float RowTop(Row r) => List.ToScreen(r.Top);

    /// <summary>The row under the pointer, with its screen top.</summary>
    private Row? RowAt(float mx, float my, out float top)
    {
        top = 0f;
        if (!List.Contains(mx, my) || List.OverBar(mx, my)) return null;
        float cy = List.ToContent(my);
        var rows = _rows;
        for (int i = 0; i < rows.Count; i++)
            if (cy >= rows[i].Top && cy < rows[i].Top + rows[i].H) { top = RowTop(rows[i]); return rows[i]; }
        return null;
    }

    /// <summary>Left edge of a row's label: the master inset or the channel indent applied.</summary>
    private float RowLeft(Row r) => List.X + Pad + (r.Master ? MasterPadX : r.Kind == RowKind.Channel ? ChanIndent : 0f);

    /// <summary>Right edge of a row's control span and its vertical centre (the label's first line, or the
    /// channel row's middle).</summary>
    private void ControlAnchor(Row r, float top, out float right, out float cy)
    {
        right = List.X + List.W - Pad - ScrollColumn.BarW - (r.Master ? MasterPadX : 0f);
        cy = r.Kind == RowKind.Channel ? top + r.H * 0.5f : top + (r.Master ? MasterTop : 0f) + RowPadY + BodyLh * 0.5f;
    }

    internal void TrackRect(Row r, float top, out float x0, out float x1, out float cy)
    {
        ControlAnchor(r, top, out float right, out cy);
        x1 = right - r.CtlW + TrackW; x0 = right - r.CtlW;
    }

    internal void ValueRect(Row r, float top, out float x0, out float x1, out float y0, out float y1)
    {
        ControlAnchor(r, top, out float right, out float cy);
        x0 = right - r.CtlW + TrackW + Gap; x1 = x0 + ValueW;
        y0 = cy - CtlH * 0.5f; y1 = cy + CtlH * 0.5f;
    }

    /// <summary>The one box of a dropdown / text / button / swatch control (the right-aligned span; a knob
    /// swatch's is its hex field).</summary>
    internal void BoxRect(Row r, float top, out float x0, out float x1, out float y0, out float y1)
    {
        ControlAnchor(r, top, out float right, out float cy);
        x1 = right; x0 = right - r.CtlW;
        if (r.Ctl == KnobKind.Swatch && r.K != null) x0 = right - HexW;
        y0 = cy - CtlH * 0.5f; y1 = cy + CtlH * 0.5f;
    }

    /// <summary>The colour box left of a swatch's hex field: its press expands the channels.</summary>
    private void SwatchRect(Row r, float top, out float x0, out float x1, out float y0, out float y1)
    {
        ControlAnchor(r, top, out float right, out float cy);
        x1 = right - HexW - 0.4f * Em; x0 = x1 - SwatchW;
        y0 = cy - 0.7f * Em; y1 = cy + 0.7f * Em;
    }

    /// <summary>The <c>(?)</c> marker's left edge on an Advanced row (after the label's first line).</summary>
    private float MarkX(Row r)
        => RowLeft(r) + (Body != null && r.Label.Length > 0 ? UiText.Measure(Body, r.Label[0]) : 0f) + 0.4f * Em;

    private bool In(float mx, float my, float x0, float x1, float y0, float y1) => mx >= x0 && mx < x1 && my >= y0 && my < y1;

    private bool OverClose(float mx, float my)
        => mx >= X + W - Pad - CloseW && mx < X + W - Pad + 0.2f * Em && my >= Y && my < Y + HeaderH;

    /// <summary>The header button's box (right of the title, left of the ×).</summary>
    public void HeaderButtonRect(out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad - CloseW - 0.6f * Em; x0 = x1 - HeaderBtnW;
        float mid = Y + HeaderH * 0.5f;
        y0 = mid - HeaderBtnH * 0.5f; y1 = mid + HeaderBtnH * 0.5f;
    }

    private bool OverHeaderButton(float mx, float my)
    {
        if (HeaderAction.Length == 0) return false;
        HeaderButtonRect(out float x0, out float x1, out float y0, out float y1);
        return In(mx, my, x0, x1, y0, y1);
    }

    /// <summary>The Reset to defaults box, top-right of the knob column head.</summary>
    private void ResetRect(out float x0, out float x1, out float y0, out float y1)
    {
        x1 = List.X + List.W - Pad; x0 = x1 - ResetW;
        float mid = List.Y - KHeadH + 0.6f * Em + BodyLh * 0.5f;
        y0 = mid - 0.8f * Em; y1 = mid + 0.8f * Em;
    }

    private bool OverReset(float mx, float my)
    {
        if (!_resettable) return false;
        ResetRect(out float x0, out float x1, out float y0, out float y1);
        return In(mx, my, x0, x1, y0, y1);
    }

    // ---- input (cold) ----

    /// <summary>Press. Consumed when it landed on the panel (or its open list); a press elsewhere is not ours --
    /// unless <see cref="Modal"/>, where it closes the panel and is ours.</summary>
    public bool MouseDown(float mx, float my)
    {
        if (!Open) return false;
        if (Drop.Open)
        {
            Row? dr = Drop.Tag as Row;
            Drop.MouseDown(mx, my, out int picked);
            if (picked >= 0 && dr != null) PickChoice(dr, picked);
            _pressed = true;
            return true;
        }
        if (!Contains(mx, my))
        {
            if (!Modal) return false;
            Close(); _pressed = true;
            return true;
        }
        _pressed = true; _grab = null; _moving = false;
        Row? armed = _confirm; _confirm = null;
        Tip.Leave();
        if (_editRow != null) CommitEdit();
        if (OverClose(mx, my)) { Close(); return true; }
        if (OverHeaderButton(mx, my)) { HeaderActionPressed?.Invoke(); return true; }
        if (my < Y + HeaderH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        if (Cats.MouseDown(mx, my) || SubCol.MouseDown(mx, my) || List.MouseDown(mx, my)) return true;
        int c = NavAt(Cats, Categories.Length, mx, my);
        if (c >= 0)
        {
            if (CategoryPicked != null) CategoryPicked(c);
            else if (c == Category) return true;
            else Category = c;
            SelectSub(0); SubCol.Reset(); Relayout();
            return true;
        }
        int s = NavAt(SubCol, _subs.Length, mx, my);
        if (s >= 0) { if (s != Sub) { SelectSub(s); Relayout(); } return true; }
        KnobGroup? g0 = Sub >= 0 && Sub < _subs.Length ? _subs[Sub] : null;
        if (OverReset(mx, my)) { g0?.ResetRows(); RefreshStatus(g0); return true; }
        // A parent flipped from chat since the layout: the strip is stale, re-lay out before hit-testing.
        if (g0 != null && _stripLaid != (FirstOffAncestor() != null)) LayoutRows();
        Row? r = RowAt(mx, my, out float top);
        if (r == null) return true;
        if (r.Kind == RowKind.Advanced) { AdvOpen = !AdvOpen; LayoutRows(); return true; }
        if (r.Kind != RowKind.Knob && r.Kind != RowKind.Channel) return true;
        if (Greyed(r)) return true;
        if (r.Ctl == KnobKind.Slider) { SliderPress(r, top, mx, my); return true; }
        if (r.Ctl == KnobKind.Dropdown) { DropPress(r, top, mx, my); return true; }
        if (r.K == null) { ConsumerPress(r, top, mx, my); return true; }
        Knob k = r.K;
        switch (k.Kind)
        {
            case KnobKind.Toggle:
                if (k is Knob<bool> kb) kb.Set(!kb.V); else { k.Parse(r.On ? "0" : "1"); r.Dirty = true; }
                RefreshStatus(g0);
                break;
            case KnobKind.Text:
            {
                BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                if (In(mx, my, bx0, bx1, by0, by1)) BeginEdit(r, k.Format());
                break;
            }
            case KnobKind.Swatch:
            {
                BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                if (In(mx, my, bx0, bx1, by0, by1)) { BeginEdit(r, k.Format()); break; }
                SwatchRect(r, top, out float sx0, out float sx1, out float sy0, out float sy1);
                if (In(mx, my, sx0, sx1, sy0, sy1)) { _expanded = ReferenceEquals(_expanded, k) ? null : k; LayoutRows(); }
                break;
            }
            case KnobKind.Button:
            {
                BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                if (!In(mx, my, bx0, bx1, by0, by1) || k is not KnobButton kb2) break;
                if (kb2.Confirm && !(ReferenceEquals(armed, r) && Clock() - _confirmAt < (long)(KnobButton.ConfirmSec * Stopwatch.Frequency)))
                {
                    _confirm = r; _confirmAt = Clock();
                    break;
                }
                kb2.Press();
                RefreshStatus(g0);
                break;
            }
            case KnobKind.Link:
            {
                if (k is KnobLink link && CopyText(link.Url)) { _copied = r; _copiedAt = Clock(); }
                break;
            }
        }
        return true;
    }

    /// <summary>A press on a slider or channel row: the value box starts an edit, the track (with a margin)
    /// starts the drag and applies at once.</summary>
    private void SliderPress(Row r, float top, float mx, float my)
    {
        ValueRect(r, top, out float vx0, out float vx1, out float vy0, out float vy1);
        if (In(mx, my, vx0, vx1, vy0, vy1)) { BeginEdit(r, ""); return; }
        TrackRect(r, top, out float tx0, out float tx1, out float cy);
        if (mx >= tx0 - 0.4f * Em && mx < tx1 + 0.4f * Em && my >= cy - Em && my < cy + Em) { _grab = r; SetSliderFromX(r, mx); }
    }

    /// <summary>A press on a dropdown row: its box opens the list on the current choice.</summary>
    private void DropPress(Row r, float top, float mx, float my)
    {
        BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
        string[] choices = ChoicesOf(r);
        if (In(mx, my, bx0, bx1, by0, by1) && choices.Length > 0)
            Drop.OpenAt(choices, r.Selected, r, bx0, by0, by1, bx1 - bx0, CtlH, VpH, search: choices.Length > Dropdown.MaxVisible);
    }

    /// <summary>A press on a consumer row that is neither slider nor dropdown: a text box starts its edit, a
    /// button fires only from its box, anything else (toggle, swatch) anywhere -- then the consumer relays out.</summary>
    private void ConsumerPress(Row r, float top, float mx, float my)
    {
        if (r.Ctl == KnobKind.Text || r.Ctl == KnobKind.Button)
        {
            BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
            if (!In(mx, my, bx0, bx1, by0, by1)) return;
            if (r.Ctl == KnobKind.Text) { BeginEdit(r, r.Value); return; }
        }
        RowPressed?.Invoke(r);
        Relayout();
    }

    /// <summary>After a press that may have changed the group's status line: re-lay out when it did.</summary>
    private void RefreshStatus(KnobGroup? g0)
    {
        if (g0?.Status == null) return;
        if (g0.Status() != _statusText) LayoutRows();
    }

    /// <summary>Motion: a header drag moves the panel, a grabbed slider or thumb tracks, the open list hovers;
    /// otherwise the nav hover, the row hover and the marker hover for the tooltip. True when consumed (a drag)
    /// or something to redraw changed.</summary>
    public bool MouseMove(float mx, float my)
    {
        _lastMx = mx; _lastMy = my;
        if (!Open) return false;
        if (_moving)
        {
            X = Window.ClampAxis(mx - _mgx, W, VpW); Y = Window.ClampAxis(my - _mgy, H, VpH);
            Place();
            return true;
        }
        if (Drop.Open) return Drop.MouseMove(mx, my);
        if (Cats.MouseMove(mx, my) || SubCol.MouseMove(mx, my) || List.MouseMove(mx, my)) return true;
        if (_grab != null) { SetSliderFromX(_grab, mx); return true; }
        int hc = NavAt(Cats, Categories.Length, mx, my), hs = NavAt(SubCol, _subs.Length, mx, my);
        bool changed = hc != _hoverCat || hs != _hoverSub;
        _hoverCat = hc; _hoverSub = hs;
        Row? r = RowAt(mx, my, out float top);
        if (!ReferenceEquals(r, _hoverRow)) { _hoverRow = r; changed = true; }
        if (r != null && r.HasMark && Small != null && r.K != null)
        {
            float x0 = MarkX(r), x1 = x0 + Tooltip.MarkerW(Small);
            if (mx >= x0 && mx < x1) { Tip.Hover(r, r.K.Help, x0, top, top + r.H); return true; }
        }
        if (Tip.Leave()) changed = true;
        return changed && Contains(mx, my);
    }

    /// <summary>Wheel: the open list, else whichever column is under the last pointer position.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        if (Drop.Open) return Drop.MouseWheel(dy);
        float step = WheelEm * Em;
        if (Cats.Contains(_lastMx, _lastMy)) { Cats.MouseWheel(dy, step); return true; }
        if (SubCol.Contains(_lastMx, _lastMy)) { SubCol.MouseWheel(dy, step); return true; }
        if (List.Contains(_lastMx, _lastMy) || Contains(_lastMx, _lastMy)) { List.MouseWheel(dy, step); Tip.Leave(); return true; }
        return false;
    }

    /// <summary>Release: consumed iff the press was ours. Ends every drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressed;
        _pressed = false; _grab = null; _moving = false;
        Cats.MouseUp(); SubCol.MouseUp(); List.MouseUp(); Drop.MouseUp();
        return c;
    }

    public bool Moving => _moving;
    /// <summary>True while a slider is being dragged: a consumer's model changes per motion.</summary>
    public bool SliderDragging => _grab != null;
    public bool Dragging => _grab != null || _moving || Cats.Dragging || SubCol.Dragging || List.Dragging || Drop.Dragging;

    /// <summary>A pick from the open list: a consumer row takes the index and the consumer relays out; a knob's
    /// choice label round-trips through <see cref="Knob.Parse"/>, which takes a label for any T.</summary>
    private void PickChoice(Row r, int i)
    {
        string[] choices = ChoicesOf(r);
        if (i < 0 || i >= choices.Length) return;
        if (r.K == null) { r.Selected = i; RowPressed?.Invoke(r); Relayout(); return; }
        switch (r.K)
        {
            case Knob<int> ki: ki.Set(ki.Values[i]); break;
            case Knob<float> kf: kf.Set(kf.Values[i]); break;
            case Knob<string> ks: ks.Set(ks.Values[i]); break;
            case Knob<bool> kb: kb.Set(kb.Values[i]); break;
            case Knob<uint> ku: ku.Set(ku.Values[i]); break;
            default: r.K!.Parse(choices[i]); r.Dirty = true; break;
        }
    }

    /// <summary>The slider: pointer x to a value snapped to the knob's step, applied as a set per motion. A
    /// channel row sets its byte of the swatch; a consumer row goes through <see cref="ApplyValue"/>.</summary>
    private void SetSliderFromX(Row r, float mx)
    {
        TrackRect(r, RowTop(r), out float x0, out float x1, out _);
        float norm = x1 > x0 ? (mx - x0) / (x1 - x0) : 0f;
        norm = norm < 0f ? 0f : norm > 1f ? 1f : norm;
        if (r.K == null) { Apply(r, r.Min + norm * (r.Max - r.Min)); return; }
        if (r.Kind == RowKind.Channel) { SetChannel(r, (int)MathF.Round(norm * 255f)); return; }
        switch (r.K)
        {
            case Knob<float> kf:
            {
                float v = kf.Min + norm * (kf.Max - kf.Min);
                if (kf.Step > 0f) v = kf.Min + MathF.Round((v - kf.Min) / kf.Step) * kf.Step;
                if (v != kf.V) kf.Set(v);
                break;
            }
            case Knob<int> ki:
            {
                int v = ki.Min + (int)MathF.Round(norm * (ki.Max - ki.Min));
                if (ki.Step > 1) v = ki.Min + (int)MathF.Round((v - ki.Min) / (float)ki.Step) * ki.Step;
                if (v != ki.V) ki.Set(v);
                break;
            }
        }
    }

    /// <summary>A consumer slider's value: the consumer writes it and mirrors Norm; the value text follows.</summary>
    private void Apply(Row r, float v)
    {
        ApplyValue?.Invoke(r, v);
        r.Value = FormatValue(r);
    }

    /// <summary>One byte of the swatch (0 R … 3 A) as a set of the whole colour.</summary>
    private static void SetChannel(Row r, int v)
    {
        if (r.K is not Knob<uint> ku || r.Channel < 0) return;
        v = v < 0 ? 0 : v > 255 ? 255 : v;
        int shift = 8 * (3 - r.Channel);
        uint next = (ku.V & ~(0xFFu << shift)) | ((uint)v << shift);
        if (next != ku.V) ku.Set(next);
    }

    // ---- text entry ----
    /// <summary>The panel wants typed text: a field edit, or an open searchable list (#681).</summary>
    public bool Editing => _editRow != null || Drop.Searching;
    public string EditText => _editText;
    public bool EditingRow(Row r) => ReferenceEquals(_editRow, r);

    private void BeginEdit(Row r, string seed) { _editRow = r; _editText = seed; _grab = null; }

    /// <summary>Typed text (finished UTF-8 from the host): appended, capped at 64 chars.</summary>
    public void EditInsert(string s)
    {
        if (Drop.Searching) { Drop.Type(s); return; }
        if (_editRow == null) return;
        foreach (char c in s)
            if (_editText.Length < 64 && c >= ' ' && c != '\n' && c != '\r') _editText += c;
    }

    public void EditBackspace()
    {
        if (Drop.Searching) { Drop.Backspace(); return; }
        if (_editRow != null && _editText.Length > 0) _editText = _editText.Substring(0, _editText.Length - 1);
    }

    /// <summary>Commit: a searching list accepts its top match; a consumer text row hands the text to
    /// <see cref="CommitLabel"/> and a consumer slider parses it for <see cref="ApplyValue"/>; a knob's text
    /// goes through its own Parse (a slider clamps, a swatch wants rrggbbaa; a channel row takes 0–255).
    /// Empty or unparseable text keeps the old value.</summary>
    public void CommitEdit()
    {
        if (Drop.Searching)
        {
            Row? dr = Drop.Tag as Row;
            int i = Drop.Accept();
            if (i >= 0 && dr != null) PickChoice(dr, i);
            return;
        }
        Row? r = _editRow; string t = _editText;
        _editRow = null; _editText = "";
        if (r == null || t.Length == 0) return;
        if (r.K == null)
        {
            if (r.Ctl == KnobKind.Text) CommitLabel?.Invoke(r, t);
            else if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) Apply(r, v);
            return;
        }
        if (r.Kind == RowKind.Channel)
        {
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v)) SetChannel(r, v);
            return;
        }
        if (r.K.Parse(t)) r.Dirty = true;
    }

    /// <summary>Escape: backs out of an edit, else closes an open list.</summary>
    public void CancelEdit()
    {
        if (_editRow != null) { _editRow = null; _editText = ""; return; }
        if (Drop.Open) Drop.Close();
    }

    /// <summary>SDL keycodes the panel acts on.</summary>
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyKpEnter = 0x40000058, KeyBackspace = 0x08;

    /// <summary>A key while the panel owns the keyboard. Escape backs out of an edit, then the list, then the panel.
    /// Returns true when the panel is still open afterwards.</summary>
    public bool Key(uint sdlKey)
    {
        if (!Open) return false;
        switch (sdlKey)
        {
            case KeyEscape:
                if (_editRow != null || Drop.Open) CancelEdit(); else Close();
                break;
            case KeyReturn: case KeyKpEnter:
                CommitEdit();
                break;
            case KeyBackspace: EditBackspace(); break;
        }
        return Open;
    }

    // ---- draw (hot, zero-alloc) ----

    /// <summary>The off-default dot: a small filled square centred on (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    private void Dot(VertexBatch b, float cx, float cy, float a = 1f)
        => Fill(b, cx - DotR, cy - DotR, cx + DotR, cy + DotR, Accent2, a);

    /// <summary>Draw the whole panel into <paramref name="b"/>: chrome, the columns clipped to their boxes,
    /// their scrollbars, then the open dropdown list and the tooltip on top. Reads cached geometry and cached
    /// value text only.</summary>
    public void Emit(VertexBatch b)
    {
        if (!Open || Body == null || Small == null) return;
        GlyphAtlas body = Body, small = Small;
        float x = X, y = Y, w = W, h = H, em = Em;

        Fill(b, x, y, x + w, y + h, PanelBg, 0.96f);
        Outline(b, x, y, x + w, y + h, Border);
        // header: title, the close ×
        Fill(b, x + 1f, y + 1f, x + w - 1f, y + HeaderH, HeaderBg);
        Fill(b, x + 1f, y + HeaderH - 1f, x + w - 1f, y + HeaderH, Border);
        float hbase = y + (HeaderH - body.LineHeight) * 0.5f + body.Ascent;
        float closeX1 = x + w - Pad + 0.2f * em, closeX0 = closeX1 - CloseW;
        UiText.Draw(b, body, "×", closeX0 + (CloseW - UiText.Measure(body, "×")) * 0.5f, hbase, Grip, closeX1);
        float titleClip1 = closeX0 - 0.6f * em;
        if (HeaderAction.Length > 0)
        {
            HeaderButtonRect(out float hx0, out float hx1, out float hy0, out float hy1);
            Fill(b, hx0, hy0, hx1, hy1, NavOn);
            Outline(b, hx0, hy0, hx1, hy1, Frame2);
            UiText.Draw(b, body, HeaderAction, hx0 + 0.9f * em, hbase, Tx, hx1 - 0.4f * em);
            titleClip1 = hx0 - 0.6f * em;
        }
        UiText.Draw(b, body, Title, x + Pad, hbase, Tx, titleClip1);

        // categories
        Fill(b, Cats.X, Cats.Y, Cats.X + Cats.W, Cats.Y + Cats.H, CatBg);
        Fill(b, Cats.X + Cats.W - 1f, Cats.Y, Cats.X + Cats.W, Cats.Y + Cats.H, ColBorder);
        b.ClipY(Cats.Y, Cats.Y + Cats.H);
        for (int i = 0; i < Categories.Length; i++) Nav(b, body, Cats, i, Categories[i], i == Category, i == _hoverCat, false);
        b.ClearClip();
        Cats.Draw(b, C(Accent));

        // submenus, each with its dot when a row is off-default
        if (SubFrac > 0f)
        {
            Fill(b, SubCol.X, SubCol.Y, SubCol.X + SubCol.W, SubCol.Y + SubCol.H, SubBg);
            Fill(b, SubCol.X + SubCol.W - 1f, SubCol.Y, SubCol.X + SubCol.W, SubCol.Y + SubCol.H, ColBorder);
            b.ClipY(SubCol.Y, SubCol.Y + SubCol.H);
            var subs = _subs;
            for (int i = 0; i < subs.Length; i++) Nav(b, body, SubCol, i, subs[i].Submenu, i == Sub, i == _hoverSub, SubDirty(subs[i]));
            b.ClearClip();
            SubCol.Draw(b, C(Accent));
        }

        // knob column head: submenu title + its path, the reset box at the right
        float kx = List.X, khTop = List.Y - KHeadH;
        float titleClip = kx + List.W - Pad;
        if (_resettable)
        {
            ResetRect(out float rx0, out float rx1, out float ry0, out float ry1);
            Outline(b, rx0, ry0, rx1, ry1, Frame2);
            UiText.Draw(b, small, ResetText, rx0 + 0.7f * em, (ry0 + ry1) * 0.5f - small.LineHeight * 0.5f + small.Ascent, Tx2, rx1);
            titleClip = rx0 - 0.5f * em;
        }
        UiText.Draw(b, body, _khTitle, kx + Pad, khTop + 0.6f * em + body.Ascent, Tx, titleClip);
        if (_khPath.Length > 0) UiText.Draw(b, small, _khPath, kx + Pad, khTop + 0.6f * em + body.LineHeight + small.Ascent, Tx3, titleClip);
        Fill(b, kx, List.Y - 1f, kx + List.W, List.Y, ColBorder);

        // rows
        float clip0 = List.Y, clip1 = List.Y + List.H;
        float colRight = kx + List.W - Pad - ScrollColumn.BarW;
        b.ClipY(clip0, clip1);
        var rows = _rows;
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            float top = RowTop(r);
            if (top + r.H <= clip0 || top >= clip1) continue;
            switch (r.Kind)
            {
                case RowKind.Section:
                    UiText.Draw(b, small, r.Text, kx + Pad, top + 1f * em + small.Ascent, Tx3, colRight);
                    continue;
                case RowKind.Note:
                    for (int l = 0; l < r.Help.Length; l++)
                        UiText.Draw(b, small, r.Help[l], kx + Pad, top + 0.3f * em + l * small.LineHeight + small.Ascent, Tx2, colRight);
                    continue;
                case RowKind.Status:
                {
                    Fill(b, kx + Pad, top + 0.4f * em, colRight, top + r.H - 0.4f * em, 0xFFFFFF, 0.03f);
                    Fill(b, kx + Pad, top + 0.4f * em, kx + Pad + 3f, top + r.H - 0.4f * em, Accent2);
                    for (int l = 0; l < r.Help.Length; l++)
                        UiText.Draw(b, small, r.Help[l], kx + Pad + 0.6f * em, top + 0.4f * em + l * small.LineHeight + small.Ascent, Tx, colRight);
                    continue;
                }
                case RowKind.Strip:
                {
                    float sy0 = top + 0.6f * em;
                    Fill(b, kx + Pad, sy0, colRight, top + r.H, StripBg);
                    Outline(b, kx + Pad, sy0, colRight, top + r.H, StripBorder);
                    for (int l = 0; l < r.Help.Length; l++)
                        UiText.Draw(b, small, r.Help[l], kx + Pad + 0.8f * em, sy0 + 0.5f * em + l * small.LineHeight + small.Ascent, Warn, colRight - 0.8f * em);
                    continue;
                }
                case RowKind.Advanced:
                {
                    Fill(b, kx + Pad, top + 0.8f * em, colRight, top + 0.8f * em + 1f, ColBorder);
                    UiText.Draw(b, small, r.Text, kx + Pad, top + 1.4f * em + small.Ascent, ReferenceEquals(r, _hoverRow) ? Hi : Tx2, colRight);
                    continue;
                }
                case RowKind.Channel:
                {
                    r.Refresh();
                    float a = Greyed(r) ? OffAlpha : 1f;
                    ControlAnchor(r, top, out _, out float ccy);
                    UiText.Draw(b, body, r.Text, RowLeft(r), ccy - body.LineHeight * 0.5f + body.Ascent, Tx2, colRight, a);
                    Slider(b, body, small, r, top, ccy, "", a);
                    continue;
                }
            }
            Knob? k = r.K;
            r.Refresh();
            float alpha = Greyed(r) ? OffAlpha : 1f;
            float left = RowLeft(r);
            ControlAnchor(r, top, out float right, out float cy);
            float rowTop = top + (r.Master ? MasterTop : 0f);
            if (r.Master)
            {
                Fill(b, kx + Pad, top + MasterTop, colRight, top + r.H - MasterBottom, MasterBg);
                Outline(b, kx + Pad, top + MasterTop, colRight, top + r.H - MasterBottom, ColBorder);
            }
            else Fill(b, kx + Pad, top + r.H - 1f, colRight, top + r.H, 0xFFFFFF, 0.03f);
            if (r.OffDefault) Dot(b, kx + 0.45f * em, cy);
            float ctlX0 = right - r.CtlW;
            float labelClip = r.Ctl == KnobKind.Button ? right : ctlX0 - Gap;
            uint labelRgb = r.OffDefault ? Hi : Tx;
            float pen = left;
            for (int l = 0; l < r.Label.Length; l++)
            {
                float lb = rowTop + RowPadY + l * body.LineHeight + body.Ascent;
                float end = UiText.Draw(b, body, r.Label[l], left, lb, labelRgb, labelClip, alpha);
                if (r.Master) UiText.Draw(b, body, r.Label[l], left + 1f, lb, labelRgb, labelClip, alpha);
                if (l == 0) pen = end;
            }
            float base0 = rowTop + RowPadY + body.Ascent;
            if (r.HasMark)
            {
                pen += 0.4f * em;
                Tooltip.DrawMarker(b, small, pen, base0, ReferenceEquals(Tip.Key, r) ? Hi : Tx2);
                pen += Tooltip.MarkerW(small);
            }
            if (r.Restart)
            {
                float bx0 = pen + 0.5f * em, bx1 = bx0 + BadgeW, by0 = cy - small.LineHeight * 0.5f, by1 = cy + small.LineHeight * 0.5f;
                uint bc = r.Moved ? Warn : Off;
                Outline(b, bx0, by0, bx1, by1, r.Moved ? Warn : BadgeFrame, alpha);
                UiText.Draw(b, small, BadgeText, bx0 + 0.45f * em, cy - small.LineHeight * 0.5f + small.Ascent, bc, bx1, alpha);
            }
            float helpBase = rowTop + RowPadY + (r.Label.Length > 0 ? r.Label.Length : 1) * body.LineHeight + 0.1f * em + small.Ascent;
            uint helpRgb = r.Ctl == KnobKind.Link ? Accent2 : Tx2;
            for (int l = 0; l < r.Help.Length; l++)
                UiText.Draw(b, small, r.Help[l], left, helpBase + l * small.LineHeight, helpRgb, colRight, alpha);

            switch (r.Ctl)
            {
                case KnobKind.Toggle:
                {
                    float tx0 = right - ToggleW, ty0 = cy - ToggleH * 0.5f, ty1 = cy + ToggleH * 0.5f;
                    Fill(b, tx0, ty0, right, ty1, r.On ? Accent : ToggleOff, alpha);
                    Outline(b, tx0, ty0, right, ty1, r.On ? Accent2 : Frame, alpha);
                    float kd = ToggleH - 0.35f * em, kx0 = r.On ? right - 0.17f * em - kd : tx0 + 0.17f * em;
                    Fill(b, kx0, cy - kd * 0.5f, kx0 + kd, cy + kd * 0.5f, r.On ? Hi : Grip, alpha);
                    break;
                }
                case KnobKind.Slider:
                    Slider(b, body, small, r, top, cy, UnitOf(k), alpha);
                    break;
                case KnobKind.Dropdown:
                {
                    BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                    bool open = Drop.Open && ReferenceEquals(Drop.Tag, r);
                    string[] choices = ChoicesOf(r);
                    string cur = (uint)r.Selected < (uint)choices.Length ? choices[r.Selected] : r.Value;
                    Dropdown.DrawBox(b, body, bx0, by0, bx1, by1, cy - body.LineHeight * 0.5f + body.Ascent, cur, C(Input), Tx, alpha);
                    Outline(b, bx0, by0, bx1, by1, open ? Accent2 : Frame2, alpha);
                    break;
                }
                case KnobKind.Text:
                case KnobKind.Swatch:
                {
                    if (r.Ctl == KnobKind.Swatch)
                    {
                        if (k == null)
                        {
                            // A consumer swatch: the colour box is the whole control; its press is the consumer's.
                            BoxRect(r, top, out float cx0, out float cx1, out float cy0, out float cy1);
                            Fill(b, cx0, cy0, cx1, cy1, r.Rgba >> 8, (r.Rgba & 0xFF) / 255f);
                            Outline(b, cx0, cy0, cx1, cy1, Frame);
                            break;
                        }
                        SwatchRect(r, top, out float swx0, out float swx1, out float swy0, out float swy1);
                        Fill(b, swx0, swy0, swx1, swy1, r.Rgba >> 8, (r.Rgba & 0xFF) / 255f * alpha);
                        Outline(b, swx0, swy0, swx1, swy1, ReferenceEquals(_expanded, k) ? Accent2 : Frame, alpha);
                    }
                    BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                    bool editing = EditingRow(r);
                    Fill(b, bx0, by0, bx1, by1, Input, alpha);
                    Outline(b, bx0, by0, bx1, by1, editing ? Accent2 : Frame2, alpha);
                    string t = editing ? _editText : r.Value;
                    float tend = UiText.Draw(b, body, t, bx0 + 0.4f * em, cy - body.LineHeight * 0.5f + body.Ascent, Tx, bx1 - 0.3f * em, alpha);
                    if (editing) Fill(b, tend, by0 + 0.2f * em, tend + 1.5f, by1 - 0.2f * em, Hi);
                    break;
                }
                case KnobKind.Button:
                {
                    BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
                    bool armed = ConfirmArmed(r);
                    Fill(b, bx0, by0, bx1, by1, NavOn, alpha);
                    Outline(b, bx0, by0, bx1, by1, armed ? Warn : ReferenceEquals(r, _hoverRow) ? Accent2 : Frame2, alpha);
                    UiText.Draw(b, body, armed ? ConfirmText : r.Text, bx0 + 0.9f * em, cy - body.LineHeight * 0.5f + body.Ascent, armed ? Warn : Tx, bx1 - 0.4f * em, alpha);
                    break;
                }
                case KnobKind.Link:
                {
                    if (CopiedShown(r))
                        UiText.Draw(b, small, CopiedText, right - UiText.Measure(small, CopiedText), cy - small.LineHeight * 0.5f + small.Ascent, Ok, right);
                    else if (ReferenceEquals(r, _hoverRow))
                        Fill(b, kx + Pad, top, colRight, top + r.H, 0xFFFFFF, 0.03f);
                    break;
                }
            }
        }
        b.ClearClip();
        List.Draw(b, C(Accent));
        Drop.Draw(b, body, body.Ascent, C(ListBg), C(Selected), Tx);
        Tip.Draw(b, small, small.Ascent, C(ListBg), Tx, VpW, VpH);
    }

    /// <summary>A slider control (a knob row or a channel row): track, fill, grip, the value box (the edit text
    /// while editing) and the unit.</summary>
    private void Slider(VertexBatch b, GlyphAtlas body, GlyphAtlas small, Row r, float top, float cy, string unit, float alpha)
    {
        float em = Em;
        TrackRect(r, top, out float sx0, out float sx1, out _);
        float th = 0.35f * em;
        Fill(b, sx0, cy - th * 0.5f, sx1, cy + th * 0.5f, Track, alpha);
        Outline(b, sx0, cy - th * 0.5f, sx1, cy + th * 0.5f, Frame2, alpha);
        float knob = sx0 + r.Norm * (sx1 - sx0);
        Fill(b, sx0, cy - th * 0.5f, knob, cy + th * 0.5f, Accent, alpha);
        Fill(b, knob - 0.375f * em, cy - 0.55f * em, knob + 0.375f * em, cy + 0.55f * em, Grip, alpha);
        ValueRect(r, top, out float vx0, out float vx1, out float vy0, out float vy1);
        bool editing = EditingRow(r);
        Fill(b, vx0, vy0, vx1, vy1, Input, alpha);
        Outline(b, vx0, vy0, vx1, vy1, editing ? Accent2 : Frame2, alpha);
        string vtext = editing ? _editText : r.Value;
        float vbase = cy - body.LineHeight * 0.5f + body.Ascent;
        float vw = UiText.Measure(body, vtext);
        float vx = editing ? vx0 + 0.3f * em : vx1 - 0.3f * em - vw;
        if (vx < vx0 + 0.2f * em) vx = vx0 + 0.2f * em;
        float vend = UiText.Draw(b, body, vtext, vx, vbase, Tx, vx1 - 0.2f * em, alpha);
        if (editing) Fill(b, vend, vy0 + 0.2f * em, vend + 1.5f, vy1 - 0.2f * em, Hi);
        if (unit.Length > 0) UiText.Draw(b, small, unit, vx1 + 0.3f * em, cy - small.LineHeight * 0.5f + small.Ascent, Tx3, vx1 + 0.3f * em + UnitW, alpha);
    }

    /// <summary>One nav item: the selected one gets the filled band and the accent bar, a hovered one a faint band,
    /// a submenu with an off-default row its dot after the name.</summary>
    private void Nav(VertexBatch b, GlyphAtlas body, ScrollColumn col, int i, string text, bool on, bool hover, bool dot)
    {
        float top = NavTop(col, i), x0 = col.X, x1 = col.X + col.W - (col.Overflows ? ScrollColumn.BarW : 0f);
        if (top + NavH <= col.Y || top >= col.Y + col.H) return;
        if (on) { Fill(b, x0, top, x1, top + NavH, NavOn); Fill(b, x0, top, x0 + 3f, top + NavH, Accent2); }
        else if (hover) Fill(b, x0, top, x1, top + NavH, 0xFFFFFF, 0.03f);
        float clip = x1 - 0.3f * Em - (dot ? 0.5f * Em + 2f * DotR : 0f);
        float pen = UiText.Draw(b, body, text, x0 + NavPadX, top + (NavH - body.LineHeight) * 0.5f + body.Ascent, on ? Hi : Tx2, clip);
        if (dot) Dot(b, pen + 0.5f * Em + DotR, top + NavH * 0.5f);
    }
}
