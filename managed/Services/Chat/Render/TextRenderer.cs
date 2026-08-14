using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Services.Chat.Core;

namespace Sowx.Services.Chat.Render;

/// <summary>
/// Owned-render pass for the native chatbox (slice #19 text; slice #21 window). Bakes a stb_truetype
/// glyph atlas once at init (cold), owns a tiny GL 3.3-core shader + VAO/VBO, and draws whatever
/// vertex batch has been <see cref="Publish"/>ed inside the existing SwapBuffers hook. Callers build
/// the batch cold (ingest/input thread) via <see cref="VertexBatch"/>; the hot <see cref="Draw"/>
/// only uploads-if-changed and issues one <c>glDrawArrays</c>, allocating nothing in steady state.
/// The client GL context is CORE (no fixed-function/immediate mode), so everything is shader-driven;
/// blend/depth/cull/scissor are snapshot-restored so the engine's own state cache stays consistent.
/// </summary>
public static unsafe class TextRenderer
{
    /// <summary>Font search order, first existing wins. DejaVu preferred (covers our four ranges incl.
    /// Cyrillic); Liberation/FreeSans are the near-universal Linux fallbacks. The Steam Linux Runtime
    /// (Steam Deck) ships the Debian-style truetype/dejavu path, not the Arch-style TTF one.
    /// ponytail: fixed list; a fontconfig query is the ceiling if a distro ships none of these.</summary>
    private static readonly string[] FontCandidates =
    {
        "/usr/share/fonts/TTF/DejaVuSans.ttf",                         // Arch
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",            // Debian/Ubuntu + Steam runtime
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",                     // Fedora
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
        "/usr/share/fonts/gnu-free/FreeSans.ttf",
    };
    private const float FontPx = 18f;
    private const int AtlasSize = 512;
    private const int Floats = VertexBatch.Floats;   // x,y,u,v,r,g,b,a

    // ponytail: static ranges; dynamic on-demand atlas is the ceiling. DejaVuSans covers all four
    // (no CJK -- DejaVu ships none, so CJK is out of scope for v1).
    private static readonly (int lo, int hi)[] Ranges =
    {
        (0x0020, 0x007E),   // Basic Latin
        (0x00A0, 0x00FF),   // Latin-1 Supplement (accented Latin, ß, etc.)
        (0x0100, 0x017F),   // Latin Extended-A (Ć, œ, ...)
        (0x0400, 0x04FF),   // Cyrillic
        (0x25BE, 0x25BE),   // ▾ channel-dropdown arrow
        (0x2699, 0x2699),   // ⚙ gear (chat options)
    };

    private static ExtenderLog? _log;
    private static GlyphAtlas? _atlas;
    private static bool _diagged;              // one-shot: log GL error + viewport on the first drawn frame

    /// <summary>The baked atlas (null until <see cref="Init"/>, or when stbtt is absent under run-demo).</summary>
    public static GlyphAtlas? Atlas => _atlas;
    public static bool Ready => _atlas != null;

    /// <summary>A published, immutable frame: the built <see cref="RenderFrame"/> plus a per-frame
    /// <see cref="Scratch"/> buffer (same length, allocated cold at publish). The hot draw copies the base
    /// verts into scratch and applies the fade/slide there, so it allocates nothing and a concurrent
    /// <see cref="Publish"/> can never hand the animator a half-built array.</summary>
    private sealed record Frame(RenderFrame Rf, float[] Scratch);
    private static volatile Frame? _frame;
    private static Frame? _uploaded;   // last frame pushed to the VBO (reference identity)
    private static bool _settledScratch;   // did the last settled upload use the alpha'd scratch (vs pristine verts)?

    // GL objects (created lazily on the first drawn frame; no context exists at init).
    private static uint _prog, _vao, _vbo, _tex;
    private static int _uProj, _uTex, _drawCount;
    private static bool _texUp, _vaoUp, _progUp;

    // Portrait pass (#26): its own RGBA program + VAO/VBO (a second buffer so the text VBO's
    // upload-if-changed cache stays valid). The engine CAurTexture* is NEVER cached -- see ResolveTid (#33).
    private static uint _progRgb, _pvao, _pvbo;
    private static int _uProjRgb, _uTexRgb;
    private static bool _progRgbUp, _pvaoUp;
    private static bool _portraitDrew;   // one-shot: log the first portrait that actually binds a live TID

