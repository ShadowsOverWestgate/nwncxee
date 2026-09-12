using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Graphics;

using Sowx.Services.Chat.Core.Model;

namespace Sowx.Ui;

/// <summary>
/// The UI's GL RIG, not a renderer (#176): it owns the shared GL objects -- a stb_truetype glyph
/// atlas baked once at init (cold), a GL 3.3-core program, and one VAO/VBO pair -- and exposes the
/// primitives every UI service draws through. Nothing here decides WHAT to draw. Bakes a stb_truetype glyph atlas once at init (cold), owns a tiny
/// GL 3.3-core shader + VAO/VBO rig, and exposes the generic drawing primitives any service draws
/// through -- <see cref="DrawBatch"/> (a caller-built vertex batch) and <see cref="DrawPortrait"/>
/// (an engine-resident texture quad). Callers build batches cold (ingest/input thread) via
/// <see cref="VertexBatch"/>; the hot draw only uploads-if-changed and issues one <c>glDrawArrays</c>,
/// allocating nothing in steady state. The client GL context is CORE (no fixed-function/immediate
/// mode), so everything is shader-driven; blend/depth/cull/scissor are snapshot-restored so the
/// engine's own state cache stays consistent. The chat-coupled Publish/RenderFrame draw path lives
/// in <see cref="Sowx.Services.Chat.Render.ChatRenderer"/>, which drives these primitives.
/// </summary>
public static unsafe class TextRig
{
    /// <summary>Font search order, first existing wins. DejaVu preferred (covers our four ranges incl.
    /// Cyrillic); Liberation/FreeSans are the near-universal Linux fallbacks. The Steam Linux Runtime
    /// (Steam Deck) ships the Debian-style truetype/dejavu path, not the Arch-style TTF one.
    /// ponytail: fixed list; a fontconfig query is the ceiling if a distro ships none of these.</summary>
    private static readonly string[] FontCandidates =
    {
        // The copy shipped beside the extender wins (#419): Windows fonts lack the gear/arrow/pin glyphs, and
        // the Linux paths below resolve through Wine's Z: drive under Proton, masking a missing Windows font.
        Path.Combine(AppContext.BaseDirectory, "DejaVuSans.ttf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguisym.ttf"),   // has the symbols
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
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
    private static AtlasSheet? _sheet;         // the one bitmap every face lives in; swapped whole on re-bake
    private static string? _fontPath;          // the default typeface
    private static float _bakedPx;             // glyph height the default face was baked at
    /// <summary>Faces the sheet is baked from; index 0 is the default. Extra faces are appended by <see cref="Face"/>.</summary>
    private static readonly List<FaceRequest> _requests = new();
    private static readonly Dictionary<string, byte[]> _fontBytes = new();

    /// <summary>The default UI face (null until <see cref="Init"/>, or when stbtt is absent under run-demo).</summary>
    public static GlyphAtlas? Atlas => _sheet?.Default;

    /// <summary>A face of the default typeface at <paramref name="px"/>. COLD on a miss: the first request re-bakes
    /// the whole sheet (allocates heavily), so ask in <c>Initialize</c>, never on the present thread. The returned
    /// object is epoch-scoped: re-fetch when <see cref="AtlasEpoch"/> changes. Null when headless or refused.</summary>
    public static GlyphAtlas? Face(float px) => _fontPath == null ? null : Face(_fontPath, px);

    /// <summary>Hot, alloc-free lookup of an already-baked default-typeface face; null on a miss (never bakes).</summary>
    public static GlyphAtlas? BakedFace(float px)
        => _sheet != null && _fontPath != null && _sheet.TryFace(_fontPath, px, out GlyphAtlas f) ? f : null;

    /// <summary>A face of <paramref name="typeface"/> (a TTF path) at <paramref name="px"/>; see <see cref="Face(float)"/>.</summary>
    public static GlyphAtlas? Face(string typeface, float px)
    {
        if (_sheet == null || _log == null) return null;
        if (_sheet.TryFace(typeface, px, out GlyphAtlas f)) return f;
        if (!_fontBytes.TryGetValue(typeface, out byte[]? bytes))
        {
            if (!File.Exists(typeface)) { _log.Info($"TextRig: typeface not found: {typeface}"); return null; }
            _fontBytes[typeface] = bytes = File.ReadAllBytes(typeface);
        }
        _requests.Add(new FaceRequest(typeface, bytes, px));
        if (!Rebake()) { _requests.RemoveAt(_requests.Count - 1); return null; }
        return _sheet.TryFace(typeface, px, out f) ? f : null;
    }

    /// <summary>Bake <see cref="_requests"/> into a fresh sheet and swap it in; on refusal the old sheet stays.
    /// Every face's UVs move, so one epoch bump invalidates all cached glyph geometry.</summary>
    private static bool Rebake()
    {
        AtlasSheet? baked = AtlasSheet.Bake(_requests, Ranges, AtlasSize, _log!);
        if (baked == null) return false;
        _sheet = baked;
        _texUp = false;   // the hot Draw uploads the new sheet bitmap on the next frame
        AtlasEpoch++;     // cached-geometry consumers rebuild: their glyph UVs went stale
        return true;
    }

    /// <summary>Injected log (set in <see cref="Init"/>); the chat draw path reads it via this.</summary>
    internal static ExtenderLog? Log => _log;

    /// <summary>Bumped on every atlas re-bake (<see cref="SetFont"/>), so a cached-geometry consumer
    /// (the attribution overlay) can tell its glyph UVs went stale and rebuild.</summary>
    internal static int AtlasEpoch { get; private set; }

    /// <summary>Last swap-time viewport (px), published each hot frame for cold consumers (the settings window
    /// clamps itself on-screen with these). 0 until the first frame draws.</summary>
    public static volatile int ViewportW, ViewportH;
    public static bool Ready => _sheet != null;

    // GL objects (created lazily on the first drawn frame; no context exists at init). _vbo is the shared
    // draw rig ChatRenderer's Draw/DrawOverlay upload through; internal so it can bind it directly.
    private static uint _prog, _vao, _tex;
    internal static uint _vbo;
    // Batch rig: DrawBatch's own VAO/VBO so its uploads never clobber the shared _vbo (keeps Draw's cache live).
    private static uint _bvao, _bvbo;
    private static bool _bvaoUp;
    private static int _uProj, _uTex, _uAlpha, _uOffset;
    /// <summary>Whole-batch alpha multiplier for the text program, applied via <c>uAlpha</c>. Default 1 (chat's
    /// <c>ChatRenderer.Draw</c> never touches it); <see cref="DrawBatch"/> sets it for the dialog fade and restores 1.</summary>
    private static float _textAlpha = 1f;
    /// <summary>Vertex translation for the text program, applied via <c>uOffset</c>. Default (0,0); the dialog's
    /// clipped content pass sets it to the scroll/body offset and restores 0 (chat's <c>ChatRenderer.Draw</c> is untouched).</summary>
    private static float _texOffX, _texOffY;
    // _texUp is volatile: a cold SetFont clears it to force the hot Draw to re-upload the re-baked atlas.
    // _texUp/_vaoUp are internal so ChatRenderer's Draw/DrawOverlay can gate the same shared rig.
    internal static volatile bool _texUp;
    internal static bool _vaoUp;
    private static bool _progUp;

    // Portrait pass (#26): its own RGBA program + VAO/VBO (a second buffer so the text VBO's
    // upload-if-changed cache stays valid). The engine CAurTexture* is NEVER cached -- see ResolveTid (#33).
    // internal: ChatRenderer's multi-portrait sub-pass drives this same rig.
    internal static uint _progRgb, _pvao, _pvbo;
    internal static int _uProjRgb, _uTexRgb;
    private static bool _progRgbUp;
    internal static bool _pvaoUp;
    /// <summary>Sampler object the portrait pass reads the engine's texture through -- see
    /// <see cref="EnsurePortraitSampler"/>. Same lifetime as <see cref="_pvao"/>: a GL name in the ENGINE's
    /// context, created once and never deleted.</summary>
    internal static uint _psamp;

    internal static readonly delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _aurGet =
        (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)Native.Sym(SymbolMap.AurTextureGet);

    /// <summary>Cold init: load the font, bake the atlas (incl. its solid-white chrome texel). GL state
    /// is driven directly at draw time (snapshot/restore), so nothing to resolve here.</summary>
    public static void Init(ExtenderLog log)
    {
        _log = log;
        if (_sheet != null || _fontPath != null) return;   // idempotent: probe and ChatService may both call
        string? fontPath = Array.Find(FontCandidates, File.Exists);
        if (fontPath == null) { log.Info($"TextRig: no font found (tried {FontCandidates.Length} paths)"); return; }
        log.Info($"TextRig: using font {fontPath}");
        _fontPath = fontPath;
        _fontBytes[fontPath] = File.ReadAllBytes(fontPath);
        _bakedPx = FontPx;
        _requests.Add(new FaceRequest(fontPath, _fontBytes[fontPath], FontPx));
        _sheet = AtlasSheet.Bake(_requests, Ranges, AtlasSize, log);
        log.Info(_sheet != null ? "TextRig: atlas ready" : "TextRig: atlas bake unavailable");
    }

    /// <summary>Smallest glyph height the picker offers, and the largest the *slider* offers.</summary>
    public const float MinFontPx = 10f, MaxFontPx = 32f;
    /// <summary>Atlas-bake ceiling. Above the slider max so the game's UI scale can push the effective px past
    /// 32 (effective = font * GUIScale) without the slider itself going that high; caps the atlas size.</summary>
    public const float AtlasMaxPx = 96f;

    /// <summary>Re-bake the atlas at a new glyph height (cold, on a settings change) and flag the GL texture
    /// for re-upload next frame. No-op when unchanged (debounced for sub-pixel slider drags) or headless.
    /// Swaps the atlas reference atomically; the caller re-publishes the frame so its UVs/metrics match.</summary>
    public static void SetFont(float px)
    {
        if (_sheet == null || _log == null) return;
        px = px < MinFontPx ? MinFontPx : px > AtlasMaxPx ? AtlasMaxPx : px;   // effective px (font * UI scale)
        if (System.Math.Abs(px - _bakedPx) < 0.5f) return;   // integer-ish sizes: ignore sub-pixel churn
        FaceRequest old = _requests[0];
        _requests[0] = old with { Px = px };
        if (Rebake()) _bakedPx = px; else _requests[0] = old;
    }

    internal static void BindText(float* proj)
    {
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_uProj, 1, (byte)Gl.FALSE, proj);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);   // already on TEXTURE0
        Gl.Uniform1i(_uTex, 0);
        Gl.Uniform1f(_uAlpha, _textAlpha);     // 1 for chat; the dialog fade overrides via DrawBatch
        Gl.Uniform2f(_uOffset, _texOffX, _texOffY);   // (0,0) for chat; the dialog scroll pass overrides
        Gl.BindVertexArray(_vao);
    }

    // ---- portrait pass (#26) ------------------------------------------------------------------

    /// <summary>resref -&gt; live GL texture id, or 0 if not yet uploaded / unresolvable. Re-fetches the
    /// engine <c>CAurTexture*</c> from the resman EVERY frame and NEVER caches it (#33): the engine frees
    /// and reloads its portrait textures on area transitions and module unloads, so any stored pointer
    /// dangles and crashes when bound (confirmed via core dump: cached ptr -> AurTextureGetTID -> jump into
    /// freed heap). AurTextureGet is a resref-keyed lookup that returns the current live (borrowed) pointer,
    /// so asking it per frame is the only way to never hold a stale one. Zero GC alloc on our side
    /// (stackalloc name; engine owns the texture).</summary>
    internal static int ResolveTid(string resref)
    {
        IntPtr tex = ResolveTexture(resref);
        // A non-null texture whose TID is still 0 is simply "not uploaded yet"; a portrait still streaming
        // in appears once ready. AurTextureGet only returns Zero for an empty name (guarded upstream:
        // hasPortrait requires a non-empty resref), so the Zero branch is effectively unreachable.
        return Sowx.Framework.AurTex.Tid(tex);
    }

    /// <summary>NWN portrait resrefs are a base plus one size letter -- the suffix table in
    /// <c>CNWPortrait::GetPortrait</c> (nm <c>0x81e910</c>) is <c>{ "", "T", "S", "M", "L", "H" }</c>. Medium is
    /// 64x100; tiny is 16x25, which is what the engine hands the chat log (<c>HandleServerToPlayerChat</c> calls
    /// virtual <c>+0x1d8</c> with <c>edx = 1</c>, @nm <c>0x75be6e</c>) and why those portraits looked blurry.</summary>
    private const byte SizeMedium = (byte)'m';

    /// <summary>If <paramref name="name"/> (a NUL-terminated resref of <paramref name="len"/> bytes including
    /// the NUL) ends in a size letter SMALLER than medium, rewrite it to medium in place. Pure, allocation-free
    /// and self-checked; the caller is responsible for putting the original letter back if the medium variant
    /// turns out not to exist.</summary>
    /// <returns>the index that was rewritten, or -1 if nothing was.</returns>
    private static int PreferMedium(Span<byte> name, int len)
    {
        int last = len - 2;                                     // the byte before the NUL
        if (last < 1) return -1;                                // "" or a one-letter name has no base to keep
        byte c = name[last];
        if (c != (byte)'t' && c != (byte)'T' && c != (byte)'s' && c != (byte)'S') return -1;
        name[last] = SizeMedium;
        return last;
    }

    /// <summary>Portrait TID, preferring the MEDIUM size variant of the resref and falling back to the resref
    /// as given. The engine hands the chat log a tiny (<c>_t</c>) portrait and both portrait paths magnify it,
    /// so the preference is what fixes the blur; the fallback is what keeps a custom portrait that ships no
    /// <c>_m</c> from vanishing outright. Nothing is cached and nothing is allocated -- the rewrite happens in
    /// the stack buffer the resref is marshalled into. A resref already at medium or larger costs exactly one
    /// lookup, as before; only a genuine miss pays a second.</summary>
    internal static int ResolvePortraitTid(string resref)
    {
        if (resref.Length == 0) return 0;
        Span<byte> name = stackalloc byte[17];
        int len = FillCName(resref, name);
        int at = PreferMedium(name, len);
        if (at >= 0)
        {
            int tid;
            fixed (byte* p = name) tid = Sowx.Framework.AurTex.Tid(_aurGet(p, IntPtr.Zero, null, null, 0, 0));
            if (tid != 0) return tid;
            FillCName(resref, name);                            // no medium variant: ask for exactly what we were given
        }
        fixed (byte* q = name) return Sowx.Framework.AurTex.Tid(_aurGet(q, IntPtr.Zero, null, null, 0, 0));
    }

    /// <summary>Self-check for the resref -&gt; C-string marshalling that feeds AurTextureGet: truncation at 16
    /// bytes and NUL-termination (a missing NUL would walk past the name into stack garbage), plus the portrait
    /// size-suffix preference. Headless.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        Span<byte> buf = stackalloc byte[17];
        bool ok = true;
        ok &= FillCName("0123456789abcdef", buf) == 17 && buf[16] == 0 && buf[0] == (byte)'0' && buf[15] == (byte)'f';
        ok &= FillCName("0123456789abcdefEXTRA", buf) == 17 && buf[16] == 0 && buf[15] == (byte)'f';   // over-length truncates
        ok &= FillCName("po_hu_0", buf) == 8 && buf[7] == 0 && buf[0] == (byte)'p';                    // short name NUL-terminates in place
        ok &= FillCName("", buf) == 1 && buf[0] == 0;                                                  // empty -> lone NUL

        int n = FillCName("po_hu_m_99_t", buf);
        ok &= PreferMedium(buf, n) == n - 2 && buf[n - 2] == (byte)'m' && buf[n - 1] == 0;   // tiny -> medium
        n = FillCName("po_hu_m_99_s", buf);
        ok &= PreferMedium(buf, n) == n - 2 && buf[n - 2] == (byte)'m';                      // small -> medium
        n = FillCName("po_hu_m_99_m", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[n - 2] == (byte)'m';                           // already medium: untouched
        n = FillCName("po_hu_m_99_l", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[n - 2] == (byte)'l';                           // larger: never downsized
        n = FillCName("po_hu_m_99_h", buf);
        ok &= PreferMedium(buf, n) < 0;
        n = FillCName("", buf);
        ok &= PreferMedium(buf, n) < 0;                                                      // empty name is left alone
        n = FillCName("t", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[0] == (byte)'t';                               // a lone letter is a base, not a suffix
        n = FillCName("0123456789abcdet", buf);
        ok &= PreferMedium(buf, n) == 15 && buf[15] == (byte)'m' && buf[16] == 0;            // a full-length resref rewrites in place

        log.Info(ok ? "TextRig.SelfCheck: passed"
                    : "TextRig.SelfCheck: FAILED (resref C-string marshalling / portrait size preference)");
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
    /// (content = 1/1.28 of a pow2-padded height; the rest is black at the image bottom). The nominal
    /// 100/128 leaves a row or two of the pad visible along the bottom edge -- linear filtering samples
    /// across the content/pad boundary -- so the crop is two texels tighter, as a fraction so it holds at
    /// every portrait resolution. ponytail: if a live frame ever shows a portrait cropped or with a black
    /// band, this one const is the knob (1.0 = raw).</summary>
    private const float PortraitContentV = 98f / 128f;   // 0.765625, nominal 0.78125 less two texel rows

    /// <summary>Two triangles for a portrait quad. The engine uploads portraits GL-bottom-origin, so the
    /// image arrives upside down: map the quad top to V=1 (image top) and the bottom to the content floor,
    /// which flips it upright AND crops the pow2 black pad band. Full U (no horizontal padding). y-down like
    /// the text ortho. <paramref name="residual"/> slides it with the body; <paramref name="alpha"/> fades it.</summary>
    internal static void FillQuad(float* v, PortraitQuad q, float residual, float alpha)
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

    internal static bool EnsurePortraitProgram()
    {
        if (_progRgbUp) return _progRgb != 0;
        _progRgbUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcRgb);
        if (vs == 0 || fs == 0) return false;
        _progRgb = Gl.CreateProgram();
        Gl.AttachShader(_progRgb, vs); Gl.AttachShader(_progRgb, fs); Gl.LinkProgram(_progRgb);
        int ok; Gl.GetProgramiv(_progRgb, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: portrait program link failed"); _progRgb = 0; return false; }
        _uProjRgb = UniformOf(_progRgb, "uProj"); _uTexRgb = UniformOf(_progRgb, "uTex");
        _log?.Info("TextRig: portrait shader program ready");
        return true;
    }

    /// <summary>Lazily create the sampler both portrait paths draw through. A portrait source (medium, 64x100)
    /// lands in a plate around 36px tall, so the quad is a ~2.8x MINIFICATION -- with whatever filter the engine
    /// last left on that <c>CAurTexture</c> name (often NEAREST) that reads as the reported aliasing. A sampler
    /// object overrides the texture's own filter/wrap for the units it is bound to and mutates NOTHING on the
    /// texture, which is the point: <c>glTexParameteri</c> here would persist on an object the engine owns and
    /// caches (#237), and <c>glGenerateMipmap</c> would respecify it (the AMD/Mesa mip trap). Deliberately
    /// un-mipmapped: the engine's portrait texture may carry only level 0, and a <c>LINEAR_MIPMAP_*</c> min
    /// filter against an incomplete chain is undefined (black, in practice). CLAMP wrap because the crop UVs
    /// stop short of the pow2 padding -- REPEAT could fetch across the edge.
    /// ponytail: bilinear still skips texels at 2.8x; the ceiling is a mip chain on a texture we own, or a
    /// supersampled fetch in <see cref="FSrcRgb"/>.</summary>
    /// <returns>false when the driver's loader gave us no sampler entry points -- callers then draw exactly as
    /// they did before, inheriting the texture's own filter.</returns>
    internal static bool EnsurePortraitSampler()
    {
        if (_psamp != 0) return true;
        if (Gl.GenSamplers == null || Gl.BindSampler == null || Gl.SamplerParameteri == null) return false;
        uint s; Gl.GenSamplers(1, &s);
        if (s == 0) return false;
        Gl.SamplerParameteri(s, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(s, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        _psamp = s;
        return true;
    }

    internal static void SetupPortraitVao()
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
    internal static void Ortho(float* m, float w, float h)
    {
        for (int i = 0; i < 16; i++) m[i] = 0;
        m[0] = 2f / w; m[5] = -2f / h; m[10] = -1f; m[12] = -1f; m[13] = 1f; m[15] = 1f;
    }

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\nuniform mat4 uProj;\nuniform vec2 uOffset;\n" +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\n" +
        "void main(){ gl_Position = uProj*vec4(aPos + uOffset,0.0,1.0); vUV = aUV; vCol = aCol; vA = aA; }\n";
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nuniform float uAlpha;\nout vec4 o;\n" +
        "void main(){ float a = texture(uTex, vUV).r * vA * uAlpha; o = vec4(vCol, a); }\n";

    internal static bool EnsureProgram()
    {
        if (_progUp) return _prog != 0;
        _progUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrc);
        if (vs == 0 || fs == 0) return false;
        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs); Gl.LinkProgram(_prog);
        int ok; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: program link failed"); _prog = 0; return false; }
        _uProj = Uniform("uProj"); _uTex = Uniform("uTex"); _uAlpha = Uniform("uAlpha"); _uOffset = Uniform("uOffset");
        _log?.Info("TextRig: shader program ready");
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
            _log?.Info($"TextRig: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
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

    internal static void UploadTexture()
    {
        AtlasSheet? a = _sheet;   // snapshot: a cold re-bake swap must not pair one sheet's Pixels with another's side
        if (a == null) return;
        if (_tex == 0) { uint t; Gl.GenTextures(1, &t); _tex = t; }   // create once; a re-bake respecifies the same id (no leak)
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        // #468: LINEAR so scaled/animated text resamples smoothly; the 1px gutter keeps level-0 neighbours apart.
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        fixed (byte* px = a.Pixels)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, a.Side, a.Side, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        // #237: back to the GL default, in the ENGINE's context, or every engine texture upload after this
        // one reads its rows one byte at a time. GLRender::CreateTexture @0x4ca2b0 -- the CAurTexture upload
        // path, and the one an area transition runs for every texture it loads -- calls glTexImage2D with no
        // pixel-store call of its own, and GLRender::DirectCreateTexture @0x4c9cc0 SKIPS both its set and its
        // restore when the caller asks for 4 (`if (align != 4)`). Both assume the default is still in place.
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        _texUp = true;
        _log?.Info($"TextRig: atlas uploaded as GL tex {_tex} ({a.Side}², {a.FaceCount} face(s))");
    }

    /// <summary>Create a VAO + its own VBO wired to the text vertex layout (pos2, uv2, rgb3, alpha1).
    /// A VAO captures the array-buffer bound at attrib-pointer time, so each rig owns its buffer: the shared
    /// pair backs <c>ChatRenderer.Draw</c>'s settled-frame cache; the batch pair lets <see cref="DrawBatch"/> upload
    /// without clobbering it (chat keeps its cache while a dialog modal is open).</summary>
    private static uint MakeVao(out uint vbo)
    {
        uint a; Gl.GenVertexArrays(1, &a);
        uint vb; Gl.GenBuffers(1, &vb); vbo = vb;
        Gl.BindVertexArray(a);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
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
        return a;
    }

    internal static void SetupVao() { _vao = MakeVao(out _vbo); _vaoUp = true; }
    private static void SetupBatchVao() { _bvao = MakeVao(out _bvbo); _bvaoUp = true; }

    /// <summary>Upload <paramref name="count"/> floats from <paramref name="b"/> to the shared VBO.
    /// Count is the active vert data (Rf.Verts.Length), not the buffer length (which may be larger after
    /// scratch recycling).</summary>
    internal static void UploadBuffer(float[] b, int count)
    {
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        fixed (float* p = b) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(count * sizeof(float)), p, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Draw a caller-built vertex batch (x,y,u,v,r,g,b,a) through the shared text program, atlas,
    /// VAO and VBO -- the same primitives the chatbox uses, so rects sample the solid texel and glyphs sample
    /// their atlas cell. Used by <c>DialogService</c> (#dialog) to render its own overlay inside the frame
    /// present ChatService owns. Sets its own full GL preamble; no state snapshot (#210). Uploads go to the batch
    /// rig's own VBO (<see cref="MakeVao"/>), so chat's shared VBO and its settled-frame cache stay intact --
    /// chat does not re-upload while a dialog modal is open.</summary>
    /// <param name="offX">Vertex translation (px) applied via <c>uOffset</c> -- the dialog scrolls its content
    /// with it; 0 for un-scrolled batches.</param>
    /// <param name="clip">When true, scissor the draw to (<paramref name="cx"/>,<paramref name="cy"/>,
    /// <paramref name="cw"/>,<paramref name="ch"/>) in screen px (y-down, top-left); the box is saved + restored.</param>
    public static void DrawBatch(float[] verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (verts == null || count == 0 || _sheet == null || _log == null || alpha <= 0f) return;
        if (!Gl.Init(_log)) return;
        _textAlpha = alpha < 1f ? alpha : 1f;   // read by BindText; restored to 1 before returning
        _texOffX = offX; _texOffY = offY;       // ditto -- restored to (0,0) before returning

        // #210: no state snapshot -- UiHostService resyncs the engine on hand-back, and every draw below
        // sets its own full preamble.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (EnsureProgram())
        {
            if (!_texUp) UploadTexture();
            if (!_bvaoUp) SetupBatchVao();

            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            int* vp = stackalloc int[4];
            Gl.GetIntegerv(Gl.VIEWPORT, vp);
            ViewportW = vp[2]; ViewportH = vp[3];
            float* m = stackalloc float[16];
            Ortho(m, vp[2], vp[3]);

            // GL scissor is bottom-left origin; our rects are y-down top-left, so flip Y.
            if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, vp[3] - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
            else Gl.Disable(Gl.SCISSOR_TEST);

            BindText(m);
            Gl.BindVertexArray(_bvao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _bvbo);
            fixed (float* p = verts)
                Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(count * Floats * sizeof(float)), p, Gl.DYNAMIC_DRAW);
            Gl.DrawArrays(Gl.TRIANGLES, 0, count);
            // No settled-cache invalidation: the batch rig owns _bvbo, so chat's _vbo (and its cache) is untouched.

        }

        _textAlpha = 1f; _texOffX = 0f; _texOffY = 0f;   // leave the shared program neutral for chat's Draw
    }

    /// <summary>Draw a single speaker portrait quad (resref -> engine-resident GL texture, never cached -- see
    /// <see cref="ResolveTid"/>) through the shared RGBA portrait program. Used by <c>DialogService</c> to put
    /// a portrait in its overlay. No-op until the engine has uploaded the texture (TID 0) or when the resref is
    /// empty. Sets its own full GL preamble (no snapshot, #210); the image is flipped upright + pow2-pad-cropped like the chat pass.</summary>
    public static void DrawPortrait(string resref, float x, float y, float w, float h, float alpha = 1f,
                                    bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (string.IsNullOrEmpty(resref) || _sheet == null || _log == null || alpha <= 0f) return;
        if (_aurGet == null || !Sowx.Framework.AurTex.Ready) return;
        if (!Gl.Init(_log)) return;
        if (!EnsurePortraitProgram()) return;
        int tid = ResolvePortraitTid(resref);
        if (tid == 0) return;   // not uploaded yet this frame

        // #210: no state snapshot -- UiHostService resyncs the engine on hand-back, and every draw below
        // sets its own full preamble.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (!_pvaoUp) SetupPortraitVao();

        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        float* m = stackalloc float[16];
        Ortho(m, vp[2], vp[3]);
        if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, vp[3] - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
        else Gl.Disable(Gl.SCISSOR_TEST);

        float* verts = stackalloc float[Floats * 6];
        FillQuad(verts, new PortraitQuad(x, y, w, h, resref, default), 0f, alpha < 1f ? alpha : 1f);

        Gl.UseProgram(_progRgb);
        Gl.UniformMatrix4fv(_uProjRgb, 1, (byte)Gl.FALSE, m);
        Gl.Uniform1i(_uTexRgb, 0);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(Floats * 6 * sizeof(float)), verts, Gl.DYNAMIC_DRAW);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        bool samp = EnsurePortraitSampler();
        if (samp) Gl.BindSampler(0, _psamp);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        // Unbind is load-bearing, not tidiness: a sampler left on unit 0 would override the glyph atlas's own filter/wrap state.
        if (samp) Gl.BindSampler(0, 0);
    }

    /// <summary>Portrait content aspect (width:height); the engine crops portraits taller-than-wide. Public so
    /// callers size a portrait rect to match the texture and avoid distortion.</summary>
    public const float PortraitAspect = 0.64f;
}
