using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: BATCH ISSUE. Uploading the staged per-draw records and bone snapshots, then submitting the built groups as one multidraw per (program, VDO, texture, primitive) cut -- and the blended and water variants that ride the same records under different GL state. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#153: forget every cached handle. Called at each enrollment -- the cadence at which the
    /// tids themselves are re-harvested -- and on release. Handles stay resident until their textures die;
    /// the cache only ever answers for tids the current enrollment produced.</summary>
    /// <summary>#220: forget every cached handle. Only meaningful while the cache holds ENGINE names --
    /// those get recycled behind us and the wipe is the only heal. With <c>ownedtex</c> armed the names are
    /// Alloy's own, invalidated precisely by <c>OwnedTextures.Drain</c> -&gt; <see cref="DropHandle"/>, and
    /// wiping here would only make the next mint re-resident an already-resident handle (GL_INVALID_
    /// OPERATION -- the error burst #220 recorded on transitions).</summary>
    /// <summary>#220: the 4.6 context is gone, so every handle went with it. Cleared unconditionally, and
    /// nothing is made non-resident -- that would be a call into a dead context.</summary>
    internal static void ForgetHandles()
    {
        InvalidateRecords();   // #242: the persistent records hold the handles this forgets
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hVal, 0, _hVal.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    private static void WipeHandles()
    {
        if (OwnedTextures.Armed) return;
        InvalidateRecords();   // #242: same reason as ForgetHandles -- a record outlives the wipe otherwise
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    /// <summary>#242: push the records that MOVED into the persistent per-draw SSBO, then the small
    /// draw-to-slot index array this pass's order needs. The records are indexed by ownership slot and
    /// survive the frame, so what goes up is the mover set -- a walking creature, an animated texture, a
    /// fading tile -- rather than the whole owned table.
    /// <para>Past a quarter of the table, naming slots costs more than sending everything, so the frame is
    /// promoted to one wholesale upload. That is also what a reallocation and a dirty-list overflow take.
    /// </para>
    /// <para>No orphan any more, and that is the trade this makes: a bare <c>BufferSubData</c> over a buffer
    /// the GPU may still be reading can make the driver sync, which is the stall #84's orphan was avoiding.
    /// Orphaning is not available to a persistent buffer -- the whole point is that last frame's contents are
    /// still there. ponytail: if the Upload zone grows, the upgrade is triple-buffering through GpuRing's
    /// persistent coherent mapping, which is already bound; it is not a return to the orphan.</para></summary>
    private static void UploadDraws()
    {
        int cap = _recOwnCap + MaxSkyDraws;
        bool grew = false;
        if (_drawBuf == 0) { uint b; Gl.GenBuffers(1, &b); _drawBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _drawBuf);
        if (cap > _recCapGpu)
        {
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER,
                          (IntPtr)((long)cap * DrawFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
            grew = true;
        }
        // Against what this frame DRAWS, not against the high-water capacity: _recOwnCap latches at the
        // largest area of the session and never shrinks, so thresholding on it would leave a small interior
        // issuing thousands of scattered SubData calls into a buffer that can no longer orphan -- one
        // enrollment's worth of hitch, once a cadence, in exactly the "just left the city" case.
        bool whole = grew || _recDirtyAll || _recDirtyN > _drawN / 4;
        fixed (float* p = _dData)
        {
            if (whole)
                Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero,
                                 (IntPtr)((long)cap * DrawFloats * sizeof(float)), p);
            else
                for (int d = 0; d < _recDirtyN; d++)
                {
                    long off = (long)_recDirty[d] * DrawFloats * sizeof(float);
                    Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)off,
                                     (IntPtr)(DrawFloats * sizeof(float)), p + _recDirty[d] * DrawFloats);
                }
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, _drawBuf);
        if (_bindless)
        {
            if (_texHBuf == 0) { uint b; Gl.GenBuffers(1, &b); _texHBuf = b; }
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _texHBuf);
            if (grew)
                Gl.BufferData(Gl.SHADER_STORAGE_BUFFER,
                              (IntPtr)((long)cap * 8 * sizeof(ulong)), null, Gl.DYNAMIC_DRAW);
            fixed (ulong* p = _dTexH)
            {
                if (whole)
                    Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero,
                                     (IntPtr)((long)cap * 8 * sizeof(ulong)), p);
                else
                    for (int d = 0; d < _recDirtyN; d++)
                    {
                        long off = (long)_recDirty[d] * 8 * sizeof(ulong);
                        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)off,
                                         (IntPtr)(8 * sizeof(ulong)), p + _recDirty[d] * 8);
                    }
            }
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTexH, _texHBuf);
        }
        if (grew) _recCapGpu = cap;
        _recDirtyN = 0; _recDirtyAll = false;
        // ...and the one array that IS per draw and per pass. Four bytes each, orphaned like the records
        // used to be: it is rewritten whole every pass, so last pass's contents are worth nothing.
        if (_slotBuf == 0) { uint b; Gl.GenBuffers(1, &b); _slotBuf = b; }
        var sSize = (IntPtr)((long)_drawN * sizeof(int));
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _slotBuf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, sSize, null, Gl.DYNAMIC_DRAW);
        fixed (int* p = _dSlot) Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, sSize, p);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDrawIdx, _slotBuf);
    }

    /// <summary>#243: put the CPU-staged per-draw index array back on <see cref="BindDrawIdx"/>. The cull
    /// binds its own compacted one for the passes it drives; every other pass reads the staged order.</summary>
    private static void BindStagedSlots()
    {
        if (_slotBuf != 0) Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDrawIdx, _slotBuf);
    }

    /// <summary>#137: push this pass's bone snapshots into the bone block. Orphan-then-fill for
    /// <see cref="UploadDraws"/>'s reason. Nothing to upload is the common case (no skinmesh on screen), and
    /// the block stays bound empty rather than unbound -- a stage that declares it must find something there
    /// even on a frame where no draw reads it.</summary>
    private static void UploadBones()
    {
        if (!_skinning) return;
        if (_boneBuf == 0) { uint b; Gl.GenBuffers(1, &b); _boneBuf = b; }
        var size = (IntPtr)((long)Math.Max(_boneSlots, 1) * BoneFloats * sizeof(float));
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _boneBuf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, size, null, Gl.DYNAMIC_DRAW);
        if (_boneSlots > 0)
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero,
                             (IntPtr)((long)_boneSlots * BoneFloats * sizeof(float)), _boneStage);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, _boneBuf);
    }

    /// <summary>Phase B: one bind set + one glMultiDrawElementsBaseVertex per batch. uDrawBase is re-sent for
    /// every batch (and it is a DIFFERENT location per program, so the program switch cannot leave a stale
    /// one behind) -- the shaders read record uDrawBase + gl_DrawID, which is what carries the per-part model
    /// matrix, texture rotation and material colours that used to be per-part uniforms.</summary>
    private static void IssueBatches(int from, int groups, float* vpRaw, int* viewport, float near, float far,
                                     bool indirect = false)
    {
        long tIssue = FrameZones.Now;
        try { IssueBatchesInner(from, groups, vpRaw, viewport, near, far, indirect); }
        finally { FrameZones.Add(FrameZones.Zone.Issue, tIssue); }
    }

    private static void IssueBatchesInner(int from, int groups, float* vpRaw, int* viewport, float near, float far,
                                          bool indirect)
    {
        // A probe capture must not carry the debug viz through: /draw viz would bake flat cyan (or a
        // cluster heat map) into the cube and leave it there for every frame that samples it afterwards.
        int viz = _capturing ? 0 : _lightViz;
        // Which program is bound, -1 for neither yet. In the opaque range this switches once (lit batches
        // sort last inside a class); in the blended range it can alternate, because there the order is the
        // camera's and nothing else. ponytail: a lit rebind re-sends the cascade and cube uniforms, so an
        // alternating blended set pays for them per switch. The set is the small remainder by construction;
        // if it ever is not, the fix is a second sort digit inside the distance order, not a cache.
        // #129: three programs now, not two -- fullbright (0), clustered lit (1) and water (2). Keyed on the
        // batch's CLASS for water and on _gLit for the other two, because water is a class the whole way
        // down: its stage samples the framebuffer copy, and no lit batch ever should.
        int boundProg = -1;
        uint boundVao = 0; int boundTex = -1;
        uint boundSrc = 0, boundDst = 0;
        // GL_BLEND as this pass found it: DrawOwned's preamble disables it, and TransparentPass enables it
        // before its own range, so the first batch of either kind agrees with the state already set.
        bool boundBlend = _blendOn;
        float boundCut = float.NaN;   // per PROGRAM: reset on every UseProgram below
        int* boundSlot = stackalloc int[MapSlots];
        for (int s = 0; s < MapSlots; s++) boundSlot[s] = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = from; g < groups; g++)
        {
            bool water = _gCls[g] == (byte)AlloyClass.Water;
            bool lit = _gLit[g];
            int wantProg = water ? 2 : lit ? 1 : 0;
            if (boundProg != wantProg)
            {
                boundProg = wantProg;
                boundCut = float.NaN;
                if (water)
                {
                    Gl.UseProgram(_progWater);
                    Gl.UniformMatrix4fv(_watVp, 1, 0, vpRaw);
                    Gl.UniformMatrix4fv(_watView, 1, 0, _waterView);
                    Gl.UniformMatrix4fv(_watProj, 1, 0, _waterProj);
                    Gl.Uniform2f(_watNearFar, _waterNear, _waterFar);
                    float* dials = stackalloc float[4];
                    dials[0] = FrameSnapshot.Tint; dials[1] = FrameSnapshot.Wave;
                    dials[2] = FrameSnapshot.Ssr; dials[3] = FrameSnapshot.Foam;
                    Gl.Uniform4fv(_watDials, 1, dials);
                    // #147: REAL seconds, not the game clock -- a module runs time-of-day at any multiple of
                    // real time and the whole sea would move at that multiple.
                    Gl.Uniform1f(_watWaveTime, FrameSnapshot.WaveTime);
                    float* tess = stackalloc float[2];
                    tess[0] = FrameSnapshot.TessSegments; tess[1] = FrameSnapshot.TessMax;
                    Gl.Uniform2fv(_watTess, 1, tess);
                    if (_watWind >= 0)
                    {
                        var w3 = AreaWind();
                        float* wv = stackalloc float[3];
                        wv[0] = w3.X; wv[1] = w3.Y; wv[2] = 0f;
                        Gl.Uniform3fv(_watWind, 1, wv);
                    }
                    AlloyLights.BindForDraw();
                    AlloyLights.SetDrawUniforms(_watGrid, _watTile, _watZ, _watFalloff, _watWrap,
                                                _watAtten, _watHot, _watWrapN, viewport, near, far);
                    Gl.Uniform1i(_watViz, viz);
                    Gl.Uniform1i(_watCapture, _capturing ? 1 : 0);
                    UploadFeatureTerms(_watFeat, viewport);
                    fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(_watEye, 1, (float*)eye);
                    UploadAreaTerms(_watAmbient, _watDiffuse, _watDir);
                    UploadSunCascades(_watSunTex, _watSunVp, _watSunTexel, _watSunBias, _watSunCount);
                    UploadLocalCubes(_watCubeTex, _watCubeLight, _watCubePlane, _watCubeCount, _watCubeSoft);
                    UploadProbe(_watProbeMips, _watProbeGain, _watSceneEnv);
                    // The cascade/cube/probe uploads each left their own unit current for a moment; the copy
                    // pair has to be back on its units before the first water draw samples them.
                    FrameSnapshot.Bind();
                }
                else if (lit)
                {
                    Gl.UseProgram(_progLit46);
                    Gl.Uniform1i(_litTex, 0);
                    Gl.UniformMatrix4fv(_litVp, 1, 0, vpRaw);
                    AlloyLights.BindForDraw();
                    AlloyLights.SetDrawUniforms(_litGrid, _litTile, _litZ, _litFalloff, _litWrap,
                                                _litAtten, _litHot, _litWrapN, viewport, near, far);
                    Gl.Uniform1i(_litViz, viz);
                    Gl.Uniform1i(_litCapture, _capturing ? 1 : 0);
                    UploadFeatureTerms(_litFeat, viewport);
                    fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(_litEye, 1, (float*)eye);
                    UploadAreaTerms();
                    UploadSunCascades();
                    UploadLocalCubes();
                    UploadProbe();
                }
                else
                {
                    Gl.UseProgram(_prog46);
                    Gl.Uniform1i(_locTex, 0);
                    float* white = stackalloc float[3] { 1f, 1f, 1f };
                    Gl.Uniform3fv(_locTint, 1, white);    // ...and white, which is Mat()'s ordinary branch
                    Gl.Uniform1i(_locViz, viz);
                    Gl.UniformMatrix4fv(_locVp, 1, 0, vpRaw);
                    UploadFeatureTerms(_locFeat, viewport);
                }
            }
            int s = _gStart[g];
            Gl.Uniform1i(water ? _watBase : lit ? _litBase : _locBase, s);
            // #126: the alpha cutout, per batch because it is per class -- the engine's own
            // fAlphaDiscardValue, which is a global there and a uniform here.
            float cut = AlloyClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(water ? _watCut : lit ? _litCut : _locCut, cut); boundCut = cut; }
            // ...and the blend state, per batch, because the opaque PASS now mixes both: a plain opaque batch
            // draws with GL_BLEND off and a cutout batch with it on and the material's own pair, which is
            // exactly the pair of states the engine draws buckets 0 and 1 in. Batches are class-grouped, so
            // this toggles about once per pass.
            bool blend = AlloyClasses.Blends(_gCls[g]);
            if (blend != boundBlend)
            {
                boundBlend = blend;
                if (blend) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
            }
            if (blend)
            {
                uint src = BlendGl(_gSrc[g]), dst = BlendGl(_gDst[g]);
                if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            }
            // #212: tracked by GL NAME, not by slot -- a slot now has two VAOs and ownership picks.
            uint vao = VaoName(_gVao[g], _gCls[g]);
            if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
            // #153: a merged lit batch spans many textures; its samplers come off the handle block per draw,
            // and binding here would be dead state. Unlit and water batches still bind.
            bool handles = _bindless && lit && !water;
            if (!handles && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            // #120: slots 1..5 on units 3..7. Unbound slots get texture 0 rather than being left holding a
            // previous batch's map -- the shader's slot mask says not to sample them, but a stale binding is
            // one override away from being sampled anyway.
            for (int t = 0; !handles && t < MapSlots; t++)
            {
                int want = _gTexN[g * MapSlots + t];
                if (want == boundSlot[t]) continue;
                boundSlot[t] = want;
                // #151: one DSA call instead of ActiveTexture+Bind+ActiveTexture -- this loop is a third of
                // the pass's GL traffic. The fallback keeps pre-4.5 drivers drawing.
                if (Gl.BindTextureUnit != null) Gl.BindTextureUnit(PbrUnit0 + (uint)t, (uint)want);
                else
                {
                    Gl.ActiveTexture(Gl.TEXTURE0 + PbrUnit0 + (uint)t);
                    Gl.BindTexture(Gl.TEXTURE_2D, (uint)want);
                    Gl.ActiveTexture(Gl.TEXTURE0);   // unit 0 stays current: the diffuse rebinds there
                }
            }
            // #147: water is drawn as PATCHES so the tessellator sees it. Same index buffer, same triangles
            // -- only the primitive mode changes, and the tess-control stage retires a patch to level 1 when
            // there is no wave to resolve, so a millpond costs the same as before.
            // #243: the same batch, from a list the GPU compacted. The bind set above is unchanged (the
            // cull moves WHICH draws are in the batch, never what state they draw under), and the record
            // lookup is unchanged too -- uDrawBase is still this batch's start, because the cull compacts
            // each batch into its own source region rather than into one global run.
            if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
            else
                Gl.MultiDrawElementsBaseVertex(water ? Gl.PATCHES : _gMode[g], counts + s, GlUnsignedShort,
                                               (void**)(offs + s), _gLen[g], bases + s);
            _batches++;
            _draws += _gLen[g];
            _drawsByClass[_gCls[g]] += _gLen[g];
        }
        // Hand the per-batch toggling back to the pass level: the next pass reads this rather than assuming
        // what the last batch left bound.
        _blendOn = boundBlend;
    }

    /// <summary>ADR 0011 steps 4 and 7 (#126): the blending classes. Blended draws sorted back-to-front with
    /// the depth mask OFF -- which is what makes grass's punch-through structurally impossible rather than
    /// fixed, since nothing alpha-blended can write depth any more -- and additive follows it unsorted,
    /// because addition commutes and the engine leaves its own depth mask off there too
    /// (<c>Material::SetBlendingMode</c> @0x4c5c50 turns it off for exactly that one blend pair).
    /// <para>Depth TESTING stays on, against the opaque set's depth: a blended surface behind a wall is still
    /// hidden by it. The per-batch <c>glBlendFunc</c> comes off the material, in
    /// <see cref="IssueBatches"/>.</para>
    /// <para>The same ordering caveat reaches the CUTOUT class, which draws back in the opaque pass: its
    /// graded texels blend, so they composite against whatever the framebuffer already holds. Alloy opaque
    /// geometry is there (it draws first, in class order); anything the engine still owns is not, and a
    /// cutout in front of an engine-drawn wall therefore blends against stale colour AND depth-writes over
    /// it. The upgrade that removes the dependency entirely is alpha-to-coverage -- ADR 0011 names it beside
    /// the alpha test -- which needs a multisampled target and is worth taking when the Alloy frame binds its
    /// own (the M3 exit step).</para>
    /// <para>NAMED APPROXIMATION, for as long as the Alloy pass anchors ahead of the engine's walk: these classes
    /// composite BEFORE everything the engine still draws itself, and nothing they draw writes depth. So a
    /// creature standing behind an owned window paints over it rather than being seen through it (vanilla
    /// gets a different wrong answer: it depth-writes its blended geometry, so the creature vanishes
    /// instead), and the SKYBOX -- which the engine draws between buckets 0 and 1, depth-mask off -- paints
    /// over a blended or additive surface seen against the sky. The first resolves when the dynamic classes
    /// are owned (#127), the second when the skybox is (#125); neither is a #126 regression to chase, and
    /// both are why ADR 0011's frame anchor is a named exit criterion rather than a detail.</para></summary>
    private static void TransparentPass(int from, int to, Matrix4x4* vp, int* viewport, float near, float far)
    {
        if (to <= from) return;
        Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        IssueBatches(from, to, (float*)vp, viewport, near, far);
    }

    /// <summary>ADR 0011 <see cref="PassId.WaterOwned"/> (#129): the water class, drawn from the one
    /// framebuffer copy taken immediately before it.
    /// <para>Depth writes go back ON, which is the one piece of state that separates this pass from the
    /// blended one and the reason it can be here at all: additive draws after water and has to be able to
    /// stand in FRONT of it. That is vanilla's arrangement -- effects are invisible to the reflection and are
    /// not painted over by the surface -- and the ticket keeps it deliberately.</para>
    /// <para>Stands down whole whenever the copy could not be taken (<see cref="FrameSnapshot.Ready"/> false) or
    /// the program did not build. Nothing partial: <see cref="BucketGate"/>'s bucket-6 gate reads the
    /// same flag, so the engine draws every water surface itself on exactly the passes this one does not.</para></summary>
    private static void WaterOwnedPass(int from, int to, Matrix4x4* vp, float* view, float* proj,
                                       int* viewport, float near, float far)
    {
        if (to <= from || !FrameSnapshot.Ready || !_waterArmed) return;
        _waterView = view; _waterProj = proj; _waterNear = near; _waterFar = far;
        Gl.DepthMask(1);
        FrameSnapshot.Bind();
        IssueBatches(from, to, (float*)vp, viewport, near, far);
        // The one flag IterSight reads: staged-and-drawn, not merely staged.
        _waterDrew = true;
        // Leave the state the additive pass expects to find: it enables blending itself, but the depth mask
        // is pass-level state and additive must not write depth.
        Gl.DepthMask(0);
    }

    /// <summary>One <c>Aurora::AuroraBlendModes</c> value as its GL blend factor -- the engine's own
    /// translation table at 0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x4cb0c0 indexes. Out-of-range
    /// values fall back to the pair the engine's own default is, rather than to whatever GL enum 0 means.</summary>
    private static uint BlendGl(int aurora) => aurora switch
    {
        0 => GlSrcAlpha,
        1 => GlOneMinusSrcAlpha,
        2 => GlOne,
        3 => GlZero,
        4 => GlDstColor,
        5 => GlSrcColor,
        // #125 review: the table's entry 6 is 0x0305, ONE_MINUS_DST_ALPHA -- read out of
        // blendTranslationTable @0x12f5040 rather than inferred from the name beside it.
        6 => GlOneMinusDstAlpha,
        _ => GlSrcAlpha,
    };
}
