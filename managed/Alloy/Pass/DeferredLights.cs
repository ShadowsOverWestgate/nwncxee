using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-188 (ADR 0029): the deferred point-light pass -- one compute dispatch over the render target after
/// the opaque and cutout passes, reading <see cref="GBuffer"/> and adding every point light's diffuse, specular,
/// film highlight and hot-light debt into the HDR target. Runs between <see cref="PassId.CutoutOwned"/> and
/// <see cref="PassId.GrassOwned"/>: grass's root blend copies the lit frame under each blade.
/// <para>The arithmetic lives in <c>cx_deferred_cs</c>, which composes the SAME <c>cx_lightfrag</c> and
/// <c>cx_material</c> the forward loop is -- this class's whole job is to hand that stage every uniform, sampler
/// and buffer the lit program gets, so the two cannot differ by an input.</para></summary>
internal static unsafe class DeferredLights
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_deferred_cs";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>: one tile, and the
    /// dispatch covers the viewport in ceil(size / this) groups.</summary>
    private const int Group = 16;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredLights");

    /// <summary>The deferred pass's stats buffer: [0] tiles whose list overflowed, [1] the lights they dropped,
    /// [2] pixels that found no usable shadow record, [3] lights <see cref="DeferredShadows"/> left unshadowed
    /// past <c>shadow.perpixel</c>'s budget. The shader's binding, and a point nothing else in the colour bracket
    /// takes; the shadow pass binds the same buffer, so one harvest covers both dispatches.</summary>
    internal const uint BindStats = 29;
    private const int StatsWords = 8;
    /// <summary>Frames between stats harvests. The counters accumulate GPU-side and are never reset, so a
    /// sampled harvest loses nothing -- the delta since the last one is what the period line adds up.</summary>
    private const int HarvestEvery = 64;

    /// <summary><c>_prog</c> is the program the pass BINDS and <c>_progFull</c> the shipped one it goes back to
    /// -- they differ only while <c>/alloy bench</c> is walking the ablation ladder (<see cref="Ablate"/>).</summary>
    private static uint _prog, _progFull, _bufStats;
    private static AblateProgram _abl;
    /// <summary>CX-188: the <c>CX_SH_INLOOP</c> variant -- the walk's own hardware tap per shadowing light past
    /// the record, for the escapes that mean "every light": <c>shadow.cap 0</c>, <c>shadow.perpixel 0</c>, or no
    /// record this frame. The shipped program has no tap in its loop (a missed record is filled before the walk),
    /// because the tap's presence alone cost the pass 2.7 ms. Built on the first frame that needs it; a link
    /// failure is latched and said once, and those escapes then run the shipped program's record budget.</summary>
    private static uint _progInLoop;
    private static bool _inLoopFailed, _inLoopOn, _ablating;
    /// <summary>One program's uniform locations: the walk's (<c>_l</c>, whichever of the shipped, escape or ablation
    /// program is bound) and the film pass's (<c>_lFilm</c>), taken once at each one's link.</summary>
    private struct Locs
    {
        internal int Rect, InvVp, Proj, View, Eye, Cubes, Material, LightOcc, LmMovers, Tiles, LightCount, ReflK, ShSize, ShRes;
    }
    private static Locs _l, _lFilm;
    /// <summary>CX-188: the FILM pass -- this stage compiled with <c>CX_FILM_PASS</c>, a second dispatch that adds
    /// the water film's static highlights (CxFilmStatics) and nothing else. The walk program carries no film:
    /// with the film's light loop in it the pass ran 2.1 ms slower (/alloy bench, 2026-10-08), the same cliff the
    /// in-loop fallback tap caused. Dispatched only while <see cref="Weather.ReflK"/> says a film can lift.</summary>
    private static uint _progFilm;

    /// <summary>The stats buffer <see cref="DeferredShadows"/> counts into as well. Minted by
    /// <see cref="Ready"/>, which <see cref="Active"/> calls before any pass of the frame runs.</summary>
    internal static uint StatsBuf => _bufStats;
    /// <summary>Dispatches this period, for the <c>[alloy]</c> line.</summary>
    private static int _runs;
    /// <summary>Tiles that overflowed their light list and lights they dropped, since the last period line, plus
    /// the GPU-side totals the deltas are taken against.</summary>
    private static uint _overTiles, _overLights, _lastTiles, _lastLights;
    /// <summary>CX-188 step 3, since the last period line: pixels that found no usable visibility record and
    /// lights the per-pixel budget left unshadowed, plus the GPU-side totals the deltas are taken against.
    /// CX-188 budget: the two differ now. A record MISS falls back to one hardware tap per light -- a harder
    /// shadow, never a missing one, and a count that fills the screen means the record is not working. A light
    /// past the BUDGET is deliberate and costs nothing: it is the number <c>shadow.perpixel</c> is spending.</summary>
    private static uint _shMiss, _shDrop, _lastMiss, _lastDrop;
    /// <summary>CX-188 budget, since the last period line: the walk's own census -- lights walked and hardware
    /// shadow taps it still paid, BOTH in the shader's own <c>CX_WALK_UNIT</c> sixteens, and the
    /// deferred pixels both are divided by. These are the two numbers the budget is about: a lights-per-pixel
    /// that no longer grows with the area's lamp count, and a taps-per-pixel that the per-pixel shadow budget
    /// holds near zero.</summary>
    private static ulong _walked, _taps, _pixels;
    private static uint _lastWalk, _lastTaps, _lastPix;
    /// <summary>CX-188 ablation: list ENTRIES the walk touched, in the same <c>CX_WALK_UNIT</c> sixteens.
    /// Entries minus lights lit is what the reach window, the relevance cut and the facing test rejected --
    /// work the walk paid for and got no light back from, which is the hypothesis the ladder tests.</summary>
    private static ulong _entries;
    private static uint _lastEnt;
    private static GpuReadback _statsRead;
    private static int _statsSeq, _statsFrame;

    /// <summary>The colour pass may draw opaque and cutout batches with the <c>CX_DEFERRED</c> twin this frame:
    /// <c>light.deferred</c> on, this pass's program linked, the G-buffer allocated, and a main-view (non-capture)
    /// perspective pass. Read by <see cref="Issue"/> per pass; false = the forward loop, said once and loudly when
    /// it is a failure rather than the knob.
    /// <para>Every <c>/alloy viz</c> mode draws forward (ADR 0029), so a viz frame is never deferred: a debug view
    /// that showed this path's own answer could not disagree with it.</para>
    /// <para>The capture and the planar mirror are the FRAME's half of the gate -- it reaches
    /// <see cref="Run"/> only on a non-capture perspective pass a twin actually wrote
    /// (<see cref="GBuffer.Written"/>), which is what keeps a frame from being half-deferred.</para>
    /// <para>CX-188 step 3: <see cref="DeferredShadows"/> is part of the gate. Without its per-pixel record
    /// every point-light shadow on an opaque surface would be a single hardware tap, and the honest answer to
    /// that is the forward loop, whose own filter is intact -- not a deferred frame with worse shadows.</para></summary>
    internal static bool Active => Lighting.Deferred.V && AlloyRenderer._lightViz == 0
                                   && GBuffer.Ready && !GBuffer.Failed && Programs._progLitDef != 0
                                   && Ready() && DeferredShadows.Ready();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass. <paramref name="vp"/> is the JITTERED view-projection the prepass rasterised with
    /// (world positions are reconstructed from its inverse); <paramref name="viewport"/> the render viewport.</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far)
    {
        if (!Active) return;
        // The HDR target IS the image this pass stores into and its depth attachment the surface it
        // reconstructs: with neither armed there is no deferred frame to correct.
        if (!PostChain.Armed || PostChain.HdrTex == 0 || PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned HDR target is not armed", "no deferred point lights this frame"); return; }
        if (viewport[2] <= 0 || viewport[3] <= 0) return;
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;
        // The same expression AlloyRenderer derives _eyePos with, from the same matrix: V has to be the lit
        // stage's own normalize(uEyePos - vWorld), not an approximation of it.
        if (!Matrix4x4.Invert(*view, out Matrix4x4 camToWorld)) return;
        Vector3 eye = camToWorld.Translation;

        if (!_ablating) SelectEscape(AlloyLights.ShadowCap <= 0f || AlloyLights.ShadowPerPixel <= 0f || DeferredShadows.Res <= 0);
        Gl.UseProgram(_prog);
        // The light SSBO, the cluster table and the mover spheres (binding 25), exactly as a lit draw gets them.
        AlloyLights.BindForDraw();
        // ...and this pass's own CxPass block, the cascade and cube arrays, and uLocalCubes. Filled from the same
        // frame state and the same viewport as the colour pass's binds, so the block compares equal and no upload
        // happens -- but it is FILLED rather than assumed: a pass that borrowed another view's cluster table
        // leaves the block saying so (uBounds), and this walk reads that.
        SetUniforms(in _l, viewport, near, far, &invVp, view, proj, &eye);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _bufStats);

        BindUnit(TextureSlots.ShadowVis, DeferredShadows.TexVis);
        BindUnit(TextureSlots.ShadowKey, DeferredShadows.TexKey);
        BindUnit(TextureSlots.Gbuf0, GBuffer.Tex0);
        BindUnit(TextureSlots.Gbuf1, GBuffer.Tex1);
        BindUnit(TextureSlots.Gbuf2, GBuffer.Tex2);
        BindUnit(TextureSlots.Gbuf3, GBuffer.Tex3);
        // The frame's own depth attachment, read while it is attached: a compute dispatch touches no
        // framebuffer, so there is no feedback loop to have. The lit stage samples the same texture at the same
        // time for the same reason (SsaoPass.BindForDraw's uSceneDepth).
        BindUnit(TextureSlots.GbufDepth, PostChain.DepthTex);
        Gl.BindImageTexture(0, PostChain.HdrTex, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, GBuffer.TexLight, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);

        Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
        GBuffer.LightLive = true;
        // The store is read back three ways before the frame ends: as an ATTACHMENT (grass's root blend, the
        // blended class), as a texture FETCH (Ssr's colour copy, the resolve) and as an image again.
        Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        // The film's highlights, added on top: a linear term of the same pixel (g.specT x film), so a second
        // read-modify-write of the HDR target is exact. Not on an ablation rung, whose ladder prices the walk.
        if (!_ablating && Weather.ReflK > 0)
        {
            Gl.UseProgram(_progFilm);
            SetUniforms(in _lFilm, viewport, near, far, &invVp, view, proj, &eye);
            Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
            Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        }

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);
        BindUnit(TextureSlots.Gbuf0, 0); BindUnit(TextureSlots.Gbuf1, 0);
        BindUnit(TextureSlots.Gbuf2, 0); BindUnit(TextureSlots.Gbuf3, 0);
        BindUnit(TextureSlots.GbufDepth, 0);
        BindUnit(TextureSlots.ShadowVis, 0); BindUnit(TextureSlots.ShadowKey, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current: the colour loop rebinds diffuse there
        Gl.UseProgram(0);
        _runs++;
        Harvest();
    }

    /// <summary>Copy the tile-list overflow counters out, sampled: they accumulate GPU-side and are never reset,
    /// so a harvest every <see cref="HarvestEvery"/> frames loses nothing -- the delta since the last one is what
    /// the period line adds up. Through <see cref="GpuReadback"/>, never <c>glGetBufferSubData</c>, which on
    /// NVIDIA waits out the whole queued frame.</summary>
    private static void Harvest()
    {
        if (--_statsFrame > 0) return;
        _statsFrame = HarvestEvery;
        if (!GpuReadback.EntryPoints || !_statsRead.Ensure(StatsWords * sizeof(uint))) return;
        // BUFFER_UPDATE: the copy reads a SHADER-written buffer, which SHADER_STORAGE alone does not order
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        int w = _statsSeq % GpuReadback.Slots, r = (_statsSeq + 1) % GpuReadback.Slots;
        _statsSeq++;
        _statsRead.Copy(_bufStats, 0, w, 0, StatsWords * sizeof(uint));
        _statsRead.Fence(w);
        uint* v = (uint*)_statsRead.Ready(r);
        if (v == null) return;
        _overTiles += v[0] - _lastTiles; _overLights += v[1] - _lastLights;
        _shMiss += v[2] - _lastMiss; _shDrop += v[3] - _lastDrop;
        _walked += v[4] - _lastWalk; _taps += v[5] - _lastTaps; _pixels += v[6] - _lastPix;
        _entries += v[7] - _lastEnt;
        _lastTiles = v[0]; _lastLights = v[1]; _lastMiss = v[2]; _lastDrop = v[3];
        _lastWalk = v[4]; _lastTaps = v[5]; _lastPix = v[6]; _lastEnt = v[7];
    }

    private static void BindUnit(uint unit, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
    }

    /// <summary>Every uniform the walk and the film pass both take, through <paramref name="l"/>, the bound
    /// program's own locations.</summary>
    private static void SetUniforms(in Locs l, int* viewport, float near, float far, Matrix4x4* invVp,
                                    Matrix4x4* view, Matrix4x4* proj, Vector3* eye)
    {
        AlloyRenderer.BindPass(l.Cubes, viewport, near, far);
        AlloyRenderer.UploadMaterialModel(l.Material);
        Gl.Uniform2f(l.LightOcc, Lighting.HiddenLights.V ? 1f : 0f, AlloyRenderer.GlossyRough);
        Gl.Uniform1i(l.LmMovers, LightmapBake.MoverMode);
        Gl.Uniform4iv(l.Rect, 1, viewport);
        Gl.UniformMatrix4fv(l.InvVp, 1, 0, (float*)invVp);
        Gl.UniformMatrix4fv(l.Proj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(l.View, 1, 0, (float*)view);
        Gl.Uniform3fv(l.Eye, 1, (float*)eye);
        // light.deferredtiles: the per-tile cull, or each pixel's own froxel list (the parity A/B). The tile
        // walk culls uLights itself, so it needs the record count and R4's pick budget -- the same two the
        // cluster build is handed.
        Gl.Uniform1i(l.Tiles, Lighting.DeferredTiles.V ? 1 : 0);
        Gl.Uniform1i(l.LightCount, AlloyLights.UploadedLights);
        Gl.Uniform1i(l.ReflK, Weather.ReflK);
        // CX-188 step 3: the visibility record the shadow dispatch just wrote, and the divisor the walk
        // upsamples it by. 0 = no record this frame (said loudly there), and every light takes the walk's own
        // hardware tap.
        Gl.Uniform2i(l.ShSize, DeferredShadows.Width, DeferredShadows.Height);
        Gl.Uniform1i(l.ShRes, DeferredShadows.Res);
    }

    /// <summary>Build the program on the first frame that wants it, once. A link failure is LOUD and latched: the
    /// alternative is opaque surfaces with no point light on them and nothing in the log.</summary>
    private static bool Ready()
    {
        if (_prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing", "deferred point lights are off for the session");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "DeferredLights");
        if (prog == 0)
        {
            _log?.Always("DeferredLights: the deferred point-light program did not build (the driver's own log is "
                       + "above) -- light.deferred is off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the compute program did not link", "deferred point lights are off for the session");
        }
        uint film = GlProgram.Compute(AblateProgram.WithDefine(ShaderStages.Source(ShdCs), "CX_FILM_PASS 1"), _log, "DeferredLights film");
        if (film == 0)
        {
            _log?.Always("DeferredLights: the film pass did not build (the driver's own log is above) -- light.deferred is "
                       + "off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the film pass did not link", "deferred point lights are off for the session");
        }
        _progFilm = film;
        _lFilm = Take(film);
        uint b;
        Gl.CreateBuffers(1, &b); _bufStats = b;
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0, 0, 0, 0, 0 };
        Gl.NamedBufferData(_bufStats, (IntPtr)(StatsWords * sizeof(uint)), zero, Gl.DYNAMIC_DRAW);
        _lastTiles = _lastLights = _lastMiss = _lastDrop = 0;
        _lastWalk = _lastTaps = _lastPix = _lastEnt = 0; _statsFrame = 1;
        _progFull = prog;
        Select(prog);
        _log?.Info("DeferredLights: ready -- one 16x16 compute dispatch over the G-buffer");
        return true;
    }

    /// <summary>Bind <paramref name="prog"/> as the pass's program and take its uniform locations. Called at
    /// link and again whenever the bench swaps an ablation variant in or out: a variant is its own program
    /// object, so nothing about the shipped one's locations carries over.</summary>
    private static void Select(uint prog)
    {
        _prog = prog;
        _l = Take(prog);
    }

    /// <summary>A program's locations, and its two cube arrays' units -- which carry no layout(binding), the lib
    /// being shared with programs that bind them on other units, so they take theirs here at link.</summary>
    private static Locs Take(uint prog)
    {
        Locs l = default;
        l.Rect = GlProgram.Loc(prog, "uRect");
        l.InvVp = GlProgram.Loc(prog, "uInvVp");
        l.Proj = GlProgram.Loc(prog, "uProj");
        l.View = GlProgram.Loc(prog, "uView");
        l.Eye = GlProgram.Loc(prog, "uEyePos");
        l.Cubes = GlProgram.Loc(prog, "uLocalCubes");
        l.Material = GlProgram.Loc(prog, "uMaterial");
        l.LightOcc = GlProgram.Loc(prog, "uLightOcc");
        l.LmMovers = GlProgram.Loc(prog, "uLmMovers");
        l.Tiles = GlProgram.Loc(prog, "uTiles");
        l.LightCount = GlProgram.Loc(prog, "uLightCount");
        l.ReflK = GlProgram.Loc(prog, "uReflK");
        l.ShSize = GlProgram.Loc(prog, "uShSize");
        l.ShRes = GlProgram.Loc(prog, "uShRes");
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
        return l;
    }

    /// <summary>CX-188: run the pass on the ablation ladder's rung <paramref name="stage"/> (1..8, or the probe at 9), or on the
    /// shipped program at 0. <c>/alloy bench</c> and nothing else calls this, and the bench restores 0 on every
    /// step and at finish. False = that rung did not link, which refuses THE STEP: the shipped program stays
    /// bound and the feature is untouched.</summary>
    internal static bool Ablate(int stage)
    {
        if (_progFull == 0) return stage == 0;
        _ablating = stage > 0;
        if (stage <= 0) { Select(_progFull); _inLoopOn = false; return true; }
        uint p = _abl.Get(ShdCs, stage, _log, "DeferredLights");
        if (p == 0) { Select(_progFull); return false; }
        Select(p);
        return true;
    }

    /// <summary>Bind the in-loop-tap variant while <paramref name="want"/> (an "every light" escape is on) and the
    /// shipped program otherwise -- on a CHANGE only, since <see cref="Select"/> re-reads every location.</summary>
    private static void SelectEscape(bool want)
    {
        if (want == _inLoopOn) return;
        if (want && _progInLoop == 0 && !_inLoopFailed)
        {
            _progInLoop = GlProgram.Compute(AblateProgram.WithDefine(ShaderStages.Source(ShdCs), "CX_SH_INLOOP 1"),
                                            _log, "DeferredLights in-loop taps");
            if (_progInLoop == 0)
            {
                _inLoopFailed = true;
                _log?.Always("DeferredLights: the in-loop-tap variant did not link (the driver's own log is above) -- "
                           + "shadow.cap 0 / shadow.perpixel 0 keep the record's four-light budget instead of tapping every light");
            }
        }
        if (want && _progInLoop == 0) return;
        Select(want ? _progInLoop : _progFull);
        _inLoopOn = want;
    }

    /// <summary>Context loss: the program and any per-size objects are gone.</summary>
    internal static void Forget()
    {
        _prog = 0; _progFull = 0; _bufStats = 0; _progFilm = 0;
        _progInLoop = 0; _inLoopFailed = false; _inLoopOn = false; _ablating = false;
        _abl.Forget();
        _statsRead.ContextLost();
        _lastTiles = _lastLights = _lastMiss = _lastDrop = 0;
        _lastWalk = _lastTaps = _lastPix = _lastEnt = 0;
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        string s = $"deferred lights {(!Lighting.Deferred.V ? "off" : Lighting.DeferredTiles.V ? "on, tiled" : "on, froxel lists")}"
                 + $" ({_runs} dispatches"
                 // the list cap is a budget WITH a counter: a tile that dropped a light is a visibly unlit lamp,
                 // and a silent one is the VAO drip's bug again
                 + (_overTiles > 0 ? $", TILE LIGHT LIST OVERFLOW {_overTiles} tiles / {_overLights} lights dropped (cap {AlloyLights.ClusterCap})" : "")
                 // CX-188 budget: what the per-pixel shadow budget spent, and the pixels that fell out of it.
                 // The first is the saving; the second takes the walk's own hardware tap per light, and a number
                 // that climbs there is the record not reaching the pixels it is for.
                 + (_shDrop > 0 ? $", {_shDrop} lights unshadowed past the budget of {Shadows.PerPixel.V}" : "")
                 + (_shMiss > 0 ? $", {_shMiss} pixels with no usable shadow record ({(_inLoopOn ? "hard taps in the walk" : "hard taps of the nearest texel's lights")})" : "")
                 // CX-188 budget: what a pixel actually spent, sampled over the harvested frames. "lights lit"
                 // and not "walked": a list entry the reach window, the relevance cut or the facing test
                 // dropped is not in it, and the list's own length is the cluster build's tally. Both counters
                 // come up in the shader's CX_WALK_UNIT sixteens, so each resolves to about 1/16 per pixel --
                 // an average, not an audit, and near-zero taps is the answer the budget is for.
                 + (_pixels > 0 ? $", {16.0 * _walked / _pixels:F1} lights lit/px, {16.0 * _taps / _pixels:F2} hard taps/px" : "")
                 // CX-188 ablation: and the LIST LENGTH the walk read to find them. "entries" is every list
                 // slot the loop touched; the rejected half is what the reach window, the relevance cut and
                 // the facing test threw away after paying to index it -- the cost the ablation ladder prices.
                 + (_pixels > 0 ? $", {16.0 * _entries / _pixels:F1} entries/px ({16.0 * (_entries - Math.Min(_entries, _walked)) / _pixels:F1} rejected)" : "")
                 + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "") + ")";
        _runs = 0; _overTiles = 0; _overLights = 0; _shMiss = 0; _shDrop = 0;
        _walked = 0; _taps = 0; _pixels = 0; _entries = 0;
        return s;
    }
}
