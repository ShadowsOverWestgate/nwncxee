using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Services.Render;

/// <summary>#173: the FRAME DISPATCH. What the owned bracket does once per pass and in what order: the prepass, the opaque block, the pass-slot walk that hands each ADR 0011 slot to its owner, and the probe capture that rides the same staged batches. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class OwnedDrawService
{
    /// <summary>The owned opaque block (hot, zero-alloc): view*proj off the matrix stacks and the mirror-FBO
    /// inputs once, then -- inside the #80 bracket -- <see cref="BuildBatches"/> revalidates every owned part
    /// and stages its draw (texture id re-fetched every frame per the borrowed-pointer rule, which is also what
    /// keeps animated textures cycling), and <see cref="IssueBatches"/> submits the result as one multidraw per
    /// (program, VDO, texture, primitive) group. Anything that fails revalidation falls straight back to the
    /// engine walk.</summary>
    private static void DrawOwned()
    {
        // #129: last pass's snapshot is not this pass's. Revoked first thing, so every path out of here that
        // never reaches PassId.FbCopy leaves owned water disarmed -- and BatchService's bucket-6 gate reads
        // the same flag, so the engine draws water itself on exactly those passes.
        FrameSnapshot.Revoke();
        if (_progFailed || _batchUnavailable) return;   // EndFrame revokes + disarms; never pay the bracket for nothing
        // Checked BEFORE anything stamps OwnGen: BuildBatches marks parts as drawn, which is only honest while
        // the issue phase cannot fail. Missing entry points are the one way it could.
        if (Gl.MultiDrawElementsBaseVertex == null || Gl.BindBufferBase == null) { _batchUnavailable = true; return; }

        int* viewport = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, viewport);
        int engFbo; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &engFbo);
        int colorType = 0, colorName = 0, depthType = 0, depthName = 0;
        if (engFbo != 0)
        {
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &colorType);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &colorName);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &depthType);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &depthName);
        }
        bool srgb = Gl.IsEnabled(Gl.FRAMEBUFFER_SRGB) != 0;
        // #151: settle the AA question with the actual FBO topology, once. The user sees "MSAA on" in the
        // game options while the owned frame draws into PostChain's single-sample RGBA16F target -- whether
        // the ENGINE's world FBO is itself multisampled here decides where anti-aliasing has to be put back.
        if (engFbo != 0 && (engFbo ^ colorName << 8 ^ depthName << 16 ^ colorType << 24 ^ depthType << 28) != _fboDiagSig && _fboDiagLeft > 0)
        {
            // re-log on CHANGE, not once: the first anchor fires on the menu/loading FBO, and the in-game
            // world FBO is the one the AA question is actually about. Capped so a ping-ponging engine
            // cannot spam the log.
            _fboDiagSig = engFbo ^ colorName << 8 ^ depthName << 16 ^ colorType << 24 ^ depthType << 28;
            _fboDiagLeft--;
            int cs = AttachmentSamples(colorType, colorName), ds = AttachmentSamples(depthType, depthName);
            int ctxSamples = 0; Gl.GetIntegerv(Gl.SAMPLES, &ctxSamples);
            _log?.Info($"OwnedDrawService: FBO topology -- engine fbo {engFbo}, colour {TypeName(colorType)} " +
                       $"{colorName} ({cs}x), depth {TypeName(depthType)} {depthName} ({ds}x), " +
                       $"GL_SAMPLES here {ctxSamples}; the owned HDR target is 1x by construction, so owned " +
                       $"passes are anti-aliased only if the engine attachment they END UP in is multisampled");
        }
        // #132: the engine's clear colour, read HERE and nowhere else. A GL share list shares objects, not
        // per-context state, so glClearColor cannot be queried from inside the 4.6 bracket -- that context
        // has its own, and PostChain's target would clear to it. This is the value the engine's own Clear(7)
        // just used at the anchor.
        float* engClear = stackalloc float[4] { 0f, 0f, 0f, 1f };
        if (Gl.GetFloatv != null) Gl.GetFloatv(Gl.COLOR_CLEAR_VALUE, engClear);
        // ...and the refusals that need no bracket at all, ahead of the preamble they would otherwise
        // waste -- PoseSkinned alone force-solves every owned skinmesh through the engine.
        if (!PostChain.Precheck(engFbo, depthName, viewport)) return;
        // Only now: posing force-solves every owned skinmesh through the engine, and a pass that is about
        // to hand the frame back would have the engine solve each of them a second time on its own walk.
        // Still ENGINE context, which is what PartSkin::Draw needs.
        PoseSkinned();

        // row-major v*M convention: clip = v * Model * View * Proj; raw bytes read column-major by GLSL are
        // the transpose, so uVP * d.model * vec4(aPos,1) is exact -- (M*V*P)^T = (V*P)^T * M^T, which is why
        // #84 can hoist view*proj to a per-program uniform and leave only the model matrix per draw.
        _sceneEnv = _enrollScene != IntPtr.Zero && *(IntPtr*)(_enrollScene + SceneEnvMap) != IntPtr.Zero;

        Matrix4x4 view = *(Matrix4x4*)(_mtxStacks + ModeView * StackStride + StackTop);
        Matrix4x4 proj = *(Matrix4x4*)(_mtxStacks + ModeProjection * StackStride + StackTop);
        Matrix4x4 vp = view * proj;
        // every froxel input comes off THIS pass's projection, never the capture side (see TryPerspective)
        bool perspective = OwnedLights.TryPerspective((float*)&proj, out float p00, out float p11,
                                                      out float near, out float far);
        // The scene camera, for the shadow pass (#99). Same row-vector convention as above, so the inverse
        // view's translation IS the eye and view-space -Z rotated by it is the look direction. Gated on
        // TryPerspective, which an ortho/UI pass fails outright.
        if (perspective && Matrix4x4.Invert(view, out Matrix4x4 camToWorld))
        {
            _eyePos = camToWorld.Translation;   // #120: this pass's eye, for the specular view vector
            _lodScale = proj.M22 * viewport[3] * 0.5f;
            FrameInputs.LatchSceneCam(camToWorld.Translation,
                Vector3.Normalize(Vector3.TransformNormal(new Vector3(0f, 0f, -1f), camToWorld)),
                p11, viewport[2] * viewport[3], _frameSeq);
        }
        // Cap 5 is the engine's own lit predicate (Ghidra MatStatic @0x450560 / RenderFlat @0x450eb0): with
        // it off every part gets flat white, so the flat variant IS vanilla for this pass.
        bool lightingOn = _litCount > 0 && _lighting && !_litFailed && perspective
                          && (_isEnabled == null || _isEnabled(CapLighting) != 0);

        if (!GlContextService.Enter()) return;
        if (EnsureProgram() && BindTarget(engFbo, colorType, colorName, depthType, depthName))
        {
            // #117/#119: the lighting rig, the GL state and the batch BUILD are bracket preamble -- both
            // colour-bracket passes draw the same staged batches, and building twice would revalidate (and
            // re-stamp OwnGen for) every owned part twice. The passes come off FrameGraph, not off call order.
            if (lightingOn)
            {
                OwnedLights.Upload(_frameSeq);
                // The cluster buffers are only meaningful once THIS pass's build has run: a skipped build
                // leaves them holding another camera's binning (or, before the first dispatch, undefined
                // BufferData contents), and a lit part would then sample arbitrary light indices.
                lightingOn = OwnedLights.Build((float*)&view, p00, p11, near, far)
                             && _progLit46 != 0;
            }
            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
            Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
            Gl.Disable(Gl.BLEND); _blendOn = false;
            Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.ActiveTexture(Gl.TEXTURE0);
            _lastPixels = viewport[2] * viewport[3];
            // #129: built HERE, not at the pass, because staging has to know. A water part staged for a pass
            // whose program never built would be stamped as owned (and so suppressed in the engine's walk)
            // and then never drawn -- so the decision is made before BuildBatches, which skips the class
            // outright when it is false.
            // ...and on the LIGHT RIG, for a reason the class check cannot supply: the water stage calls
            // SowxClusteredLight unconditionally, and the cluster buffers are only meaningful once THIS
            // pass's build has run. BuildBatches skips lit batches on !lightingOn, but a water batch is
            // keyed on its class rather than on _gLit, so without this term SOWX_OWNLIGHT=0 (or a failed
            // lit program, or a build that refused) would leave the water program reading undefined
            // BufferData contents -- an unbounded per-fragment light loop, not a dim sea.
            _waterArmed = FrameSnapshot.Usable && lightingOn && EnsureWaterProgram();
            _waterDrew = false;
            // #139/#132: from here to PassId.PostProcess the colour passes draw into an RGBA16F target that
            // shares the engine's depth, and everything they write is LINEAR. Asked BEFORE anything is
            // staged, because a refusal is no longer survivable: there is no display-space variant of the
            // owned stages to fall back to, so the whole pass stands down and the engine renders it vanilla.
            // Staging first would stamp OwnGen on parts the iterator then suppresses and nobody draws.
            if (!PostChain.Begin(_fbo46, depthType, depthName, viewport, engClear))
            { GlContextService.Leave(); return; }
            int groups = BuildBatches(lightingOn);
            if (groups > 0)
            {
                StageSky();
                UploadDraws();
                UploadBones();
                foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
                {
                    if (bracket != Bracket.Colour) continue;
                    switch (id)
                    {
                        case PassId.DepthPrepass:
                            DepthPrepassPass(_gOpaqueEnd, &vp, viewport, depthType, depthName);
                            break;
                        case PassId.OpaqueOwned:
                            OpaqueOwnedPass(_gOpaqueEnd, &vp, viewport, near, far);
                            // The GPU-side cluster stats belong to the SCREEN's build. Harvested here rather
                            // than after the bracket because the probe capture rebuilds the clusters once per
                            // face, and reading afterwards would report cube face 6's overflow as the frame's.
                            if (_takeOverflow)
                            { _takeOverflow = false; _clusterOverflow += OwnedLights.TakeClusterOverflow(); }
                            break;
                        case PassId.GrassOwned:
                            bool tg = _qSkyGrass.Begin(Gl.TIME_ELAPSED);
                            GrassField.Draw(&vp, viewport, near, far, lightingOn);
                            if (tg) _qSkyGrass.End(Gl.TIME_ELAPSED);
                            // Grass draws unblended by construction and toggles GL_BLEND itself; hand the
                            // tracked state back exactly as it was, or the next pass's own bookkeeping
                            // (IssueBatches reads _blendOn as "what is already set") starts from a lie.
                            if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
                            break;
                        case PassId.SkyOwned:
                            SkyOwnedPass(&vp, viewport);
                            break;
                        case PassId.BlendedOwned:
                            TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far);
                            break;
                        case PassId.FbCopy:
                            _waterTiming = _qWater.Begin(Gl.TIME_ELAPSED);
                            // Only when there is water to read it. The copy is a full-screen blit of colour
                            // AND depth -- cheap, but not free, and every frame without a water surface on
                            // screen would be paying for a texture nothing samples.
                            if (_gWaterEnd > _gBlendEnd && !_capturing)
                            {
                                // The blended class left GL_BLEND on and the depth mask off; the blit does
                                // not care, but leaving the tracked state honest does -- IssueBatches below
                                // reads _blendOn as "what is already set".
                                FrameSnapshot.Copy(viewport);
                            }
                            break;
                        case PassId.WaterOwned:
                            WaterOwnedPass(_gBlendEnd, _gWaterEnd, &vp, (float*)&view, (float*)&proj,
                                           viewport, near, far);
                            if (_waterTiming) { _qWater.End(Gl.TIME_ELAPSED); _waterTiming = false; }
                            break;
                        case PassId.AdditiveOwned:
                            TransparentPass(_gWaterEnd, groups, &vp, viewport, near, far);
                            if (_colourTiming) { _qColour.End(Gl.TIME_ELAPSED); _colourTiming = false; }
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
                            bool tp = _qParticles.Begin(Gl.TIME_ELAPSED);
                            Particles.Draw(&view, &proj, viewport, near, far, lightingOn);
                            if (tp) _qParticles.End(Gl.TIME_ELAPSED);
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
                            bool tb = _qPost.Begin(Gl.TIME_ELAPSED);
                            // Bloom runs against the HDR target and binds its own attachments; the engine's
                            // target has to come back before the composite writes into it, and with it the
                            // viewport and the sRGB-encode state the pass was entered with.
                            PostChain.Bloom();
                            BindTarget(engFbo, colorType, colorName, depthType, depthName);
                            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                            PostChain.Composite(viewport);
                            if (tb) _qPost.End(Gl.TIME_ELAPSED);
                            break;
                    }
                }
            }
            Gl.BindVertexArray(0);
        }
        GlContextService.Leave();
    }

    /// <summary>ADR 0011 step 1 (#119): the owned opaque set again, depth-only and colour-masked, so early-Z
    /// throws away the fragments the shading pass would otherwise light and then overwrite. Every owned part
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
    private static void DepthPrepassPass(int groups, Matrix4x4* vp, int* viewport, int depthType, int depthName)
    {
        if (!_depthPrepass || !EnsureDepthProgram()) return;
        int skipped = 0, drawn = 0;
        bool timing = _qPrepass.Begin(Gl.TIME_ELAPSED);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.DepthFunc(Gl.LESS);
        Gl.UseProgram(_progDepth);
        Gl.UniformMatrix4fv(_depthVp, 1, 0, (float*)vp);
        // #124: dissolving batches run the keyhole stage instead, so they stay IN the prepass. Groups are
        // already keyed on the dissolve bit, so this switches at most once per pass in practice; `keyed`
        // tracks which program is current so the plain batches do not re-bind theirs on every alternation.
        bool keyed = false;
        int boundVao = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = 0; g < groups; g++)
        {
            if (ShaderFeatures.OutOfPrepass(_gFeat[g])) { skipped += _gLen[g]; continue; }
            // #126: a cutout discards, so its prepass depth would be its SOLID silhouette and the colour
            // pass's LEQUAL would cull whatever stands in its holes -- grass turning the ground behind it
            // into a rectangle. ADR 0011 leaves cutouts-in-the-prepass a MEASUREMENT question (they cost a
            // texture fetch per fragment to save shading later); this is the measurement's off side, and
            // _prepassSkipped is what says how much overdraw it leaves on the table.
            if (_gCls[g] == (byte)OwnedClass.Cutout) { skipped += _gLen[g]; continue; }
            bool dis = _gDis[g];
            if (dis && _progDepthKey == 0) { skipped += _gLen[g]; continue; }
            if (dis != keyed)
            {
                keyed = dis;
                Gl.UseProgram(dis ? _progDepthKey : _progDepth);
                Gl.UniformMatrix4fv(dis ? _depthKeyVp : _depthVp, 1, 0, (float*)vp);
                if (dis) UploadFeatureTerms(_depthKeyFeat, viewport);
            }
            int s = _gStart[g];
            if (keyed) _prepassKeyed += _gLen[g];
            Gl.Uniform1i(keyed ? _depthKeyBase : _depthBase, s);
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(_vVao46[_gVao[g]]); boundVao = _gVao[g]; }
            Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                           (void**)(offs + s), _gLen[g], bases + s);
            drawn += _gLen[g];
        }
        Gl.ColorMask(1, 1, 1, 1);
        if (timing) _qPrepass.End(Gl.TIME_ELAPSED);
        // Counted from what this loop submitted, not from the pass's total draw count: since #126 that total
        // spans the blending classes too, and the prepass walks the opaque range alone.
        _prepassDraws += drawn;
        _prepassSkipped += skipped;
        DepthResource.Publish(depthType, depthName, viewport, PassGen);
    }

    /// <summary>ADR 0011 step 2, the owned opaque set: submit the staged batches, shaded.
    /// <para>LEQUAL rather than EQUAL against the prepass depth. All three owned vertex stages declare
    /// <c>invariant gl_Position</c> over the identical clip expression (<see cref="ClipExpr"/>), so equal is
    /// what they produce -- but LEQUAL is also correct when the prepass stood down, which EQUAL is not, and
    /// early-Z rejects the hidden fragments either way.</para></summary>
    private static void OpaqueOwnedPass(int groups, Matrix4x4* vp, int* viewport, float near, float far)
    {
        Gl.DepthFunc(Gl.LEQUAL);
        // SAMPLES_PASSED covers the OPAQUE set alone, so the overdraw figure stays comparable with #119's
        // baseline; the colour timer is ended after the blending classes (see DrawOwned), because "what does
        // the owned colour work cost" is the question the [owned] line exists to answer.
        bool counting = _qSamples.Begin(Gl.SAMPLES_PASSED);
        _colourTiming = _qColour.Begin(Gl.TIME_ELAPSED);
        IssueBatches(0, groups, (float*)vp, viewport, near, far);
        if (counting) _qSamples.End(Gl.SAMPLES_PASSED);
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
        IntPtr bsp = _enrollScene != IntPtr.Zero ? *(IntPtr*)(_enrollScene + SceneLayout.SceneBspRoot) : IntPtr.Zero;
        if (!EnvProbe.Wanted(_eyePos, bsp, _frameSeq)) return;
        if (!EnvProbe.Begin(_eyePos, bsp, _frameSeq, far, ProbeClear())) return;

        long draws = _draws, batches = _batches;
        float buildMs = OwnedLights.LastBuildMs;   // the [owned] line's cluster timer is the SCREEN's build
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
            OwnedLights.Build((float*)&view, 1f, 1f, fnear, ffar);
            IssueBatches(0, groups, (float*)&vp, faceVp, fnear, ffar);
        }
        if (ok) EnvProbe.End(); else Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _capturing = false;
        _eyePos = eye;
        _draws = draws; _batches = batches;
        OwnedLights.LastBuildMs = buildMs;
    }

    /// <summary>What the probe's faces are cleared to -- the sky stand-in.
    /// <para>ponytail: the skybox is still engine-drawn, so it is absent from the owned set the capture
    /// draws; a black clear would make outdoor metal reflect a black dome, which is WORSE than the stock
    /// spheremap it replaces. The area's own ambient plus half its directional is a defensible sky colour
    /// and costs one uniform. Upgrade path: the real sky, once the skybox class is owned (ADR 0011 step 3).</para></summary>
    private static Vector3 ProbeClear()
    {
        Vector3 amb = FrameInputs.HasAreaAmbient
            ? FrameInputs.AreaAmbient * OwnedLights.AmbientScale
            : new Vector3(DefaultAmbient);
        Vector3 sun = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * OwnedLights.SunScale : default;
        return amb + sun * 0.5f;
    }
}
