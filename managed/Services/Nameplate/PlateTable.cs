using System;
using System.Collections.Generic;
using System.Text;
using Sowx.Framework;
using Sowx.Services.Chat.Core;

using Sowx.Ui;

namespace Sowx.Services.Nameplate;

/// <summary>
/// The id-keyed table of overhead elements: what the hooks recorded, cold-resolved once, and what the
/// frame is allowed to draw. Preallocated end to end -- <see cref="Refresh"/> and everything downstream
/// of it allocate nothing.
/// <para>Two threads touch this. The hooks run on the message/input thread and only ever <b>append</b> to
/// a fixed ring (<see cref="Note"/> / <see cref="NoteBubble"/>), doing every string resolve, colour parse
/// and marshalling there. The render thread drains that ring at the top of <see cref="Refresh"/> and owns
/// the table itself, so the hot walk needs no synchronisation of its own.</para>
/// <para>The join key is the object <b>id</b>, never a <c>CNWCObject*</c> (#33/#138/#145). A
/// <c>GetGameObject</c> miss is the despawn signal and evicts the entry outright.</para>
/// </summary>
public sealed class PlateTable
{
    /// <summary>Objects we will track at once. Well past what a screenful of hover + Tab can produce; a
    /// full table refuses new candidates rather than growing on a frame.</summary>
    public const int MaxEntries = 64;

    /// <summary>Bubble lines kept per object. The config's <c>BubbleQueue</c> clamps to this.</summary>
    public const int MaxLines = 4;

    /// <summary>Wrapped lines kept per bubble line. The config's <c>BubbleLines</c> clamps to this. 32 is the
    /// figure the RE notes cite for the engine's own bubble (still unverified, docs/re/nameplate.md); it is far
    /// enough past a paragraph-length emote that nothing vanilla would have shown is cut by us.</summary>
    public const int MaxWrapped = 32;

    /// <summary>How long a bubble line holds at full opacity, as a fraction of its decay; the rest is the fade
    /// tail. The shape the top line already faded on, now applied to every line in the queue.</summary>
    public const float HoldFraction = 0.75f;

    /// <summary>Pending records the hooks may bank between two frames. Overflow is counted, not grown.</summary>
    private const int PendingRing = 128;

    /// <summary>Segments of an <c>ObjectTextBubbleOverride</c> we keep. The server end composes at most a
    /// damage row plus a status row (<c>sow-codebase TextBubbleService.ComposeOverride</c>); this is generous
    /// headroom over that, and a longer override keeps its first <see cref="MaxParas"/> segments.
    /// <para>ponytail: the ceiling is the para pool, not anything engine-side -- raise the const if a server
    /// ever sends more rows.</para></summary>
    public const int MaxParas = 6;

    /// <summary>One overhead chat line: the cold-resolved runes and per-rune colours, plus the wrap cache
    /// keyed on everything that can change its shape.</summary>
    public sealed class BubbleLine
    {
        /// <summary>Allocation-avoidance threshold, not a limit: a longer line grows both buffers ONCE, on the
        /// drain, and no rune is ever dropped. The pool is <c>MaxEntries * MaxLines</c> = 256 lines, so this
        /// costs ~2 MB; 1024 runes is past anything the chat channel realistically carries.</summary>
        private const int Prealloc = 1024;

        public readonly List<Rune> Runes = new(Prealloc);
        public uint[] Colours = new uint[Prealloc];
        public long PostedMs;
        /// <summary>Seconds at full opacity before the fade tail starts, from <see cref="PostedMs"/>.</summary>
        public float HoldSec;
        /// <summary>Length of the fade tail, seconds. Zero means the line drops on the frame the hold ends.</summary>
        public float FadeSec;
        public int Channel = -1;

        /// <summary>Per-line opacity 0..1, recomputed every frame from the clock. The line lives until this
        /// reaches 0 -- so a line past its hold is still drawn, fading, rather than vanishing.</summary>
        public float Alpha;
        /// <summary>Set once the line is in its fade tail, naturally or because a newcomer evicted it. A fading
        /// line no longer counts against the queue cap, which is what lets the cap hold a fading line beyond it.</summary>
        public bool Fading;
        /// <summary>The wrap dropped lines past the cap, so the last kept line ends in an ellipsis.</summary>
        public bool Truncated;

        /// <summary>Put the line into its fade tail starting now. Idempotent: a line already fading keeps the
        /// tail it is part way through rather than restarting it.</summary>
        public void BeginFade(long nowMs, float fadeSec)
        {
            if (Fading) return;
            float age = (nowMs - PostedMs) / 1000f;
            HoldSec = age > 0f ? age : 0f;
            FadeSec = fadeSec;
            Fading = true;
        }

        /// <summary>Wrapped spans, valid while <see cref="WrapWidth"/> / <see cref="WrapEpoch"/> /
        /// <see cref="WrapScale"/> still match the frame.</summary>
        public TextWrapper.LineSpan[] Spans = new TextWrapper.LineSpan[MaxWrapped];
        public int SpanCount;
        public float WrapWidth = -1f, WrapScale = -1f;
        public int WrapEpoch = -1;
        /// <summary>Wrapped-line cap the cached spans were cut to. In the key because the cap is a LIVE knob
        /// (the settings slider): without it, dragging it rewraps nothing until the width or scale changes.</summary>
        public int WrapCap = -1;
        public float WrapPixelW;
    }

    /// <summary>One tracked object. A reference type held in a preallocated pool: <see cref="Used"/> is the
    /// only liveness bit, so an eviction costs no allocation and a re-sight reuses the slot.</summary>
    public sealed class Entry
    {
        public bool Used;
        public uint Id;
        public byte Type;
        public byte Reaction;

        // ---- cold-resolved, once per sighting ----
        /// <summary>The plate name, already through <see cref="PlateLayout.BreakName"/>: LF is the only
        /// break, and <see cref="NameStart"/>/<see cref="NameLen"/> index its lines. Set through
        /// <see cref="SetName"/> so the spans can never disagree with the string.</summary>
        public string Name = "";
        /// <summary>Spans of <see cref="Name"/>'s lines, so the frame measures and emits each one without a
        /// substring. Capped at <see cref="MaxWrapped"/> -- the same ceiling the bubble wrap uses.
        /// <para>These are the WRAPPED lines once <see cref="EnsureNameWrap"/> has run: a wrap-introduced break
        /// is a span boundary and nothing else, so <see cref="Name"/> itself still carries only the author's
        /// own breaks and everything reading the string (the <c>/tell</c> prefill) is unaffected.</para></summary>
        public readonly int[] NameStart = new int[MaxWrapped];
        public readonly int[] NameLen = new int[MaxWrapped];
        public int NameLineCount;
        /// <summary>The name wrapped past <see cref="MaxWrapped"/> lines, so the last kept line ends in
        /// <see cref="Ellipsis"/> (already trimmed to leave room for it).</summary>
        public bool NameTruncated;
        /// <summary>Wrap-cache key for the name spans, mirroring <see cref="BubbleLine"/>'s: the width, the
        /// glyph-atlas epoch and <see cref="Metrics.Scale"/>. -1 means "unwrapped", which is what
        /// <see cref="SetName"/> resets them to.</summary>
        public float NameWrapWidth = -1f, NameWrapScale = -1f;
        public int NameWrapEpoch = -1;
        public string PortraitResref = "";

