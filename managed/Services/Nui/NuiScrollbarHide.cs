using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// NUI scrollbar visibility on <see cref="SymbolMap.NkDoScrollbarV"/> / <c>H</c>. <see cref="NuiKnobs.AutoHide"/>
/// restores upstream nuklear's <c>if (target &lt;= track) return 0;</c>, which Beamdog removed: a bar whose content
/// fits is neither drawn nor hit-tested. <see cref="NuiKnobs.HoverBars"/> fades one out off-container by scaling
/// the alpha of the call's own draw commands, so offset, glide and clamp stay the engine's. Hot, no allocation.
/// </summary>
internal static unsafe class NuiScrollbarHide
{
    private const int StyleScrollV = 0x3060, StyleScrollH = 0x2bb8, CtxBarSizeX = 0x41ec, CtxBarSizeY = 0x41f0;  // not-an-offset: nk_context ABI (same on both builds), not an engine class
    private const int InMouseX = 0x14c, InLeftDown = 0x10c, InClickedX = 0x114;  // not-an-offset: nk_input ABI
    /// <summary>A scrollbar's <c>out</c> is always <c>&amp;window-&gt;buffer</c> (window+0x68), so the panel being ended is
    /// <c>window-&gt;layout</c> 0x40 past it -- for a group too, whose window is <c>nk_group_scrolled_end</c>'s stack copy.</summary>
    private const int OutLayout = 0x40, PanelBounds = 0x08;  // not-an-offset: nuklear window/panel ABI
    private const int CmdBufEnd = 0x30, BufferMemory = 0x40, BufferAllocated = 0x58, CmdNext = 0x08;  // not-an-offset: nk_command_buffer / nk_buffer / nk_command ABI
    /// <summary>Per <c>nk_command_type</c> 0..18: the first <c>nk_color</c>'s offset, 0 = none. Multi-colour rects carry four
    /// colours and text two, each 4 bytes apart; alpha is a colour's last byte.</summary>
    private static ReadOnlySpan<byte> ColorAt => [0, 0, 0x1a, 0x22, 0x1c, 0x1a, 0x18, 0x1a, 0x18, 0x20, 0x20, 0x1e, 0x1c, 0x10, 0x10, 0x10, 0x18, 0x30, 0];  // not-an-offset: nuklear command ABI
    private const int MultiColor = 6, Text = 16, LastType = 18;
    /// <summary>Seconds to fade fully in, and out.</summary>
    private const float FadeIn = 0.12f, FadeOut = 0.35f;
    /// <summary>A bar unseen this long has closed; its fade slot is free.</summary>
    private const float SlotIdle = 0.5f;
    private const int Slots = 16;

    private struct Fade
    {
        public float X, Y, W, H, Alpha;
        public int Axis;
        public long Seen;
    }

