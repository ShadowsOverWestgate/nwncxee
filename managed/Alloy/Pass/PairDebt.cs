using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-188 step 4 (ADR 0029): the HOT-LIGHT DEBT pass -- one compute dispatch over the deferred frame
/// that takes a creature's shadow back out of the baked term, once per pixel. The deferred stage compiles the
/// walk's hot branch out (<c>CX_NO_HOT</c>), so this is where <c>lmSub</c> comes from: each 16x16 tile culls
/// this frame's <see cref="PairShadows"/> pairs against its own view-space box, and the pixels of a tile that
/// holds one evaluate the pair tile and subtract <c>lmShare * min(lmSub.x / lmLum + lmSub.y, 1)</c> -- the
/// spec's own line, on the G-buffer's own lanes.
/// <para><b>Why a second dispatch and not a branch in <see cref="DeferredLights"/>.</b> A tile with no pair
/// over it exits at the first barrier having read NO G-buffer, and most tiles of a frame are that tile. Inside
/// the deferred pass the same work would be charged to every pixel's register budget and to the one dispatch's
/// own timing; here it is a term of its own on the period line, and <c>lightmap.moverlights 0</c> is a clean
/// A/B of the whole feature. Adding and subtracting into the HDR image commutes, so only the memory barrier
/// between the two passes matters, not their order.</para></summary>
internal static unsafe class PairDebt
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_pairdebt";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>.</summary>
    private const int Group = 16;

    /// <summary>The pass's own stats buffer: [0] tiles whose pair list overflowed, [1] the pairs they dropped,
    /// [2] tiles that held a pair, [3] the ablation ladder's sink, which the shipped program never writes and
    /// nothing reads. A point nothing else in the colour bracket takes
    /// (<see cref="TriggerHighlight"/>'s is a later program's).</summary>
    private const uint BindStats = 30;
    private const int StatsWords = 4;
    /// <summary>Frames between stats harvests. The counters accumulate GPU-side and are never reset, so a
    /// sampled harvest loses nothing -- the delta since the last one is what the period line adds up.</summary>
    private const int HarvestEvery = 64;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("PairDebt");
    /// <summary><c>_prog</c> is the program the pass BINDS and <c>_progFull</c> the shipped one it goes back
    /// to -- they differ only while <c>/alloy bench</c> walks the ablation ladder (<see cref="Ablate"/>).</summary>
    private static uint _prog, _progFull, _bufStats;
    private static AblateProgram _abl;
    private static int _locRect, _locInvVp, _locProj, _locView, _locCubes, _locLmMovers, _locLightOcc;
    private static int _runs;
    private static uint _overTiles, _overPairs, _lastTiles, _lastPairs, _lastCov;
    private static uint _covTiles;
    private static GpuReadback _statsRead;
    private static int _statsSeq, _statsFrame;

    /// <summary>Is there a debt to take this frame? The pairs exist (which already requires
    /// <see cref="DeferredLights.Active"/>, so a forward frame never reaches here), the atlas is allocated and
    /// this program linked.</summary>
    /// <summary>The debt pass failed for the session: <see cref="PairShadows.On"/> reads it so the pairing stands
    /// down with it and the hot lights keep their dyn pages.</summary>
    internal static bool StoodDown => _down.Failed;

    internal static bool Active => PairShadows.On && PairShadows.Count > 0 && PairShadows.Tex != 0
                                   && GBuffer.Ready && !GBuffer.Failed && Ready();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass, immediately after <see cref="DeferredLights"/>. <paramref name="vp"/> is the JITTERED
    /// view-projection the prepass rasterised with (world positions come off its inverse).</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far)
    {
        if (!Active) return;
        if (!PostChain.Armed || PostChain.HdrTex == 0 || PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned HDR target is not armed", "no hot-light debt this frame"); return; }
        if (viewport[2] <= 0 || viewport[3] <= 0) return;
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;

        Gl.UseProgram(_prog);
        // the light SSBO, the cluster table, the mover spheres -- and PairShadows' own table and atlas
        AlloyLights.BindForDraw();
        // ...and this pass's CxPass block, the cascade and cube arrays, and uLocalCubes, from the same frame
        // state and the same viewport the colour pass bound: CxLocalBakedVis reads the static cube through it.
        AlloyRenderer.BindPass(_locCubes, viewport, near, far);
        Gl.Uniform1i(_locLmMovers, LightmapBake.MoverMode);
        // unreached here (no glossy mirror walk in this stage), bound so the program is never left reading an
        // unset uniform that a later edit might make reachable
        Gl.Uniform2f(_locLightOcc, Lighting.HiddenLights.V ? 1f : 0f, AlloyRenderer.GlossyRough);
        Gl.Uniform4iv(_locRect, 1, viewport);
        Gl.UniformMatrix4fv(_locInvVp, 1, 0, (float*)&invVp);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _bufStats);

        BindUnit(TextureSlots.Gbuf0, GBuffer.Tex0);
        BindUnit(TextureSlots.Gbuf1, GBuffer.Tex1);
        BindUnit(TextureSlots.Gbuf2, GBuffer.Tex2);
        BindUnit(TextureSlots.Gbuf3, GBuffer.Tex3);
        BindUnit(TextureSlots.GbufDepth, PostChain.DepthTex);
        Gl.BindImageTexture(0, PostChain.HdrTex, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, GBuffer.TexLight, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA32UI);

        Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
        // the store is read back as an attachment (grass's root blend), as a texture fetch (Ssr's copy) and as
        // an image again -- the same three ways DeferredLights' own store is
        Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA32UI);
        BindUnit(TextureSlots.Gbuf0, 0); BindUnit(TextureSlots.Gbuf1, 0);
        BindUnit(TextureSlots.Gbuf2, 0); BindUnit(TextureSlots.Gbuf3, 0);
        BindUnit(TextureSlots.GbufDepth, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current
        Gl.UseProgram(0);
        _runs++;
        Harvest();
    }

    /// <summary>Copy the per-tile counters out, sampled, through <see cref="GpuReadback"/> -- never
    /// <c>glGetBufferSubData</c>, which on NVIDIA waits out the whole queued frame.</summary>
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
        _overTiles += v[0] - _lastTiles; _overPairs += v[1] - _lastPairs; _covTiles += v[2] - _lastCov;
        _lastTiles = v[0]; _lastPairs = v[1]; _lastCov = v[2];
    }

    private static void BindUnit(uint unit, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
    }

    /// <summary>Build the program on the first frame that wants it, once. A link failure is LOUD and latched:
    /// a deferred frame whose hot branch is compiled out and whose debt pass is missing shows every creature's
    /// shadow gone from the baked ground, and nothing in the log.</summary>
    private static bool Ready()
    {
        if (_prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing",
                              "the hot-light debt is off for the session and baked surfaces take no creature shadow on the deferred path");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "PairDebt");
        if (prog == 0)
        {
            _log?.Always("PairDebt: the hot-light debt program did not build (the driver's own log is above) -- "
                       + "baked surfaces take no creature shadow from static lights on the deferred path; "
                       + "light.deferred 0 is the forward path, which still does");
            return _down.Fail(_log, "the compute program did not link",
                              "the hot-light debt is off for the session");
        }
        uint b;
        Gl.CreateBuffers(1, &b); _bufStats = b;
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0 };
        Gl.NamedBufferData(_bufStats, (IntPtr)(StatsWords * sizeof(uint)), zero, Gl.DYNAMIC_DRAW);
        _lastTiles = _lastPairs = _lastCov = 0; _statsFrame = 1;
        _progFull = prog;
        Select(prog);
        _log?.Info("PairDebt: ready -- one 16x16 compute dispatch takes the creature shadows back out of the baked term");
        return true;
    }

    /// <summary>Bind <paramref name="prog"/> and take its uniform locations. At link, and again whenever the
    /// bench swaps an ablation variant in or out: a variant is its own program object.</summary>
    private static void Select(uint prog)
    {
        _prog = prog;
        _locRect = GlProgram.Loc(prog, "uRect");
        _locInvVp = GlProgram.Loc(prog, "uInvVp");
        _locProj = GlProgram.Loc(prog, "uProj");
        _locView = GlProgram.Loc(prog, "uView");
        _locCubes = GlProgram.Loc(prog, "uLocalCubes");
        _locLmMovers = GlProgram.Loc(prog, "uLmMovers");
        _locLightOcc = GlProgram.Loc(prog, "uLightOcc");
        // the two cube arrays carry no layout(binding) -- the lib is shared with programs that bind them on
        // other units -- so they take theirs here, as every lit program does at its own link
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
    }

    /// <summary>CX-188: run the debt on the ablation ladder's rung <paramref name="stage"/> (1 = the G-buffer
    /// read and the tile slab, 2 = + the pair cull and the sort), or on the shipped program at 0. Called by
    /// <c>/alloy bench</c> and nothing else; it restores 0 on every step and at finish. False = that rung did
    /// not link, which refuses THE STEP -- the shipped program stays bound. Rungs 1 and 2 subtract no debt, so
    /// those steps draw the creature shadows missing from the baked ground.</summary>
    internal static bool Ablate(int stage)
    {
        if (_progFull == 0) return stage == 0;
        if (stage <= 0) { Select(_progFull); return true; }
        uint p = _abl.Get(ShdCs, stage, _log, "PairDebt");
        if (p == 0) { Select(_progFull); return false; }
        Select(p);
        return true;
    }

    /// <summary>Context loss: the program and the stats buffer are gone.</summary>
    internal static void Forget()
    {
        _prog = 0; _progFull = 0; _bufStats = 0;
        _abl.Forget();
        _statsRead.ContextLost();
        _lastTiles = _lastPairs = _lastCov = 0;
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        float n = MathF.Max(frames, 1);
        string s = _down.Failed ? "pair debt STOOD DOWN: " + _down.Reason
                 : $"pair debt {_runs} dispatches, {_covTiles / n:F0} 16x16 tiles owed/frame"
                 // the cap is a budget WITH a counter: a tile that dropped a pair is a creature shadow with a
                 // missing bite out of it, and a silent one is the VAO drip's bug again
                 + (_overTiles > 0 ? $", TILE PAIR LIST OVERFLOW {_overTiles} tiles / {_overPairs} pairs dropped" : "")
                 + (_down.Reason.Length > 0 && !_down.Failed ? ", last refusal: " + _down.Reason : "");
        _runs = 0; _overTiles = 0; _overPairs = 0; _covTiles = 0;
        return s;
    }
}