        // ---- the module's ObjectTextBubbleOverride (docs/re/nameplate.md §8.5) --------------------
        /// <summary>1 replace, 2 prepend, 3 append, 0 none. Polled every frame in <see cref="Refresh"/>, not
        /// latched on the hook: a player starts sneaking WHILE hovered and the engine re-runs nothing.</summary>
        public int OverrideMode;
        /// <summary>The override verbatim, untrimmed -- the cache key the poll compares against.</summary>
        public string OverrideText = "";
        /// <summary>The override's LF-separated segments, colour-parsed. In <see cref="OverrideMode"/> 3 the
        /// FIRST is the splice that continues the plate's last line and the rest are lines below it; in mode 2
        /// the LAST is the splice that prefixes the name and the rest are lines above it; in mode 1 they are
        /// the whole plate text. Reuses <see cref="BubbleLine"/> for its runes, colours and wrap cache -- these
        /// never expire, so nothing ages them.</summary>
        public readonly BubbleLine[] Paras = new BubbleLine[MaxParas];
        public int ParaCount;

        // ---- the bubble queue, oldest first ----
        public readonly BubbleLine[] Lines = new BubbleLine[MaxLines];
        public int LineCount;

        // ---- per-frame, filled by Refresh ----
        public float AnchorX, AnchorY;
        public bool Plated;
        public int Damage = -1;
        public float Distance;
        public float DistanceFade;
        /// <summary>Set on the frame the entry is drawn as a bubble rather than a plate.</summary>
        public bool ShowBubble;
        /// <summary>Index of the newest live bubble line, or -1.</summary>
        public int TopLine = -1;

        // ---- animation, advanced by Refresh and consumed by the draw ------------------------
        /// <summary>True on the frames the trigger still holds (hovered/Tab-plated, or a live bubble line).
        /// The entry outlives it: it is what <see cref="Alpha"/> eases toward, and only alpha reaching 0
        /// releases the slot.</summary>
        public bool Alive;
        /// <summary>Presence alpha 0..1, eased toward <see cref="Alive"/>. Multiplied into the draw alpha, so a
        /// plate fades in and out instead of popping.</summary>
        public float Alpha;
        /// <summary>Vertical slide residual in draw px, added to the placed box and eased to zero: positive
        /// means "still below where it belongs". Seeded on a newcomer and topped up whenever the measured box
        /// grows, which is how a bubble pushed up the stack animates instead of jumping.</summary>
        public float YOff;
        /// <summary>Last frame's measured box height, to detect the growth the slide absorbs. 0 = unmeasured.</summary>
        public float LastH;
        /// <summary>Set on a freshly claimed slot: the next draw seeds the entry slide and clears it.</summary>
        public bool AnimNew;
        /// <summary>Last frame's separation push in draw px (negative = shoved upward). Kept so a slot change
        /// can be absorbed into <see cref="YOff"/> (<c>PlateLayout.AbsorbPush</c>) instead of teleporting, and
        /// so a box that steps out of separation while <see cref="FadingOut"/> keeps the slot it died in.</summary>
        public float LastPush;

        /// <summary>True while this box is on its way OUT: the trigger has dropped and the presence alpha is
        /// easing to zero, or the bubble's newest line is already in its fade tail. Such a box is excluded from
        /// separation entirely -- it neither pushes nor is pushed -- so sweeping the cursor across a crowd leaves
        /// no trail of dying plates shoving the live one around.
        /// <para>Specifically NOT "alpha &lt; 1": a box fading IN is a real, arriving box and takes part
        /// normally. It reads the fade state the entry already tracks rather than inferring it.</para></summary>
        public bool FadingOut => !Alive || (ShowBubble && (TopLine < 0 || Lines[TopLine].Fading));

        /// <summary>The instant this box's content was sent, the key the stack is ordered on. A bubble answers
        /// with its NEWEST line, so an object that keeps talking keeps its place at the bottom of the stack. A
        /// plate has no send time and answers <see cref="long.MaxValue"/> -- a live hover is happening now, so it
        /// sorts ahead of every message and sits nearest its own object.</summary>
        public long SortKey => ShowBubble && TopLine >= 0 ? Lines[TopLine].PostedMs : long.MaxValue;

        public Entry()
        {
            for (int i = 0; i < MaxLines; i++) Lines[i] = new BubbleLine();
            for (int i = 0; i < MaxParas; i++) Paras[i] = new BubbleLine();
        }

        /// <summary>Release the slot. Every animation field goes with it: a pooled entry that kept a
        /// mid-fade alpha would lend it to whatever object claims the slot next.</summary>
        public void Release()
        {
            Used = false; LineCount = 0; TopLine = -1; Damage = -1;
            OverrideMode = 0; OverrideText = ""; ParaCount = 0;
            Alive = false; ShowBubble = false;
            Alpha = 0f; YOff = 0f; LastH = 0f; LastPush = 0f; AnimNew = true;
            // The pooled lines outlive the entry, so their fade state has to go too: a slot that kept a
            // half-faded line would lend it to whatever object claims the slot next.
            for (int i = 0; i < MaxLines; i++) { Lines[i].Alpha = 0f; Lines[i].Fading = false; }
        }
    }

    private readonly Entry[] _entries = new Entry[MaxEntries];
    private readonly Entry[] _visible = new Entry[MaxEntries];
    private int _visibleCount;

    /// <summary>The entries the frame should draw, compacted by the last <see cref="Refresh"/>.</summary>
    public Entry[] Visible => _visible;
    public int VisibleCount => _visibleCount;

    /// <summary>How many candidates were refused because the table was full. Nonzero says the ceiling is
    /// real; it is reported by <c>/nameplate</c>.</summary>
    public long Overflow { get; private set; }

    public PlateTable() { for (int i = 0; i < MaxEntries; i++) _entries[i] = new Entry(); }

    // ---- the hook-side ring -------------------------------------------------------------------

    private struct Pending
    {
        public uint Id;
        public byte Type;
        public byte Reaction;
        public bool IsPlate;
        public string? Name;
        public string? Portrait;
        public ColorRun[]? Runs;
        public float DecaySec;
        public int Channel;
        public long PostedMs;
    }

    private readonly Pending[] _pending = new Pending[PendingRing];
    private int _pendHead, _pendTail;
    private readonly object _pendLock = new();

    private void Bank(in Pending p)
    {
        lock (_pendLock)
        {
            int next = (_pendHead + 1) % PendingRing;
            if (next == _pendTail) { Overflow++; return; }   // full: drop the newest, never grow on a hook
            _pending[_pendHead] = p;
            _pendHead = next;
        }
    }

