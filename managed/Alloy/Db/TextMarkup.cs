using System;

namespace CX.Alloy.Db;

/// <summary>The engine's in-string markup, <c>CAurFont::ProcessTextMarkup</c> @0x496070: exactly two tokens,
/// <c>&lt;c</c> + three raw bytes + <c>&gt;</c> (6 bytes) and <c>&lt;/c</c> (4 bytes, closing <c>&gt;</c>
/// optional); anything else is a literal glyph, and markup consumes no width. The colour stack is the engine's
/// own PROCESS-WIDE 16-entry ring, mutated by measuring too, so an unbalanced token leaks into the next.</summary>
public static class TextMarkup
{
    private const int Depth = 16;
    private static readonly float[] Stack = NewStack();
    private static int _index;

    private static float[] NewStack()
    {
        var s = new float[Depth * 4];
        for (int i = 0; i < s.Length; i++) s[i] = 1f;
        return s;
    }

    /// <summary>Put the ring back to its one-time-initialised state (all white, index 0). Not something the engine
    /// does -- only the self-check, so one case cannot bleed into the next.</summary>
    internal static void ResetStack()
    {
        for (int i = 0; i < Stack.Length; i++) Stack[i] = 1f;
        _index = 0;
    }

    /// <summary>Try to read a token at <paramref name="at"/>. Returns the bytes consumed (6, 4) or 0 when this is
    /// not markup. On 6 the out colour is the token's; on 4 it is the popped one; on 0 the outs are the inputs.
    /// <paramref name="r"/>..<paramref name="a"/> carry the CURRENT colour in and the new one out.</summary>
    public static int Process(ReadOnlySpan<byte> s, int at, ref float r, ref float g, ref float b, ref float a)
    {
        if (At(s, at + 1) == (byte)'/')
        {
            if (At(s, at + 2) != (byte)'c') return 0;
            _index = (_index + Depth - 1) % Depth;
            int o = _index * 4;
            r = Stack[o]; g = Stack[o + 1]; b = Stack[o + 2]; a = Stack[o + 3];
            return 4;
        }
        if (At(s, at + 1) != (byte)'c') return 0;
        byte cr = At(s, at + 2), cg = At(s, at + 3), cb = At(s, at + 4);
        if (cr == 0 || cg == 0 || cb == 0) return 0;
        if (At(s, at + 5) != (byte)'>') return 0;
        int p = _index * 4;
        Stack[p] = r; Stack[p + 1] = g; Stack[p + 2] = b; Stack[p + 3] = a;
        _index = (_index + 1) % Depth;
        r = cr / 255f; g = cg / 255f; b = cb / 255f; a = 1f;   // the engine forces alpha to 1
        return 6;
    }

    /// <summary>The engine parses a NUL-terminated buffer, so a read past the end is a zero byte, which aborts
    /// the token. A span has no terminator, so one is supplied here.</summary>
    private static byte At(ReadOnlySpan<byte> s, int k) => (uint)k < (uint)s.Length ? s[k] : (byte)0;

    /// <summary>The measuring entry: the measure pass runs the same parser -- and so mutates the same global
    /// ring, exactly as the engine does -- but never uses the colour it produces.</summary>
    public static int Skip(ReadOnlySpan<byte> s, int at)
    {
        float r = 1f, g = 1f, b = 1f, a = 1f;
        return Process(s, at, ref r, ref g, ref b, ref a);
    }
}
