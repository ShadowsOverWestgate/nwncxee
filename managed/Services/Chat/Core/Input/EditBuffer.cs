using System.Text;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>
/// The chat input line as pure Unicode state: text, a caret, an optional selection
/// anchor, and an input-history ring. No native/engine references — the native shell
/// (HotkeyInputService / ChatService) reads SDL and drives these methods, so this whole
/// class is unit-testable off-device via <see cref="SelfCheck"/>.
/// </summary>
public sealed class EditBuffer
{
    /// <summary>Max input lines kept for up/down recall.</summary>
    private const int HistoryCap = 32;

    /// <summary>Max input length. ponytail: vanilla is unbounded (its edit max-length is -1); this is a
    /// runaway-paste guard set far above any real chat line, not a vanilla-parity value.</summary>
    public const int MaxLength = 1000;

    private readonly StringBuilder _text = new();
    private int _cursor;
    private int _anchor = -1;                 // selection start; -1 = no selection
    private readonly List<string> _history = new();
    private int _histIdx;                     // == _history.Count means "current (unsaved) line"

    /// <summary>Current caret position, clamped to [0, length].</summary>
    public int Cursor => _cursor;

    /// <summary>The full edit text.</summary>
    public string Text => _text.ToString();

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

    /// <summary>Decode finished UTF-8 codepoints (SDL_TEXTINPUT) and insert at the caret.</summary>
    public void InsertUtf8(ReadOnlySpan<byte> utf8) => Insert(Encoding.UTF8.GetString(utf8));

    /// <summary>Insert text at the caret, replacing any active selection first.</summary>
    public void Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        DeleteSelection();
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

    /// <summary>Delete the char before the caret, or the selection if there is one.</summary>
    public void Backspace()
    {
        if (DeleteSelection()) return;
        if (_cursor == 0) return;
        _text.Remove(--_cursor, 1);
    }

    /// <summary>Delete the char at the caret, or the selection if there is one.</summary>
    public void Delete()
    {
        if (DeleteSelection()) return;
        if (_cursor >= _text.Length) return;
        _text.Remove(_cursor, 1);
    }

    /// <summary>Move the caret left; <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveLeft(bool extend) => MoveTo(_cursor - 1, extend);
    /// <summary>Move the caret right; <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveRight(bool extend) => MoveTo(_cursor + 1, extend);
    /// <summary>Move the caret one word left (Ctrl+Left); <paramref name="extend"/> grows/starts a selection.
    /// A word stop skips any whitespace to the left, then the run of non-whitespace before it.</summary>
    public void MoveWordLeft(bool extend) => MoveTo(WordLeft(_cursor), extend);
    /// <summary>Move the caret one word right (Ctrl+Right); <paramref name="extend"/> grows/starts a selection.</summary>
    public void MoveWordRight(bool extend) => MoveTo(WordRight(_cursor), extend);

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

    /// <summary>Select the whole line (Ctrl+A). No-op appearance on an empty line (anchor==cursor).</summary>
    public void SelectAll() { _anchor = 0; _cursor = _text.Length; }

    /// <summary>Move the caret to line start.</summary>
    public void Home(bool extend) => MoveTo(0, extend);
    /// <summary>Move the caret to line end.</summary>
    public void End(bool extend) => MoveTo(_text.Length, extend);

    /// <summary>Recall the previous (older) history entry into the edit line.</summary>
    public void HistoryPrev()
    {
        if (_histIdx == 0) return;
        Set(_history[--_histIdx]);
    }

    /// <summary>Recall the next (newer) history entry, or back to an empty current line.</summary>
    public void HistoryNext()
    {
        if (_histIdx >= _history.Count) return;
        _histIdx++;
        Set(_histIdx >= _history.Count ? string.Empty : _history[_histIdx]);
    }

