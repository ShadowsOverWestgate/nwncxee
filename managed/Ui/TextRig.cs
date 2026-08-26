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

    /// <summary>The baked atlas (null until <see cref="Init"/>, or when stbtt is absent under run-demo).</summary>
    public static GlyphAtlas? Atlas => _atlas;

    /// <summary>Injected log (set in <see cref="Init"/>); the chat draw path reads it via this.</summary>
    internal static ExtenderLog? Log => _log;

    /// <summary>Bumped on every atlas re-bake (<see cref="SetFont"/>), so a cached-geometry consumer
    /// (the attribution overlay) can tell its glyph UVs went stale and rebuild.</summary>
    internal static int AtlasEpoch { get; private set; }

    /// <summary>Last swap-time viewport (px), published each hot frame for cold consumers (the settings window
    /// clamps itself on-screen with these). 0 until the first frame draws.</summary>
    public static volatile int ViewportW, ViewportH;
    public static bool Ready => _atlas != null;

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

    internal static readonly delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _aurGet =
        (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)Native.Sym(SymbolMap.AurTextureGet);

    /// <summary>Cold init: load the font, bake the atlas (incl. its solid-white chrome texel). GL state
    /// is driven directly at draw time (snapshot/restore), so nothing to resolve here.</summary>
    public static void Init(ExtenderLog log)
    {
        _log = log;
        if (_atlas != null) return;   // idempotent: probe and ChatService may both call
        string? fontPath = Array.Find(FontCandidates, File.Exists);
        if (fontPath == null) { log.Info($"TextRig: no font found (tried {FontCandidates.Length} paths)"); return; }
        log.Info($"TextRig: using font {fontPath}");
        _fontBytes = File.ReadAllBytes(fontPath);
        _bakedPx = FontPx;
        _atlas = GlyphAtlas.Bake(_fontBytes, FontPx, Ranges, AtlasSize, log);
        log.Info(_atlas != null ? "TextRig: atlas ready" : "TextRig: atlas bake unavailable");
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
        AtlasEpoch++;     // cached-geometry consumers (attribution overlay) rebuild: their glyph UVs went stale
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
        log.Info(ok ? "TextRig.SelfCheck: passed"
                    : "TextRig.SelfCheck: FAILED (resref C-string marshalling)");
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
        _log?.Info($"TextRig: atlas uploaded as GL tex {_tex} ({a.Width}x{a.Height})");
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
    /// present ChatService owns. State is snapshot/restored like <c>ChatRenderer.Draw</c>. Uploads go to the batch
    /// rig's own VBO (<see cref="MakeVao"/>), so chat's shared VBO and its settled-frame cache stay intact --
    /// chat does not re-upload while a dialog modal is open.</summary>
    /// <param name="offX">Vertex translation (px) applied via <c>uOffset</c> -- the dialog scrolls its content
    /// with it; 0 for un-scrolled batches.</param>
    /// <param name="clip">When true, scissor the draw to (<paramref name="cx"/>,<paramref name="cy"/>,
    /// <paramref name="cw"/>,<paramref name="ch"/>) in screen px (y-down, top-left); the box is saved + restored.</param>
    public static void DrawBatch(float[] verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (verts == null || count == 0 || _atlas == null || _log == null || alpha <= 0f) return;
        if (!Gl.Init(_log)) return;
        _textAlpha = alpha < 1f ? alpha : 1f;   // read by BindText; restored to 1 before returning
        _texOffX = offX; _texOffY = offY;       // ditto -- restored to (0,0) before returning

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
            if (!_bvaoUp) SetupBatchVao();

            byte sDepth = Gl.IsEnabled(Gl.DEPTH_TEST), sBlend = Gl.IsEnabled(Gl.BLEND);
            byte sCull = Gl.IsEnabled(Gl.CULL_FACE), sScissor = Gl.IsEnabled(Gl.SCISSOR_TEST);
            int* sbox = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, sbox);   // save the box (chat's pattern)
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

            if (sDepth != 0) Gl.Enable(Gl.DEPTH_TEST);
            if (sCull != 0) Gl.Enable(Gl.CULL_FACE);
            Gl.Scissor(sbox[0], sbox[1], sbox[2], sbox[3]);
            if (sScissor != 0) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
            if (sBlend == 0) Gl.Disable(Gl.BLEND);
        }

        _textAlpha = 1f; _texOffX = 0f; _texOffY = 0f;   // leave the shared program neutral for chat's Draw
        Gl.BindVertexArray((uint)save[1]);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)save[2]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)save[4]);
        Gl.ActiveTexture((uint)save[3]);
        Gl.UseProgram((uint)save[0]);
    }

    /// <summary>Draw a single speaker portrait quad (resref -> engine-resident GL texture, never cached -- see
    /// <see cref="ResolveTid"/>) through the shared RGBA portrait program. Used by <c>DialogService</c> to put
    /// a portrait in its overlay. No-op until the engine has uploaded the texture (TID 0) or when the resref is
    /// empty. Own GL state save/restore; the image is flipped upright + pow2-pad-cropped like the chat pass.</summary>
    public static void DrawPortrait(string resref, float x, float y, float w, float h, float alpha = 1f,
                                    bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (string.IsNullOrEmpty(resref) || _atlas == null || _log == null || alpha <= 0f) return;
        if (_aurGet == null || !Sowx.Framework.AurTex.Ready) return;
        if (!Gl.Init(_log)) return;
        if (!EnsurePortraitProgram()) return;
        int tid = ResolveTid(resref);
        if (tid == 0) return;   // not uploaded yet this frame

        int* save = stackalloc int[5];
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &save[0]);
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &save[1]);
        Gl.GetIntegerv(Gl.ARRAY_BUFFER_BINDING, &save[2]);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &save[3]);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &save[4]);

        if (!_pvaoUp) SetupPortraitVao();

        byte sDepth = Gl.IsEnabled(Gl.DEPTH_TEST), sBlend = Gl.IsEnabled(Gl.BLEND);
        byte sCull = Gl.IsEnabled(Gl.CULL_FACE), sScissor = Gl.IsEnabled(Gl.SCISSOR_TEST);
        int* sbox = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, sbox);
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
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);

        if (sDepth != 0) Gl.Enable(Gl.DEPTH_TEST);
        if (sCull != 0) Gl.Enable(Gl.CULL_FACE);
        Gl.Scissor(sbox[0], sbox[1], sbox[2], sbox[3]);
        if (sScissor != 0) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
        if (sBlend == 0) Gl.Disable(Gl.BLEND);

        Gl.BindVertexArray((uint)save[1]);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)save[2]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)save[4]);
        Gl.ActiveTexture((uint)save[3]);
        Gl.UseProgram((uint)save[0]);
    }

    /// <summary>Portrait content aspect (width:height); the engine crops portraits taller-than-wide. Public so
    /// callers size a portrait rect to match the texture and avoid distortion.</summary>
    public const float PortraitAspect = 0.64f;
}
