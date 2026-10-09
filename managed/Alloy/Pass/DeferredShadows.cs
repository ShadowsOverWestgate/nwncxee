using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-188 step 3 (ADR 0029): the deferred path's SHADOW pass -- one compute dispatch between the cutout
/// pass and <see cref="DeferredLights"/> that chooses, per PIXEL, the four shadow-casting lights reaching it most
/// strongly and evaluates each one's cube visibility once into the visibility record this class owns. The light
/// walk in <c>cx_deferred_cs</c> then reads a light's shadow out of that record instead of filtering it inline
/// per candidate, which is what <c>shadow.cubedist</c>'s -10.7 ms was measuring.
/// <para>The record is two render-size (or half-size) textures -- RGBA8 visibility and RG32UI (the four packed
/// cube slots plus the surface's depth and normal key) -- whose format is <c>cx_shrec.glsl</c>'s and nothing
/// else. <c>shadow.deferredres</c> picks the resolution; at half the reader upsamples with a depth- and
/// normal-aware bilateral filter, which also mixes the four fixed disc rotations the stage interleaves across a
/// 2x2 block of record texels.</para>
/// <para>The program is a session-level requirement of the deferred path: without it every point-light shadow
/// on an opaque surface would be one hardware tap, so a link failure stands <see cref="DeferredLights.Active"/>
/// down and the frame goes forward instead (ADR 0029's all-or-nothing rule). An allocation that fails mid-frame
/// is a per-frame refusal: loud, counted, and the walk's own hardware tap draws it.</para></summary>
internal static unsafe class DeferredShadows
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_defshadow_cs";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>.</summary>
    private const int Group = 8;

    /// <summary>Bytes a record texel the two targets cost together, for the allocation line.</summary>
    private const int BytesPerTexel = 4 + 8;

    /// <summary>Lights one record texel can hold -- <c>cx_shrec.glsl</c>'s <c>CX_SH_MAX</c>, which is the
    /// definition; this is the ceiling <c>shadow.perpixel</c> is declared against. Four packed slot bytes in one
    /// word and four visibility channels in one RGBA8, so widening it is a format change.</summary>
    internal const int RecordLights = 4;

    /// <summary>How far above the best DROPPED light's score a record lane must sit to keep its shadow in full
    /// -- <c>cx_shrec.glsl</c>'s <c>CX_SH_FADE</c>, which is the definition; this is the mirror the continuity
    /// self-check sweeps. An octave: a lane at the budget's edge is fully dissolved, so the light that falls out
    /// of the record had already faded to unshadowed and the budget's membership step is invisible.</summary>
    internal const float RecordFade = 2f;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredShadows");

    /// <summary><c>_prog</c> is the program the pass BINDS and <c>_progFull</c> the shipped one it goes back
    /// to -- they differ only while <c>/alloy bench</c> walks the ablation ladder (<see cref="Ablate"/>).</summary>
    private static uint _prog, _progFull;
    private static AblateProgram _abl;
    private static int _locRect, _locInvVp, _locProj, _locCubes, _locShSize, _locShRes;
    private static uint _texVis, _texKey;
    private static int _w, _h;
    /// <summary>Dispatches this period, and the record size the last one ran at, for the <c>[alloy]</c> line.</summary>
    private static int _runs;

    /// <summary>The record is allocated and was written this frame: the divisor the reader must upsample by,
    /// 0 when there is no record and every shadow is the walk's own hardware tap. Read by
    /// <see cref="DeferredLights"/> after <see cref="Run"/>.</summary>
    internal static int Res { get; private set; }

    /// <summary>The record's size in texels -- the reader's own bounds test.</summary>
    internal static int Width => _w;
    internal static int Height => _h;
    internal static uint TexVis => _texVis;
    internal static uint TexKey => _texKey;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass. <paramref name="vp"/> is the JITTERED view-projection the prepass rasterised with;
    /// <paramref name="viewport"/> the render viewport. Sets <see cref="Res"/> for the reader.</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* proj, int* viewport, float near, float far)
    {
        Res = 0;
        if (_prog == 0 || viewport[2] <= 0 || viewport[3] <= 0) return;
        if (PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned depth attachment is not armed", "point-light shadows take one hardware tap this frame"); return; }
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;
        int div = Shadows.DeferredRes.V;
        int w = (viewport[2] + div - 1) / div, h = (viewport[3] + div - 1) / div;
        if (!Ensure(w, h)) return;

        Gl.UseProgram(_prog);
        // The light SSBO, the cluster table and the mover spheres, and this pass's own CxPass block plus the
        // cube arrays -- the same binds a lit draw and the deferred walk get, so the ranking this pass does
        // cannot differ from the walk's by an input.
        AlloyLights.BindForDraw();
        AlloyRenderer.BindPass(_locCubes, viewport, near, far);
        Gl.Uniform4iv(_locRect, 1, viewport);
        Gl.UniformMatrix4fv(_locInvVp, 1, 0, (float*)&invVp);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform2i(_locShSize, w, h);
        Gl.Uniform1i(_locShRes, div);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, DeferredLights.BindStats, DeferredLights.StatsBuf);

        BindUnit(TextureSlots.Gbuf0, GBuffer.Tex0);
        BindUnit(TextureSlots.Gbuf1, GBuffer.Tex1);
        BindUnit(TextureSlots.Gbuf2, GBuffer.Tex2);
        BindUnit(TextureSlots.Gbuf3, GBuffer.Tex3);
        BindUnit(TextureSlots.GbufDepth, PostChain.DepthTex);
        Gl.BindImageTexture(0, _texVis, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(1, _texKey, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG32UI);

        Gl.DispatchCompute((uint)(w + Group - 1) / Group, (uint)(h + Group - 1) / Group, 1);
        // the record is read back by the next dispatch as a TEXTURE FETCH, which the image-store bit alone
        // does not order
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG32UI);
        BindUnit(TextureSlots.Gbuf0, 0); BindUnit(TextureSlots.Gbuf1, 0);
        BindUnit(TextureSlots.Gbuf2, 0); BindUnit(TextureSlots.Gbuf3, 0);
        BindUnit(TextureSlots.GbufDepth, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.UseProgram(0);
        _runs++;
        Res = div;
    }

    /// <summary>(Re)allocate the record at <paramref name="w"/>x<paramref name="h"/> texels. False = the record
    /// cannot be had on this context, said once and loudly: the frame then takes one hardware tap per light.</summary>
    private static bool Ensure(int w, int h)
    {
        if (_texVis != 0 && _w == w && _h == h) return true;
        if (Gl.BindImageTexture == null)
            return _down.Refuse(_log, "glBindImageTexture is unresolved",
                                "point-light shadows take one hardware tap on the deferred path");
        // immutable storage, one level: a new size is a new name. NEAREST because the bilateral upsample
        // fetches texels and weights them itself -- and an integer texture under LINEAR is incomplete.
        RenderTarget.Create(ref _texVis, Gl.RGBA8, w, h, Gl.NEAREST);
        RenderTarget.Create(ref _texKey, Gl.RG32UI, w, h, Gl.NEAREST);
        _w = w; _h = h;
        _log?.Info($"DeferredShadows: visibility record {w}x{h} ({(long)w * h * BytesPerTexel >> 20} MiB)");
        return true;
    }

    private static void BindUnit(uint unit, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
    }

    /// <summary>Build the program on the first frame that wants it, once. False is latched and LOUD: the
    /// deferred path then stands down in favour of the forward loop, which has shadows of its own.</summary>
    internal static bool Ready()
    {
        if (_prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing",
                              "deferred point lights are off for the session -- the forward loop walks every light");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "DeferredShadows");
        if (prog == 0)
        {
            _log?.Always("DeferredShadows: the per-pixel shadow program did not build (the driver's own log is "
                       + "above) -- deferred point lights are off for the session and the forward light loop, "
                       + "whose own shadow filter is intact, draws the frame");
            return _down.Fail(_log, "the compute program did not link",
                              "deferred point lights are off for the session");
        }
        _progFull = prog;
        Select(prog);
        _log?.Info("DeferredShadows: ready -- one 8x8 compute dispatch writes the per-pixel visibility record");
        return true;
    }

    /// <summary>Bind <paramref name="prog"/> and take its uniform locations. At link, and again whenever the
    /// bench swaps an ablation variant in or out: a variant is its own program object, so none of the shipped
    /// program's locations carry over.</summary>
    private static void Select(uint prog)
    {
        _prog = prog;
        _locRect = GlProgram.Loc(prog, "uRect");
        _locInvVp = GlProgram.Loc(prog, "uInvVp");
        _locProj = GlProgram.Loc(prog, "uProj");
        _locCubes = GlProgram.Loc(prog, "uLocalCubes");
        _locShSize = GlProgram.Loc(prog, "uShSize");
        _locShRes = GlProgram.Loc(prog, "uShRes");
        // The two cube arrays carry no layout(binding) -- the lib is shared with programs that bind them on
        // other units -- so they take theirs here, as every lit program does at its own link.
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
    }

    /// <summary>CX-188: run the record on the ablation ladder's rung <paramref name="stage"/> (1 = the
    /// G-buffer read and the gate, 2 = + the candidate ranking), or on the shipped program at 0. Called by
    /// <c>/alloy bench</c> and nothing else; it restores 0 on every step and at finish. False = that rung did
    /// not link, which refuses THE STEP -- the shipped program stays bound.
    /// <para>Rungs 1 and 2 write an all-visible record, so those steps are a brighter frame as well as a
    /// cheaper one. That is what a shadow pass with its taps taken out means.</para></summary>
    internal static bool Ablate(int stage)
    {
        if (_progFull == 0) return stage == 0;
        if (stage <= 0) { Select(_progFull); return true; }
        uint p = _abl.Get(ShdCs, stage, _log, "DeferredShadows");
        if (p == 0) { Select(_progFull); return false; }
        Select(p);
        return true;
    }

    /// <summary>Context loss: the program and the record textures are gone.</summary>
    internal static void Forget()
    {
        _prog = 0; _progFull = 0; _texVis = 0; _texKey = 0; _w = _h = 0; Res = 0;
        _abl.Forget();
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        return _down.Failed ? "defshadow STOOD DOWN"
             : !Lighting.Deferred.V ? "defshadow off"
             : n == 0 ? "defshadow idle"
             : $"defshadow {_w}x{_h} ({(float)n / MathF.Max(frames, 1):F2} dispatches/frame, "
             + $"{(long)_w * _h * BytesPerTexel >> 20} MiB"
             + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "") + ")";
    }
}
