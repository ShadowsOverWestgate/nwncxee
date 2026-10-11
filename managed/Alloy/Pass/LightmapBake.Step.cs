using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The bake itself, page-major and light-major: a page opens, each static light renders the static set
/// into the one scratch cube and draws every rect it reaches, and the finish stage folds the scratch pair into the
/// resident one.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Bytes this thread allocated inside <see cref="Run"/> this period, for the period line: the
    /// frame line's GC total said gigabytes a period and nothing said whose.</summary>
    private static long _allocRun;
    /// <summary>Once a frame, at the frame slot after the probe captures: adopt cache hits, pack newcomers,
    /// advance the bake by its pacing, collect readbacks and upload the records. Inside the colour bracket,
    /// with the shadow bracket's gather staged; the caller restores its target after.</summary>
    internal static void Run(bool capturing, int frame)
    {
        // a parked migration holds two page arrays; the master going off (or a stand-down) must not keep them.
        // A capture frame only pauses it: the probe's frame is one frame, and the window is already paused work.
        if (!Enabled) { if (Migrating) GrowAbort(gl: true); AbortReadback(gl: true); return; }
        if (capturing) return;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        try { RunInner(frame); }
        finally { _allocRun += GC.GetAllocatedBytesForCurrentThread() - a0; }
    }

    private static void RunInner(int frame)
    {
        if (!Ensure()) return;
        _frame = frame;
        _cap = PageCap();
        long t = Stopwatch.GetTimestamp();
        InstallAreaCache();   // CX-56: before the sweep -- the plan's set is what arms Enrol at all
        t = Phase(Ph.Area, t);
        if (_rekeyPending) Rekey();
        t = Phase(Ph.Rekey, t);
        Sweep();
        t = Phase(Ph.Sweep, t);
        RefineSweep();
        t = Phase(Ph.Refine, t);
        Adopt();
        t = Phase(Ph.Adopt, t);
        AreaUpload();
        t = Phase(Ph.Upload, t);
        // CX-102: a page array growing is a 192-MiB-a-layer migration. It runs in slices BEFORE the pack that
        // asked for it, so the frame that needs a new page is not the frame that pays for every old one.
        GrowStep();
        t = Phase(Ph.Grow, t);
        Pack();
        t = Phase(Ph.Pack, t);
        CollectReadback();
        IssueStashed();
        t = Phase(Ph.Collect, t);
        AreaSnap();
        t = Phase(Ph.Snap, t);
        // CX-102: FinishPage converts the scratch into a RESIDENT layer, and a layer the migration has
        // already copied would take those texels into the retiring array. The page in flight keeps its scratch
        // and its light cursor across the window exactly as it does across the pacing.
        int steps = HeldByMigration() ? 0 : Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame;
        for (int k = 0; k < steps; k++) if (!Step()) break;
        t = Phase(Ph.Steps, t);
        UploadRecords();
        Phase(Ph.Records, t);
        PhaseFrameEnd();
    }

    /// <summary>Every owned slot the bake has not seen is offered from the frame, in view or not: the bake is
    /// over the area, not over what the camera happened to show, so a part seen for the first time is already
    /// baked. Round-robin: a chart arrives from the worker whenever it does.</summary>
    private const int SweepPerFrame = 1024;
    private static int _sweepAt;
    private static void Sweep()
    {
        if (SetHash == 0 || AreaAwaited) return;
        int per = Hold ? HoldSweepPerFrame : SweepPerFrame;
        // CX-102 measured this walk and left it alone: an enrolled slot costs one int read (the _slotRect test
        // short-circuits ahead of SlotPlacement) and an empty one two (_slotRect then _oTri), so a steady-state
        // pass over 1024 of them is ~3 us -- a matrix is only copied for a slot that actually enrols. A
        // dirty gate over it could not close either -- a mesh MeshChart permanently refuses leaves its slot
        // at -1 for ever (168 "no rect" charted draws a frame, live), which is indistinguishable from a chart
        // that has not landed yet, so every pass would count as having found something.
        for (int k = 0; k < per; k++)
        {
            int i = _sweepAt; if (++_sweepAt >= _slotRect.Length) _sweepAt = 0;
            if (_slotRect[i] != -1 || !AlloyRenderer.SlotPlacement(i, out int vao, out int item, out Matrix4x4 m)) continue;
            if (m.M44 == 0f) continue;   // never staged yet: the record's matrix is unwritten, the staging loop enrols it
            Enrol(i, vao, item);
        }
        StampSweep(per);
    }

    /// <summary>The static set moved under a page in flight: its light indices name other lights now. The
    /// page's rects stay queued and start over against the new set (or re-key away from it).</summary>
    private static void AbortBake()
    {
        _bakePage = -1; _bakeRects.Clear(); _bakeLights.Clear(); _bakePicks.Clear();
    }

    /// <summary>One (page, light) step of the bake, or the page's finish when its lights are done. False =
    /// nothing to do this frame (no queued rect, or the scratch cube could not be rendered).</summary>
    private static bool Step()
    {
        if (_bakePage < 0 && !BeginPage()) return false;
        if (_bakeLight < _bakeLights.Count)
        {
            int l = _bakeLights[_bakeLight];
            Vector4 pr = _setPosR[l];
            var pos = new Vector3(pr.X, pr.Y, pr.Z);
            // the cube starts at the self-shadow skip radius: a brazier's bowl sits right under its light
            // node and blocked every steep ray, a dark disc round each one that only grazing rays escaped
            if (!CasterPass.BakeStaticCube(_gl.Cube, _cubeSize, pos, BakeNear, pr.W)) { _bakeSkips++; return false; }
            BakeLight(l, pos, pr.W);
            _bakeLight++; _bakeSteps++;
            return true;
        }
        FinishPage();
        return true;
    }

    /// <summary>Open the lowest page holding queued rects: its rect list, the static lights reaching any of
    /// them, the scratch cleared and every rect's coverage laid down.</summary>
    private static bool BeginPage()
    {
        // a queued rect is a pending one: with none pending, the scan below is 20-30k rects for nothing, every frame
        if (_pending == 0 || _frame - _queuedAt < (Hold ? HoldSettleFrames : SettleFrames)) return false;
        int page = int.MaxValue;
        for (int ri = 0; ri < _rectN; ri++)
            if (_rects[ri].State == RState.Queued && _rects[ri].Page < page) page = _rects[ri].Page;
        if (page == int.MaxValue) return false;
        _bakeRects.Clear(); _bakeLights.Clear(); _bakePicks.Clear();
        for (int ri = 0; ri < _rectN; ri++)
        {
            if (_rects[ri].State != RState.Queued || _rects[ri].Page != page) continue;
            // no live owner, or the chart range moved out from under it (a VDO slot re-pointed): the rect
            // cannot draw, so it is dropped and its readers re-enrol into a fresh one rather than land black
            ref Rect r = ref _rects[ri];
            if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out _, out _, out _, out _)) { Drop(ri); continue; }
            _bakeRects.Add(ri); _bakePicks.Add(PickOf(r.C, r.R));
        }
        if (_bakeRects.Count == 0) return false;
        for (int l = 0; l < _setN; l++)
            for (int k = 0; k < _bakeRects.Count; k++)
            {
                ref Rect r = ref _rects[_bakeRects[k]];
                if (Touches(l, r.C, r.R)) { _bakeLights.Add(l); break; }
            }
        _bakePage = page; _bakeLight = 0;
        _pageT0 = Stopwatch.GetTimestamp(); _pageSkew = 0;
        // the scratch planes: A' = sum rgb + coverage alpha, B' = sum luminance * direction, C' = per channel the mover's luminance
        BindScratch();
        float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
        Gl.ClearBufferfv(Gl.COLOR, 0, zero); Gl.ClearBufferfv(Gl.COLOR, 1, zero); Gl.ClearBufferfv(Gl.COLOR, 2, zero);
        Gl.DepthMask(1); Gl.ClearBufferfv(Gl.DEPTH, 0, zero); Gl.DepthMask(0);
        Gl.UseProgram(_gl.ProgBake);
        AlloyLights.BindForDraw();
        Gl.Uniform1i(_bMode, 0);
        BindNullCubes();
        Gl.Uniform1f(_bDepth, -1f);
        Gl.BindVertexArray(MeshChart.Vao);
        for (int k = 0; k < _bakeRects.Count; k++) DrawRect(_bakeRects[k]);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (_logLeft > 0) LogNearest(page);
        int crowded = 0;
        if (_logLeft > 0)
            for (int k = 0; k < _bakeRects.Count; k++)
            {
                ref Rect r = ref _rects[_bakeRects[k]];
                int n = 0;
                for (int l = 0; l < _setN && n <= Channels; l++) if (Touches(l, r.C, r.R)) n++;
                if (n > Channels) crowded++;
            }
        if (_logLeft > 0) _log?.Info($"LightmapBake: page {page} bake begins -- {_bakeRects.Count} rects, {_bakeLights.Count} static lights, "
                                     + $"{crowded} rects touched by more than {Channels} (their weaker lights' creature shadows are the live estimate)"
                                     + (_knownLogged ? "" : "; known differences on baked surfaces: no static specular beyond the one directional lobe, non-Static placeables cast no shadow from static lights, a fixed-disc penumbra where the live loop runs PCSS"));
        _knownLogged = true;
        return true;
    }

    /// <summary>Diag for a surface baked wrong: the page's rect nearest the camera, its sphere, and the
    /// static lights that reach it (with the nearest one's distance against its reach when none does).</summary>
    private static void LogNearest(int page)
    {
        Vector3 cam = FrameInputs.CameraPos;
        int best = -1; float bd = float.MaxValue;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            float d = (_rects[_bakeRects[k]].C - cam).LengthSquared();
            if (d < bd) { bd = d; best = _bakeRects[k]; }
        }
        if (best < 0) return;
        ref Rect r = ref _rects[best];
        int n = 0, nl = -1; float nd = float.MaxValue;
        for (int l = 0; l < _setN; l++)
        {
            if (Touches(l, r.C, r.R)) n++;
            float d = (new Vector3(_setPosR[l].X, _setPosR[l].Y, _setPosR[l].Z) - r.C).Length();
            if (d < nd) { nd = d; nl = l; }
        }
        _log?.Info($"LightmapBake: [diag] page {page} nearest rect {r.W}x{r.H} at ({r.C.X:F1},{r.C.Y:F1},{r.C.Z:F1}) r={r.R:F1} slot {r.Slot}: "
                   + $"{n} static lights reach it" + (nl >= 0 ? $", nearest light {nd:F1}u away with reach {_setPosR[nl].W:F1}" : "")
                   + $", {(cam - r.C).Length():F1}u from the camera");
    }

    /// <summary>Compare-on stand-ins on both cube units: the bake's shadow samplers are validated on every
    /// draw, sampled or not, and a unit a lit pass left empty is undefined behaviour (NVIDIA 131222).</summary>
    private static void BindNullCubes()
    {
        uint n = AlloyRenderer.CubeShadowOr(0);
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, n);
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeDynUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, n);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static void BindScratch()
    {
        AttachPair(-1);
        new PassState(depth: false, depthWrite: false, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
    }

    /// <summary>The FBO over the three planes as its colour outputs: the scratch set (-1) or one resident layer.</summary>
    private static void AttachPair(int layer)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        if (layer < 0)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.ScrA, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _gl.ScrB, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, _gl.ScrC, 0);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _gl.ScrZ, 0);
        }
        else
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _gl.TexA, 0, layer);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, _gl.TexB, 0, layer);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, _gl.TexC, 0, layer);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        }
        uint* bufs = stackalloc uint[3] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1, Gl.COLOR_ATTACHMENT2 };
        Gl.DrawBuffers(3, bufs);
        Gl.Viewport(0, 0, PageSize, PageSize);
    }

    private static void DrawRect(int ri)
    {
        ref Rect r = ref _rects[ri];
        if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out int baseVertex, out nint off, out int count, out _)) return;
        Gl.Uniform1i(_bSlot, r.Slot);
        Gl.Uniform4f(_bRect, r.X, r.Y, r.S, 0f);
        Gl.Uniform1f(_bSoftWorld, 1f / (MeshChart.AtlasTexelsPerUnit * r.S));
        Gl.DrawElementsBaseVertex(Gl.TRIANGLES, count, CasterPass.GlUnsignedShort, (void*)off, baseVertex);
    }

    /// <summary>One light over the page: the bake program takes the light and its scratch cube exactly the
    /// way the lit stage takes a cube slot (slot 0 of a one-cube array), and every rect it reaches adds.</summary>
    /// <summary>The scratch cube's near plane: the live receiver's self-shadow skip radius (<c>shadow.near</c>
    /// x <c>light.radius</c>), so the geometry a light node sits in casts nothing in the bake.</summary>
    private static float BakeNear => AlloyLights.ShadowNearWorld;

    private static void BakeLight(int l, Vector3 pos, float far)
    {
        bool q = _gl.QBake.Begin(Gl.TIME_ELAPSED);
        BindScratch();
        new PassState(depth: true, depthWrite: true, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
        Gl.DepthFunc(Gl.GREATER);
        Gl.UseProgram(_gl.ProgBake);
        AlloyLights.BindForDraw();
        Gl.Uniform1i(_bMode, 1);
        Gl.Uniform1f(_bDepth, (float)(_bakeLight + 1) / BakeDepthSteps * 2f - 1f);
        Vector3 c = _setCol[l];
        Gl.Uniform4f(_bLight, pos.X, pos.Y, pos.Z, far);
        Gl.Uniform3f(_bColor, c.X, c.Y, c.Z);
        BindNullCubes();
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, _gl.Cube);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_bCubeCount, 1);
        // CX-43: the scratch cube as slot 0 of the pass block; the colour pass's next bind puts its own back.
        byte* b = AlloyRenderer.PassBegin();
        int* vp = stackalloc int[4] { 0, 0, PageSize, PageSize };
        AlloyLights.FillDraw(b, vp, 1f, 100f);
        AlloyRenderer.PassVec(b, AlloyRenderer.PbLight, pos.X, pos.Y, pos.Z, far);
        *(Vector2*)(b + AlloyRenderer.PbLocalPlane) = new Vector2(MathF.Max(BakeNear, CasterPass.CubeNearPlane), 1f / _cubeSize);
        // the disc everywhere (no screen to stand it down against), the near skip, no cap; PCSS off:
        // the bake has no raw view of its cube -- a fixed disc at the softness knob. ponytail: bind the
        // scratch cube through the raw sampler too if the bake's penumbra ever needs to match the live one.
        AlloyRenderer.PassVec(b, AlloyRenderer.PbLocalSoft, AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, 0f, 0f);
        *(float*)(b + AlloyRenderer.PbLocalFloor) = AlloyLights.ShadowFloor;
        AlloyRenderer.PassVec(b, AlloyRenderer.PbLocalPcss, 0f, 0f, Shadows.Taps.V, 0f);
        *(float*)(b + AlloyRenderer.PbFade) = 1f;
        AlloyRenderer.FlushPassBlock();
        Gl.BindVertexArray(MeshChart.Vao);
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            if (!Touches(l, r.C, r.R)) continue;
            Gl.Uniform1i(_bChan, _bakePicks[k].ChannelOf(l));
            DrawRect(_bakeRects[k]);
        }
        Gl.BindVertexArray(0);
        Gl.DepthFunc(Gl.LEQUAL);   // every later pass assumes LEQUAL (#364)
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // the frame's own cube array back on the unit the lit programs expect it on
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (q) _gl.QBake.End(Gl.TIME_ELAPSED);
    }

    /// <summary>The page's final pass: normalise B, dilate two texels through the coverage alpha, convert
    /// into the resident layer -- only where the scratch says a bake happened, so cache-hit rects already on
    /// the page (scratch alpha 0) are left alone. Then the readback of what landed, and the records.</summary>
    private static void FinishPage()
    {
        int page = _bakePage;
        AttachPair(page);
        // the finish and the readback cover the union of what landed (+ the dilation margin), never the page
        int x0 = PageSize, y0 = PageSize, x1 = 0, y1 = 0;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            x0 = Math.Min(x0, r.X - Gutter); y0 = Math.Min(y0, r.Y - Gutter);
            x1 = Math.Max(x1, r.X + r.W + Gutter); y1 = Math.Max(y1, r.Y + r.H + Gutter);
        }
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, PageSize); y1 = Math.Min(y1, PageSize);
        new PassState(depth: false, depthWrite: false, cull: false, scissor: true, srgb: false).Apply();
        Gl.Scissor(x0, y0, x1 - x0, y1 - y0);
        Gl.UseProgram(_gl.ProgFinish);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrA);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrB);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrC);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_fA, 0); Gl.Uniform1i(_fB, 1); Gl.Uniform1i(_fC, 2);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        // the readback: the union's raw bits into one PBO, fenced; CollectReadback writes the files. A previous
        // page's fence still open is polled, never waited on: the render thread does not stall for a cache
        // file, and a page landing that close behind another goes uncached (a miss next session, nothing else).
        if (!ReadbackIdle) { CollectReadback(); IssueStashed(); }
        if (Gl.FenceSync == null || Gl.MapNamedBufferRange == null) _uncached += _bakeRects.Count;
        // AttachPair again: IssueStashed above attached ITS page and unbound, and a stash it could not queue
        // leaves ReadbackIdle true -- reading with no FBO bound would file the backbuffer under these rect keys
        else if (ReadbackIdle) { AttachPair(page); QueueReadback(page, x0, y0, x1 - x0, y1 - y0, _bakeRects); }
        // CX-102: a copy still draining must not turn this landing into a cache miss -- its region waits and is
        // queued the moment the PBO is free. A landed rect keeps its page and place until a Repack.
        else StashReadback(page, x0, y0, x1 - x0, y1 - y0, _bakeRects);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        for (int k = 0; k < _bakeRects.Count; k++) { Land(_bakeRects[k], false); _pending--; }
        double ms = (Stopwatch.GetTimestamp() - _pageT0 - _pageSkew) * 1000.0 / Stopwatch.Frequency;
        _pageSkew = 0;
        _pageMs += ms; _pageMsN++;
        if (_logLeft-- > 0) _log?.Info($"LightmapBake: page {page} landed -- {_bakeRects.Count} rects over {_bakeLights.Count} lights in {ms:F0} ms wall" + (_gl.QBake.Measured ? $", {_gl.QBake.Avg / 1e6:F2} ms GPU per light step" : ""));
        AbortBake();
    }
}
