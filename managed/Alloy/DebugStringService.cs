using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Alloy;

/// <summary>
/// CX-98: the engine's debug string layer -- <c>AurPostString</c> / <c>AurPostStringEx</c> (NWScript
/// <c>PostString</c>) and <c>AurPrintString</c>, drawn by <c>AurRenderStrings</c> @0x436480 after
/// <c>CGuiMan</c> -- redrawn by CX's own 2D text. One hook, on <c>AurPrintString</c> @0x435f30: in Alloy mode
/// the call is swallowed and the string is captured; every captured string is drawn at the present by
/// <see cref="TextRig"/>, on the engine's own 10x14 character grid.
/// </summary>
/// <remarks>
/// <para><b>Why the draw primitive and not <c>AurRenderStrings</c>.</b> The decompile
/// (<c>~/nwn-decomp/8193.37-17</c>) shows <c>AurRenderStrings</c> to be three things: <c>PostTraceStrings</c>,
/// one <c>RenderInterface::LoadMatrixIdentity</c>, and <c>PostedString::Post</c> @0x4362d0 per entry of the
/// <c>posts</c> list. <b>Expiry lives inside <c>Post</c></b>: it decrements <c>PostedString+0x404</c> by
/// <c>DeltaT</c> and, past the lifetime, runs <c>~PostedString</c> (which splices the entry out of
/// <c>posts</c>) and <c>operator delete</c>. Skipping <c>AurRenderStrings</c> therefore means reimplementing
/// the engine's own allocation lifetime from managed code, for no visible gain -- and any slip there is a
/// leak or a double free. Swallowing <c>AurPrintString</c> instead leaves the list, the ordering, the
/// same-id replacement and the expiry exactly the engine's, and removes strictly MORE drawing: the console
/// cursor (<c>Update</c> @0x430030) and <c>CGuiCGModelViewer::Update</c> print through the same primitive
/// without going through <c>AurRenderStrings</c> at all.</para>
/// <para>What still runs engine-side on an Alloy frame is <c>PostTraceStrings</c> (nothing calls
/// <c>trace(char*)</c> @0x435600 in this build, so the list is empty and it does nothing),
/// <c>RenderInterface::LoadMatrixIdentity</c> and <c>RenderInterface::SetColor4f</c>. Both of those are
/// CPU-side state caches -- <c>GLRender::LoadMatrixIdentity</c> touches <c>m_aurMtxStack</c> and a dirty
/// flag, <c>GLRender::SetColor4f</c> a global colour -- so the layer issues no GL call at all, let alone a
/// draw. CX-109 gave that first one a second job: it is the once-per-frame write that leaves the MODEL stack
/// at the identity (it carries no <c>SetMatrixMode</c>, and the mode the frame leaves selected is 2), which is
/// the evidence behind <see cref="AlloyCamera.ModelBase"/>.</para>
/// <para><b>The layer is player-visible, so retiring it was not an option.</b>
/// <c>CNWCMessage::HandleServerToPlayerCCMessage</c> case 0x16 reads (text, id, x, y, anchor, colour,
/// fadeColour, life, font) off the wire and calls <c>AurPostStringEx</c>: that is NWScript
/// <c>PostString</c>, which any module can call on any release client.</para>
/// </remarks>
public sealed unsafe class DebugStringService : IExtenderService
{
    /// <summary>The engine's character cell, in logical px before the GUI scale (<c>AurPrintString</c>:
    /// <c>cols = w / (scale*10)</c>, <c>rows = h / (scale*14)</c>).</summary>
    private const float CellW = 10f, CellH = 14f;
    /// <summary>...and the viewport the engine refuses to draw in at all.</summary>
    private const float MinW = 300f, MinH = 100f;

    /// <summary>Posted strings drawn in one frame. Vanilla has no ceiling (the list is a
    /// <c>CExoArrayList</c>); at 14 logical px a row this is more rows than a 4K screen has.</summary>
    internal const int MaxStrings = 128;
    /// <summary>Bytes kept per string. Vanilla stores 0x3ff; a 1920-wide screen is 192 columns, so a string
    /// past this is already off the right edge.</summary>
    internal const int MaxBytes = 256;

    /// <summary>One captured <c>AurPrintString</c> call. <c>Len</c> is what we stored, <c>Full</c> what
    /// <c>strlen</c> would have said -- the grid clamps against the full length, as vanilla does.</summary>
    private struct Post
    {
        public int Col, Row, Len, Full;
        public float R, G, B, A;
    }

    private static readonly Post[] _cur = new Post[MaxStrings];
    private static readonly byte[] _curText = new byte[MaxStrings * MaxBytes];
    private static int _curN;

