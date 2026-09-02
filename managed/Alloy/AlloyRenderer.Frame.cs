using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#173: the FRAME DISPATCH. What the Alloy bracket does once per pass and in what order: the prepass, the opaque block, the pass-slot walk that hands each ADR 0011 slot to its owner, and the probe capture that rides the same staged batches. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The Alloy opaque block (hot, zero-alloc): view*proj off the matrix stacks and the mirror-FBO
    /// inputs once, then -- inside the #80 bracket -- <see cref="BuildBatches"/> revalidates every Alloy part
    /// and stages its draw (texture id re-fetched every frame per the borrowed-pointer rule, which is also what
    /// keeps animated textures cycling), and <see cref="IssueBatches"/> submits the result as one multidraw per
    /// (program, VDO, texture, primitive) group. Anything that fails revalidation falls straight back to the
    /// engine walk.</summary>
    /// <summary>#216/#263: the bail-out a pass takes AFTER the shim has already taken the engine's frame
    /// away. There is nothing to hand back to and -- since the renderer is chosen at boot -- no way to warm
    /// the engine's resource world mid-session, so this is a HARD FAILURE: the reason is logged and put on
    /// screen naming the restart, and the mode does not move. A no-op before the shim's window, where
    /// returning here really does leave the engine's own frame intact.</summary>
    private static void NoFrameBehindUs(string why)
    {
        if (RspShim.InWindow) AlloyGate.Fail(why);
    }

    private static void DrawOwned()
    {
        long tAlloy = FrameZones.Now;
        try { DrawOwnedInner(); } finally { FrameZones.Add(FrameZones.Zone.Alloy, tAlloy); }
    }

    private static void DrawOwnedInner()
    {
        // #308 follow-up: consumed FIRST, above every early return -- a flag left standing across a refused
        // colour frame would make the NEXT frame's colour pass skip the pose against a shadow bracket that
        // may have sat that frame out, and every skinned part would draw a stale (or bind) pose.
        bool posedByShadow = PosedByShadow;
        PosedByShadow = false;
        // #129: last pass's snapshot is not this pass's. Revoked first thing, so every path out of here that
        // never reaches PassId.FbCopy leaves Alloy water disarmed -- and BucketGate's bucket-6 gate reads
        // the same flag, so water is simply not drawn on exactly those passes (#285: a hole, not vanilla).
        FrameSnapshot.Revoke();
        if (_progFailed || _batchUnavailable) return;   // EndFrame revokes + disarms; never pay the bracket for nothing
        // Checked BEFORE anything stamps OwnGen: BuildBatches marks parts as drawn, which is only honest while
        // the issue phase cannot fail. Missing entry points are the one way it could.
        if (Gl.MultiDrawElementsBaseVertex == null || Gl.BindBufferBase == null)
        {
            // #263: the verdict froze before this frame, so nothing re-reads DrawCapable. Say it here or the
            // world goes bare with no line at all.
            _batchUnavailable = true;
            AlloyGate.Fail("glMultiDrawElementsBaseVertex or glBindBufferBase is missing");
            return;
        }

        // #257: where the frame goes is LATCHED, not interrogated. Every frame used to open with nine
        // GL queries -- viewport, framebuffer binding, four attachment params, sRGB, and the two clear
        // values -- because the engine owned the frame's output configuration and could change it under us.
        // Under the cutover it does not: the answer is the same every frame until something that CANNOT
        // happen silently happens (a resize, an area change, a context loss). So the queries run once per
        // such generation, and once every LogEvery frames they run again as an ASSERTION -- re-read,
        // compared, and any drift logged and adopted. What survives unchanged is the argument for reading
        // the clear values in ENGINE context at all: a GL share list shares objects, not per-context state,
        // so glClearColor is not queryable from inside the 4.6 bracket -- that context has its own.
        ref Target t = ref ThisFrameTarget();
        int* viewport = stackalloc int[4] { t.Vp0, t.Vp1, t.Vp2, t.Vp3 };
        int engFbo = t.Fbo, colorType = t.ColorType, colorName = t.ColorName,
            depthType = t.DepthType, depthName = t.DepthName;
        bool srgb = t.Srgb;
        float* engClear = stackalloc float[4] { t.Clear0, t.Clear1, t.Clear2, t.Clear3 };
        float engClearDepth = t.ClearDepth;
        // ...and the refusals that need no bracket at all, ahead of the preamble they would otherwise
        // waste -- PoseSkinned alone solves every Alloy skinmesh's pose.
        if (!PostChain.Precheck(engFbo, depthName, viewport)) return;
        // Only now: a pass about to hand the frame back would have the engine solve each of these poses
        // again on its own walk, so the solve is not worth doing for it. #262: the solve no longer enters
        // engine code at all, but it still reads engine animation state, so it stays on this side.
        // #308 follow-up: the shadow bracket poses first (ShadowPose) so the bake and this pass skin with
        // the SAME frame's bones; re-solving here would tear them apart again. The solve only runs here on
        // a frame the shadow bracket sat out (flag consumed at the top of this method).
        if (!posedByShadow)
        {
            long tPose = FrameZones.Now;
            PoseSkinned();
            FrameZones.Add(FrameZones.Zone.Pose, tPose);
        }

        // row-major v*M convention: clip = v * Model * View * Proj; raw bytes read column-major by GLSL are
        // the transpose, so uVP * d.model * vec4(aPos,1) is exact -- (M*V*P)^T = (V*P)^T * M^T, which is why
        // #84 can hoist view*proj to a per-program uniform and leave only the model matrix per draw.
        _sceneEnv = _enrollScene != IntPtr.Zero && *(IntPtr*)(_enrollScene + SceneEnvMap) != IntPtr.Zero;

        Matrix4x4 view = *(Matrix4x4*)(_mtxStacks + ModeView * StackStride + StackTop);
        Matrix4x4 proj = *(Matrix4x4*)(_mtxStacks + ModeProjection * StackStride + StackTop);
        Matrix4x4 vp = view * proj;
        // every froxel input comes off THIS pass's projection, never the capture side (see TryPerspective)
        bool perspective = AlloyLights.TryPerspective((float*)&proj, out float p00, out float p11,
                                                      out float near, out float far);
        // #120: this pass's eye, for the specular view vector. Same row-vector convention as above, so the
        // inverse view's translation IS the eye. Pass-local state, read off this pass's own stacks -- the
        // SCENE camera is not derived here any more (#257 reads it whole at the anchor), so there is no
        // contest between passes left to resolve. Ahead of the frustum, which measures its fog clamp from it.
        if (perspective && Matrix4x4.Invert(view, out Matrix4x4 camToWorld)) _eyePos = camToWorld.Translation;
        SetPassFrustum((float*)&vp, perspective);   // #330: the route gate's planes, this pass's own
        // Cap 5 is the engine's own lit predicate (Ghidra MatStatic @0x450560 / RenderFlat @0x450eb0): with
        // it off every part gets flat white, so the flat variant IS vanilla for this pass.
        bool lightingOn = _litCount > 0 && _lighting && !_litFailed && perspective
                          && (_isEnabled == null || _isEnabled(CapLighting) != 0);

        if (!GlContextService.Enter()) { NoFrameBehindUs("the 4.6 context could not be made current"); return; }
        long tOwn = FrameZones.Now;
        OwnedTextures.Drain();   // #211: the world edge queued deletes; this is the first context to own them
        OwnedBuffers.ClearDirty();   // #260: geometry uploaded itself at the engine's commit; close the window
        OwnedBuffers.HighWater();    // ...and report the mark the funnel recorded, where a string is affordable
        FrameZones.Add(FrameZones.Zone.Own, tOwn);
        if (EnsureProgram() && BindTarget(engFbo, colorType, colorName, depthType, depthName))
        {
            // #117/#119: the lighting rig, the GL state and the batch BUILD are bracket preamble -- both
            // colour-bracket passes draw the same staged batches, and building twice would revalidate (and
            // re-stamp OwnGen for) every Alloy part twice. The passes come off FrameGraph, not off call order.
            if (lightingOn)
            {
                long tLights = FrameZones.Now;
                AlloyLights.Upload(_frameSeq);
                // The cluster buffers are only meaningful once THIS pass's build has run: a skipped build
                // leaves them holding another camera's binning (or, before the first dispatch, undefined
                // BufferData contents), and a lit part would then sample arbitrary light indices.
                lightingOn = AlloyLights.Build((float*)&view, p00, p11, near, far)
                             && _progLit46 != 0;
                FrameZones.Add(FrameZones.Zone.Lights, tLights);
            }
            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            // #292: cull ON is the engine's default -- Scene::RenderDrawBucket @0x46d060 leaves
            // Enable(CULL_FACE) + SetCullFace(BACK) behind on every exit, and that is the state its
            // sub-list 0 (everything not marked `twosided`) draws in. The batch loops override it per batch;
            // this states the default the passes with no batch of their own inherit.
            new PassState(depth: true, depthWrite: true, cull: true, srgb: srgb).Apply();
            _blendOn = false;
            Gl.ActiveTexture(Gl.TEXTURE0);
            _lastPixels = viewport[2] * viewport[3];
            // #129: built HERE, not at the pass, because staging has to know. A water part staged for a pass
            // whose program never built would be stamped as owned (and so suppressed in the engine's walk)
            // and then never drawn -- so the decision is made before BuildBatches, which skips the class
            // outright when it is false.
            // ...and on the LIGHT RIG, for a reason the class check cannot supply: the water stage calls
            // SowxClusteredLight unconditionally, and the cluster buffers are only meaningful once THIS
            // pass's build has run. BuildBatches skips lit batches on !lightingOn, but a water batch is
            // keyed on its class rather than on _gLit, so without this term /draw light 0 (or a failed
            // lit program, or a build that refused) would leave the water program reading undefined
            // BufferData contents -- an unbounded per-fragment light loop, not a dim sea.
            _waterArmed = FrameSnapshot.Usable && lightingOn && EnsureWaterProgram();
            // #139/#132: from here to PassId.PostProcess the colour passes draw into an RGBA16F target that
            // shares the engine's depth, and everything they write is LINEAR. Asked BEFORE anything is
            // staged, because a refusal is no longer survivable: there is no display-space variant of the
            // Alloy stages to fall back to, so the whole pass stands down and the engine renders it vanilla.
            // Staging first would stamp OwnGen on parts the iterator then suppresses and nobody draws.
            long tPostBegin = FrameZones.Now;
            bool postOk = PostChain.Begin(_fbo46, depthType, depthName, viewport, engClear, engClearDepth);
            FrameZones.Add(FrameZones.Zone.Post, tPostBegin);
            if (!postOk) { GlContextService.Leave(); return; }
            GpuFrameStart();
            long tBuild = FrameZones.Now;
            int groups = BuildBatches(lightingOn);
            FrameZones.Add(FrameZones.Zone.Build, tBuild);
            if (groups > 0)
            {
                long tUp = FrameZones.Now;
                StageSky();
                UploadDraws();
                // #308 follow-up: the shadow bracket already uploaded this frame's block; re-orphaning it
                // here would throw away the very pose the bake just sampled from.
                if (!posedByShadow) UploadBones();
                FrameZones.Add(FrameZones.Zone.Upload, tUp);
                // #243: Alloy's own visibility, dispatched twice -- frustum-only for the depth prepass, then
                // frustum + hi-Z for the colour pass once the prepass has filled the depth the pyramid is
                // built from.
                // #290: staged from EVERY batch now, not the opaque prefix. _gOpaqueEnd is handed over as
                // the COMPACTING boundary instead: batches above it are sorted back-to-front by a second
                // dispatch rather than appended by the atomic.
                long tCull = FrameZones.Now;
                // The batch list's draw count, read off the last batch rather than from _drawN -- StageSky
                // appends to _drawN below and its draws have no source record and no batch of their own.
                int cullDraws = _gStart[groups - 1] + _gLen[groups - 1];
                // #287: pixels per unit of world radius at unit depth, the LOD test's one input. Zero when
                // this pass has no perspective projection, and the test then stands down.
                float lodScale = perspective ? p11 * viewport[3] * 0.5f : 0f;
                _lodScale = lodScale;
                // #303: the spheres go up whatever the cull mode -- the shadow gather reads the same buffer.
                fixed (float* sph = _dSph)
                    CullPass.UploadSpheres(sph, _recOwnCap + MaxSkyDraws);
                fixed (uint* src = _dSrc)
                    // A probe capture draws the same batches six more times from six other cameras, so its
                    // faces cannot ride a list culled for THIS one -- passing zero groups stands the cull
                    // down for the whole pass rather than leaving a stale list armed behind it.
                    if (CullPass.Begin(_capturing ? 0 : groups, cullDraws, src,
                                       lodScale, _gOpaqueEnd, _eyePos))
                        CullPass.Dispatch(CullPass.PhasePrepass, &vp, hiZ: false, PassGen);
                FrameZones.Add(FrameZones.Zone.Cull, tCull);
                // #322: the loop's own CPU work, with the brackets that fire inside it (multidraw issue,
                // the colour cull dispatch, post, the probe capture) subtracted out -- same parent/child
                // pattern as the Probe bracket below.
                long issue0 = FrameZones.Ticks(FrameZones.Zone.Issue), cull0 = FrameZones.Ticks(FrameZones.Zone.Cull);
                long post0 = FrameZones.Ticks(FrameZones.Zone.Post), probe0 = FrameZones.Ticks(FrameZones.Zone.Probe);
                long tPasses = FrameZones.Now;
                foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
                {
                    if (bracket != Bracket.Colour) continue;
                    switch (id)
                    {
                        case PassId.DepthPrepass:
                            long tPre = FrameZones.Now;
                            DepthPrepassPass(_gOpaqueEnd, &vp, viewport);
                            // #243: the pyramid, from the depth that pass just wrote. Inside the same frame
                            // on purpose -- a pyramid carried from last frame pops at the silhouette of
                            // every moving occluder, and the prepass depth is already paid for (#119).
                            if (DepthResource.Filled(PassGen))
                                CullPass.BuildHiZ(PostChain.DepthTex, viewport[2], viewport[3], viewport,
                                                  PostChain.Fbo, PassGen);
                            FrameZones.Add(FrameZones.Zone.Prepass, tPre);
                            break;
                        case PassId.OpaqueOwned:
                            // Second dispatch, and it is unconditional rather than folded into the prepass
                            // case: a frame graph without a prepass in it must still leave the colour pass a
                            // list to draw, and hi-Z simply stands down when there is no depth to build from.
                            long tCull2 = FrameZones.Now;
                            CullPass.Dispatch(CullPass.PhaseColour, &vp, hiZ: DepthResource.Filled(PassGen), PassGen);
                            FrameZones.Add(FrameZones.Zone.Cull, tCull2);
                            OpaqueOwnedPass(_gOpaqueEnd, &vp, viewport, near, far);
                            // The GPU-side cluster stats belong to the SCREEN's build. Harvested here rather
                            // than after the bracket because the probe capture rebuilds the clusters once per
                            // face, and reading afterwards would report cube face 6's overflow as the frame's.
                            if (_takeOverflow)
                            { _takeOverflow = false; _clusterOverflow += AlloyLights.TakeClusterOverflow(); }
                            break;
                        case PassId.GrassOwned:
                            GpuBegin(GpuTerm.Grass);
                            GrassField.Draw(&vp, viewport, near, far, lightingOn);
                            GpuEnd(GpuTerm.Grass);
                            // Grass draws unblended by construction and toggles GL_BLEND itself; hand the
                            // tracked state back exactly as it was, or the next pass's own bookkeeping
                            // (IssueBatches reads _blendOn as "what is already set") starts from a lie.
                            if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
                            break;
                        case PassId.SkyOwned:
                            GpuBegin(GpuTerm.Sky);
                            SkyOwnedPass(&vp, viewport);
                            GpuEnd(GpuTerm.Sky);
                            break;
                        case PassId.BlendedOwned:
                            GpuBegin(GpuTerm.Blended);
                            TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far);
                            GpuEnd(GpuTerm.Blended);
                            break;
                        case PassId.FbCopy:
                            GpuBegin(GpuTerm.Water);
                            // Only when there is water to read it. The copy is a full-screen blit of colour
                            // AND depth -- cheap, but not free, and every frame without a water surface on
                            // screen would be paying for a texture nothing samples.
                            if (_gWaterEnd > _gBlendEnd && !_capturing)
                            {
                                // The blended class left GL_BLEND on and the depth mask off; the blit does
                                // not care, but leaving the tracked state honest does -- IssueBatches below
                                // reads _blendOn as "what is already set".
                                long tBlit = FrameZones.Now;
                                FrameSnapshot.Copy(viewport);
                                FrameZones.Add(FrameZones.Zone.Blit, tBlit);
                            }
                            break;
                        case PassId.WaterOwned:
                            WaterOwnedPass(_gBlendEnd, _gWaterEnd, &vp, (float*)&view, (float*)&proj,
                                           viewport, near, far);
                            GpuEnd(GpuTerm.Water);
                            break;
                        case PassId.AdditiveOwned:
                            GpuBegin(GpuTerm.Additive);
                            TransparentPass(_gWaterEnd, groups, &vp, viewport, near, far);
                            GpuEnd(GpuTerm.Additive);
                            // The blending classes leave the depth mask off and GL_BLEND on; the probe
                            // capture that follows draws the opaque set again and must find neither.
                            Gl.Disable(Gl.BLEND); _blendOn = false; Gl.DepthMask(1);
                            break;
                        case PassId.BeamsOwned:
                            // Beams bind a program and a VAO of their own and hand the state back exactly as
                            // the additive arm left it; nothing here tracks _blendOn on their behalf.
                            Beams.Draw(&view, &proj);
                            break;
                        case PassId.ParticlesOwned:
                            // Particles bind a program, a VAO and a buffer of their own and hand the state
                            // back the way the beams left it -- blend off, depth mask on, culling off.
                            GpuBegin(GpuTerm.Particles);
                            // #336: the particle pass's fill -- samples over pixels is its overdraw, the
                            // number that says whether a half-resolution pass would pay.
                            bool partCounting = _qPartSamples.Begin(Gl.SAMPLES_PASSED);
                            long blit0 = FrameZones.Ticks(FrameZones.Zone.Blit);
                            long tParts = FrameZones.Now;
                            Particles.Draw(&view, &proj, viewport, near, far, lightingOn);
                            FrameZones.Add(FrameZones.Zone.Parts, tParts,
                                           FrameZones.Ticks(FrameZones.Zone.Blit) - blit0);
                            if (partCounting) _qPartSamples.End(Gl.SAMPLES_PASSED);
                            GpuEnd(GpuTerm.Particles);
                            break;
                        case PassId.ProbeCapture:
                            if (!lightingOn) break;
                            CaptureProbe(_gOpaqueEnd, far);
                            // The capture bound its own framebuffer and viewport; whatever runs after the
                            // bracket must find the engine's target again, not a 128px cube face.
                            BindTarget(engFbo, colorType, colorName, depthType, depthName);
                            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
                            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                            break;
                        case PassId.PostProcess:
                            if (!PostChain.Armed) break;
                            long tPost = FrameZones.Now;
                            GpuBegin(GpuTerm.Post);
                            // Bloom runs against the HDR target and binds its own attachments; the engine's
                            // target has to come back before the composite writes into it, and with it the
                            // viewport and the sRGB-encode state the pass was entered with.
                            PostChain.Bloom();
                            BindTarget(engFbo, colorType, colorName, depthType, depthName);
                            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                            PostChain.Composite(viewport);
                            // #232 second mode: the owned outline+fill highlight, drawn into the finished
                            // display-space frame with the depth Composite just blitted to it. Not a
                            // FrameGraph slot: it is part of finishing the post-process output, like Bloom.
                            OutlineHighlightPass(groups, &vp, viewport);
                            GpuEnd(GpuTerm.Post);
                            FrameZones.Add(FrameZones.Zone.Post, tPost);
                            break;
                    }
                }
                FrameZones.Add(FrameZones.Zone.Passes, tPasses,
                               FrameZones.Ticks(FrameZones.Zone.Issue) - issue0
                               + FrameZones.Ticks(FrameZones.Zone.Cull) - cull0
                               + FrameZones.Ticks(FrameZones.Zone.Post) - post0
                               + FrameZones.Ticks(FrameZones.Zone.Probe) - probe0);
            }
            Gl.BindVertexArray(0);
        }
        GlContextService.Leave();
    }

    /// <summary>ADR 0011 step 1 (#119): the Alloy opaque set again, depth-only and colour-masked, so early-Z
    /// throws away the fragments the shading pass would otherwise light and then overwrite. Every Alloy part
    /// is opaque, so the set qualifies -- with two exclusions, both #123's:
    /// <list type="bullet">
    /// <item>a batch whose feature DISPLACES vertices (wind sway, roof toggle). This stage does not displace,
    /// so its depth is the part's rest silhouette, and the colour pass's LEQUAL would then kill real
    /// fragments -- a roof offset by 500m fails everywhere, and whatever stands behind a swaying plant dies
    /// at the plant's rest outline. Note <see cref="DepthSafe"/> cannot catch this: it is a TEXT check, and
    /// the displacing stage still contains <see cref="ClipExpr"/> verbatim on its undisplaced path.</item>
    /// <item>the roof anchor discard is a fragment discard, and those punch holes the same way -- but it only
    /// exists on a roof, which the first exclusion already removed.</item>
    /// </list>
    /// The cutout class the ADR makes a measurement question is still not owned.
    /// <para>Publishes <see cref="DepthResource"/> on the way out: the buffer is the deliverable, not a
    /// private optimisation.</para></summary>
    private static void DepthPrepassPass(int groups, Matrix4x4* vp, int* viewport)
    {
        if (!_depthPrepass || !EnsureDepthProgram()) return;
        int skipped = 0, drawn = 0;
        GpuBegin(GpuTerm.Prepass);
        // #243: the FRUSTUM-only list. The prepass is what the hi-Z pyramid is built from, so it cannot be
        // culled against the pyramid it produces -- and it must not be: a part the prepass skips is one the
        // pyramid does not know about, which only ever makes the colour cull more conservative.
        // The dispatch spans every batch since #290; this loop still walks the opaque range alone, so the
        // blending batches' commands in this phase's buffer are simply never submitted.
        bool indirect = CullPass.Active;
        if (indirect) CullPass.BindForDraw(CullPass.PhasePrepass);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.DepthFunc(Gl.LESS);
        uint boundVao = 0;
        // #292: the prepass makes the SAME per-batch cull decision as the colour pass, off the same
        // _gNoCull, because #119's constraint is about the depth the colour pass then tests LEQUAL against.
        // That constraint says every owned vertex stage must compute gl_Position character-for-character
        // identically; culling is the other half of it -- an identical clip position on a triangle only one
        // of the two passes rasterises still loses the fragment. Same batches, same state, same depth.
        int boundCull = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        // #322: TWO SWEEPS -- all plain batches under one program bind, then all keyhole ones under the
        // other. Dissolve membership is per-frame camera state (thousands of parts keyhole at once under a
        // roofed area), so the cut order interleaves the two programs, and binding per alternation was the
        // measured Prepass tower (~5,5ms CPU). Depth-only LESS writes are order-independent, so the reorder
        // costs nothing; each program now binds at most once per pass.
        for (int sweep = 0; sweep < 2; sweep++)
        {
            bool dis = sweep == 1;
            bool bound = false;
            // #336: the keyhole sweep timed as its own term, and skippable (/draw prepasskey 0).
            if (dis) { GpuEnd(GpuTerm.Prepass); if (!_prepassKey) break; GpuBegin(GpuTerm.PrepassKey); }
            for (int g = 0; g < groups; g++)
            {
                if (ShaderFeatures.OutOfPrepass(_gFeat[g])) { if (sweep == 0) skipped += _gLen[g]; continue; }
                // #126: a cutout discards, so its prepass depth would be its SOLID silhouette and the colour
                // pass's LEQUAL would cull whatever stands in its holes -- grass turning the ground behind it
                // into a rectangle. ADR 0011 leaves cutouts-in-the-prepass a MEASUREMENT question (they cost
                // a texture fetch per fragment to save shading later); this is the measurement's off side,
                // and _prepassSkipped is what says how much overdraw it leaves on the table.
                if (_gCls[g] == (byte)AlloyClass.Cutout) { if (sweep == 0) skipped += _gLen[g]; continue; }
                if (_gDis[g] != dis) continue;
                if (dis && _progDepthKey == 0) { skipped += _gLen[g]; continue; }
                if (!bound)
                {
                    bound = true;
                    Gl.UseProgram(dis ? _progDepthKey : _progDepth);
                    Gl.UniformMatrix4fv(dis ? _depthKeyVp : _depthVp, 1, 0, (float*)vp);
                    if (dis) UploadFeatureTerms(_depthKeyFeat, viewport);
                }
                int s = _gStart[g];
                if (dis) _prepassKeyed += _gLen[g];
                Gl.Uniform1i(dis ? _depthKeyBase : _depthBase, s);
                int wantCull = _gNoCull[g] ? 0 : 1;
                if (wantCull != boundCull)
                {
                    boundCull = wantCull;
                    if (wantCull != 0) { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK); }
                    else Gl.Disable(Gl.CULL_FACE);
                }
                // #212: the SAME VAO the colour pass will bind for this class. Identical buffers through
                // both passes is what keeps #119's invariant-gl_Position depth match trivially true.
                uint vao = VaoName(_gVao[g], _gCls[g]);
                if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
                if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
                else
                    Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                                   (void**)(offs + s), _gLen[g], bases + s);
                drawn += _gLen[g];
            }
        }
        if (indirect) { CullPass.UnbindForDraw(); BindStagedSlots(); }
        Gl.ColorMask(1, 1, 1, 1);
        GpuEnd(GpuTerm.PrepassKey);
        // Counted from what this loop submitted, not from the pass's total draw count: since #126 that total
        // spans the blending classes too, and the prepass walks the opaque range alone.
        // #243: under the cull this is what was STAGED, not what the GPU kept -- the count that decides is in
        // the parameter buffer and does not come back. The period line's cull clause carries the kept figure;
        // this one is the ceiling it culls from, and reading it as "draws" would overstate the pass.
        _prepassDraws += drawn;
        _prepassSkipped += skipped;
        // #214: OUR depth attachment, not the engine's -- a texture by construction, so consumers get a
        // sampleable name rather than whatever the engine happened to allocate.
        DepthResource.Publish((int)Gl.ATTACHMENT_TEXTURE, (int)PostChain.DepthTex, viewport, PassGen);
    }

    /// <summary>ADR 0011 step 2, the Alloy opaque set: submit the staged batches, shaded.
    /// <para>LEQUAL rather than EQUAL against the prepass depth. All three Alloy vertex stages declare
    /// <c>invariant gl_Position</c> over the identical clip expression (<see cref="ClipExpr"/>), so equal is
    /// what they produce -- but LEQUAL is also correct when the prepass stood down, which EQUAL is not, and
    /// early-Z rejects the hidden fragments either way.</para></summary>
    private static void OpaqueOwnedPass(int opaqueEnd, Matrix4x4* vp, int* viewport, float near, float far)
    {
        Gl.DepthFunc(Gl.LEQUAL);
        // SAMPLES_PASSED covers the OPAQUE set alone, so the overdraw figure stays comparable with #119's
        // baseline. #325: two calls so opaque and cutout time apart; the seam re-binds the indirect buffer,
        // the slots and the program (one lit uniform re-send), and draws exactly the same batches.
        bool counting = _qSamples.Begin(Gl.SAMPLES_PASSED);
        IssueClass(0, _gCutoutStart, GpuTerm.Opaque, GpuTerm.OpaqueKey, vp, viewport, near, far);
        IssueClass(_gCutoutStart, opaqueEnd, GpuTerm.Cutout, GpuTerm.CutoutKey, vp, viewport, near, far);
        if (counting) _qSamples.End(Gl.SAMPLES_PASSED);
    }

    /// <summary>#336: one opaque class's batches under two GPU terms -- the plain run, then the keyhole
    /// dissolve partition #327 placed at its tail. The seam is one more re-bind, and only when the class has
    /// a dissolving tail at all; a roofless frame issues exactly as before.</summary>
    private static void IssueClass(int from, int to, GpuTerm plain, GpuTerm keyed,
                                   Matrix4x4* vp, int* viewport, float near, float far)
    {
        int tail = from;
        while (tail < to && !_gDis[tail]) tail++;
        GpuBegin(plain);
        IssueColour(from, tail, vp, viewport, near, far);
        GpuEnd(plain);
        if (tail >= to) return;
        GpuBegin(keyed);
        IssueColour(tail, to, vp, viewport, near, far);
        GpuEnd(keyed);
    }

    /// <summary>#121: capture the area's IBL probe -- the same staged batches, drawn six more times into a
    /// small cube from the player's position. Runs at the END of the colour bracket, after both on-screen
    /// passes have issued, for two reasons: the cluster build is per-pass state that each face has to
    /// overwrite (a face lit by the SCREEN's froxels gets the wrong point lights, which is precisely wrong in
    /// the interiors the probe exists for), and trashing it here costs nothing because the next pass rebuilds
    /// it anyway.
    /// <para>Not per frame: <see cref="EnvProbe.Wanted"/> gates on the area, the walked distance and a frame
    /// floor. The #119 meters are saved and restored across it -- a capture's draws are not the frame's
    /// draws, and folding them in would make the overdraw and batching figures lie on capture frames.</para></summary>
    private static void CaptureProbe(int groups, float far)
    {
        long tProbe = FrameZones.Now;
        // The six faces submit through IssueBatches, which books its own zone -- so the parent subtracts it
        // exactly as the shim subtracts the Alloy frame. Without this a capture frame counts the submission
        // twice, and a capture frame is precisely the shape that latches the worst-frame snapshot.
        long issue0 = FrameZones.Ticks(FrameZones.Zone.Issue);
        try { CaptureProbeInner(groups, far); }
        finally { FrameZones.Add(FrameZones.Zone.Probe, tProbe, FrameZones.Ticks(FrameZones.Zone.Issue) - issue0); }
    }

    private static void CaptureProbeInner(int groups, float far)
    {
        IntPtr bsp = _enrollScene != IntPtr.Zero ? *(IntPtr*)(_enrollScene + SceneLayout.SceneBspRoot) : IntPtr.Zero;
        if (!EnvProbe.Wanted(_eyePos, bsp, _frameSeq)) return;
        if (!EnvProbe.Begin(_eyePos, bsp, _frameSeq, far, ProbeClear())) return;

        long draws = _draws, batches = _batches;
        float buildMs = AlloyLights.LastBuildMs;   // the [alloy] line's cluster timer is the SCREEN's build
        Vector3 eye = _eyePos;
        _capturing = true;
        _eyePos = EnvProbe.Origin;
        (float fnear, float ffar) = EnvProbe.FacePlanes;
        int* faceVp = stackalloc int[4] { 0, 0, EnvProbe.Size, EnvProbe.Size };
        bool ok = true;
        for (int f = 0; f < 6 && ok; f++)
        {
            ok = EnvProbe.Face(f, out Matrix4x4 vp);
            if (!ok) break;
            Matrix4x4 view = EnvProbe.FaceView(f);
            // A cube face is exactly 90deg, so the projection's p00 and p11 are both cot(45) = 1.
            AlloyLights.Build((float*)&view, 1f, 1f, fnear, ffar);
            IssueBatches(0, groups, (float*)&vp, faceVp, fnear, ffar);
        }
        if (ok) EnvProbe.End(); else Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _capturing = false;
        _eyePos = eye;
        _draws = draws; _batches = batches;
        AlloyLights.LastBuildMs = buildMs;
    }

    /// <summary>What the probe's faces are cleared to -- the sky stand-in.
    /// <para>ponytail: the skybox is still engine-drawn, so it is absent from the Alloy set the capture
    /// draws; a black clear would make outdoor metal reflect a black dome, which is WORSE than the stock
    /// spheremap it replaces. The area's own ambient plus half its directional is a defensible sky colour
    /// and costs one uniform. Upgrade path: the real sky, once the skybox class is owned (ADR 0011 step 3).</para></summary>
    private static Vector3 ProbeClear()
    {
        Vector3 amb = FrameInputs.HasAreaAmbient
            ? FrameInputs.AreaAmbient * AlloyLights.AmbientScale
            : new Vector3(DefaultAmbient);
        Vector3 sun = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * AlloyLights.SunScale : default;
        return amb + sun * 0.5f;
    }
}