    private static readonly delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _aurGet =
        (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)Native.Sym(SymbolMap.AurTextureGet);
    private static readonly delegate* unmanaged<IntPtr, int> _aurTid =
        (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.AurTextureGetTID);

    /// <summary>Cold init: load the font, bake the atlas (incl. its solid-white chrome texel). GL state
    /// is driven directly at draw time (snapshot/restore), so nothing to resolve here.</summary>
    public static void Init(ExtenderLog log)
    {
        _log = log;
        if (_atlas != null) return;   // idempotent: probe and ChatService may both call
        string? fontPath = Array.Find(FontCandidates, File.Exists);
        if (fontPath == null) { log.Info($"TextRenderer: no font found (tried {FontCandidates.Length} paths)"); return; }
        log.Info($"TextRenderer: using font {fontPath}");
        byte[] font = File.ReadAllBytes(fontPath);
        _atlas = GlyphAtlas.Bake(font, FontPx, Ranges, AtlasSize, log);
        log.Info(_atlas != null ? "TextRenderer: atlas ready" : "TextRenderer: atlas bake unavailable");
    }

    /// <summary>Publish the frame to draw next (cold). Allocates the scratch buffer here so the hot draw
    /// never does. Pass null to draw nothing.</summary>
    public static void Publish(RenderFrame? rf)
        => _frame = rf == null ? null : new Frame(rf, new float[rf.Verts.Length]);

    /// <summary>Chrome-only convenience (the probe demo): draw a flat batch, no body/animation.</summary>
    public static void Publish(float[] verts, int vertCount)
        => Publish(new RenderFrame { Verts = verts, Count = vertCount, BodyStart = vertCount, BodyEnd = vertCount });

    /// <summary>Clear the drawn batch (e.g. probe demo off).</summary>
    public static void Clear() => _frame = null;

    // ---- demo (probe rig: `~sowx text`) -------------------------------------------------------

    private static bool _demoOn;
    private const string Demo =
        "<cFFD200>SoW</c> chat -- Ćma œuf ß: <c46C246>Привет</c>, " +
        "<c4AA3FF>мир</c>! now wrap this line across the box.";

    /// <summary>Toggle the hardcoded demo string over the shared primitives (slice #19 owned-render demo).</summary>
    public static bool ToggleDemo()
    {
        if (_atlas == null) { _log?.Info("TextRenderer: no atlas; can't draw text"); return false; }
        _demoOn = !_demoOn;
        if (_demoOn) { var b = BuildDemo(_atlas); Publish(b.Item1, b.Item2); }
        else Clear();
        _log?.Info($"TextRenderer: demo={_demoOn}");
        return _demoOn;
    }

    private static (float[], int) BuildDemo(GlyphAtlas atlas)
    {
        var runes = new List<Rune>();
        var cols = new List<uint>();
        ParseColored(Demo, runes, cols);
        var batch = new VertexBatch(atlas.SolidU, atlas.SolidV);

        const float maxWidth = 320f, startX = 60f, startY = 200f;
        List<TextWrapper.LineSpan> lines = TextWrapper.WrapRunes(runes, maxWidth, atlas.AdvanceOf);
        for (int li = 0; li < lines.Count; li++)
        {
            float baseline = startY + (li + 1) * atlas.LineHeight, penX = startX;
            TextWrapper.LineSpan span = lines[li];
            for (int k = span.Start; k < span.End; k++)
            {
                Rune r = runes[k];
                if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
                uint c = cols[k];
                batch.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
                penX += g.Advance;
            }
        }
        return (batch.ToArray(), batch.VertexCount);
    }