    /// <summary>What <see cref="_batch"/> was baked from, so an unchanged layer re-draws the same vertices.</summary>
    private static readonly Post[] _was = new Post[MaxStrings];
    private static readonly byte[] _wasText = new byte[MaxStrings * MaxBytes];
    private static int _wasN = -1;
    private static GlyphAtlas? _wasAtlas;
    private static float _wasW, _wasH, _wasGui;

    private static readonly VertexBatch _batch = new(0f, 0f);
    private static ExtenderLog? _log;
    private static AurPrintStringFn _orig;
    private static bool _armed, _overflowed;
    /// <summary>The face px last asked of <see cref="TextRig.Face(float)"/>: a miss bakes, so it is asked once.</summary>
    private static float _askedPx;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);

        _orig = hooks.InstallAurPrintString(&OnAurPrintString);
        if (_orig == null)
        {
            log.Info("DebugStringService: standing down (AurPrintString unresolved) -- the engine keeps drawing "
                   + "its own posted strings over the Alloy frame");
            return;
        }
        FrameRegistry.Global.Register(FramePhase.PrePresent, 60, Draw, "Alloy.debugstrings", log);
        _armed = true;
        log.Info("DebugStringService: armed -- in Alloy mode AurPrintString is swallowed and the posted-string "
               + "layer is drawn by CX's 2D text");
    }

    // ---- the hook ----------------------------------------------------------------------------

    /// <summary><c>AurPrintString(const char*, int col, int row, float r, float g, float b, float a,
    /// const char* font)</c> -- the debug layer's ONE draw primitive. Vanilla mode hands it straight back;
    /// an Alloy frame captures and returns, so the engine's <c>StringGob</c> is never built.
    /// Zero-alloc: the text is copied as bytes, and never becomes a string.</summary>
    [UnmanagedCallersOnly]
    private static void OnAurPrintString(byte* text, int col, int row, float r, float g, float b, float a, byte* font)
    {
        // CX-95: the GUI is Alloy's at the menus too, where the world verdict is Idle -- the strings follow the GUI.
        if (!_armed || !(AlloyGate.Alloy || GuiPass.Capturing))
        {
            if (_orig != null) _orig(text, col, row, r, g, b, a, font);
            return;
        }
        Capture(text, col, row, r, g, b, a);
    }

    /// <summary>Store one string for this frame's draw. Engine text is a single-byte codepage, so a byte IS
    /// its codepoint (Latin-1) and no decode is needed.</summary>
    private static void Capture(byte* text, int col, int row, float r, float g, float b, float a)
    {
        if (text == null || *text == 0) return;
        if (_curN >= MaxStrings)
        {
            if (!_overflowed)
            {
                _overflowed = true;
                _log?.Always($"[debugstrings] more than {MaxStrings} posted strings in one frame -- the rest are "
                           + "DROPPED, not drawn by the engine either");
            }
            return;
        }
        int full = 0;
        while (full < PostedTextCap && text[full] != 0) full++;
        int n = full < MaxBytes ? full : MaxBytes;
        int at = _curN * MaxBytes;
        for (int i = 0; i < n; i++) _curText[at + i] = text[i];
        _cur[_curN] = new Post
        {
            Col = col, Row = row, Len = n, Full = full,
            R = r, G = g, B = b, A = a < 0f ? 0f : a > 1f ? 1f : a,
        };
        _curN++;
    }

    /// <summary>Vanilla's own text ceiling: <c>PostedString</c> holds <c>strncpy(.., 0x3ff)</c>.</summary>
    private const int PostedTextCap = 0x3ff;   // not-an-offset: the engine's strncpy length

    // ---- the grid ----------------------------------------------------------------------------

    /// <summary>The engine's character grid, transcribed from <c>AurPrintString</c> @0x435f30: cells of
    /// 10x14 logical px times the game's GUI scale, row 0 at the top, a negative column or row counting back
    /// from the right or bottom edge, and both clamped so the string stays on screen. False is the viewport
    /// the engine itself draws nothing in (under 300x100).</summary>
    internal static bool Place(int col, int row, int len, float vw, float vh, float gui,
                              out float x, out float yTop, out float cellH)
    {
        cellH = CellH * gui;
        x = 0f; yTop = 0f;
        if (gui <= 0f || vw < MinW || vh < MinH) return false;
        float cellW = CellW * gui;
        int rows = (int)(vh / cellH), cols = (int)(vw / cellW);
        if (rows <= 0 || cols <= 0) return false;
        if (row >= rows) row = rows - 1;
        if (row < 0) { row += rows; if (row < 0) row = 0; }
        if (col < 0) col = cols + col - len + 1;
        if (col + len > cols) col = cols - len;
        if (col < 0) col = 0;
        x = col * cellW;
        yTop = row * cellH;
        return true;
    }

    /// <summary>The face the layer is drawn in: sized to the engine's row pitch so a module posting to
    /// consecutive rows still gets consecutive rows, not overlapping ones.</summary>
    internal static float FacePx(float gui)
    {
        float px = MathF.Round(CellH * gui * 0.8f);
        return px < 8f ? 8f : px > TextRig.AtlasMaxPx ? TextRig.AtlasMaxPx : px;
    }

    // ---- the draw ----------------------------------------------------------------------------

    /// <summary>Per presented frame, after the window roll-call: every string the engine asked for this frame
    /// is on screen, over everything else CX draws. The frame's capture is consumed here -- vanilla's layer
    /// is re-issued from scratch every frame too.</summary>
    private static void Draw()
    {
        int n = _curN;
        _curN = 0;
        if (!_armed) return;
        if (n == 0)
        {
            if (_wasN != 0) { _batch.Clear(); _wasN = 0; }
            return;
        }

        float vw = TextRig.ViewportW, vh = TextRig.ViewportH;
        float gui = Metrics.GameUiScale;
        if (vw <= 0f || vh <= 0f) return;

        float px = FacePx(gui);
        GlyphAtlas? atlas = TextRig.BakedFace(px);
        if (atlas == null && px != _askedPx)
        {
            _askedPx = px;   // cold: a miss bakes, and a refusal is asked once, not every frame
            atlas = TextRig.Face(px);
            if (atlas == null)
                _log?.Always($"[debugstrings] no {px}px face -- the posted-string layer draws in the UI face, "
                           + "so its rows will not sit on the engine's 14px pitch");
        }
        atlas ??= TextRig.Atlas;
        if (atlas == null) return;

        if (Changed(n, atlas, vw, vh, gui)) Bake(n, atlas, vw, vh, gui);
        TextRig.DrawBatch(_batch.AsSpan(), _batch.VertexCount);
    }

    /// <summary>Is this frame's layer different from the one <see cref="_batch"/> holds? A posted string is
    /// static for its whole lifetime in the common case, so the answer is normally no and the frame costs one
    /// comparison walk instead of a glyph walk.</summary>
    private static bool Changed(int n, GlyphAtlas atlas, float vw, float vh, float gui)
    {
        if (n != _wasN || !ReferenceEquals(atlas, _wasAtlas) || vw != _wasW || vh != _wasH || gui != _wasGui) return true;
        for (int i = 0; i < n; i++)
        {
            ref Post a = ref _cur[i];
            ref Post b = ref _was[i];
            if (a.Col != b.Col || a.Row != b.Row || a.Len != b.Len || a.Full != b.Full
                || a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A) return true;
            int at = i * MaxBytes;
            for (int k = 0; k < a.Len; k++) if (_curText[at + k] != _wasText[at + k]) return true;
        }
        return false;
    }

    /// <summary>Rebuild the layer's vertices. Cold in the steady state (see <see cref="Changed"/>); allocates
    /// nothing after the first frame -- <see cref="VertexBatch.Clear"/> keeps the float list's capacity.</summary>
    private static void Bake(int n, GlyphAtlas atlas, float vw, float vh, float gui)
    {
        _batch.Reset(atlas.SolidU, atlas.SolidV);
        for (int i = 0; i < n; i++)
        {
            ref Post p = ref _cur[i];
            int at = i * MaxBytes;
            _was[i] = p;
            for (int k = 0; k < p.Len; k++) _wasText[at + k] = _curText[at + k];
            if (!Place(p.Col, p.Row, p.Full, vw, vh, gui, out float x, out float yTop, out _)) continue;
            float baseline = yTop + atlas.Ascent;
            for (int k = 0; k < p.Len && x < vw; k++)
            {
                int cp = _curText[at + k];
                if (atlas.TryGet(cp, out Glyph gl)) { _batch.Glyph(gl, x, baseline, p.R, p.G, p.B, p.A); x += gl.Advance; }
                else x += atlas.SpaceAdvance;
            }
        }
        _wasN = n; _wasAtlas = atlas; _wasW = vw; _wasH = vh; _wasGui = gui;
    }

    // ---- off-device proof --------------------------------------------------------------------

    /// <summary>The grid transcription and the capture ring, both pure: the two places a silent mistake would
    /// put a posted string in the wrong place or lose it.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DebugStringService.SelfCheck");

        // 800x448 at scale 1: 80 columns of 10px, 32 rows of 14px.
        const float w = 800f, h = 448f;
        check.Expect(Place(0, 0, 5, w, h, 1f, out float x, out float y, out float ch) && x == 0f && y == 0f && ch == 14f,
                     "(0,0) is the top-left cell and the row pitch is 14px at scale 1");
        check.Expect(Place(3, 2, 5, w, h, 1f, out x, out y, out _) && x == 30f && y == 28f,
                     "a cell is 10x14px: (3,2) lands at (30,28)");
        check.Expect(Place(3, 2, 5, w, h, 2f, out x, out y, out ch) && x == 60f && y == 56f && ch == 28f,
                     "the GUI scale multiplies the cell, not the column");
        // A negative column right-aligns: cols + col - len + 1, then the overflow clamp.
        check.Expect(Place(-1, 0, 5, w, h, 1f, out x, out _, out _) && x == 750f,
                     "col -1 puts a 5-character string against the right edge");
        check.Expect(Place(-1, 0, 1, w, h, 1f, out x, out _, out _) && x == 790f,
                     "col -1 with one character is the last column");
        // A negative row counts back from the bottom; -1 is the last row.
        check.Expect(Place(0, -1, 1, w, h, 1f, out _, out y, out _) && y == 31f * 14f,
                     "row -1 is the bottom row");
        check.Expect(Place(0, -99, 1, w, h, 1f, out _, out y, out _) && y == 0f,
                     "a row past the top clamps to row 0, it does not wrap");
        check.Expect(Place(0, 99, 1, w, h, 1f, out _, out y, out _) && y == 31f * 14f,
                     "a row past the bottom clamps to the last row");
        // An overlong string is pushed left until it fits, then pinned at column 0.
        check.Expect(Place(78, 0, 5, w, h, 1f, out x, out _, out _) && x == 750f,
                     "a string that would run off the right edge is pushed back");
        check.Expect(Place(0, 0, 200, w, h, 1f, out x, out _, out _) && x == 0f,
                     "a string wider than the screen starts at column 0");
        // ...and the viewport the engine itself draws nothing in.
        check.Expect(!Place(0, 0, 1, 299f, 400f, 1f, out _, out _, out _), "a viewport under 300 wide draws nothing");
        check.Expect(!Place(0, 0, 1, 800f, 99f, 1f, out _, out _, out _), "a viewport under 100 tall draws nothing");
        check.Expect(Place(0, 0, 1, MinW, MinH, 1f, out _, out _, out _), "300x100 exactly is the smallest that draws");

        check.Expect(FacePx(1f) >= 8f && FacePx(1f) <= 14f, "the face is sized to fit the 14px row pitch");
        check.Expect(FacePx(2f) > FacePx(1f), "the face follows the GUI scale");

        // The capture ring: truncation, the full length the grid clamps against, and the ceiling.
        int keptN = _curN; bool keptOverflow = _overflowed;
        _curN = 0; _overflowed = false;
        byte* s = stackalloc byte[4]; s[0] = (byte)'a'; s[1] = 0xC7; s[2] = (byte)'z'; s[3] = 0;
        Capture(s, 2, 3, 1f, 0.5f, 0.25f, 2f);
        check.Expect(_curN == 1 && _cur[0].Len == 3 && _cur[0].Full == 3 && _cur[0].Col == 2 && _cur[0].Row == 3,
                     "a captured string keeps its text, column and row");
        check.Expect(_curText[0] == (byte)'a' && _curText[1] == 0xC7 && _curText[2] == (byte)'z',
                     "codepage bytes are stored verbatim -- a byte is its own codepoint");
        check.Expect(_cur[0].A == 1f, "alpha is clamped to [0,1]");
        byte* empty = stackalloc byte[1]; empty[0] = 0;
        Capture(empty, 0, 0, 1f, 1f, 1f, 1f);
        Capture(null, 0, 0, 1f, 1f, 1f, 1f);
        check.Expect(_curN == 1, "an empty or null string is not captured");
        ExtenderLog? live = _log; _log = null;   // the ceiling says so loudly: not into a headless run's log
        for (int i = _curN; i < MaxStrings + 4; i++) Capture(s, 0, i, 1f, 1f, 1f, 1f);
        _log = live;
        check.Expect(_curN == MaxStrings && _overflowed, "the ring stops at its ceiling and says so");
        _curN = keptN; _overflowed = keptOverflow;

        log.Info(check.Ok ? "DebugStringService.SelfCheck: passed" : "DebugStringService.SelfCheck: FAILED");
    }
}
