using System.Collections.Generic;
using System.Reflection;
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
        (0x25AA, 0x25AA),   // ▪ tab pin/keep button (#46)
        (0x25BE, 0x25BE),   // ▾ channel-dropdown arrow
        (0x2699, 0x2699),   // ⚙ gear (chat options)
    };

    private static ExtenderLog? _log;
    private static GlyphAtlas? _atlas;
    private static byte[]? _fontBytes;         // kept so a settings change can re-bake at a new size
    private static float _bakedPx;             // glyph height the current atlas was baked at
    private static bool _diagged;              // one-shot: log GL error + viewport on the first drawn frame

    /// <summary>The baked atlas (null until <see cref="Init"/>, or when stbtt is absent under run-demo).</summary>
    public static GlyphAtlas? Atlas => _atlas;

    /// <summary>Last swap-time viewport (px), published each hot frame for cold consumers (the settings window
    /// clamps itself on-screen with these). 0 until the first frame draws.</summary>
    public static volatile int ViewportW, ViewportH;
    public static bool Ready => _atlas != null;

    /// <summary>A published frame: the built <see cref="RenderFrame"/> plus a per-frame
    /// <see cref="Scratch"/> buffer (same length, allocated cold at publish). The hot draw copies the base
    /// verts into scratch and applies the fade/slide there, so it allocates nothing and a concurrent
    /// <see cref="Publish"/> can never hand the animator a half-built array.
    /// <see cref="Settled"/>/<see cref="OpaqueCount"/> track the settled-frame cache (#53): when all span
    /// alphas are stable (0 or 1) and residual is 0, Scratch holds the correct data from the last ApplyAnim
    /// and subsequent draws skip the copy+fade walk.</summary>
    private sealed class Frame
    {
        public readonly RenderFrame Rf;
        public readonly float[] Scratch;
        public bool Settled;
        public int OpaqueCount;
        public Frame(RenderFrame rf, float[] scratch) { Rf = rf; Scratch = scratch; }
    }
    /// <summary>All published frames (one per chat window, #41). Volatile-swapped atomically by
    /// <see cref="Publish(RenderFrame[])"/>; the hot <see cref="Draw"/> iterates the snapshot.</summary>
    private static volatile Frame[] _frames = Array.Empty<Frame>();

    // GL objects (created lazily on the first drawn frame; no context exists at init).
    private static uint _prog, _vao, _vbo, _tex;
    private static int _uProj, _uTex, _drawCount;
    // _texUp is volatile: a cold SetFont clears it to force the hot Draw to re-upload the re-baked atlas.
    private static volatile bool _texUp;
    private static bool _vaoUp, _progUp;

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
        _fontBytes = File.ReadAllBytes(fontPath);
        _bakedPx = FontPx;
        _atlas = GlyphAtlas.Bake(_fontBytes, FontPx, Ranges, AtlasSize, log);
        log.Info(_atlas != null ? "TextRenderer: atlas ready" : "TextRenderer: atlas bake unavailable");
    }

    /// <summary>Smallest glyph height the picker offers, and the largest the *slider* offers.</summary>
    public const float MinFontPx = 10f, MaxFontPx = 32f;
    /// <summary>Atlas-bake ceiling. Above the slider max so the game's UI scale can push the effective px past
    /// 32 (effective = font * GUIScale) without the slider itself going that high; caps the atlas size.</summary>
    public const float AtlasMaxPx = 96f;

    /// <summary>Atlas side for a glyph height: glyph area scales with px², so the side scales with px. Round up
    /// to a 128 multiple, floored at the built-in <see cref="AtlasSize"/> (GL3.3 core allows NPOT, but a tidy
    /// multiple keeps the bitmap cache-friendly).</summary>
    private static int AtlasSizeFor(float px)
    {
        int s = ((int)(AtlasSize * px / FontPx) + 127) / 128 * 128;
        return s < AtlasSize ? AtlasSize : s;
    }

    /// <summary>Re-bake the atlas at a new glyph height (cold, on a settings change) and flag the GL texture
    /// for re-upload next frame. No-op when unchanged (debounced for sub-pixel slider drags) or headless.
    /// Swaps the atlas reference atomically; the caller re-publishes the frame so its UVs/metrics match.</summary>
    public static void SetFont(float px)
    {
        if (_fontBytes == null || _log == null) return;
        px = px < MinFontPx ? MinFontPx : px > AtlasMaxPx ? AtlasMaxPx : px;   // effective px (font * UI scale)
        if (System.Math.Abs(px - _bakedPx) < 0.5f) return;   // integer-ish sizes: ignore sub-pixel churn
        GlyphAtlas? baked = GlyphAtlas.Bake(_fontBytes, px, Ranges, AtlasSizeFor(px), _log);
        if (baked == null) return;
        _atlas = baked; _bakedPx = px;
        _texUp = false;   // the hot Draw uploads the new atlas bitmap on the next frame
        _attrVerts = null; // invalidate cached attribution overlay
    }

    /// <summary>Publish frames for all chat windows (#41). Reuses the <see cref="Frame"/> wrapper (and its
    /// Scratch buffer + Settled state) when the <see cref="RenderFrame"/> is reference-equal to a prior
    /// publish, so a focusedOnly/dirty rebuild avoids LOH scratch allocations for unchanged windows (#53).
    /// Changed frames recycle an old scratch buffer when one is large enough (each claimed at most once),
    /// always wrapped in a fresh <see cref="Frame"/> (Settled=false) so the hot draw never tears Rf+Scratch.</summary>
    public static void Publish(RenderFrame[] rfs)
    {
        Frame[] old = _frames;
        var arr = new Frame[rfs.Length];
        for (int i = 0; i < rfs.Length; i++)
        {
            Frame? reuse = null;
            for (int j = 0; j < old.Length; j++)
                if (ReferenceEquals(old[j].Rf, rfs[i])) { reuse = old[j]; break; }
            if (reuse != null) { arr[i] = reuse; continue; }
            // Recycle an unclaimed scratch buffer from the previous publish if large enough.
            float[]? scratch = null;
            int need = rfs[i].Verts.Length;
            for (int j = 0; j < old.Length; j++)
            {
                if (old[j].Scratch.Length >= need)
                {
                    bool claimed = false;
                    for (int k = 0; k < i; k++)
                        if (ReferenceEquals(arr[k].Scratch, old[j].Scratch)) { claimed = true; break; }
                    if (!claimed) { scratch = old[j].Scratch; break; }
                }
            }
            arr[i] = new Frame(rfs[i], scratch ?? new float[need]);
        }
        _frames = arr;
    }

    /// <summary>Single-frame convenience (probe demo, backward compat).</summary>
    public static void Publish(RenderFrame? rf)
        => _frames = rf == null ? Array.Empty<Frame>() : new[] { new Frame(rf, new float[rf.Verts.Length]) };

    /// <summary>Chrome-only convenience (the probe demo): draw a flat batch, no body/animation.</summary>
    public static void Publish(float[] verts, int vertCount)
        => Publish(new RenderFrame { Verts = verts, Count = vertCount, BodyStart = vertCount, BodyEnd = vertCount });

    /// <summary>Clear the drawn batch (e.g. probe demo off).</summary>
    public static void Clear() => _frames = Array.Empty<Frame>();

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
        List<TextWrapper.LineSpan> lines = TextWrapper.WrapRunes(runes, maxWidth, atlas.AdvanceOfCached);
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

    /// <summary>Hot: draw all published frames (one per chat window, #41) through the owned GL3 program.
    /// Applies the fade/slide/scroll (map #13) per frame; the slide residual only moves the focused window's
    /// body (<see cref="RenderFrame.ApplyResidual"/>). One GL state save/restore wraps all windows; the VBO
    /// is re-specified per window (N<=4, tiny buffers — negligible bus cost). Zero-alloc in every path.</summary>
    public static void Draw()
    {
        Frame[] frames = _frames;
        if (frames.Length == 0 || _atlas == null || _log == null) return;
        if (!Gl.Init(_log)) return;

        float focusTau = 70f;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].Rf.ApplyResidual) { focusTau = frames[i].Rf.TauMs; break; }
        float globalResidual = ChatAnim.Tick(Environment.TickCount64, focusTau);
        DateTime now = DateTime.UtcNow;

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

            byte sDepth = Gl.IsEnabled(Gl.DEPTH_TEST), sBlend = Gl.IsEnabled(Gl.BLEND);
            byte sCull = Gl.IsEnabled(Gl.CULL_FACE), sScissor = Gl.IsEnabled(Gl.SCISSOR_TEST);
            int* sbox = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, sbox);
            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            int* vp = stackalloc int[4];
            Gl.GetIntegerv(Gl.VIEWPORT, vp);
            ViewportW = vp[2]; ViewportH = vp[3];
            float* m = stackalloc float[16];
            Ortho(m, vp[2], vp[3]);

            for (int i = 0; i < frames.Length; i++)
            {
                Frame f = frames[i];
                RenderFrame rf = f.Rf;
                if (rf.Count == 0) continue;

                float residual = rf.ApplyResidual ? globalResidual : 0f;
                DateTime ls = rf.LastScroll;
                bool stable = IsStable(rf, residual, now, ls, out int opaque);
                int uploadLen = rf.Verts.Length;
                if (stable && f.Settled && opaque == f.OpaqueCount)
                {
                    // ponytail: settled — Scratch has correct data from the last ApplyAnim.
                    // Single-frame: VBO still holds it; multi-frame: re-upload (shared VBO).
                    if (frames.Length > 1) UploadBuffer(f.Scratch, uploadLen);
                }
                else if (stable)
                {
                    ApplyAnim(f, residual, now, ls);
                    UploadBuffer(f.Scratch, uploadLen);
                    f.Settled = true;
                    f.OpaqueCount = opaque;
                }
                else
                {
                    f.Settled = false;
                    ApplyAnim(f, residual, now, ls);
                    UploadBuffer(f.Scratch, uploadLen);
                }
                _drawCount = rf.Count;

                if (!_diagged)
                {
                    while (Gl.GetError() != 0) { }
                    _diagged = true;
                    DrawFrame(f, m, vp, residual, now, ls);
                    _log?.Info($"TextRenderer: draw verts={_drawCount} viewport={vp[2]}x{vp[3]} " +
                               $"was[depth={sDepth} blend={sBlend} cull={sCull} scissor={sScissor}] glErr=0x{Gl.GetError():x}");
                }
                else DrawFrame(f, m, vp, residual, now, ls);
            }

            if (sDepth != 0) Gl.Enable(Gl.DEPTH_TEST);
            if (sCull != 0) Gl.Enable(Gl.CULL_FACE);
            Gl.Scissor(sbox[0], sbox[1], sbox[2], sbox[3]);
            if (sScissor != 0) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
            if (sBlend == 0) Gl.Disable(Gl.BLEND);
        }

        Gl.BindVertexArray((uint)save[1]);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)save[2]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)save[4]);
        Gl.ActiveTexture((uint)save[3]);
        Gl.UseProgram((uint)save[0]);
    }

    /// <summary>True when the frame's animation output is stable: residual is 0, no blink, and every span's
    /// fade alpha is exactly 0 or 1 (not mid-transition). A stable frame's Scratch won't change across draws,
    /// so ApplyAnim can be skipped. <paramref name="opaqueCount"/> tracks how many spans are at alpha 1 — a
    /// scroll resets the per-window <c>ls</c> and flips faded spans back to opaque, changing this count.</summary>
    private static bool IsStable(RenderFrame rf, float residual, DateTime now, DateTime ls, out int opaqueCount)
    {
        opaqueCount = 0;
        if (residual != 0f) return false;
        if (rf.BlinkEnd > rf.BlinkStart) return false;
        if (rf.Scrolled || !rf.AnimEnabled) { opaqueCount = rf.Spans.Count; return true; }
        List<LineSpanVerts> spans = rf.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            float a = ChatAnim.FadeAlpha(now, spans[i].Ts, ls, true, rf.HoldSec, rf.FadeSec);
            if (a > 0f && a < 1f) return false;
            if (a >= 1f) opaqueCount++;
        }
        return true;
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
            float a = f.Rf.Scrolled ? 1f : ChatAnim.FadeAlpha(now, s.Ts, ls, f.Rf.AnimEnabled, f.Rf.HoldSec, f.Rf.FadeSec);
            int st = s.Start * Floats, en = s.End * Floats;
            for (int v = st; v < en; v += Floats) { dst[v + 1] += residual; dst[v + 7] *= a; }
        }
        // Blink: modulate unread-tab border alpha (500ms period, on 300ms / dim 200ms).
        if (f.Rf.BlinkEnd > f.Rf.BlinkStart)
        {
            float ba = (Environment.TickCount64 % 500) < 300 ? 0.85f : 0.20f;
            int bs = f.Rf.BlinkStart * Floats, be = f.Rf.BlinkEnd * Floats;
            for (int v = bs; v < be; v += Floats) dst[v + 7] *= ba;
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
            DrawPortraits(rf.Portraits, proj, residual, now, ls, rf.Scrolled, rf.AnimEnabled, rf.HoldSec, rf.FadeSec);
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
                                      float residual, DateTime now, DateTime ls, bool scrolled,
                                      bool animEnabled, double holdSec, double fadeSec)
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
            float a = scrolled ? 1f : ChatAnim.FadeAlpha(now, quads[i].Ts, ls, animEnabled, holdSec, fadeSec);
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
        GlyphAtlas? a = _atlas;   // snapshot: a cold SetFont swap must not pair one atlas's Pixels with another's W/H
        if (a == null) return;
        if (_tex == 0) { uint t; Gl.GenTextures(1, &t); _tex = t; }   // create once; a re-bake respecifies the same id (no leak)
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        fixed (byte* px = a.Pixels)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, a.Width, a.Height, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        _texUp = true;
        _log?.Info($"TextRenderer: atlas uploaded as GL tex {_tex} ({a.Width}x{a.Height})");
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

    /// <summary>Upload <paramref name="count"/> floats from <paramref name="b"/> to the shared VBO.
    /// Count is the active vert data (Rf.Verts.Length), not the buffer length (which may be larger after
    /// scratch recycling).</summary>
    private static void UploadBuffer(float[] b, int count)
    {
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        fixed (float* p = b) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(count * sizeof(float)), p, Gl.DYNAMIC_DRAW);
    }

    // ---- attribution overlay (main menu only) -------------------------------------------------

    private static float[]? _attrVerts;
    private static int _attrCount;

    /// <summary>Draw the attribution text in the top-left corner (main menu only). Lazily builds and caches
    /// the vertex data; shares the text shader program, VAO, VBO, and atlas texture with <see cref="Draw"/>.
    /// Called from the SwapBuffers hook when not in-game.</summary>
    public static void DrawOverlay()
    {
        if (_atlas == null || _log == null) return;
        if (!Gl.Init(_log)) return;
        if (_attrVerts == null) BuildAttribution();
        if (_attrCount == 0) return;

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

            byte sDepth = Gl.IsEnabled(Gl.DEPTH_TEST), sBlend = Gl.IsEnabled(Gl.BLEND);
            byte sCull = Gl.IsEnabled(Gl.CULL_FACE), sScissor = Gl.IsEnabled(Gl.SCISSOR_TEST);
            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            int* vp = stackalloc int[4];
            Gl.GetIntegerv(Gl.VIEWPORT, vp);
            ViewportW = vp[2]; ViewportH = vp[3];
            float* m = stackalloc float[16];
            Ortho(m, vp[2], vp[3]);

            BindText(m);
            // LINEAR smooths the synthetic-italic shear (NEAREST creates hard stairstepping).
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            fixed (float* p = _attrVerts)
                Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(_attrVerts.Length * sizeof(float)), p, Gl.DYNAMIC_DRAW);
            Gl.DrawArrays(Gl.TRIANGLES, 0, _attrCount);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);

            // Overlay re-specifies the shared VBO; invalidate the settled-frame cache so Draw()
            // re-uploads on the next in-game frame instead of reading stale attribution data.
            Frame[] frames = _frames;
            for (int i = 0; i < frames.Length; i++) frames[i].Settled = false;

            if (sDepth != 0) Gl.Enable(Gl.DEPTH_TEST);
            if (sCull != 0) Gl.Enable(Gl.CULL_FACE);
            if (sScissor != 0) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
            if (sBlend == 0) Gl.Disable(Gl.BLEND);
        }

        Gl.BindVertexArray((uint)save[1]);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)save[2]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)save[4]);
        Gl.ActiveTexture((uint)save[3]);
        Gl.UseProgram((uint)save[0]);
    }

    private static void BuildAttribution()
    {
        GlyphAtlas atlas = _atlas!;
        string version = typeof(TextRenderer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version.Substring(0, plus);

        string prefix = $"SoW Client Extender {version}. ";
        const string italic = "Wonderbringer, let it turn true.";

        var batch = new VertexBatch(atlas.SolidU, atlas.SolidV);
        float penX = 10f, baseline = atlas.LineHeight + 2f;
        const float a = 0.7f;

        foreach (Rune r in prefix.EnumerateRunes())
        {
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            batch.Glyph(g, penX, baseline, 1f, 1f, 1f, a);
            penX += g.Advance;
        }
        // ponytail: gold = (1, 0.82, 0); synthetic italic via vertex shear
        foreach (Rune r in italic.EnumerateRunes())
        {
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            batch.GlyphItalic(g, penX, baseline, 1f, 0.82f, 0f, a);
            penX += g.Advance;
        }

        _attrVerts = batch.ToArray();
        _attrCount = batch.VertexCount;
    }
}