    /// <summary>Throwaway &lt;cRRGGBB&gt;...&lt;/c&gt; parser for the demo (real ingest uses ColorRunParser).</summary>
    private static void ParseColored(string s, List<Rune> runes, List<uint> cols)
    {
        uint color = 0xFFFFFF;
        var seg = new StringBuilder();
        void Flush() { foreach (Rune r in seg.ToString().EnumerateRunes()) { runes.Add(r); cols.Add(color); } seg.Clear(); }
        int i = 0;
        while (i < s.Length)
        {
            if (i + 9 <= s.Length && s[i] == '<' && s[i + 1] == 'c'
                && uint.TryParse(s.AsSpan(i + 2, 6), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
                && s[i + 8] == '>')
            { Flush(); color = rgb; i += 9; }
            else if (i + 4 <= s.Length && s[i] == '<' && s[i + 1] == '/' && s[i + 2] == 'c' && s[i + 3] == '>')
            { Flush(); color = 0xFFFFFF; i += 4; }
            else { seg.Append(s[i]); i++; }
        }
        Flush();
    }

    // ---- draw ---------------------------------------------------------------------------------

    /// <summary>Hot: draw the published frame through the owned GL3 program. Applies the fade/slide/scroll
    /// (map #13) into a per-frame scratch buffer when anything is animating; otherwise draws the static
    /// batch exactly as before (upload-if-changed). Zero-alloc in every path.</summary>
    public static void Draw()
    {
        Frame? f = _frame;
        if (f == null || f.Rf.Count == 0 || _atlas == null || _log == null) return;
        if (!Gl.Init(_log)) return;   // resolves pointers only; changes no GL state (and latches failure)

        RenderFrame rf = f.Rf;
        float residual = ChatAnim.Tick(Environment.TickCount64);   // advance the slide/scroll ease once per frame
        DateTime now = DateTime.UtcNow, ls = ChatAnim.LastScroll;
        (bool midFade, bool anyFaded) = ScanFade(rf, now, ls);
        bool dynamic = residual != 0f || midFade;   // content changes this frame -> rebuild+upload scratch

        // Snapshot engine GL state BEFORE any of our binds -- incl. the one-time inits below, which
        // clobber unit-0 texture / VAO / array-buffer bindings the engine caches. The final restore
        // is the ONLY exit for these.
        int* save = stackalloc int[5];
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &save[0]);
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &save[1]);
        Gl.GetIntegerv(Gl.ARRAY_BUFFER_BINDING, &save[2]);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &save[3]);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &save[4]);

        if (EnsureProgram())
        {
            if (!_texUp) UploadTexture();
            if (!_vaoUp) SetupVao();
            _drawCount = rf.Count;
            if (dynamic)
            {
                ApplyAnim(f, residual, now, ls);   // verts -> scratch, with y+=residual and a*=fade per line
                UploadBuffer(f.Scratch);
                _uploaded = null;                  // moving/ramping; re-settle to a one-shot upload once it stops
            }
            // Settled: upload once. Fully-faded lines need the alpha'd scratch; a fresh frame the pristine
            // verts. Both fade crossings pass through the mid-ramp window above (dynamic), so this cache --
            // keyed on frame identity + the faded flag -- can never present stale content.
            else if (f != _uploaded || _settledScratch != anyFaded)
            {
                if (anyFaded) { ApplyAnim(f, 0f, now, ls); UploadBuffer(f.Scratch); }
                else UploadBuffer(rf.Verts);
                _uploaded = f; _settledScratch = anyFaded;
            }

            if (_drawCount > 0)
            {
                // Force the raster bracket: the UI pass can leave scissor-test on (clipping us to a stale
                // widget rect), cull-face on (our y-down quads wind CW->back-facing after the ortho y-flip),
                // or depth-test on. Snapshot each and restore, so the engine's GL-state cache stays consistent.
                byte sDepth = Gl.IsEnabled(Gl.DEPTH_TEST), sBlend = Gl.IsEnabled(Gl.BLEND);
                byte sCull = Gl.IsEnabled(Gl.CULL_FACE), sScissor = Gl.IsEnabled(Gl.SCISSOR_TEST);
                int* sbox = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, sbox);   // body draw sets its own box
                Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
                Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

                int* vp = stackalloc int[4];
                Gl.GetIntegerv(Gl.VIEWPORT, vp);
                float* m = stackalloc float[16];
                Ortho(m, vp[2], vp[3]);   // full swap-time viewport; y-down pixel coords

                if (!_diagged)
                {
                    while (Gl.GetError() != 0) { }   // drain the engine's accumulated errors first
                    _diagged = true;
                    DrawFrame(f, m, vp, residual, now, ls);
                    _log?.Info($"TextRenderer: draw verts={_drawCount} viewport={vp[2]}x{vp[3]} " +
                               $"was[depth={sDepth} blend={sBlend} cull={sCull} scissor={sScissor}] glErr=0x{Gl.GetError():x}");
                }
                else DrawFrame(f, m, vp, residual, now, ls);

                if (sDepth != 0) Gl.Enable(Gl.DEPTH_TEST);
                if (sCull != 0) Gl.Enable(Gl.CULL_FACE);
                Gl.Scissor(sbox[0], sbox[1], sbox[2], sbox[3]);   // restore the engine's clip rect
                if (sScissor != 0) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
                if (sBlend == 0) Gl.Disable(Gl.BLEND);
            }
        }

        Gl.BindVertexArray((uint)save[1]);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)save[2]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)save[4]);
        Gl.ActiveTexture((uint)save[3]);
        Gl.UseProgram((uint)save[0]);
    }

    /// <summary>Scan the visible lines' fade in one pass. <c>mid</c> = some line is mid-ramp (alpha strictly
    /// in (0,1)), so the body is still changing and must re-upload every frame -- checked across ALL spans,
    /// since a newer line can ramp while the oldest is already fully faded. <c>faded</c> = the oldest line
    /// (largest age; spans are newest-first, so it's the last) is below full opacity, so a settled upload
    /// needs the alpha'd scratch rather than the pristine verts. Cheap: a handful of visible spans, no alloc.</summary>
    private static (bool mid, bool faded) ScanFade(RenderFrame rf, DateTime now, DateTime ls)
    {
        if (rf.Scrolled) return (false, false);   // reading history: fade frozen, so nothing is mid/faded
        int n = rf.Spans.Count;
        if (n == 0) return (false, false);
        bool mid = false;
        for (int i = 0; i < n; i++)
        {
            float a = ChatAnim.FadeAlpha(now, rf.Spans[i].Ts, ls);
            if (a > 0f && a < 1f) { mid = true; break; }
        }
        return (mid, ChatAnim.FadeAlpha(now, rf.Spans[n - 1].Ts, ls) < 1f);
    }

    /// <summary>Copy the base verts into scratch, then per body line add the shared slide/scroll residual
    /// to y and multiply the fade alpha in. Zero-alloc: scratch is owned by the frame.</summary>
    private static void ApplyAnim(Frame f, float residual, DateTime now, DateTime ls)
    {
        float[] src = f.Rf.Verts, dst = f.Scratch;
        Array.Copy(src, dst, src.Length);
        List<LineSpanVerts> spans = f.Rf.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            LineSpanVerts s = spans[i];
            float a = f.Rf.Scrolled ? 1f : ChatAnim.FadeAlpha(now, s.Ts, ls);
            int st = s.Start * Floats, en = s.End * Floats;
            for (int v = st; v < en; v += Floats) { dst[v + 1] += residual; dst[v + 7] *= a; }
        }
    }

    /// <summary>Draw the frame in three slices: chrome above the body, the animated body under a body-rect
    /// scissor (with the portrait pass), then chrome below. <paramref name="vp"/> is the current viewport.</summary>
    private static void DrawFrame(Frame f, float* proj, int* vp, float residual, DateTime now, DateTime ls)
    {
        RenderFrame rf = f.Rf;
        BindText(proj);
        if (rf.BodyStart > 0) Gl.DrawArrays(Gl.TRIANGLES, 0, rf.BodyStart);

        int bodyVerts = rf.BodyEnd - rf.BodyStart;
        if (bodyVerts > 0 || rf.Portraits.Count > 0)
        {
            // Scissor to the body rect (GL is bottom-left origin, so flip y against the viewport height).
            int sx = (int)rf.Bx, sy = (int)(vp[3] - (rf.By + rf.Bh)), sw = (int)rf.Bw, sh = (int)rf.Bh;
            if (sx < 0) { sw += sx; sx = 0; }
            if (sy < 0) { sh += sy; sy = 0; }
            if (sw < 0) sw = 0;
            if (sh < 0) sh = 0;
            Gl.Scissor(sx, sy, sw, sh);
            Gl.Enable(Gl.SCISSOR_TEST);
            if (bodyVerts > 0) Gl.DrawArrays(Gl.TRIANGLES, rf.BodyStart, bodyVerts);
            DrawPortraits(rf.Portraits, proj, residual, now, ls, rf.Scrolled);
            Gl.Disable(Gl.SCISSOR_TEST);
        }

        int tail = _drawCount - rf.BodyEnd;
        if (tail > 0)
        {
            BindText(proj);   // the portrait pass switched program/VAO/buffer
            Gl.DrawArrays(Gl.TRIANGLES, rf.BodyEnd, tail);
        }
    }

    private static void BindText(float* proj)
    {
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_uProj, 1, (byte)Gl.FALSE, proj);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);   // already on TEXTURE0
        Gl.Uniform1i(_uTex, 0);
        Gl.BindVertexArray(_vao);
    }

    // ---- portrait pass (#26) ------------------------------------------------------------------

    /// <summary>Second pass: bind each speaker's engine-resident portrait texture and draw it as a quad
    /// through the RGBA program. Cache-hit path is zero-alloc (dict lookup + one virtual TID read); only a
    /// first-seen resref pays an <c>AurTextureGet</c>. A texture whose TID is still 0 (not yet uploaded by
    /// the engine's RefreshTextures pass, or a bad resref) is simply skipped this frame.</summary>
    /// <summary>At most this many portraits per frame (bounds the stack scratch below). Far above the
    /// rows a chat body can show; a silent cap, so log if we ever hit it.</summary>
    private const int MaxPortraitsPerFrame = 64;

    private static void DrawPortraits(IReadOnlyList<PortraitQuad> quads, float* proj,
                                      float residual, DateTime now, DateTime ls, bool scrolled)
    {
        int k = quads.Count;
        if (k == 0 || _aurGet == null || _aurTid == null) return;
        if (!EnsurePortraitProgram()) return;
        if (!_pvaoUp) SetupPortraitVao();
        if (k > MaxPortraitsPerFrame) { _log?.Info($"[chat] portrait cap hit ({k}>{MaxPortraitsPerFrame})"); k = MaxPortraitsPerFrame; }

        // Resolve every TID and build all geometry FIRST. AurTextureGet's create path (first load of a
        // resref) can touch GL and rebind ARRAY_BUFFER, so it must never run between our BindBuffer and
        // BufferData -- keep all resolves ahead of the single upload below. Steady state is a per-frame
        // resman lookup returning a borrowed pointer (verified: no AddRef, no GL on the hit path) + a TID
        // read; zero GC alloc on our side. We never cache the pointer -- see ResolveTid (#33).
        int* tids = stackalloc int[k];
        float* verts = stackalloc float[Floats * 6 * k];
        int drawn = 0;
        for (int i = 0; i < k; i++)
        {
            float a = scrolled ? 1f : ChatAnim.FadeAlpha(now, quads[i].Ts, ls);   // portrait fades with its line (frozen while reading history)
            if (a <= 0f) continue;                                 // fully faded -> skip
            int tid = ResolveTid(quads[i].Resref);
            if (tid == 0) continue;   // not uploaded yet, or unresolvable -> skip this frame
            tids[drawn] = tid;
            FillQuad(verts + drawn * Floats * 6, quads[i], residual, a);   // slide with the body + fade
            drawn++;
        }
        if (drawn == 0) return;

        Gl.UseProgram(_progRgb);
        Gl.UniformMatrix4fv(_uProjRgb, 1, (byte)Gl.FALSE, proj);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_uTexRgb, 0);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(Floats * 6 * drawn * sizeof(float)), verts, Gl.DYNAMIC_DRAW);
        for (int i = 0; i < drawn; i++)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)tids[i]);
            Gl.DrawArrays(Gl.TRIANGLES, i * 6, 6);
        }
        if (!_portraitDrew) { _portraitDrew = true; _log?.Info($"[chat] portrait first-draw tid={tids[0]} ({drawn} shown)"); }
    }

    /// <summary>resref -&gt; live GL texture id, or 0 if not yet uploaded / unresolvable. Re-fetches the
    /// engine <c>CAurTexture*</c> from the resman EVERY frame and NEVER caches it (#33): the engine frees
    /// and reloads its portrait textures on area transitions and module unloads, so any stored pointer
    /// dangles and crashes when bound (confirmed via core dump: cached ptr -> AurTextureGetTID -> jump into
    /// freed heap). AurTextureGet is a resref-keyed lookup that returns the current live (borrowed) pointer,
    /// so asking it per frame is the only way to never hold a stale one. Zero GC alloc on our side
    /// (stackalloc name; engine owns the texture).</summary>
    private static int ResolveTid(string resref)
    {
        IntPtr tex = ResolveTexture(resref);
        // A non-null texture whose TID is still 0 is simply "not uploaded yet"; a portrait still streaming
        // in appears once ready. AurTextureGet only returns Zero for an empty name (guarded upstream:
        // hasPortrait requires a non-empty resref), so the Zero branch is effectively unreachable.
        return tex == IntPtr.Zero ? 0 : _aurTid(tex);
    }

    /// <summary>Cosmetic reset at a session (module) boundary: re-arm the one-shot first-portrait log for the
    /// next session. Crash-safety no longer lives here -- the portrait pointer is never cached (see ResolveTid,
    /// #33), so nothing engine-owned survives a boundary to drop. Does NOT touch GL objects that outlive a
    /// session (atlas, programs, VAOs/VBOs). Cold, zero-alloc.</summary>
    public static void ClearSessionState()
    {
        _portraitDrew = false;   // let the next session log its first live portrait again
    }

    /// <summary>Self-check for the resref -&gt; C-string marshalling that feeds AurTextureGet: truncation at 16
    /// bytes and NUL-termination (a missing NUL would walk past the name into stack garbage). Headless.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        Span<byte> buf = stackalloc byte[17];
        bool ok = true;
        ok &= FillCName("0123456789abcdef", buf) == 17 && buf[16] == 0 && buf[0] == (byte)'0' && buf[15] == (byte)'f';
        ok &= FillCName("0123456789abcdefEXTRA", buf) == 17 && buf[16] == 0 && buf[15] == (byte)'f';   // over-length truncates
        ok &= FillCName("po_hu_0", buf) == 8 && buf[7] == 0 && buf[0] == (byte)'p';                    // short name NUL-terminates in place
        ok &= FillCName("", buf) == 1 && buf[0] == 0;                                                  // empty -> lone NUL
        log.Info(ok ? "TextRenderer.SelfCheck: passed"
                    : "TextRenderer.SelfCheck: FAILED (resref C-string marshalling)");
    }

    private static IntPtr ResolveTexture(string resref)
    {
        Span<byte> name = stackalloc byte[17];   // 16 resref bytes + NUL
        FillCName(resref, name);
        fixed (byte* p = name) return _aurGet(p, IntPtr.Zero, null, null, 0, 0);
    }

    /// <summary>Write <paramref name="resref"/> into <paramref name="dest"/> (>= 17 bytes) as a
    /// null-terminated C string: copy up to 16 ASCII bytes, then a NUL. The engine resref is null-padded
    /// ASCII, max 16 bytes. Returns bytes written including the NUL.</summary>
    private static int FillCName(string resref, Span<byte> dest)
    {
        int n = 0;
        foreach (char c in resref) { if (n >= 16) break; dest[n++] = (byte)c; }
        dest[n] = 0;
        return n + 1;
    }

    /// <summary>Fraction of a portrait texture that is real image, from the engine's own ConvertPortrait
    /// (content = 1/1.28 of a pow2-padded height; the rest is black at the image bottom). ponytail: if a
    /// live frame ever shows a portrait cropped or with a black band, this one const is the knob (1.0 = raw).</summary>
    private const float PortraitContentV = 1f / 1.28f;   // 0.78125

    /// <summary>Two triangles for a portrait quad. The engine uploads portraits GL-bottom-origin, so the
    /// image arrives upside down: map the quad top to V=1 (image top) and the bottom to the content floor,
    /// which flips it upright AND crops the pow2 black pad band. Full U (no horizontal padding). y-down like
    /// the text ortho. <paramref name="residual"/> slides it with the body; <paramref name="alpha"/> fades it.</summary>
    private static void FillQuad(float* v, PortraitQuad q, float residual, float alpha)
    {
        float x0 = q.X, y0 = q.Y + residual, x1 = q.X + q.W, y1 = q.Y + q.H + residual;
        float vTop = 1f, vBot = 1f - PortraitContentV;
        Vtx(v, 0, x0, y0, 0, vTop, alpha); Vtx(v, 1, x1, y0, 1, vTop, alpha); Vtx(v, 2, x1, y1, 1, vBot, alpha);
        Vtx(v, 3, x0, y0, 0, vTop, alpha); Vtx(v, 4, x1, y1, 1, vBot, alpha); Vtx(v, 5, x0, y1, 0, vBot, alpha);
    }

    private static void Vtx(float* v, int i, float x, float y, float u, float w, float alpha)
    {
        int o = i * Floats;
        v[o] = x; v[o + 1] = y; v[o + 2] = u; v[o + 3] = w;
        v[o + 4] = 1f; v[o + 5] = 1f; v[o + 6] = 1f; v[o + 7] = alpha;
    }

    private const string FSrcRgb =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = vec4(texture(uTex, vUV).rgb, vA); }\n";

    private static bool EnsurePortraitProgram()
    {
        if (_progRgbUp) return _progRgb != 0;
        _progRgbUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcRgb);
        if (vs == 0 || fs == 0) return false;
        _progRgb = Gl.CreateProgram();
        Gl.AttachShader(_progRgb, vs); Gl.AttachShader(_progRgb, fs); Gl.LinkProgram(_progRgb);
        int ok; Gl.GetProgramiv(_progRgb, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRenderer: portrait program link failed"); _progRgb = 0; return false; }
        _uProjRgb = UniformOf(_progRgb, "uProj"); _uTexRgb = UniformOf(_progRgb, "uTex");
        _log?.Info("TextRenderer: portrait shader program ready");
        return true;
    }

    private static void SetupPortraitVao()
    {
        uint a; Gl.GenVertexArrays(1, &a); _pvao = a;
        uint vb; Gl.GenBuffers(1, &vb); _pvbo = vb;
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        int stride = Floats * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 3, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(3, 1, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(7 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.BindVertexArray(0);
        _pvaoUp = true;
    }

    /// <summary>Column-major ortho for glOrtho(0,w,h,0,-1,1): top-left origin, y grows downward.</summary>
    private static void Ortho(float* m, float w, float h)
    {
        for (int i = 0; i < 16; i++) m[i] = 0;
        m[0] = 2f / w; m[5] = -2f / h; m[10] = -1f; m[12] = -1f; m[13] = 1f; m[15] = 1f;
    }

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\nuniform mat4 uProj;\n" +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\n" +
        "void main(){ gl_Position = uProj*vec4(aPos,0.0,1.0); vUV = aUV; vCol = aCol; vA = aA; }\n";
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ float a = texture(uTex, vUV).r * vA; o = vec4(vCol, a); }\n";

    private static bool EnsureProgram()
    {
        if (_progUp) return _prog != 0;
        _progUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrc);
        if (vs == 0 || fs == 0) return false;
        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs); Gl.LinkProgram(_prog);
        int ok; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRenderer: program link failed"); _prog = 0; return false; }
        _uProj = Uniform("uProj"); _uTex = Uniform("uTex");
        _log?.Info("TextRenderer: shader program ready");
        return true;
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* sp = bytes)
        {
            byte* pp = sp; int len = bytes.Length;
            Gl.ShaderSource(sh, 1, &pp, &len);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* buf = stackalloc byte[512]; int n = 0;
            Gl.GetShaderInfoLog(sh, 512, &n, buf);
            _log?.Info($"TextRenderer: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
            return 0;
        }
        return sh;
    }

    private static int Uniform(string name) => UniformOf(_prog, name);

    private static int UniformOf(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }

    private static void UploadTexture()
    {
        if (_atlas == null) return;
        uint t; Gl.GenTextures(1, &t); _tex = t;
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        fixed (byte* px = _atlas.Pixels)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, _atlas.Width, _atlas.Height, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        _texUp = true;
        _log?.Info($"TextRenderer: atlas uploaded as GL tex {_tex}");
    }

    private static void SetupVao()
    {
        uint a; Gl.GenVertexArrays(1, &a); _vao = a;
        uint vb; Gl.GenBuffers(1, &vb); _vbo = vb;
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        int stride = Floats * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 3, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(3, 1, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(7 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.BindVertexArray(0);
        _vaoUp = true;
    }

    private static void UploadBuffer(float[] b)
    {
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        fixed (float* p = b) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(b.Length * sizeof(float)), p, Gl.DYNAMIC_DRAW);
    }
}