    /// <summary>
    /// Take the current text, push it onto the history ring, and clear the line.
    /// Returns the submitted text (empty submits are returned but not recorded).
    /// </summary>
    public string Submit()
    {
        string s = _text.ToString();
        if (s.Length > 0)
        {
            _history.Add(s);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
        }
        Clear();
        return s;
    }

    /// <summary>Reset text, caret, selection, and history cursor (keeps history entries).</summary>
    public void Clear()
    {
        _text.Clear();
        _cursor = 0;
        _anchor = -1;
        _histIdx = _history.Count;
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

    /// <summary>
    /// Off-device proof of the editing contract: insert-at-caret, backspace/delete,
    /// caret movement, select+replace, and history recall. Logs one PASS/FAIL line so
    /// <c>run-demo.sh</c> surfaces it in the sowx log with no game running.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var e = new EditBuffer();
        bool ok = true;
        void Expect(bool cond, string what) { if (!cond) { ok = false; log.Info($"EditBuffer.SelfCheck FAIL: {what}"); } }

        // insert at caret + mid-line insert (via UTF-8 path, incl. a multi-byte codepoint)
        e.InsertUtf8("héllo"u8);
        Expect(e.Text == "héllo" && e.Cursor == 5, "utf8 insert");
        e.MoveLeft(false); e.MoveLeft(false);            // caret between 'l' and 'o'
        e.Insert("X");
        Expect(e.Text == "hélXlo" && e.Cursor == 4, "mid-line insert");

        // backspace + delete
        e.Backspace();                                    // removes 'X'
        Expect(e.Text == "héllo" && e.Cursor == 3, "backspace");
        e.Delete();                                       // removes 'l' at caret
        Expect(e.Text == "hélo", "delete at caret");

        // caret movement bounds
        e.Home(false); Expect(e.Cursor == 0, "home");
        e.End(false);  Expect(e.Cursor == 4, "end");

        // select + replace
        e.Home(false);
        e.MoveRight(true); e.MoveRight(true);            // select "hé"
        Expect(e.SelectedText == "hé", "select range");
        Expect(e.Selection is (0, 2), "selection bounds");
        e.Insert("J");                                    // replaces selection
        Expect(e.Text == "Jlo" && !e.HasSelection, "replace selection");

        // word-wise movement (Ctrl+arrow) + word-wise selection (Ctrl+Shift+arrow)
        e.Clear(); e.Insert("foo bar baz");
        e.MoveWordLeft(false); Expect(e.Cursor == 8, "word-left to last word");
        e.MoveWordLeft(false); Expect(e.Cursor == 4, "word-left again");
        e.MoveWordRight(true); Expect(e.SelectedText == "bar", "word-right selects a word");
        e.Home(false);
        e.MoveWordRight(true); e.MoveWordRight(true);
        Expect(e.SelectedText == "foo bar", "two words selected");

        // length cap: an over-long paste truncates to MaxLength, further input is blocked
        e.Clear(); e.Insert(new string('x', MaxLength + 50));
        Expect(e.Text.Length == MaxLength, "length cap truncates paste");
        e.Insert("y"); Expect(e.Text.Length == MaxLength, "length cap blocks further input");

        // select-all (Ctrl+A)
        e.Clear(); e.Insert("abcd"); e.Home(false);
        e.SelectAll(); Expect(e.SelectedText == "abcd" && e.Cursor == 4, "select-all covers the line");

        // history recall
        e.Clear(); e.Insert("first");  string s1 = e.Submit();
        e.Insert("second"); string s2 = e.Submit();
        Expect(s1 == "first" && s2 == "second" && e.Text == "", "submit clears");
        e.HistoryPrev(); Expect(e.Text == "second", "history prev 1");
        e.HistoryPrev(); Expect(e.Text == "first", "history prev 2");
        e.HistoryNext(); Expect(e.Text == "second", "history next");
        e.HistoryNext(); Expect(e.Text == "", "history next to empty");

        log.Info(ok ? "EditBuffer.SelfCheck PASS" : "EditBuffer.SelfCheck had FAILURES");
    }
}
