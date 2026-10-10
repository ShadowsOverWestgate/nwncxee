using System.Text;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// The chat input line as pure Unicode state: text, a caret, an optional selection anchor, an undo/redo
/// history and an input-history ring. No native/engine references — the native shell (HotkeyInputService /
/// ChatService) reads SDL and drives these methods, so the class is unit-testable off-device via
/// <see cref="SelfCheck"/>. Feature toggles arrive as parameters; this class never reads a knob.
/// </summary>
public sealed class EditBuffer
{
    /// <summary>Max input lines kept for up/down recall.</summary>
    private const int HistoryCap = 100;

    /// <summary>Max undo (and redo) steps; the oldest is dropped past it.</summary>
    private const int UndoCap = 200;

    /// <summary>Max input length. ponytail: vanilla's chat edit is unbounded (its edit max-length is -1);
    /// this is CX's own runaway-paste guard, set far above any real chat line, not a vanilla-parity value.</summary>
    public const int MaxLength = 4000;

    /// <summary>One undo/redo step: the whole line plus where the caret and the selection anchor sat.</summary>
    private readonly record struct Snapshot(string Text, int Cursor, int Anchor);

    /// <summary>What the open undo group accepts as a continuation. <c>None</c> never continues.</summary>
    private enum Group { None, Type, Back, Del, Expand }

    private readonly StringBuilder _text = new();
    private int _cursor;
    private int _anchor = -1;                 // selection start; -1 = no selection
    private int _pending;                     // auto-inserted closers sitting at [cursor, cursor + _pending)
    private readonly List<string> _history = new();
    private int _histIdx;                     // == _history.Count means "current (unsaved) line"
    private string _draft = string.Empty;     // the line as it was when history browsing started
    private string _prefix = string.Empty;    // the prefix the current browse searches for ("" = every entry)
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private Group _group;
    private int _groupCaret = -1;             // caret after the group's last mutation; a mismatch ends the group

    /// <summary>Current caret position, clamped to [0, length].</summary>
    public int Cursor => _cursor;

    /// <summary>The full edit text.</summary>
    public string Text => _text.ToString();

    /// <summary>The line's length in UTF-16 units (like <see cref="Cursor"/>).</summary>
    public int Length => _text.Length;

    /// <summary>True while a selection range is active.</summary>
    public bool HasSelection => _anchor >= 0 && _anchor != _cursor;

    /// <summary>Normalized selection bounds [start, end) in UTF-16 indices (like <see cref="Cursor"/>),
    /// or null when nothing is selected. The renderer highlights this range.</summary>
    public (int start, int end)? Selection =>
        HasSelection ? (Math.Min(_anchor, _cursor), Math.Max(_anchor, _cursor)) : null;

    /// <summary>The currently selected substring (empty when nothing is selected).</summary>
    public string SelectedText
    {
        get
        {
            if (!HasSelection) return string.Empty;
            int a = Math.Min(_anchor, _cursor), b = Math.Max(_anchor, _cursor);
            return _text.ToString(a, b - a);
        }
    }

    /// <summary>The recall ring, oldest first. A snapshot copy — cold, read only to persist the ring.</summary>
    public IReadOnlyList<string> History => _history.ToArray();

    /// <summary>Decode finished UTF-8 codepoints and insert at the caret (the paste/programmatic path).</summary>
    public void InsertUtf8(ReadOnlySpan<byte> utf8) => Insert(Encoding.UTF8.GetString(utf8));

