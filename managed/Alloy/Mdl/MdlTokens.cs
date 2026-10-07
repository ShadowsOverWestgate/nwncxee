using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Alloy.Mdl;

/// <summary>CX-112: the ASCII side's lexer, ported from the engine's own three primitives so a line reads
/// exactly as the engine reads it. Span-based and allocation-free: one pass over the file's bytes, no string
/// per line and no string per word.
/// <para>The engine's semantics, from the decompile:</para>
/// <list type="bullet">
/// <item><c>AurResGetNextLine</c> (@0x432fa0, its body <c>FUN_00532e40</c>) ends a line at <c>\r</c> or
/// <c>\n</c> and folds <c>\r\n</c> (and <c>\r\r\n</c>) into one break.</item>
/// <item><c>dofile</c> @0x434930 left-trims <b>spaces and tabs only</b>, drops the line when it is then empty
/// or starts with <c>#</c> (so a comment is a whole line, never a tail), and right-trims the characters
/// <c>\t \n \r</c> and space.</item>
/// <item><c>firstword</c> @0x446630 takes the first run of bytes that are not NUL, tab, LF, CR or space --
/// note that other control bytes are NOT separators -- and the field tests are <c>strcasecmp</c>.</item>
/// <item>every value is read by an <c>sscanf</c>, so <see cref="Scan"/> ports sscanf itself: conversions are
/// SEQUENTIAL and stop dead at the first failure (a later field is never reached, however well formed), a
/// numeric conversion takes the LONGEST VALID PREFIX of its field (<c>1.5abc</c> is 1.5; <c>3.7</c> read by
/// <c>%d</c> is 3 and leaves <c>.7</c> for the next field), and a failed conversion leaves its target alone.</item>
/// <item>the engine's buffers are NUL-terminated C strings, so a NUL ends the line for every scan -- which is
/// also what stops a hostile file with an embedded NUL from spinning a scanner forever.</item>
/// </list></summary>
internal static class MdlTok
{
    /// <summary>The separators <c>firstword</c> stops on: NUL, tab, LF, CR, space (the engine's
    /// <c>0x100002601</c> bit set over bytes &lt;= 0x20).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSep(byte c) => c == 0 || c == (byte)'\t' || c == (byte)'\n' || c == (byte)'\r' || c == (byte)' ';

    /// <summary>The whitespace <c>sscanf</c> skips before a conversion, and that <c>%s</c> stops on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSpace(byte c) => c == (byte)' ' || (c >= 0x09 && c <= 0x0d);

