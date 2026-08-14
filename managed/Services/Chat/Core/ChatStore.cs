using System;
using System.Collections.Generic;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>
/// Fixed-capacity ring of <see cref="ChatLine"/>s (overwrites oldest). Tab views are materialised at
/// read by walking the ring backward (newest-first), skipping non-matching lines, stopping when the
/// caller's buffer fills -- no per-tab buffers, zero allocation in the walk beyond the caller's slice.
/// </summary>
public sealed class ChatStore
{
    /// <summary>Backlog depth. ponytail: 512 lines is plenty for a chat tab; grow the const if a
    /// use-case needs deeper history (cost is a bigger flat array, nothing else changes).</summary>
    public const int Capacity = 512;

    private readonly ChatLine[] _ring = new ChatLine[Capacity];
    private int _next;   // next write slot
    private int _size;   // valid entries, <= Capacity
    private readonly List<Action<ChatLine>> _observers = new();

    /// <summary>Register an ingest observer, invoked (in add order) for every <see cref="Append"/>.</summary>
    public void AddObserver(Action<ChatLine> observer) => _observers.Add(observer);

    /// <summary>Store <paramref name="line"/> (overwriting the oldest when full), then notify observers.</summary>
    public void Append(ChatLine line)
    {
        _ring[_next] = line;
        _next = (_next + 1) % Capacity;
        if (_size < Capacity) _size++;
        for (int i = 0; i < _observers.Count; i++) _observers[i](line);
    }

    /// <summary>Number of lines currently retained.</summary>
    public int Count => _size;

    /// <summary>Drop all retained lines (a session boundary -- #33). Zero-alloc; drops the ChatLine refs so
    /// the GC can reclaim the prior session's lines.</summary>
    public void Clear()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _next = 0;
        _size = 0;
    }

    /// <summary>How many retained lines match a tab filter (for clamping the scroll offset to the top).</summary>
    public int CountMatching(uint channelMask, uint? playerId)
    {
        int m = 0;
        for (int i = 0; i < _size; i++)
        {
            ChatLine line = _ring[(_next - 1 - i + Capacity * 2) % Capacity];
            if ((channelMask & (1u << (int)(line.Channel & 31))) == 0) continue;
            if (playerId.HasValue && line.PlayerId != playerId.Value) continue;
            m++;
        }
        return m;
    }

    /// <summary>
    /// Fill <paramref name="dest"/> newest-first with lines matching the tab predicate, up to
    /// <c>dest.Length</c>; returns the count written. A line matches when its channel bit is set in
    /// <paramref name="channelMask"/> and (when <paramref name="playerId"/> is given) its player id equals it.
    /// Zero-alloc: the only "slice" is the caller-owned <paramref name="dest"/>.
    /// </summary>
    public int View(uint channelMask, uint? playerId, ChatLine[] dest) => View(channelMask, playerId, dest, 0);

    /// <summary>
    /// As <see cref="View(uint,uint?,ChatLine[])"/>, but first skips the <paramref name="skip"/> newest
    /// matching lines -- the scrollback offset (scroll unit = one matching line). Returns 0 when the
    /// offset lands past the oldest match, so the caller can detect over-scroll and clamp back.
    /// </summary>
    public int View(uint channelMask, uint? playerId, ChatLine[] dest, int skip)
    {
        int n = 0, skipped = 0;
        // walk backward from the newest valid entry
        for (int seen = 0; seen < _size && n < dest.Length; seen++)
        {
            int idx = _next - 1 - seen;
            idx %= Capacity;
            if (idx < 0) idx += Capacity;
            ChatLine line = _ring[idx];

            // ponytail: channels assumed < 32 (they are small engine enums); mask is 1u<<channel.
            if ((channelMask & (1u << (int)(line.Channel & 31))) == 0) continue;
            if (playerId.HasValue && line.PlayerId != playerId.Value) continue;

            if (skipped < skip) { skipped++; continue; }
            dest[n++] = line;
        }
        return n;
    }

    /// <summary>Runnable self-check: ring wrap (oldest overwritten), backward filtered newest-first
    /// walk, and observer firing on Append.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        var store = new ChatStore();

        int fired = 0;
        store.AddObserver(_ => fired++);

        // Fill past capacity so the ring wraps; encode ordinal in RawText/PlayerId.
        int total = Capacity + 5;
        for (int k = 0; k < total; k++)
            store.Append(Line(channel: 1u, playerId: (uint)k, raw: "line" + k));

        ok &= fired == total;                 // observer fired once per append
        ok &= store.Count == Capacity;        // saturated, not overgrown

        var buf = new ChatLine[Capacity];
        int got = store.View(0xffffffff, null, buf);
        ok &= got == Capacity;
        ok &= buf[0].RawText == "line" + (total - 1);   // newest first
        ok &= buf[Capacity - 1].RawText == "line" + (total - Capacity); // oldest surviving = line5 (0..4 overwritten)

        // Channel filter: mix channel 1 and channel 2, ask only for channel 2.
        var s2 = new ChatStore();
        s2.Append(Line(1u, 10, "a"));
        s2.Append(Line(2u, 11, "b"));
        s2.Append(Line(1u, 12, "c"));
        s2.Append(Line(2u, 13, "d"));
        int n2 = s2.View(1u << 2, null, buf);           // bit for channel 2 only
        ok &= n2 == 2 && buf[0].RawText == "d" && buf[1].RawText == "b";

        // PlayerId filter narrows within a channel mask.
        int np = s2.View(0xffffffff, 12u, buf);
        ok &= np == 1 && buf[0].RawText == "c";

        // Match counts (used to clamp the scroll offset to the top).
        ok &= s2.CountMatching(0xffffffff, null) == 4 && s2.CountMatching(1u << 2, null) == 2;

        // Scroll offset skips the newest N matches; over-scroll returns 0 (caller clamps).
        int ns = s2.View(0xffffffff, null, buf, skip: 1);
        ok &= ns == 3 && buf[0].RawText == "c";          // "d" (newest) skipped
        ok &= s2.View(0xffffffff, null, buf, skip: 4) == 0;  // past the oldest

        // Clear drops the whole backlog (session boundary, #33).
        s2.Clear();
        ok &= s2.Count == 0 && s2.View(0xffffffff, null, buf) == 0;

        log.Info(ok ? "ChatStore.SelfCheck: passed" : "ChatStore.SelfCheck: FAILED");
    }

    private static ChatLine Line(uint channel, uint playerId, string raw) =>
        new ChatLine(channel, "", playerId, "", Array.Empty<ColorRun>(), DateTime.UtcNow, raw);
}