    /// <summary>COLD, from the <c>ShowFeedbackBubbleText</c> path: this object wants a plate. Name, portrait
    /// and reaction are resolved here, on the hook's thread, and never re-read on a frame.</summary>
    public void Note(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero) return;
        Pending p = default;
        p.Id = NameplateNative.IdOf(obj);
        p.Type = type;
        p.Reaction = NameplateNative.Reaction(obj, type);
        p.IsPlate = true;
        p.Name = ResolveName(obj, type, p.Id);
        p.Portrait = NameplateNative.Portrait(obj);
        Bank(in p);
    }

    /// <summary>The creature's stats name, else the engine's own object name -- placeables, doors and items
    /// have no creature stats and would otherwise plate as an empty box. COLD.</summary>
    private static string ResolveName(IntPtr obj, byte type, uint id)
    {
        string n = NameplateNative.CreatureName(obj, type);
        if (n.Length == 0) n = NameplateNative.ObjectName(id);
        return PlateLayout.BreakName(n);   // COLD: the only place a name acquires its line breaks
    }

    /// <summary>Set an entry's name and re-index its lines together. The one writer of <see cref="Entry.Name"/>:
    /// a slot that kept the previous tenant's spans would draw this one's name in that one's shape. The spans
    /// written here are the author's breaks alone; <see cref="EnsureNameWrap"/> replaces them with the wrapped
    /// ones the first time a frame measures the entry, and this invalidates its cache.
    /// <para>A re-set of the SAME name is a no-op. Every plate <c>Note</c> and every chat line carries a
    /// freshly resolved name, and <c>ShowFeedbackBubbleText</c> can be re-entered for an object already
    /// hovered -- the Tab path bypasses the <c>creature+0x468</c> guard that stops the hover path repeating
    /// (docs/re/nameplate.md §6.3) -- so re-invalidating on each would rewrap, and allocate, for nothing.</para></summary>
    internal static void SetName(Entry e, string name)
    {
        if (string.Equals(e.Name, name, StringComparison.Ordinal)) return;
        e.Name = name;
        e.NameLineCount = PlateLayout.SplitLines(name, e.NameStart, e.NameLen, MaxWrapped);
        e.NameTruncated = false;
        e.NameWrapWidth = -1f;   // invalidate the wrap cache: the content changed
    }

    /// <summary>Scratch for <see cref="SetOverride"/>'s segment spans. Render thread only -- the poll that
    /// calls it lives in <see cref="Refresh"/>, and nothing on the hook side touches the override.</summary>
    private static readonly int[] _segStart = new int[MaxParas], _segLen = new int[MaxParas];

    /// <summary>Adopt the module's <c>ObjectTextBubbleOverride</c> for this entry: split it into segments and
    /// colour-parse each. A re-set of the same (mode, text) is a no-op, so the per-frame poll that calls this
    /// pays nothing until a server actually flips a status.
    /// <para>COLD-on-change, but on the RENDER thread -- unlike every other resolve here, there is no hook to
    /// do it on. The engine receives the override inside <c>HandleServerToPlayerCreatureUpdate_Update</c>
    /// (disasm @0x7a268a) and <c>...GenericObjectUpdate_Update</c> (@0x7a73f9), neither of which is a symbol
    /// whose arguments name the object, so a hook there would be a guess. The allocation is one split plus one
    /// parse per status change per plated object, which is a keypress, not a frame.</para>
    /// <para>Each segment is parsed on its own, so a <c>&lt;cRGB&gt;</c> opened before a line break does not
    /// carry across it. The server end wraps every label individually, so nothing real relies on that.</para></summary>
    internal static void SetOverride(Entry e, int mode, string text)
    {
        if (e.OverrideMode == mode && string.Equals(e.OverrideText, text, StringComparison.Ordinal)) return;
        e.OverrideMode = mode;
        e.OverrideText = text;
        e.ParaCount = 0;
        if (mode is < 1 or > 3 || text.Length == 0) return;

        int n = PlateLayout.SplitOverride(text, _segStart, _segLen, MaxParas);
        for (int i = 0; i < n; i++)
            Fill(e.Paras[i], ColorRunParser.Parse(text.Substring(_segStart[i], _segLen[i])), -1, 0f, 0L);
        e.ParaCount = n;
    }

    /// <summary>COLD, from the <c>SetPopUpBubbleText</c> path when the text is a chat line rather than the
    /// plate's own: queue it against this object. The Aurora colour grammar is parsed here.</summary>
    public void NoteBubble(IntPtr obj, byte type, string text, float decaySec, int channel, long nowMs)
    {
        if (obj == IntPtr.Zero || text.Length == 0) return;
        Pending p = default;
        p.Id = NameplateNative.IdOf(obj);
        p.Type = type;
        p.Reaction = NameplateNative.Reaction(obj, type);
        p.IsPlate = false;
        p.Name = ResolveName(obj, type, p.Id);
        p.Portrait = NameplateNative.Portrait(obj);
        p.Runs = ColorRunParser.Parse(text);   // the Aurora colour grammar, parsed HERE: cold, off the frame
        p.DecaySec = decaySec;
        p.Channel = channel;
        p.PostedMs = nowMs;
        Bank(in p);
    }

    /// <summary>Drop everything. The module-unload edge: every id names a dead world. The cold-resolved
    /// strings go with it -- a pooled entry that kept its portrait resref would lend it to whatever object
    /// claims the slot next (an object whose own <c>GetPortrait</c> returns "" never overwrites it).</summary>
    public void Clear()
    {
        lock (_pendLock) { _pendHead = 0; _pendTail = 0; }
        for (int i = 0; i < MaxEntries; i++)
        {
            Entry e = _entries[i];
            e.Release(); SetName(e, ""); e.PortraitResref = "";
        }
        _visibleCount = 0;
    }

    // ---- draining -----------------------------------------------------------------------------

    /// <summary>Apply everything the hooks banked, without walking the table. The frame calls this even on the
    /// frames it decides not to draw: the ring is fixed-size, so a draw decision that skipped the drain would
    /// let it fill and turn <see cref="Overflow"/> into a count of frames rather than of refused candidates.</summary>
    /// <param name="animMs">the fade a line evicted by a newcomer gets (<c>NameplateConfig.AnimMs</c>): the box
    /// reflows at that cadence, so the line leaving it does too.</param>
    public void Drain(int queueCap, float animMs)
    {
        while (true)
        {
            Pending p;
            lock (_pendLock)
            {
                if (_pendTail == _pendHead) return;
                p = _pending[_pendTail];
                _pendTail = (_pendTail + 1) % PendingRing;
            }
            Apply(in p, queueCap, animMs);
        }
    }

    private Entry? Find(uint id)
    {
        for (int i = 0; i < MaxEntries; i++)
            if (_entries[i].Used && _entries[i].Id == id) return _entries[i];
        return null;
    }

    private Entry? Claim(uint id)
    {
        for (int i = 0; i < MaxEntries; i++)
            if (!_entries[i].Used)
            {
                Entry e = _entries[i];
                e.Release();                          // clears the previous tenant's animation state too
                e.Used = true; e.Id = id;
                SetName(e, ""); e.PortraitResref = "";   // a portraitless newcomer never overwrites these
                return e;
            }
        Overflow++;
        return null;
    }

    private void Apply(in Pending p, int queueCap, float animMs = 0f)
    {
        Entry? e = Find(p.Id) ?? Claim(p.Id);
        if (e == null) return;
        e.Type = p.Type;
        e.Reaction = p.Reaction;
        if (p.Name != null) SetName(e, p.Name);
        if (p.Portrait != null && p.Portrait.Length != 0) e.PortraitResref = p.Portrait;
        if (p.IsPlate || p.Runs == null) return;

        int cap = queueCap < 1 ? 1 : (queueCap > MaxLines ? MaxLines : queueCap);
        // Evict by starting a FADE, not by overwriting: a line pushed out of the queue leaves the way it would
        // have died of old age. A fading line no longer counts against the cap, so it can be held past it --
        // MaxLines is the real ceiling. Loop, not a single step: a cap dragged down mid-life leaves more than
        // one line over it, and the oldest must go first.
        while (LiveCount(e) >= cap)
        {
            int oldest = -1;
            for (int i = 0; i < e.LineCount; i++) if (!e.Lines[i].Fading) { oldest = i; break; }
            if (oldest < 0) break;
            e.Lines[oldest].BeginFade(p.PostedMs, animMs / 1000f);
        }

        // Only when the array itself is full is a line dropped hard -- the newcomer is never refused, and the
        // casualty is the oldest, which is already deep in its fade.
        if (e.LineCount >= MaxLines)
        {
            BubbleLine gone = e.Lines[0];
            for (int i = 0; i + 1 < e.LineCount; i++) e.Lines[i] = e.Lines[i + 1];
            e.Lines[e.LineCount - 1] = gone;
            e.LineCount--;
        }
        BubbleLine ln = e.Lines[e.LineCount];
        Fill(ln, p.Runs, p.Channel, p.DecaySec, p.PostedMs);
        e.LineCount++;
    }

    /// <summary>Lines still counted against the queue cap -- everything not already in its fade tail.</summary>
    private static int LiveCount(Entry e)
    {
        int n = 0;
        for (int i = 0; i < e.LineCount; i++) if (!e.Lines[i].Fading) n++;
        return n;
    }

    /// <summary>Flatten already-parsed colour runs into the line's rune + per-rune-colour buffers. Called
    /// from the drain, so it must not parse or allocate: the runs arrive pre-parsed from the hook thread and
    /// the buffers are preallocated.</summary>
    private static void Fill(BubbleLine ln, ColorRun[] runs, int channel, float decaySec, long postedMs)
    {
        ln.Runes.Clear();
        ln.Channel = channel;
        ln.HoldSec = decaySec * HoldFraction;
        ln.FadeSec = decaySec - ln.HoldSec;
        ln.PostedMs = postedMs;
        ln.Alpha = 1f;
        ln.Fading = false;
        ln.Truncated = false;
        ln.WrapWidth = -1f;   // invalidate the wrap cache: the content changed

        int need = 0;
        for (int i = 0; i < runs.Length; i++) need += runs[i].Text.Length;
        if (ln.Colours.Length < need + 1) ln.Colours = new uint[need + 1];

        int n = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            uint rgb = Pack(runs[i].R, runs[i].G, runs[i].B);
            foreach (Rune r in runs[i].Text.EnumerateRunes())
            {
                if (n >= ln.Colours.Length) break;
                ln.Runes.Add(r);
                ln.Colours[n++] = rgb;
            }
        }
    }

    private static uint Pack(float r, float g, float b)
        => ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);

    // ---- the per-frame walk -------------------------------------------------------------------

    /// <summary>HOT. Drains the hook ring, then walks every live entry: re-resolves the id to a live object,
    /// evicts on a miss, reads the anchor / flags / damage byte, applies the distance fade, and leaves the
    /// survivors compacted into <see cref="Visible"/>. Allocates nothing.</summary>
    /// <param name="dtMs">ms since the previous refresh; drives every ease. Clamped at zero by the math.</param>
    /// <param name="animMs">the entry fade/slide interval (<c>NameplateConfig.AnimMs</c>).</param>
    /// <param name="maxDist">metres; 0 = unlimited.</param>
    /// <param name="fadeDist">the fade band before <paramref name="maxDist"/>.</param>
    public void Refresh(long nowMs, long dtMs, float animMs, float maxDist, float fadeDist, int queueCap,
                        float vpW, float vpH)
    {
        Drain(queueCap, animMs);
        _visibleCount = 0;
        if (!NameplateNative.Ready) return;

        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        // The rect the engine projected into -- NOT necessarily the one we draw in (vpW/vpH).
        if (!NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh))
        { rx = 0f; ry = 0f; rw = vpW; rh = vpH; }
        float gui = Metrics.GameUiScale;
        uint playerArea = NameplateNative.PlayerAreaId();
        float step = PlateLayout.FadeStep(dtMs, animMs);

        for (int i = 0; i < MaxEntries; i++)
        {
            Entry e = _entries[i];
            if (!e.Used) continue;

            IntPtr obj = NameplateNative.GameObject(e.Id);
            if (obj == IntPtr.Zero) { e.Release(); continue; }   // despawn: the object is gone, nothing to fade

            ExpireLines(e, nowMs);
            e.Plated = NameplateNative.Plated(obj, e.Type);

            // The override changes under a live plate -- a player starts sneaking mid-hover and the engine
            // re-runs nothing we hook. So it is POLLED, not latched: a mode read plus a byte-vs-cached compare,
            // both allocation-free, and only the change edge reaches SetOverride.
            int om = NameplateNative.OverrideMode(obj);
            if (om != e.OverrideMode || !NameplateNative.OverrideTextIs(obj, e.OverrideText))
                SetOverride(e, om, NameplateNative.OverrideText(obj));
            e.Alive = (e.Plated || e.LineCount > 0)
                      && !NameplateNative.Retired(obj)                                       // acceptance §9
                      && !(e.Type == NameplateNative.TypeCreature && playerArea != 0
                           && NameplateNative.CreatureAreaId(obj, e.Type) != playerArea);    // acceptance §10

            // Advance BEFORE any per-frame gate: an entry that goes off-screen mid-fade must still reach 0
            // and release its slot, or the table leaks until the object despawns.
            PlateLayout.MoveToward(ref e.Alpha, e.Alive ? 1f : 0f, step);
            PlateLayout.SettleOffset(ref e.YOff, dtMs, animMs);
            if (!e.Alive && e.Alpha <= 0f) { e.Release(); continue; }

            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay)) continue;
            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out e.AnchorX, out e.AnchorY);
            if (e.AnchorX < 0f || e.AnchorY < 0f || e.AnchorX > vpW || e.AnchorY > vpH) continue;

            e.DistanceFade = 1f;
            e.Distance = 0f;
            if (hasCam)
            {
                NameplateNative.Position(obj, out float ox, out float oy, out float oz);
                float dx = ox - cx, dy = oy - cy, dz = oz - cz;
                // The engine's projection never checks the sign of its denominator, so an object BEHIND the
                // camera lands on a mirrored anchor that passes the screen-rect test above.
                if (hasFwd && dx * fx + dy * fy + dz * fz <= 0f) continue;
                e.Distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                e.DistanceFade = PlateLayout.DistanceFade(e.Distance, maxDist, fadeDist);
                if (e.DistanceFade <= 0f) continue;
            }

            e.Damage = NameplateNative.DamageLevel(obj, e.Type);
            // Acceptance §4: a live chat line wins over the plate, and un-hovering does not cut it short.
            // (Vanilla enforces that through bubble+0x14c, which our own reset clears -- see the hook.)
            // Latched while alive: a dying bubble must not flip to plate chrome half way through its fade.
            if (e.Alive) e.ShowBubble = e.TopLine >= 0;
            _visible[_visibleCount++] = e;
        }
    }

    /// <summary>Every slot, live or not -- the <c>/nameplate probe</c> door, so the report can show entries the
    /// per-frame gates rejected. Nothing else may walk this: <see cref="Visible"/> is the drawable set.</summary>
    public Entry[] Entries => _entries;

    /// <summary>Age every bubble line into its own alpha, drop the ones that reached zero and pick the newest
    /// survivor. A line past its hold is NOT dropped -- it stays, fading, which is the whole point. Hot, no
    /// allocation; the compaction is a stable swap-to-boundary so the queue keeps its posted order.</summary>
    private static void ExpireLines(Entry e, long nowMs)
    {
        int live = 0;
        for (int i = 0; i < e.LineCount; i++)
        {
            BubbleLine ln = e.Lines[i];
            ln.Alpha = PlateLayout.BubbleAlpha(nowMs, ln.PostedMs, ln.HoldSec, ln.FadeSec);
            if (ln.Alpha < 1f) ln.Fading = true;
            if (ln.Alpha > 0f)
            {
                if (live != i) { BubbleLine tmp = e.Lines[live]; e.Lines[live] = ln; e.Lines[i] = tmp; }
                live++;
            }
        }
        e.LineCount = live;
        e.TopLine = live - 1;
    }

    /// <summary>COLD-on-change: (re)wrap a bubble line to <paramref name="pixelW"/>. Keyed on the width, the
    /// glyph-atlas epoch and <see cref="Metrics.Scale"/>, exactly as the chat layout cache is -- a font
    /// re-bake changes every advance. Steady state is a four-compare no-op; only a real change pays
    /// <c>TextWrapper.WrapRunes</c>, which allocates.</summary>
    public static void EnsureWrap(BubbleLine ln, GlyphAtlas atlas, float pixelW, int epoch, int maxLines)
    {
        int cap = maxLines < 1 ? 1 : (maxLines > MaxWrapped ? MaxWrapped : maxLines);
        if (ln.WrapWidth == pixelW && ln.WrapEpoch == epoch && ln.WrapScale == Metrics.Scale
            && ln.WrapCap == cap) return;
        ln.WrapWidth = pixelW; ln.WrapEpoch = epoch; ln.WrapScale = Metrics.Scale; ln.WrapCap = cap;

        List<TextWrapper.LineSpan> spans = TextWrapper.WrapRunes(ln.Runes, pixelW, atlas.AdvanceOfCached);
        int n = spans.Count < cap ? spans.Count : cap;
        for (int i = 0; i < n; i++) ln.Spans[i] = spans[i];
        ln.SpanCount = n;
        ln.Truncated = spans.Count > n;
        ln.WrapPixelW = FitSpans(ln, pixelW, atlas.AdvanceOfCached);
    }

    /// <summary>COLD-on-change: (re)wrap the entry's NAME to <paramref name="pixelW"/>, on top of the breaks
    /// its author already asked for. Same key discipline as <see cref="EnsureWrap"/> -- the width, the
    /// glyph-atlas epoch and <see cref="Metrics.Scale"/> -- so a width knob dragged at runtime rewraps and
    /// steady state is a three-compare no-op.
    /// <para>Order is the whole point: <see cref="PlateLayout.BreakName"/> ran cold on the resolve and put the
    /// author's breaks into the string as LF, <see cref="TextWrapper.WrapRunes"/> honours those verbatim, and
    /// wrapping only ever ADDS breaks inside a line too long for the column. The result is written as spans
    /// over the unchanged string, so no wrap-introduced break can reach anything reading <c>Name</c>.</para>
    /// <para>The wrap speaks RUNE indices and the spans hold CHAR indices, so the char offset of every rune is
    /// carried alongside the rune list, with an end sentinel. Takes the advance rather than a
    /// <c>GlyphAtlas</c> so the self-check can drive it headless.</para></summary>
    /// <param name="pixelW">the name column's max width; &lt;= 0 means "unknown" and leaves the author's own
    /// breaks as the only ones.</param>
    public static void EnsureNameWrap(Entry e, Func<Rune, float> advanceOf, float pixelW, int epoch)
    {
        if (e.NameWrapWidth == pixelW && e.NameWrapEpoch == epoch && e.NameWrapScale == Metrics.Scale) return;
        e.NameWrapWidth = pixelW; e.NameWrapEpoch = epoch; e.NameWrapScale = Metrics.Scale;

        e.NameTruncated = false;
        if (e.Name.Length == 0) { e.NameLineCount = 0; return; }
        if (!(pixelW > 0f))
        {
            e.NameLineCount = PlateLayout.SplitLines(e.Name, e.NameStart, e.NameLen, MaxWrapped);
            return;
        }

        var runes = new List<Rune>(e.Name.Length);
        var at = new List<int>(e.Name.Length + 1);
        for (int ci = 0; ci < e.Name.Length; )
        {
            Rune.DecodeFromUtf16(e.Name.AsSpan(ci), out Rune r, out int used);
            runes.Add(r); at.Add(ci); ci += used;
        }
        at.Add(e.Name.Length);

        List<TextWrapper.LineSpan> spans = TextWrapper.WrapRunes(runes, pixelW, advanceOf);
        int keep = spans.Count < MaxWrapped ? spans.Count : MaxWrapped;
        e.NameTruncated = spans.Count > keep;

        if (e.NameTruncated && keep > 0)
        {
            // Trim on RUNES, not chars: a char-level trim can split a surrogate pair.
            TextWrapper.LineSpan last = spans[keep - 1];
            float dots = Ellipsis.Length * advanceOf(new Rune('.'));
            float w = 0f;
            for (int k = last.Start; k < last.End; k++) w += advanceOf(runes[k]);
            int end = last.End;
            while (end > last.Start && w + dots > pixelW) w -= advanceOf(runes[--end]);
            spans[keep - 1] = new TextWrapper.LineSpan(last.Start, end);
        }

        int n = 0;
        for (int i = 0; i < keep; i++)
        {
            int cs = at[spans[i].Start], ce = at[spans[i].End];
            if (ce <= cs && !(e.NameTruncated && i == keep - 1)) continue;   // the wrap ate the whole line
            e.NameStart[n] = cs; e.NameLen[n] = ce - cs; n++;
        }
        e.NameLineCount = n;
    }

    /// <summary>The glyphs a cut line ends in. Three ASCII dots, not U+2026: the atlas bakes named ranges and
    /// the ellipsis codepoint is not guaranteed to be in one of them.</summary>
    public const string Ellipsis = "...";

    /// <summary>Measure the wrapped spans, trimming the last one to make room for <see cref="Ellipsis"/> when
    /// the line was cut, and return the widest kept line. Pure -- the advance comes from the caller -- so the
    /// self-check exercises the trim without an atlas.</summary>
    internal static float FitSpans(BubbleLine ln, float pixelW, Func<Rune, float> advanceOf)
    {
        int n = ln.SpanCount;
        if (n <= 0) return 0f;

        float dots = Ellipsis.Length * advanceOf(new Rune('.'));
        if (ln.Truncated)
        {
            TextWrapper.LineSpan last = ln.Spans[n - 1];
            float w = 0f;
            for (int k = last.Start; k < last.End; k++) w += advanceOf(ln.Runes[k]);
            int end = last.End;
            while (end > last.Start && w + dots > pixelW) w -= advanceOf(ln.Runes[--end]);
            ln.Spans[n - 1] = new TextWrapper.LineSpan(last.Start, end);
        }

        float widest = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = 0f;
            for (int k = ln.Spans[i].Start; k < ln.Spans[i].End; k++) w += advanceOf(ln.Runes[k]);
            if (ln.Truncated && i == n - 1) w += dots;
            if (w > widest) widest = w;
        }
        return widest;
    }

    /// <summary>Off-device proof of the pure table mechanics that a live client would otherwise be the only
    /// witness to: queue rotation at the cap, decay expiry picking the newest survivor, and the colour pack.
    /// Native reads are not exercised (there is no client here); everything below is id/time arithmetic.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"PlateTable.SelfCheck FAIL: {w}"); } }

        E(Pack(1f, 0f, 0f) == 0xff0000u && Pack(0f, 1f, 0f) == 0x00ff00u && Pack(0f, 0f, 1f) == 0x0000ffu,
          "colour pack puts the channels in RGB order");
        E(Pack(0.5019608f, 1f, 0.5019608f) == 0x80ff80u, "the uninjured band round-trips to 128,255,128");

        var e = new Entry { Used = true, Id = 7 };
        Fill(e.Lines[0], ColorRunParser.Parse("one"), 1, 5f, 1000); e.LineCount = 1;
        Fill(e.Lines[1], ColorRunParser.Parse("two"), 1, 5f, 2000); e.LineCount = 2;
        E(e.Lines[0].Runes.Count == 3 && e.Lines[1].Runes.Count == 3, "two queued lines each keep their runes");

        // Decay 5s => hold 3.75s, fade tail 1.25s. Line 0 posted at 1000 is gone at 6000, line 1 at 7000.
        ExpireLines(e, 3000);
        E(e.LineCount == 2 && e.TopLine == 1, "nothing expires inside the hold window");
        E(e.Lines[0].Alpha == 1f && !e.Lines[0].Fading, "a line inside its hold is fully opaque and not fading");
        ExpireLines(e, 5000);
        E(e.LineCount == 2, "a line past its HOLD is still present -- it is fading, not gone");
        E(e.Lines[0].Fading && e.Lines[0].Alpha > 0f && e.Lines[0].Alpha < 1f, "and it is part way through its fade");
        E(e.Lines[1].Alpha == 1f, "the newer line is untouched by the older one's fade");
        ExpireLines(e, 6000);
        E(e.LineCount == 1 && e.TopLine == 0, "the older line releases only once it is transparent");
        E(e.Lines[0].Runes[0].Value == 't', "the survivor is the NEWER line, in order");
        ExpireLines(e, 8000);
        E(e.LineCount == 0 && e.TopLine == -1, "both expired leaves no line to draw");

        // Eviction by a newcomer starts a fade; it does NOT overwrite the line.
        var ev = new PlateTable();
        var evp = new Pending { Id = 21, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                PostedMs = 0, Runs = ColorRunParser.Parse("x") };
        ev.Apply(in evp, 1, 200f);
        Entry? eq = ev.Find(21);
        ev.Apply(new Pending { Id = 21, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 1000, Runs = ColorRunParser.Parse("y") }, 1, 200f);
        E(eq != null && eq.LineCount == 2, "an evicted line is held past the cap while it fades");
        E(eq!.Lines[0].Fading && eq.Lines[0].Runes[0].Value == 'x', "the evicted line kept its content");
        E(eq.Lines[1].Runes[0].Value == 'y', "the newcomer is the newest line");
        ExpireLines(eq, 1100);
        E(eq.LineCount == 2 && eq.Lines[0].Alpha > 0f && eq.Lines[0].Alpha < 1f,
          "half way through the eviction fade the line is still drawn, half transparent");
        ExpireLines(eq, 1300);
        E(eq.LineCount == 1 && eq.Lines[0].Runes[0].Value == 'y',
          "the evicted line releases once transparent, and the newcomer survives it");

        // A genuinely full array drops the oldest hard rather than refusing the newcomer.
        var fl = new PlateTable();
        for (int i = 0; i < MaxLines; i++)
            fl.Apply(new Pending { Id = 22, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                   PostedMs = i, Runs = ColorRunParser.Parse(((char)('a' + i)).ToString()) },
                     MaxLines, 200f);
        Entry? fq = fl.Find(22);
        fl.Apply(new Pending { Id = 22, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 500, Runs = ColorRunParser.Parse("z") }, MaxLines, 200f);
        E(fq != null && fq.LineCount == MaxLines, "a full array stays at the ceiling");
        E(fq!.Lines[MaxLines - 1].Runes[0].Value == 'z', "the newcomer is never refused");
        E(fq.Lines[0].Runes[0].Value == 'b', "the casualty is the OLDEST line, dropped hard");

        // A pooled slot must not lend a half-faded line to whatever claims it next.
        fq.Release();
        for (int i = 0; i < MaxLines; i++)
            E(fq.Lines[i].Alpha == 0f && !fq.Lines[i].Fading, "a released slot keeps no residual line alpha");

        E(NameplateConfig.QueueMax == MaxLines, "the config's queue ceiling is exactly what the table can hold");

        // ---- FadingOut / SortKey: what separation takes part, and in what order ----
        var fo = new Entry { Used = true, Id = 5 };
        fo.Alive = true; fo.Alpha = 0.3f;
        E(!fo.FadingOut, "a plate fading IN still takes part -- the test is direction, not alpha");
        E(fo.SortKey == long.MaxValue, "a plate has no send time and sorts as 'now'");
        fo.Alpha = 1f;
        E(!fo.FadingOut, "a settled plate takes part");
        fo.Alive = false;
        E(fo.FadingOut, "a plate whose trigger dropped is on its way out and leaves separation");
        fo.Alive = true;
        E(!fo.FadingOut, "and a re-hover before it disappears rejoins separation");

        var fb = new Entry { Used = true, Id = 6, Alive = true, Alpha = 1f, ShowBubble = true };
        Fill(fb.Lines[0], ColorRunParser.Parse("old"), 1, 5f, 1000);
        Fill(fb.Lines[1], ColorRunParser.Parse("new"), 1, 5f, 4000);
        fb.LineCount = 2;
        ExpireLines(fb, 5000);
        E(fb.Lines[0].Fading && !fb.Lines[1].Fading, "the older line is in its tail while the newer holds");
        E(!fb.FadingOut, "a bubble whose NEWEST line is still live takes part, older tails and all");
        E(fb.SortKey == 4000, "and it sorts on that newest line, so an object that keeps talking stays put");
        ExpireLines(fb, 8000);
        E(fb.Lines[0].Fading && fb.TopLine == 0, "only the newest line is left, and it is fading");
        E(fb.FadingOut, "a bubble in its own fade tail leaves separation");
        ExpireLines(fb, 20000);
        E(fb.TopLine < 0 && fb.FadingOut, "a bubble with nothing left to draw is out too");

        var t = new PlateTable();
        var p = new Pending { Id = 9, Type = NameplateNative.TypeCreature, Runs = ColorRunParser.Parse("a"), DecaySec = 5f, PostedMs = 0, Channel = 1 };
        for (int i = 0; i < 5; i++) t.Apply(in p, 2);
        Entry? q = t.Find(9);
        E(q != null && LiveCount(q) == 2, "the bubble queue holds at its cap however many lines arrive");
        ExpireLines(q!, 1000);   // the evictions faded at animMs 0, so one age is all it takes to clear them
        E(q!.LineCount == 2, "and once the evicted lines are transparent the array is back at the cap");

        // Shrinking the cap under a live queue keeps the NEWEST lines, not the oldest survivor.
        var sh = new PlateTable();
        for (int i = 0; i < MaxLines; i++)
            sh.Apply(new Pending { Id = 3, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                                   PostedMs = i, Runs = ColorRunParser.Parse(((char)('a' + i)).ToString()) }, MaxLines);
        Entry? sq = sh.Find(3);
        E(sq != null && sq.LineCount == MaxLines, "the queue fills to the table ceiling");
        sh.Apply(new Pending { Id = 3, Type = NameplateNative.TypeCreature, Channel = 1, DecaySec = 60f,
                               PostedMs = 99, Runs = ColorRunParser.Parse("z") }, 2);
        E(LiveCount(sq!) == 2, "a shrunken cap takes effect on the next line");
        ExpireLines(sq!, 1000);   // let the lines it evicted finish fading out
        E(sq!.LineCount == 2, "the over-cap lines fade out rather than vanishing");
        E(sq.Lines[1].Runes[0].Value == 'z', "the arriving line is the newest");
        E(sq.Lines[0].Runes[0].Value == 'a' + MaxLines - 1, "the survivor is the NEWEST of the dropped queue, not the oldest");

        // A pooled slot must not lend its cold-resolved strings to the object that claims it next.
        var pool = new PlateTable();
        pool.Apply(new Pending { Id = 11, Type = NameplateNative.TypeCreature, IsPlate = true,
                                 Name = "Alice", Portrait = "po_alice" }, 2);
        Entry? a1 = pool.Find(11);
        E(a1 != null && a1.PortraitResref == "po_alice", "the first tenant resolves its portrait");
        a1!.Used = false;                                   // the eviction Refresh does on a despawn
        pool.Apply(new Pending { Id = 12, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "A barrel", Portrait = "" }, 2);
        Entry? a2 = pool.Find(12);
        E(ReferenceEquals(a1, a2), "the freed slot is the one reused (the pool has no other empty slot ahead of it)");
        E(a2!.PortraitResref.Length == 0, "a portraitless newcomer does not inherit the previous tenant's portrait");
        E(a2.Name == "A barrel", "the newcomer's own name replaces the previous tenant's");
        E(a2.NameLineCount == 1 && a2.NameLen[0] == 8, "and its line spans with it");

        // A pooled slot must not inherit the previous tenant's LINE state either -- a one-line name in a
        // three-line slot would measure and emit two lines of somebody else's text.
        a2.Used = false;
        pool.Apply(new Pending { Id = 13, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "head\nbody\ntail", Portrait = "" }, 2);
        Entry? a3 = pool.Find(13);
        E(ReferenceEquals(a2, a3) && a3!.NameLineCount == 3, "a broken name indexes as its several lines");
        E(a3.NameStart[1] == 5 && a3.NameLen[1] == 4, "the middle span is the middle line, LF in neither");
        a3.Used = false;
        pool.Apply(new Pending { Id = 14, Type = NameplateNative.TypePlaceable, IsPlate = true,
                                 Name = "flat", Portrait = "" }, 2);
        E(pool.Find(14)!.NameLineCount == 1, "the next tenant of that slot is one line, not three");

        t.Clear();
        E(t.Find(9) == null, "Clear drops every entry (the module-unload edge)");
        pool.Clear();
        E(a2.PortraitResref.Length == 0 && a2.Name.Length == 0 && a2.NameLineCount == 0,
          "Clear drops the cold-resolved strings and their line spans too, so nothing crosses a module unload");

        var full = new PlateTable();
        long before = full.Overflow;
        for (uint i = 0; i < MaxEntries + 4; i++) full.Apply(new Pending { Id = i + 1, Type = 5 }, 2);
        E(full.Overflow == before + 4, "a full table refuses new candidates and counts them");

        // Aurora colour runs survive the flatten: <c> + 3 raw RGB bytes + '>' ... </c>.
        var col = new BubbleLine();
        Fill(col, ColorRunParser.Parse("<c" + (char)0xff + (char)0x20 + (char)0x20 + ">hi</c>"), 1, 5f, 0);
        E(col.Runes.Count == 2 && col.Colours[0] == 0xff2020u && col.Colours[1] == 0xff2020u,
          "a colour run reaches the per-rune colour array");

        // The ellipsis fit, on a constant advance -- EnsureWrap itself needs a baked atlas, this does not.
        var el = new BubbleLine();
        for (int i = 0; i < 10; i++) el.Runes.Add(new Rune((char)('a' + i)));
        el.Spans[0] = new TextWrapper.LineSpan(0, 5);
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        el.SpanCount = 2;
        static float Ten(Rune _) => 10f;
        el.Truncated = false;
        E(FitSpans(el, 100f, Ten) == 50f, "an uncut line measures its widest span and keeps every rune");
        el.Truncated = true;
        E(FitSpans(el, 100f, Ten) == 80f && el.Spans[1].End == 10,
          "a cut line that has room for the ellipsis keeps every rune and reports the wider line");
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        E(FitSpans(el, 60f, Ten) == 60f && el.Spans[1].End == 8,
          "a cut line trims its last span until the ellipsis fits the wrap width");
        el.Spans[1] = new TextWrapper.LineSpan(5, 10);
        E(FitSpans(el, 5f, Ten) == 50f && el.Spans[1].End == el.Spans[1].Start,
          "a width narrower than the ellipsis itself empties the last span rather than looping");

        // ---- EnsureNameWrap: the plate name wraps to the same width a bubble does ----
        // A constant 10px advance, so 45px fits four runes and the ellipsis is 30px wide.
        int adv = 0;
        Func<Rune, float> A10 = _ => { adv++; return 10f; };
        var wn = new Entry();
        string Line(Entry x, int i) => x.Name.Substring(x.NameStart[i], x.NameLen[i]);

        SetName(wn, "Bob");
        EnsureNameWrap(wn, A10, 100f, 1);
        E(wn.NameLineCount == 1 && Line(wn, 0) == "Bob" && !wn.NameTruncated,
          "a name that fits the width is one line, exactly as before");

        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 45f, 1);
        E(wn.NameLineCount == 2 && Line(wn, 0) == "aaa" && Line(wn, 1) == "bbb",
          "a name too wide for the column wraps -- the width knob now bounds the plate, not just the bubble");

        // Order: the author's break first, then the wrap INSIDE each resulting line. Never the other way.
        SetName(wn, PlateLayout.BreakName("hdr   aaa bbb"));
        E(wn.Name == "hdr\naaa bbb", "the padded name still breaks where its author asked");
        EnsureNameWrap(wn, A10, 45f, 1);
        E(wn.NameLineCount == 3 && Line(wn, 0) == "hdr" && Line(wn, 1) == "aaa" && Line(wn, 2) == "bbb",
          "an author break is honoured and the wrap only adds breaks inside a too-long line");
        E(wn.Name == "hdr\naaa bbb", "and the wrap leaves the STRING alone -- no wrap break can reach /tell");

        // The cache key: a steady frame rewraps nothing, a width change rewraps, an atlas re-bake rewraps.
        int atCall = adv;
        EnsureNameWrap(wn, A10, 45f, 1);
        E(adv == atCall, "a second call at the same width/epoch/scale measures nothing at all");
        EnsureNameWrap(wn, A10, 100f, 1);
        E(adv > atCall && wn.NameLineCount == 2, "a WIDTH change rewraps -- the knob is live, not dead");
        atCall = adv;
        EnsureNameWrap(wn, A10, 100f, 2);
        E(adv > atCall, "an atlas re-bake (epoch) rewraps: every advance changed");
        SetName(wn, "other");
        EnsureNameWrap(wn, A10, 100f, 2);
        E(wn.NameLineCount == 1 && Line(wn, 0) == "other", "a new name invalidates the cache at the same key");
        atCall = adv;
        SetName(wn, "other");
        E(adv == atCall && wn.NameWrapWidth == 100f, "re-setting the SAME name does not invalidate the wrap");

        // Over the wrapped-line ceiling: the same graceful ellipsis a cut bubble gets.
        var sb = new StringBuilder();
        for (int i = 0; i < MaxWrapped + 8; i++) { if (i > 0) sb.Append(' '); sb.Append("aaa"); }
        SetName(wn, sb.ToString());
        EnsureNameWrap(wn, A10, 45f, 1);
        E(wn.NameLineCount == MaxWrapped && wn.NameTruncated,
          "a name past the wrapped-line ceiling stops at it and says so");
        E(wn.NameLen[MaxWrapped - 1] == 1,
          "and its last line is trimmed until the three-dot ellipsis fits the width beside it");

        SetName(wn, "");
        EnsureNameWrap(wn, A10, 45f, 1);
        E(wn.NameLineCount == 0 && !wn.NameTruncated, "an empty name is no lines, not one empty one");
        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 0f, 1);
        E(wn.NameLineCount == 1, "an unknown width leaves the author's breaks as the only ones");

        // Alignment: LineX treats a wrap-produced line exactly as a break-produced one.
        SetName(wn, "aaa bbb");
        EnsureNameWrap(wn, A10, 45f, 3);
        E(wn.NameLineCount == 2 && Line(wn, 1) == "bbb", "a two-line wrap for the alignment cases");
        E(PlateLayout.LineX(20f, 50f, 50f) == 20f,
          "the widest wrapped line starts at the column, wrap-produced or break-produced");
        E(PlateLayout.LineX(20f, 50f, 20f) == 35f,
          "and a shorter one centres in the text column -- on a creature plate as on any other");

        // ---- ObjectTextBubbleOverride: the module's own plate text (docs/re/nameplate.md §8.5) ----
        var ov = new Entry();
        string Para(int i)
        {
            var p = new StringBuilder();
            foreach (Rune r in ov.Paras[i].Runes) p.Append(r.ToString());
            return p.ToString();
        }
        const string Grey = "<c\u0080\u0080\u0080>";   // the Aurora grammar: '<c' + three raw RGB bytes + '>'

        SetOverride(ov, 0, "");
        E(ov.ParaCount == 0, "no override is no paragraphs");
        SetOverride(ov, 3, "");
        E(ov.ParaCount == 0, "an EMPTY override leaves the plate untouched whatever the mode says");
        SetOverride(ov, 0, " Poisoned");
        E(ov.ParaCount == 0, "mode 0 with text is still no override -- the engine's default arm changes nothing");

        // The server end's own two test vectors (sow-codebase ComposeLabelsTests).
        SetOverride(ov, 3, " Poisoned Diseased\nSneaking Invisible");
        E(ov.ParaCount == 2 && Para(0) == " Poisoned Diseased" && Para(1) == "Sneaking Invisible",
          "append splits into the injury-line splice and the line below it, the server's leading space intact");
        SetOverride(ov, 3, "\nSneaking");
        E(ov.ParaCount == 2 && Para(0).Length == 0 && Para(1) == "Sneaking",
          "an override opening with a break has an EMPTY splice -- nothing joins the injury line");
        SetOverride(ov, 3, " Poisoned\r\nSneaking");
        E(ov.ParaCount == 2 && Para(1) == "Sneaking", "CRLF is ONE break, not two");
        SetOverride(ov, 2, "Hidden\n");
        E(ov.OverrideMode == 2 && ov.ParaCount == 1 && Para(0) == "Hidden",
          "a trailing break is not a blank line");
        SetOverride(ov, 1, "The Sign");
        E(ov.OverrideMode == 1 && ov.ParaCount == 1 && Para(0) == "The Sign",
          "replace mode carries the whole plate string, name and band included");

        SetOverride(ov, 3, " " + Grey + "Sneaking</c>");
        E(Para(0) == " Sneaking", "the colour grammar is parsed OFF the text, not drawn as tag bytes");
        E(ov.Paras[0].Colours[0] == 0xffffffu && ov.Paras[0].Colours[1] == 0x808080u,
          "and the tag's colour lands on the glyphs it wrapped, the splice space staying plain");

        // The change key. There is no hook for the override, so this cache is what the per-frame poll rides on.
        ov.Paras[0].WrapWidth = 123f;
        SetOverride(ov, 3, " " + Grey + "Sneaking</c>");
        E(ov.Paras[0].WrapWidth == 123f, "re-setting the SAME override re-parses nothing and keeps the wrap");
        SetOverride(ov, 3, " Sneaking Invisible");
        E(ov.Paras[0].WrapWidth == -1f && Para(0) == " Sneaking Invisible",
          "a CHANGED override re-resolves -- a player who starts sneaking is not cached away forever");
        ov.Paras[0].WrapWidth = 123f;
        SetOverride(ov, 2, " Sneaking Invisible");
        E(ov.Paras[0].WrapWidth == -1f && ov.OverrideMode == 2,
          "a changed MODE at the same text re-resolves too");

        SetName(ov, "Bob");
        SetOverride(ov, 3, " Sneaking");
        E(ov.Name == "Bob" && ov.NameLineCount == 1,
          "the override never touches Name, so the /tell prefill cannot pick up a 'Sneaking' suffix");

        var sb3 = new StringBuilder("x");
        for (int i = 0; i < MaxParas + 3; i++) sb3.Append("\nrow");
        SetOverride(ov, 3, sb3.ToString());
        E(ov.ParaCount == MaxParas, "an override past the paragraph ceiling stops at it rather than growing");

        log.Info(ok ? "PlateTable.SelfCheck: passed" : "PlateTable.SelfCheck: FAILED");
    }
}