    /// <summary>Everything up to the first NUL: where a C string ends, and where every scan must stop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> Cstr(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return n < 0 ? s : s[..n];
    }

    /// <summary>The next line of <paramref name="data"/> from <paramref name="at"/>, already trimmed as
    /// <c>dofile</c> trims it; <paramref name="at"/> advances past the break. An empty return is a line
    /// <c>dofile</c> would have dropped (blank or a <c>#</c> comment), and the caller skips it.</summary>
    internal static ReadOnlySpan<byte> NextLine(ReadOnlySpan<byte> data, ref int at)
    {
        int n = data.Length, start = at;
        int end = start;
        while (end < n && data[end] != (byte)'\n' && data[end] != (byte)'\r') end++;
        int after = end;
        while (after < n && data[after] == (byte)'\r') after++;
        if (after < n && data[after] == (byte)'\n') after++;
        at = after;
        while (start < end && (data[start] == (byte)' ' || data[start] == (byte)'\t')) start++;
        while (end > start && IsSpace(data[end - 1])) end--;
        if (start >= end || data[start] == (byte)'#') return default;
        return data[start..end];
    }

    /// <summary>The line's first word -- the engine's <c>firstword</c>.</summary>
    internal static ReadOnlySpan<byte> First(ReadOnlySpan<byte> line)
    {
        int i = 0;
        while (i < line.Length && !IsSep(line[i])) i++;
        return line[..i];
    }

    /// <summary>The rest of the line after its first word, which is what the engine hands
    /// <c>::Parse(line + strlen(keyword), ...)</c>.</summary>
    internal static ReadOnlySpan<byte> Rest(ReadOnlySpan<byte> line)
    {
        int i = 0;
        while (i < line.Length && !IsSep(line[i])) i++;
        return line[i..];
    }

    /// <summary>Case-insensitive ASCII compare against a literal keyword -- the engine's <c>strcasecmp</c>.</summary>
    internal static bool Is(ReadOnlySpan<byte> word, string key)
    {
        if (word.Length != key.Length) return false;
        for (int i = 0; i < key.Length; i++)
        {
            int a = word[i], b = key[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
        }
        return true;
    }

    /// <summary>One <c>sscanf</c> call, field by field: the cursor is shared, conversions stop dead at the
    /// first failure, and <see cref="Matched"/> is sscanf's return value.</summary>
    internal ref struct Scan
    {
        private readonly ReadOnlySpan<byte> _s;
        private int _at;
        private bool _dead;

        internal Scan(ReadOnlySpan<byte> s)
        {
            _s = Cstr(s);
            _at = 0;
            _dead = false;
            Matched = 0;
        }

        /// <summary>How many conversions have succeeded: <c>sscanf</c>'s return value.</summary>
        internal int Matched;

        private void Space() { while (_at < _s.Length && IsSpace(_s[_at])) _at++; }

        /// <summary><c>%f</c>: the longest valid numeric prefix of the field.</summary>
        internal float Flt(float old)
        {
            if (_dead) return old;
            Space();
            int start = _at, end = FloatEnd(_s, start);
            if (end == start
                || !float.TryParse(_s[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            { _dead = true; return old; }
            _at = end;
            Matched++;
            return v;
        }

        /// <summary><c>%d</c>: an optional sign then the longest run of digits.</summary>
        internal int Int(int old)
        {
            if (_dead) return old;
            Space();
            int start = _at, i = start;
            if (i < _s.Length && (_s[i] == (byte)'+' || _s[i] == (byte)'-')) i++;
            int digits = i;
            while (i < _s.Length && _s[i] >= (byte)'0' && _s[i] <= (byte)'9') i++;
            if (i == digits) { _dead = true; return old; }
            if (!int.TryParse(_s[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                // An overflowing field is a match for sscanf (the value is undefined); saturate rather than
                // kill the rest of the call, which is what the engine's callers see.
                v = _s[start] == (byte)'-' ? int.MinValue : int.MaxValue;
            }
            _at = i;
            Matched++;
            return v;
        }

        /// <summary><c>%s</c>: the next run of non-whitespace bytes. Empty means the conversion failed, which
        /// kills the rest of the call as sscanf does.</summary>
        internal ReadOnlySpan<byte> Word()
        {
            if (_dead) return default;
            Space();
            int start = _at;
            while (_at < _s.Length && !IsSpace(_s[_at])) _at++;
            if (_at == start) { _dead = true; return default; }
            Matched++;
            return _s[start.._at];
        }
    }

    /// <summary>The longest prefix of <paramref name="s"/> from <paramref name="at"/> that <c>strtof</c> would
    /// consume: a sign, digits with an optional point, and an exponent only when it is complete. <c>inf</c>,
    /// <c>infinity</c> and <c>nan</c> are accepted as the C library accepts them, so <c>1.#INF</c> reads as
    /// <c>1.</c> and stops at the <c>#</c>.
    /// <para>ponytail: hex floats (<c>0x1p3</c>) are not accepted. glibc's strtof does take them, no
    /// <c>.mdl</c> in either corpus contains one, and accepting them would change what a plain <c>0</c>
    /// followed by an <c>x</c> parses to.</para></summary>
    private static int FloatEnd(ReadOnlySpan<byte> s, int at)
    {
        int i = at;
        if (i < s.Length && (s[i] == (byte)'+' || s[i] == (byte)'-')) i++;
        if (Lit(s, i, "infinity")) return i + 8;
        if (Lit(s, i, "inf")) return i + 3;
        if (Lit(s, i, "nan")) return i + 3;
        int intDigits = 0;
        while (i < s.Length && s[i] >= (byte)'0' && s[i] <= (byte)'9') { i++; intDigits++; }
        int fracDigits = 0;
        if (i < s.Length && s[i] == (byte)'.')
        {
            int dot = i++;
            while (i < s.Length && s[i] >= (byte)'0' && s[i] <= (byte)'9') { i++; fracDigits++; }
            if (intDigits == 0 && fracDigits == 0) i = dot;        // a lone "." is not a number
        }
        if (intDigits == 0 && fracDigits == 0) return at;
        int mantissa = i;
        if (i < s.Length && (s[i] == (byte)'e' || s[i] == (byte)'E'))
        {
            int e = i++;
            if (i < s.Length && (s[i] == (byte)'+' || s[i] == (byte)'-')) i++;
            int expDigits = 0;
            while (i < s.Length && s[i] >= (byte)'0' && s[i] <= (byte)'9') { i++; expDigits++; }
            if (expDigits == 0) i = e;                             // an incomplete exponent is not consumed
            else mantissa = i;
        }
        return mantissa;
    }

    private static bool Lit(ReadOnlySpan<byte> s, int at, string lit)
    {
        if (at + lit.Length > s.Length) return false;
        for (int i = 0; i < lit.Length; i++)
        {
            int a = s[at + i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (a != lit[i]) return false;
        }
        return true;
    }

    /// <summary>Word <paramref name="i"/> of <paramref name="s"/> (0-based), or empty. Independent of any
    /// scan cursor: for a keyword test or a field count, never for a value sscanf reads sequentially.</summary>
    internal static ReadOnlySpan<byte> Word(ReadOnlySpan<byte> s, int i)
    {
        s = Cstr(s);
        int at = 0;
        while (true)
        {
            while (at < s.Length && IsSpace(s[at])) at++;
            if (at >= s.Length) return default;
            int end = at;
            while (end < s.Length && !IsSpace(s[end])) end++;
            if (i-- == 0) return s[at..end];
            at = end;
        }
    }

    /// <summary>How many whitespace-separated words <paramref name="s"/> holds.</summary>
    internal static int Words(ReadOnlySpan<byte> s)
    {
        s = Cstr(s);
        int at = 0, n = 0;
        while (true)
        {
            while (at < s.Length && IsSpace(s[at])) at++;
            if (at >= s.Length) return n;
            while (at < s.Length && !IsSpace(s[at])) at++;
            n++;
        }
    }

    /// <summary><c>Parse(char const*, float&amp;)</c> @0x446770: one <c>sscanf("%f")</c>.</summary>
    internal static float Flt(ReadOnlySpan<byte> s, float old)
    {
        var sc = new Scan(s);
        return sc.Flt(old);
    }

    /// <summary><c>Parse(char const*, int&amp;)</c> @0x446790: <c>sscanf("%d")</c>, and when that matches
    /// nothing the field is re-read as a word and <c>TRUE</c>/<c>FALSE</c> accepted (case-insensitively).
    /// Anything else leaves the field alone.</summary>
    internal static int Int(ReadOnlySpan<byte> s, int old)
    {
        var sc = new Scan(s);
        int v = sc.Int(old);
        if (sc.Matched != 0) return v;
        ReadOnlySpan<byte> w = Word(s, 0);
        if (Is(w, "true")) return 1;
        if (Is(w, "false")) return 0;
        return old;
    }

    /// <summary><c>Parse(char const*, Vector&amp;)</c> @0x4466a0: <c>sscanf("%f %f %f")</c>, which stops at
    /// the first field that does not convert and leaves the rest of the vector alone.</summary>
    internal static Vector3 Vec3(ReadOnlySpan<byte> s, Vector3 old)
    {
        var sc = new Scan(s);
        float x = sc.Flt(old.X), y = sc.Flt(old.Y), z = sc.Flt(old.Z);
        return new Vector3(x, y, z);
    }

    /// <summary><c>Parse(char const*, Vector4&amp;)</c> @0x4466c0: <c>sscanf("%f %f %f %f")</c>.</summary>
    internal static Vector4 Vec4(ReadOnlySpan<byte> s, Vector4 old)
    {
        var sc = new Scan(s);
        float x = sc.Flt(old.X), y = sc.Flt(old.Y), z = sc.Flt(old.Z), w = sc.Flt(old.W);
        return new Vector4(x, y, z, w);
    }

    /// <summary><c>Parse(char const*, char*)</c> @0x446740: <c>sscanf("%s")</c>, lower-cased. The engine
    /// writes an EMPTY string when the conversion matches nothing, so this does too.</summary>
    internal static string Str(ReadOnlySpan<byte> s)
    {
        var sc = new Scan(s);
        ReadOnlySpan<byte> w = sc.Word();
        return w.Length == 0 ? "" : Text(w);
    }

    /// <summary>A word as a lower-cased ASCII string.</summary>
    internal static string Text(ReadOnlySpan<byte> w)
    {
        Span<char> buf = w.Length <= 128 ? stackalloc char[w.Length] : new char[w.Length];
        for (int i = 0; i < w.Length; i++)
        {
            int c = w[i];
            buf[i] = (char)(c >= 'A' && c <= 'Z' ? c + 32 : c);
        }
        return new string(buf);
    }

    /// <summary>A word as an ASCII string with its case kept (a node name, which the engine compares
    /// case-insensitively but stores verbatim).</summary>
    internal static string TextRaw(ReadOnlySpan<byte> w)
    {
        Span<char> buf = w.Length <= 128 ? stackalloc char[w.Length] : new char[w.Length];
        for (int i = 0; i < w.Length; i++) buf[i] = (char)w[i];
        return new string(buf);
    }

    /// <summary><c>Parse(char const*, bool&amp;)</c> @0x446900: FOUR spellings only -- <c>true</c>,
    /// <c>false</c>, <c>1</c>, <c>0</c>, compared case-insensitively as whole words. <c>yes</c>, <c>on</c> and
    /// <c>2</c> all leave the field at its previous value.</summary>
    internal static bool Bool(ReadOnlySpan<byte> s, bool old)
    {
        ReadOnlySpan<byte> w = Word(s, 0);
        if (w.Length == 0) return old;
        if (Is(w, "true") || Is(w, "1")) return true;
        if (Is(w, "false") || Is(w, "0")) return false;
        return old;
    }
}