    /// <summary>Insert text at the caret, replacing any active selection first. The paste/programmatic
    /// path: one undo step of its own, never pairs, and it drops any pending auto-inserted closers.</summary>
    public void Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Begin(Group.None);
        _pending = 0;
        Put(s);
        _groupCaret = _cursor;
    }

    /// <summary>
    /// The typing path (SDL_TEXTINPUT): insert at the caret, coalescing consecutive chars into one undo
    /// step per word. With <paramref name="pairs"/> a lone bracket/quote/asterisk wraps the selection,
    /// auto-closes, or types over its own auto-inserted closer.
    /// </summary>
    public void Type(string s, bool pairs)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (pairs && s.Length == 1 && TypePair(s[0])) return;
        TypeText(s);
    }

    /// <summary>Delete the char before the caret, the selection if there is one, or — right after an
    /// auto-close — the empty pair around the caret as one.</summary>
    public void Backspace()
    {
        if (HasSelection)
        {
            Begin(Group.None);
            DeleteSelection();
            _pending = 0;
            _groupCaret = _cursor;
            return;
        }
        if (_pending > 0 && _cursor > 0 && _cursor < _text.Length && Closer(_text[_cursor - 1]) == _text[_cursor])
        {
            Begin(Group.Back);
            _text.Remove(_cursor - 1, 2);
            _cursor--;
            _pending--;
            _groupCaret = _cursor;
            return;
        }
        _pending = 0;
        if (_cursor == 0) return;
        Begin(Group.Back);
        _text.Remove(--_cursor, 1);
        _groupCaret = _cursor;
    }

    /// <summary>Delete the char at the caret, or the selection if there is one.</summary>
    public void Delete()
    {
        _pending = 0;
        if (HasSelection)
        {
            Begin(Group.None);
            DeleteSelection();
            _groupCaret = _cursor;
            return;
        }
        if (_cursor >= _text.Length) return;
        Begin(Group.Del);
        _text.Remove(_cursor, 1);
        _groupCaret = _cursor;
    }

    /// <summary>Ctrl+Backspace: delete the selection if there is one, else back to the previous word stop.</summary>
    public void DeleteWordLeft() => DeleteTo(WordLeft(_cursor));

    /// <summary>Ctrl+Delete: delete the selection if there is one, else forward to the next word stop.</summary>
    public void DeleteWordRight() => DeleteTo(WordRight(_cursor));

    /// <summary>Move the caret left; <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveLeft(bool extend) => MoveTo(_cursor - 1, extend);
    /// <summary>Move the caret right; <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveRight(bool extend) => MoveTo(_cursor + 1, extend);
    /// <summary>Move the caret one word left (Ctrl+Left); <paramref name="extend"/> grows/starts a selection.
    /// A word stop skips any whitespace to the left, then the run of non-whitespace before it.</summary>
    public void MoveWordLeft(bool extend) => MoveTo(WordLeft(_cursor), extend);
    /// <summary>Move the caret one word right (Ctrl+Right); <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveWordRight(bool extend) => MoveTo(WordRight(_cursor), extend);

    /// <summary>Put the caret at <paramref name="pos"/> (mouse click, row navigation), clamped to the line and
    /// snapped left off the middle of a surrogate pair; <paramref name="extend"/> grows/starts a selection.</summary>
    public void SetCaret(int pos, bool extend)
    {
        pos = Math.Clamp(pos, 0, _text.Length);
        if (pos > 0 && pos < _text.Length && char.IsLowSurrogate(_text[pos])) pos--;
        MoveTo(pos, extend);
    }

    /// <summary>Double-click: select the run holding the char at <paramref name="index"/> (at the line end, the
    /// char before it). A run is whitespace or non-whitespace, the classes the word stops use.</summary>
    public void SelectWordAt(int index)
    {
        _pending = 0;
        EndGroup();
        if (_text.Length == 0) { _anchor = -1; _cursor = 0; return; }
        int i = Math.Clamp(index, 0, _text.Length - 1);
        bool ws = char.IsWhiteSpace(_text[i]);
        int a = i, b = i + 1;
        while (a > 0 && char.IsWhiteSpace(_text[a - 1]) == ws) a--;
        while (b < _text.Length && char.IsWhiteSpace(_text[b]) == ws) b++;
        _anchor = a;
        _cursor = b;
    }

    /// <summary>Select the whole line (Ctrl+A). No-op appearance on an empty line (anchor==cursor).</summary>
    public void SelectAll() { _anchor = 0; _cursor = _text.Length; _pending = 0; EndGroup(); }

    /// <summary>Move the caret to line start.</summary>
    public void Home(bool extend) => MoveTo(0, extend);
    /// <summary>Move the caret to line end.</summary>
    public void End(bool extend) => MoveTo(_text.Length, extend);

    /// <summary>Replace [<paramref name="start"/>, <paramref name="end"/>) with <paramref name="with"/> as one
    /// undo step (Tab completion): bounds are clamped, the caret lands after the replacement, selection clears.</summary>
    public void ReplaceRange(int start, int end, string with)
    {
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, start, _text.Length);
        Begin(Group.None);
        _text.Remove(start, end - start);
        _cursor = start;
        _anchor = -1;
        _pending = 0;
        Put(with);
        _groupCaret = _cursor;
    }

    /// <summary>
    /// Chat shortcuts: if the maximal non-whitespace run ending exactly at the caret has an expansion, swap it in
    /// and return true. The expansion and the space that fired it share one undo step. No-op under a selection.
    /// </summary>
    public bool ExpandBeforeCaret(Func<string, string?> lookup)
    {
        if (HasSelection) return false;
        if (_cursor < _text.Length && !char.IsWhiteSpace(_text[_cursor])) return false;
        int a = _cursor;
        while (a > 0 && !char.IsWhiteSpace(_text[a - 1])) a--;
        if (a == _cursor) return false;
        string? exp = lookup(_text.ToString(a, _cursor - a));
        if (exp == null) return false;
        Begin(Group.Expand, fresh: true);
        _text.Remove(a, _cursor - a);
        _cursor = a;
        _pending = 0;
        Put(exp);
        _groupCaret = _cursor;
        return true;
    }

    /// <summary>Undo one step (Ctrl+Z); false when there is nothing left to undo.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Push(_redo);
        Restore(Pop(_undo));
        return true;
    }

    /// <summary>Redo one undone step (Ctrl+Y / Ctrl+Shift+Z); false when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        Push(_undo);
        Restore(Pop(_redo));
        return true;
    }

    /// <summary>
    /// Recall the previous (older) history entry into the edit line, keeping the in-progress line as the draft to
    /// come back to. With <paramref name="prefixSearch"/> only entries starting with that draft are visited.
    /// </summary>
    public void HistoryPrev(bool prefixSearch)
    {
        if (_histIdx >= _history.Count)
        {
            _draft = _text.ToString();
            _prefix = prefixSearch ? _draft : string.Empty;
        }
        string shown = _text.ToString();
        for (int i = _histIdx - 1; i >= 0; i--)
        {
            if (!Matches(_history[i]) || _history[i] == shown) continue;
            _histIdx = i;
            Recall(_history[i]);
            return;
        }
    }

    /// <summary>Recall the next (newer) history entry matching the prefix the browse started with, or -- past the
    /// newest -- the saved draft.</summary>
    public void HistoryNext()
    {
        if (_histIdx >= _history.Count) return;
        for (int i = _histIdx + 1; i < _history.Count; i++)
        {
            if (!Matches(_history[i])) continue;
            _histIdx = i;
            Recall(_history[i]);
            return;
        }
        _histIdx = _history.Count;
        Recall(_draft);
    }

    /// <summary>Replace the recall ring (oldest first), keeping the newest <see cref="HistoryCap"/> non-empty
    /// lines and resetting browsing to the live line.</summary>
    public void LoadHistory(IEnumerable<string> lines)
    {
        _history.Clear();
        foreach (string s in lines)
        {
            if (string.IsNullOrEmpty(s)) continue;
            _history.Add(s);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
        }
        _histIdx = _history.Count;
        _draft = string.Empty;
        _prefix = string.Empty;
    }

    /// <summary>
    /// Take the current text, push it onto the history ring, and clear the line.
    /// Returns the submitted text (empty submits, and a repeat of the newest entry, are not recorded).
    /// </summary>
    public string Submit()
    {
        string s = _text.ToString();
        if (s.Length > 0 && (_history.Count == 0 || _history[^1] != s))
        {
            _history.Add(s);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
        }
        Clear();
        return s;
    }

    /// <summary>Replace the whole line (draft restore): caret at the end, no selection, and undo, pair and
    /// history-browsing state all reset. History entries are kept.</summary>
    public void Load(string text)
    {
        Set(text);
        _undo.Clear();
        _redo.Clear();
        _pending = 0;
        EndGroup();
        _histIdx = _history.Count;
        _draft = string.Empty;
        _prefix = string.Empty;
    }

    /// <summary>Reset the line to empty, keeping the history entries.</summary>
    public void Clear() => Load(string.Empty);

    private int WordLeft(int p)
    {
        while (p > 0 && char.IsWhiteSpace(_text[p - 1])) p--;
        while (p > 0 && !char.IsWhiteSpace(_text[p - 1])) p--;
        return p;
    }

    private int WordRight(int p)
    {
        while (p < _text.Length && char.IsWhiteSpace(_text[p])) p++;
        while (p < _text.Length && !char.IsWhiteSpace(_text[p])) p++;
        return p;
    }

    /// <summary>The auto-inserted closer for <paramref name="c"/>, or '\0' when it pairs nothing. Never for
    /// <c>'</c>: an apostrophe is a letter in "don't", not a quote.</summary>
    private static char Closer(char c) => c switch
    {
        '(' => ')',
        '[' => ']',
        '"' => '"',
        '*' => '*',
        _ => '\0',
    };

    /// <summary>True for a char that can close a pair, so typing it may step over an auto-inserted one.</summary>
    private static bool IsCloser(char c) => c is ')' or ']' or '"' or '*';

    /// <summary>Handle one char as a pair operation (wrap, type-over or auto-close); false to type it plainly.</summary>
    private bool TypePair(char c)
    {
        char close = Closer(c);
        if (HasSelection)
        {
            if (close == '\0') return false;
            WrapSelection(c, close);
            return true;
        }
        if (_pending > 0 && IsCloser(c) && _cursor < _text.Length && _text[_cursor] == c)
        {
            _cursor++;
            _pending--;
            return true;
        }
        if (close == '\0') return false;
        if (_cursor < _text.Length && !char.IsWhiteSpace(_text[_cursor]) && !IsCloser(_text[_cursor])) return false;
        if (c == close && !OpensHere(c)) return false;
        if (_text.Length + 2 > MaxLength) return false;
        Begin(Group.None);
        _text.Insert(_cursor, close);
        _text.Insert(_cursor, c);
        _cursor++;
        _pending++;
        _groupCaret = _cursor;
        return true;
    }

    /// <summary>True where a symmetric pair char (<c>"</c>, <c>*</c>) reads as an opener: at the line start, after
    /// whitespace or a bracket, or after the other symmetric char, so <c>*"</c> nests but <c>**</c> does not re-pair.</summary>
    private bool OpensHere(char c)
    {
        if (_cursor == 0) return true;
        char p = _text[_cursor - 1];
        return char.IsWhiteSpace(p) || p is '(' or '[' || p == (c == '"' ? '*' : '"');
    }

    /// <summary>Put <paramref name="open"/>/<paramref name="close"/> around the selection, leaving the inner text
    /// selected so a second press nests (<c>**text**</c>).</summary>
    private void WrapSelection(char open, char close)
    {
        if (_text.Length + 2 > MaxLength) return;
        int a = Math.Min(_anchor, _cursor), b = Math.Max(_anchor, _cursor);
        Begin(Group.None);
        _text.Insert(b, close);
        _text.Insert(a, open);
        _anchor = a + 1;
        _cursor = b + 1;
        _pending = 0;
        _groupCaret = _cursor;
    }

    /// <summary>Insert typed text at the caret, continuing the typing group until the caret moves or a word ends.</summary>
    private void TypeText(string s)
    {
        bool selection = HasSelection;
        bool breaksWord = char.IsWhiteSpace(s[0]) && _cursor > 0 && !char.IsWhiteSpace(_text[_cursor - 1]);
        if (!selection && breaksWord && _group == Group.Expand && _groupCaret == _cursor)
            Begin(Group.Expand);
        else
            Begin(Group.Type, fresh: selection || breaksWord);
        if (selection) _pending = 0;
        Put(s);
        _groupCaret = _cursor;
    }

    /// <summary>Delete from the caret to <paramref name="to"/>, or the selection when there is one, as one step.</summary>
    private void DeleteTo(int to)
    {
        if (HasSelection)
        {
            Begin(Group.None);
            DeleteSelection();
        }
        else
        {
            int a = Math.Min(to, _cursor), b = Math.Max(to, _cursor);
            if (a == b) { _pending = 0; return; }
            Begin(Group.None);
            _text.Remove(a, b - a);
            _cursor = a;
        }
        _pending = 0;
        _groupCaret = _cursor;
    }

    /// <summary>Insert at the caret, replacing any selection and truncating at <see cref="MaxLength"/>.
    /// The raw edit: no undo step, no pair handling.</summary>
    private void Put(string s)
    {
        DeleteSelection();
        if (s.Length == 0) return;
        int room = MaxLength - _text.Length;
        if (room <= 0) return;
        if (s.Length > room)
        {
            if (char.IsHighSurrogate(s[room - 1])) room--;   // don't split a surrogate pair at the cap
            s = s.Substring(0, room);
            if (s.Length == 0) return;
        }
        _text.Insert(_cursor, s);
        _cursor += s.Length;
    }

    /// <summary>Open an undo step: snapshot the pre-mutation line unless <paramref name="kind"/> continues the open
    /// group, and any snapshot drops the redo stack. <c>Group.None</c> and <paramref name="fresh"/> never continue.</summary>
    private void Begin(Group kind, bool fresh = false)
    {
        if (fresh || kind == Group.None || kind != _group || _groupCaret != _cursor)
        {
            Push(_undo);
            _redo.Clear();
        }
        _group = kind;
    }

    /// <summary>Close the open undo group, so the next mutation starts a fresh one.</summary>
    private void EndGroup() { _group = Group.None; _groupCaret = -1; }

    private void Push(List<Snapshot> stack)
    {
        stack.Add(new Snapshot(_text.ToString(), _cursor, _anchor));
        if (stack.Count > UndoCap) stack.RemoveAt(0);
    }

    private static Snapshot Pop(List<Snapshot> stack)
    {
        Snapshot s = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return s;
    }

    private void Restore(Snapshot s)
    {
        _text.Clear();
        _text.Append(s.Text);
        _cursor = s.Cursor;
        _anchor = s.Anchor;
        _pending = 0;
        EndGroup();
    }

    /// <summary>True when a history entry satisfies the browse's prefix search.</summary>
    private bool Matches(string entry) =>
        _prefix.Length == 0 || entry.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Show a recalled line: undo history belongs to the line being edited, so it starts over.</summary>
    private void Recall(string s)
    {
        Set(s);
        _undo.Clear();
        _redo.Clear();
        _pending = 0;
        EndGroup();
    }

    private void Set(string s)
    {
        _text.Clear();
        _text.Append(s);
        _cursor = _text.Length;
        _anchor = -1;
    }

    private void MoveTo(int pos, bool extend)
    {
        pos = Math.Clamp(pos, 0, _text.Length);
        if (extend) { if (_anchor < 0) _anchor = _cursor; }
        else _anchor = -1;
        _cursor = pos;
        _pending = 0;
        EndGroup();
    }

    /// <summary>Remove the selection if one exists; returns whether anything was removed.</summary>
    private bool DeleteSelection()
    {
        if (!HasSelection) return false;
        int a = Math.Min(_anchor, _cursor), b = Math.Max(_anchor, _cursor);
        _text.Remove(a, b - a);
        _cursor = a;
        _anchor = -1;
        return true;
    }

    /// <summary>Feed a string one char at a time, the way SDL_TEXTINPUT delivers it.</summary>
    private static void TypeEach(EditBuffer e, string s, bool pairs)
    {
        foreach (char c in s) e.Type(c.ToString(), pairs);
    }

    /// <summary>
    /// Off-device proof of the editing contract: insert-at-caret, backspace/delete, caret movement,
    /// select+replace, auto-pairs, undo/redo grouping, shortcut expansion and history recall. Logs one
    /// PASS/FAIL line so <c>run-demo.sh</c> surfaces it in cx.log with no game running.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var e = new EditBuffer();
        var check = new Checker(log);

        // insert at caret + mid-line insert (via UTF-8 path, incl. a multi-byte codepoint)
        e.InsertUtf8("héllo"u8);
        check.Expect(e.Text == "héllo" && e.Cursor == 5, "utf8 insert");
        e.MoveLeft(false); e.MoveLeft(false);            // caret between 'l' and 'o'
        e.Insert("X");
        check.Expect(e.Text == "hélXlo" && e.Cursor == 4, "mid-line insert");

        // backspace + delete
        e.Backspace();                                    // removes 'X'
        check.Expect(e.Text == "héllo" && e.Cursor == 3, "backspace");
        e.Delete();                                       // removes 'l' at caret
        check.Expect(e.Text == "hélo", "delete at caret");

        // caret movement bounds
        e.Home(false); check.Expect(e.Cursor == 0, "home");
        e.End(false);  check.Expect(e.Cursor == 4, "end");

        // select + replace
        e.Home(false);
        e.MoveRight(true); e.MoveRight(true);            // select "hé"
        check.Expect(e.SelectedText == "hé", "select range");
        check.Expect(e.Selection is (0, 2), "selection bounds");
        e.Insert("J");                                    // replaces selection
        check.Expect(e.Text == "Jlo" && !e.HasSelection, "replace selection");

        // word-wise movement (Ctrl+arrow) + word-wise selection (Ctrl+Shift+arrow)
        e.Clear(); e.Insert("foo bar baz");
        e.MoveWordLeft(false); check.Expect(e.Cursor == 8, "word-left to last word");
        e.MoveWordLeft(false); check.Expect(e.Cursor == 4, "word-left again");
        e.MoveWordRight(true); check.Expect(e.SelectedText == "bar", "word-right selects a word");
        e.Home(false);
        e.MoveWordRight(true); e.MoveWordRight(true);
        check.Expect(e.SelectedText == "foo bar", "two words selected");

        // length cap: an over-long paste truncates to MaxLength, further input is blocked
        e.Clear(); e.Insert(new string('x', MaxLength + 50));
        check.Expect(e.Text.Length == MaxLength, "length cap truncates paste");
        e.Insert("y"); check.Expect(e.Text.Length == MaxLength, "length cap blocks further input");

        // select-all (Ctrl+A)
        e.Clear(); e.Insert("abcd"); e.Home(false);
        e.SelectAll(); check.Expect(e.SelectedText == "abcd" && e.Cursor == 4, "select-all covers the line");

        // word deletes (Ctrl+Backspace / Ctrl+Delete)
        e.Clear(); e.Insert("foo bar baz");
        e.DeleteWordLeft(); check.Expect(e.Text == "foo bar " && e.Cursor == 8, "delete word left");
        e.Home(false); e.DeleteWordRight();
        check.Expect(e.Text == " bar " && e.Cursor == 0, "delete word right");
        e.Clear(); e.Insert("keep drop"); e.Home(false); e.MoveWordRight(true);
        e.DeleteWordLeft(); check.Expect(e.Text == " drop", "word delete takes the selection when there is one");

        // caret placement + double-click word selection
        e.Clear(); e.Insert("foo bar");
        e.SetCaret(99, false); check.Expect(e.Cursor == 7 && !e.HasSelection, "set-caret clamps to the line end");
        e.SetCaret(4, false); e.SetCaret(7, true);
        check.Expect(e.SelectedText == "bar", "set-caret extends a selection");
        e.SelectWordAt(1); check.Expect(e.SelectedText == "foo" && e.Selection is (0, 3), "select word at index");
        e.SelectWordAt(e.Length); check.Expect(e.SelectedText == "bar", "select word at the line end");
        e.SelectWordAt(3); check.Expect(e.SelectedText == " ", "select whitespace run");

        // tab-completion style range replace
        e.Clear(); e.Insert("/tel Aelar");
        e.ReplaceRange(0, 4, "/tell");
        check.Expect(e.Text == "/tell Aelar" && e.Cursor == 5 && !e.HasSelection, "replace range");
        check.Expect(e.Undo() && e.Text == "/tel Aelar", "replace range is one undo step");

        // auto-pairs: openers close themselves, closers step over
        e.Clear(); e.Type("(", true);
        check.Expect(e.Text == "()" && e.Cursor == 1, "opener auto-closes");
        e.Type("(", true);
        check.Expect(e.Text == "(())" && e.Cursor == 2, "nested opener auto-closes");
        e.Type(")", true); e.Type(")", true);
        check.Expect(e.Text == "(())" && e.Cursor == 4, "typing the closers steps over both");
        e.Clear(); TypeEach(e, "word", true); e.Home(false); e.Type("(", true);
        check.Expect(e.Text == "(word" && e.Cursor == 1, "no auto-close in front of a word");
        e.Clear(); e.Type("*", true); TypeEach(e, "nods", true); e.Type("*", true);
        check.Expect(e.Text == "*nods*" && e.Cursor == 6, "emote pair typed through");
        e.Type("*", true);
        check.Expect(e.Text == "*nods**" && e.Cursor == 7, "a closed pair does not re-pair");
        e.Clear(); e.Type("*", true); e.Type("\"", true);
        check.Expect(e.Text == "*\"\"*" && e.Cursor == 2, "symmetric pairs nest");
        e.Clear(); TypeEach(e, "don't", true);
        check.Expect(e.Text == "don't", "an apostrophe never pairs");
        e.Clear(); e.Insert("*nods"); e.Type("*", true);
        check.Expect(e.Text == "*nods*" && e.Cursor == 6, "a hand-typed emote closes plainly");
        e.Clear(); e.Type("(", true); e.Backspace();
        check.Expect(e.Text == "" && e.Cursor == 0, "backspace removes an auto-inserted pair as one");
        e.Clear(); e.Insert("hi"); e.SelectAll(); e.Type("*", true);
        check.Expect(e.Text == "*hi*" && e.SelectedText == "hi", "the pair wraps the selection");
        e.Type("*", true);
        check.Expect(e.Text == "**hi**" && e.SelectedText == "hi", "a second press nests the wrap");
        e.Clear(); e.Type("(", false);
        check.Expect(e.Text == "(" && e.Cursor == 1, "pairs off inserts plainly");

        // undo/redo: one step per typed word, and a new edit drops the redo stack
        e.Clear(); TypeEach(e, "hello world", true);
        check.Expect(e.Text == "hello world", "typed a line");
        check.Expect(e.Undo() && e.Text == "hello" && e.Cursor == 5, "undo drops the last word");
        check.Expect(e.Undo() && e.Text == "" && !e.Undo(), "undo drops the first word, then nothing");
        check.Expect(e.Redo() && e.Text == "hello", "redo puts the word back");
        check.Expect(e.Redo() && e.Text == "hello world" && !e.Redo(), "redo to the top, then nothing");
        e.Undo(); e.Type("X", true);
        check.Expect(!e.Redo() && e.Text == "helloX", "a new edit clears the redo stack");

        // chat shortcuts: the expansion and the space that fired it undo together
        e.Clear(); TypeEach(e, ";bow", true);
        check.Expect(e.ExpandBeforeCaret(t => t == ";bow" ? "*bows*" : null), "shortcut expands");
        e.Type(" ", true);
        check.Expect(e.Text == "*bows* " && e.Cursor == 7, "the trigger space follows the expansion");
        check.Expect(e.Undo() && e.Text == ";bow" && e.Cursor == 4, "expansion + space undo as one step");
        check.Expect(!e.ExpandBeforeCaret(_ => null), "no expansion without a match for the run");
        e.Clear(); e.Insert("bowx"); e.SetCaret(3, false);
        check.Expect(!e.ExpandBeforeCaret(t => t == "bow" ? "*bows*" : null), "no expansion mid-word");

        // history recall
        e.Clear(); e.Insert("first");  string s1 = e.Submit();
        e.Insert("second"); string s2 = e.Submit();
        check.Expect(s1 == "first" && s2 == "second" && e.Text == "", "submit clears");
        e.HistoryPrev(false); check.Expect(e.Text == "second", "history prev 1");
        e.HistoryPrev(false); check.Expect(e.Text == "first", "history prev 2");
        e.HistoryNext(); check.Expect(e.Text == "second", "history next");
        e.HistoryNext(); check.Expect(e.Text == "", "history next to the empty draft");

        // the in-progress line survives a browse
        e.Clear(); e.Insert("draft");
        e.HistoryPrev(false); check.Expect(e.Text == "second", "browse leaves the draft");
        e.HistoryNext(); check.Expect(e.Text == "draft" && e.Cursor == 5, "the draft comes back");

        // consecutive duplicates are not recorded twice
        e.Clear(); e.Insert("same"); e.Submit();
        e.Insert("same"); e.Submit();
        check.Expect(e.History.Count == 3 && e.History[^1] == "same", "a repeat of the newest entry is not recorded");

        // prefix search (Up/Down over the entries starting with what is typed)
        e.LoadHistory(new[] { "*waves*", "", "hello", "*walks*" });
        check.Expect(e.History.Count == 3 && e.History[0] == "*waves*", "load history skips empty lines");
        e.Insert("*wa");
        e.HistoryPrev(true); check.Expect(e.Text == "*walks*", "prefix search finds the newest match");
        e.HistoryPrev(true); check.Expect(e.Text == "*waves*", "prefix search skips a non-match");
        e.HistoryPrev(true); check.Expect(e.Text == "*waves*", "prefix search holds at the oldest match");
        e.HistoryNext(); check.Expect(e.Text == "*walks*", "prefix search comes back down");
        e.HistoryNext(); check.Expect(e.Text == "*wa", "prefix search returns the draft");

        // the ring keeps only the newest HistoryCap lines
        var many = new string[HistoryCap + 10];
        for (int i = 0; i < many.Length; i++) many[i] = $"line{i}";
        e.LoadHistory(many);
        check.Expect(e.History.Count == HistoryCap && e.History[^1] == $"line{many.Length - 1}", "history cap keeps the newest");

        // Load replaces the line and forgets the undo trail
        e.Load("restored");
        check.Expect(e.Text == "restored" && e.Cursor == 8 && !e.Undo(), "load restores a draft with no undo trail");

        log.Info(check.Ok ? "EditBuffer.SelfCheck PASS" : "EditBuffer.SelfCheck had FAILURES");
    }
}