    private static ExtenderLog? _log;
    private static Knob<bool>? _enabled, _auto, _hover;
    private static IntPtr _vOrig, _hOrig, _ctx;
    private static readonly Fade[] _fades = new Fade[Slots];
    private static bool _warned;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enabled = NuiKnobs.Enabled;
        _auto = NuiKnobs.AutoHide;
        _hover = NuiKnobs.HoverBars;
        if (!SymbolMap.Require(log, "NuiScrollbarHide", SymbolMap.NkDoScrollbarV, SymbolMap.NkDoScrollbarH))
        {
            Fail("nk_do_scrollbarv or nk_do_scrollbarh did not resolve");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            _vOrig = hooks.Install(SymbolMap.NkDoScrollbarV, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, float*, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)&OnVWin);
            _hOrig = hooks.Install(SymbolMap.NkDoScrollbarH, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, float*, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)&OnHWin);
        }
        else
        {
            _vOrig = hooks.Install(SymbolMap.NkDoScrollbarV, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, double, double, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)&OnVSysV);
            _hOrig = hooks.Install(SymbolMap.NkDoScrollbarH, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, double, double, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)&OnHSysV);
        }
        if (_vOrig == IntPtr.Zero || _hOrig == IntPtr.Zero) Fail("nk_do_scrollbarv or nk_do_scrollbarh did not hook");
    }

    private static void Fail(string why)
    {
        _log?.Always($"NuiScrollbarHide: {why} -- NUI scrollbars always show, as stock");
        Notice.Show(_log, "NUI scrollbar hiding is off: " + why);
    }

    /// <summary>SysV passes the by-value <c>nk_rect</c> as two SSE eightbytes, (x,y) in xmm0 and (w,h) in xmm1: taken here as
    /// doubles, bit for bit, and handed on the same way.</summary>
    [UnmanagedCallersOnly]
    private static float OnVSysV(IntPtr state, IntPtr output, double xy, double wh, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
        => SysV(_vOrig, 0, state, output, xy, wh, has, offset, target, step, inc, style, input, font);

    [UnmanagedCallersOnly]
    private static float OnHSysV(IntPtr state, IntPtr output, double xy, double wh, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
        => SysV(_hOrig, 1, state, output, xy, wh, has, offset, target, step, inc, style, input, font);

    [UnmanagedCallersOnly]
    private static float OnVWin(IntPtr state, IntPtr output, float* scroll, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
        => Win(_vOrig, 0, state, output, scroll, has, offset, target, step, inc, style, input, font);

    [UnmanagedCallersOnly]
    private static float OnHWin(IntPtr state, IntPtr output, float* scroll, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
        => Win(_hOrig, 1, state, output, scroll, has, offset, target, step, inc, style, input, font);

    private static float SysV(IntPtr orig, int axis, IntPtr state, IntPtr output, double xy, double wh, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
    {
        Unpack(xy, out float x, out float y);
        Unpack(wh, out float w, out float h);
        if (Skip(axis, output, x, y, w, h, target, style, input, out float alpha)) return 0f;
        ulong before = Mark(output);
        float r = ((delegate* unmanaged<IntPtr, IntPtr, double, double, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)orig)(
            state, output, xy, wh, has, offset, target, step, inc, style, input, font);
        if (alpha < 1f) Scale(output, before, alpha);
        return r;
    }

    private static float Win(IntPtr orig, int axis, IntPtr state, IntPtr output, float* scroll, int has, float offset, float target, float step, float inc, IntPtr style, IntPtr input, IntPtr font)
    {
        if (Skip(axis, output, scroll[0], scroll[1], scroll[2], scroll[3], target, style, input, out float alpha)) return 0f;
        ulong before = Mark(output);
        float r = ((delegate* unmanaged<IntPtr, IntPtr, float*, int, float, float, float, float, IntPtr, IntPtr, IntPtr, float>)orig)(
            state, output, scroll, has, offset, target, step, inc, style, input, font);
        if (alpha < 1f) Scale(output, before, alpha);
        return r;
    }

    private static void Unpack(double d, out float lo, out float hi)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        lo = BitConverter.Int32BitsToSingle((int)bits);
        hi = BitConverter.Int32BitsToSingle((int)(bits >> 32));
    }

    /// <summary>True: the content fits, so the bar is skipped and 0 returned, as upstream nuklear does (both callers use
    /// only the return). Otherwise the alpha to draw it at: 1, or the container bar's fade.</summary>
    private static bool Skip(int axis, IntPtr output, float x, float y, float w, float h, float target, IntPtr style, IntPtr input, out float alpha)
    {
        alpha = 1f;
        if (!_enabled!.V) return false;
        if (input != IntPtr.Zero) _ctx = input;
        if (_auto!.V && Fits(axis, w, h, target)) return true;
        if (!_hover!.V || _ctx == IntPtr.Zero || (style != _ctx + StyleScrollV && style != _ctx + StyleScrollH)) return false;
        alpha = Advance(ref Slot(x, y, w, h, axis), Wanted(axis, output, x, y, w, h));
        return false;
    }

    /// <summary>Upstream's guard, taken on the full track before the <c>show_buttons</c> shortening.</summary>
    internal static bool Fits(int axis, float w, float h, float target) =>
        axis == 0 ? target <= MathF.Max(h, 0f) : target <= MathF.Max(w, 2f * MathF.Max(h, 1f));

    /// <summary>Wanted visible: the pointer is over the container (its content, this bar, and the other bar's gutter),
    /// or a drag that started on this bar is still held.</summary>
    private static bool Wanted(int axis, IntPtr output, float x, float y, float w, float h)
    {
        float mx = *(float*)(_ctx + InMouseX), my = *(float*)(_ctx + InMouseX + 4);
        if (*(int*)(_ctx + InLeftDown) != 0)
        {
            float cx = *(float*)(_ctx + InClickedX), cy = *(float*)(_ctx + InClickedX + 4);
            if (cx >= x && cx < x + w && cy >= y && cy < y + h) return true;
        }
        IntPtr panel = *(IntPtr*)(output + OutLayout);
        if (panel == IntPtr.Zero) return true;
        float* b = (float*)(panel + PanelBounds);
        float x0 = MathF.Min(b[0], x), y0 = MathF.Min(b[1], y);
        float x1 = MathF.Max(b[0] + b[2], x + w), y1 = MathF.Max(b[1] + b[3], y + h);
        if (axis == 0) y1 += *(float*)(_ctx + CtxBarSizeY);
        else x1 += *(float*)(_ctx + CtxBarSizeX);
        return mx >= x0 && mx < x1 && my >= y0 && my < y1;
    }

    /// <summary>One frame of the fade, linear, from the slot's last seen time; a new slot starts where it is wanted.</summary>
    private static float Advance(ref Fade f, bool wanted)
    {
        long now = Stopwatch.GetTimestamp();
        if (f.Seen == 0) f.Alpha = wanted ? 1f : 0f;
        float dt = f.Seen == 0 ? 0f : Math.Clamp((now - f.Seen) / (float)Stopwatch.Frequency, 0f, 0.1f);
        f.Seen = now;
        f.Alpha = wanted ? MathF.Min(1f, f.Alpha + dt / FadeIn) : MathF.Max(0f, f.Alpha - dt / FadeOut);
        return f.Alpha;
    }

    /// <summary>The fade slot of the bar with this track rect (it does not move while the window stays put), or the
    /// stalest slot, reset.</summary>
    private static ref Fade Slot(float x, float y, float w, float h, int axis)
    {
        long now = Stopwatch.GetTimestamp();
        int free = 0;
        long oldest = long.MaxValue;
        for (int i = 0; i < Slots; i++)
        {
            ref Fade f = ref _fades[i];
            if (f.Seen != 0 && f.Axis == axis && f.X == x && f.Y == y && f.W == w && f.H == h
                && (now - f.Seen) / (float)Stopwatch.Frequency < SlotIdle) return ref f;
            if (f.Seen < oldest) { oldest = f.Seen; free = i; }
        }
        ref Fade n = ref _fades[free];
        n = new Fade { X = x, Y = y, W = w, H = h, Axis = axis };
        return ref n;
    }

    /// <summary>Where the call's first command will land: the shared buffer's front, 8-aligned as
    /// <c>nk_command_buffer_push</c> aligns it. Not the window's own end -- a popup built inside the window may have
    /// pushed past it.</summary>
    private static ulong Mark(IntPtr output)
    {
        byte* buf = *(byte**)output;
        ulong ptr = *(ulong*)(buf + BufferMemory);
        return ((ptr + *(ulong*)(buf + BufferAllocated) + 7) & ~7UL) - ptr;
    }

    /// <summary>Scales the alpha of every command appended since <paramref name="from"/>. The buffer is dynamic and may
    /// have moved during the call, so it is walked by offset from the re-read memory pointer, as <c>nk__next</c> does.</summary>
    private static void Scale(IntPtr output, ulong from, float alpha)
    {
        byte* mem = *(byte**)(*(IntPtr*)output + BufferMemory);
        ulong end = *(ulong*)(output + CmdBufEnd);
        for (ulong off = from; off < end;)
        {
            byte* cmd = mem + off;
            int type = *(int*)cmd;
            if ((uint)type > LastType)
            {
                if (!_warned) { _warned = true; _log?.Always($"NuiScrollbarHide: unknown nuklear command type {type} -- that bar draws at full opacity"); }
                return;
            }
            int at = ColorAt[type], n = type == MultiColor ? 4 : type == Text ? 2 : at != 0 ? 1 : 0;
            for (int c = 0; c < n; c++)
            {
                byte* a = cmd + at + 4 * c + 3;
                *a = (byte)(*a * alpha + 0.5f);
            }
            ulong next = *(ulong*)(cmd + CmdNext);
            if (next <= off) return;
            off = next;
        }
    }

    /// <summary>Off-device: the fit test on both axes, the SysV rect unpacking, and the alpha walk over a hand-built
    /// dynamic command buffer (a filled rect and an image, then text) -- including a buffer that moved.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Fits(0, 8, 100, 100) && !Fits(0, 8, 100, 100.5f), "vertical: fits up to the track length");
        check.Expect(Fits(1, 100, 8, 100) && !Fits(1, 100, 8, 101) && Fits(1, 4, 8, 16), "horizontal: at least twice the height");
        long bits = (long)(uint)BitConverter.SingleToInt32Bits(12.5f) | (long)BitConverter.SingleToInt32Bits(-3f) << 32;
        Unpack(BitConverter.Int64BitsToDouble(bits), out float lo, out float hi);
        check.Expect(lo == 12.5f && hi == -3f, "an xmm-packed (x, y) unpacks bit for bit");

        byte* mem = (byte*)NativeMemory.AllocZeroed(512);
        byte* buf = (byte*)NativeMemory.AllocZeroed(0x78);
        byte* cb = (byte*)NativeMemory.AllocZeroed(0x40);
        try
        {
            *(IntPtr*)cb = (IntPtr)buf;
            *(IntPtr*)(buf + BufferMemory) = (IntPtr)mem;
            void Cmd(int off, int type, int next, uint color, int at)
            {
                *(int*)(mem + off) = type;
                *(ulong*)(mem + off + CmdNext) = (ulong)next;
                if (at != 0) *(uint*)(mem + off + at) = color;
            }
            Cmd(0, 5, 0x20, 0xff102030, 0x1a);
            *(ulong*)(buf + BufferAllocated) = 0x1c;
            ulong from = Mark((IntPtr)cb);
            Cmd(0x20, 5, 0x40, 0xff102030, 0x1a);
            Cmd(0x40, 17, 0x78, 0x80ffffff, 0x30);
            Cmd(0x78, 16, 0xb8, 0xffffffff, 0x18);
            *(uint*)(mem + 0x78 + 0x1c) = 0xff00ff00;
            *(ulong*)(cb + CmdBufEnd) = 0xb8;
            Scale((IntPtr)cb, from, 0.5f);
            check.Expect(from == 0x20 && mem[0x1a + 3] == 0xff, "the mark is the aligned front; a command before it is untouched");
            check.Expect(mem[0x20 + 0x1a + 3] == 0x80 && mem[0x20 + 0x1a] == 0x30, "a filled rect's alpha halves, its colour stays");
            check.Expect(mem[0x40 + 0x30 + 3] == 0x40, "an image's alpha halves");
            check.Expect(mem[0x78 + 0x18 + 3] == 0x80 && mem[0x78 + 0x1c + 3] == 0x80, "text: background and foreground both");
            byte* moved = (byte*)NativeMemory.AllocZeroed(512);
            Buffer.MemoryCopy(mem, moved, 512, 512);
            *(IntPtr*)(buf + BufferMemory) = (IntPtr)moved;
            Scale((IntPtr)cb, 0x40, 0f);
            check.Expect(moved[0x40 + 0x30 + 3] == 0 && mem[0x40 + 0x30 + 3] == 0x40, "the walk follows a buffer that moved");
            NativeMemory.Free(moved);
        }
        finally
        {
            NativeMemory.Free(mem);
            NativeMemory.Free(buf);
            NativeMemory.Free(cb);
        }
        log.Info(check.Ok ? "NuiScrollbarHide.SelfCheck: passed" : "NuiScrollbarHide.SelfCheck: FAILED");
    }
}
