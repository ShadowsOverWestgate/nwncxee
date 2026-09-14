using CX.Framework;

namespace CX.Alloy;

/// <summary>#208: the texture-unit contract, written down once. Which sampler lives on which GL texture unit
/// is shared between this renderer and the <c>cx_*.shd</c> stages a content repo may override, and until now
/// it lived as a scatter of <c>const uint</c> beside whichever pass happened to bind it. That is the shape of
/// contract that drifts: a unit collision is not a compile error and not a log line -- GL forbids two
/// samplers of DIFFERENT types on one image unit, so the draw raises INVALID_OPERATION and simply vanishes,
/// and two samplers of the same type just read each other's texture.
/// <para>Numbers only. There is no allocator and no reservation API here: units are a fixed, tiny, hand-read
/// set, and a manager would only move the decision somewhere a reader has to go looking for it.</para>
/// <para><see cref="Check"/> asserts the table against the sampler declarations in the EMBEDDED stage text
/// and names the slot that disagreed, gating arming through <see cref="ShaderContract.SelfCheck"/>. It sees
/// only that text -- exactly the limit <see cref="ShaderContract.RecordSafe"/> is documented as having. A
/// content override (#92) that renames a sampler still links, still binds a -1 location, and still passes
/// here. What catches THAT is <see cref="ShaderContract.SlotSafe"/>, applied at each program's build to the
/// source that actually compiled -- the two are halves of one guard, and neither covers the other's
/// case.</para></summary>
internal static class TextureSlots
{
    /// <summary>Unit 0, the material diffuse -- and the unit every pass leaves current, because the colour
    /// loop rebinds diffuse textures there thousands of times a frame.</summary>
    internal const uint Diffuse = 0;
    /// <summary>The sun cascade array (<c>sampler2DArrayShadow</c>) and the local-light cube array
    /// (<c>samplerCubeArrayShadow</c>). Both are bound once at program link, not per frame: a sampler left at
    /// its default points at unit 0 alongside the diffuse, which is the type collision above.</summary>
    internal const uint SunShadow = 1, LocalShadow = 2;
    /// <summary>The material map block: <see cref="MapCount"/> consecutive units from here, in
    /// <see cref="MapSamplers"/> order. #146 put AO last on this ladder rather than taking the frozen env
    /// bit.</summary>
    internal const uint MapBase = 3;
    /// <summary>How many units <see cref="MapBase"/> spans. Growing it walks into <see cref="Probe"/>.</summary>
    internal const int MapCount = 7;
    /// <summary>The post chain's two inputs at composite time... and, on the GRASS program, unit 10 is the
    /// displacement field instead. Deliberate and safe: they are different programs that never run in the
    /// same bracket, so the table records the reuse rather than burning a unit to avoid it.</summary>
    internal const uint PostHdr = 10, PostBloom = 11, GrassPush = 10;
    /// <summary>#305: the dynamic-pool cube array (<c>samplerCubeArrayShadow</c>), sampled by every program
    /// that samples <see cref="LocalShadow"/>. Shares <see cref="PostBloom"/>'s unit on the same argument as
    /// <see cref="GrassPush"/>: the post resolve is a different program that never runs in the lit/water/
    /// grass/particle brackets, and 0..15 is the whole budget. Bound at each program's link, because a
    /// samplerCubeArrayShadow left at its default unit 0 collides with the diffuse's sampler2D -- an
    /// INVALID_OPERATION that silently drops every draw.</summary>
    internal const uint LocalShadowDyn = PostBloom;
    /// <summary>#306: the sun's dynamic cascade array (<c>sampler2DArrayShadow</c>), sampled by every program
    /// that samples <see cref="SunShadow"/>. Shares <see cref="PostHdr"/>'s unit on
    /// <see cref="LocalShadowDyn"/>'s argument -- except on the GRASS program, where unit 10 is the
    /// displacement field, so grass binds it to <see cref="SnapshotColor"/>'s instead (free there: grass
    /// samples no frame snapshot). <see cref="AlloyRenderer.UploadSunCascades"/> binds the array on BOTH
    /// units, which is two cheap binds against burning a unit the budget does not have.</summary>
    internal const uint SunShadowDyn = PostHdr, SunShadowDynGrass = SnapshotColor;
    /// <summary>The environment probe cube -- above the whole map block for the type-collision reason.</summary>
    internal const uint Probe = 12;
    /// <summary>The aa stage's own passes, post stack only -- after the world pass, so the probe and snapshot
    /// units are free on the same argument as <see cref="PostHdr"/>: edges then weights on 12, the area
    /// table on 13, the search table on 14.</summary>
    internal const uint AaEdges = 12, AaBlend = 12, AaArea = 13, AaSearch = 14;
    /// <summary>#339: the SCENE's environment map, which vanilla's water reflects. Shares
    /// <see cref="Probe"/>'s unit -- the water program stopped sampling the probe when the port replaced
    /// Alloy's own reflection with stock's, so the two are never live in one program. A sampler2D here and
    /// a samplerCube there, which is why it may not move onto a unit the lit program also binds.</summary>
    internal const uint WaterEnv = Probe;
    /// <summary>The frame snapshot the water stage refracts through: colour then depth.</summary>
    internal const uint SnapshotColor = 13, SnapshotDepth = 14;
    /// <summary>The OIT resolve's accumulation pair. The snapshot's units: the resolve runs before the water
    /// copy is taken, and FrameSnapshot.Bind re-binds its own textures before any water draw.</summary>
    internal const uint OitAccum = SnapshotColor, OitReveal = SnapshotDepth;
    /// <summary>#490: the opaque depth the linked-list append tests against, bound for the accumulate only --
    /// the reveal unit, which is free until the resolve rebinds it.</summary>
    internal const uint OitDepth = OitReveal;
    /// <summary>The frame copy the grass root blend reads. The last material-map unit (<see cref="MapBase"/>+6):
    /// the grass program binds no material maps, and every unit from <see cref="PostHdr"/> up is taken in
    /// that program by the light rig (<see cref="LocalShadowDyn"/> on 11 was the first attempt, and the blade
    /// sampled a shadow cube).</summary>
    internal const uint GrassGround = MapBase + 6;
    /// <summary>#379: the screen-space visibility the lit stage reads. Shares <see cref="SnapshotColor"/>'s
    /// unit: the snapshot takes it at FbCopy and lit additive draws follow, so the lit program REBINDS it at
    /// every program bind (<see cref="SsaoPass.BindForDraw"/>) rather than trusting the pass's own bind.
    /// Both sampler2D; the grass program's dyn-sun array on the same unit is a different program.</summary>
    internal const uint Ssao = SnapshotColor;
    /// <summary>The scene depth the soft-particle fade reads.</summary>
    internal const uint SceneDepth = 15;
    /// <summary>#379 follow-up: the SAME prepass depth, on the same unit, read by the LIT stage -- so a
    /// fragment can ask whether the visibility at its pixel was computed from ITS OWN surface. Under #119 the
    /// prepass and the colour pass compute gl_Position character-for-character, so a prepassed fragment's
    /// gl_FragCoord.z equals the buffer exactly and anything else is another surface's occlusion. Free on
    /// this unit in the lit bracket: the particle fade and the water noise are later programs.</summary>
    internal const uint SsaoDepth = SceneDepth;
    /// <summary>#339: the engine's <c>solid_noise</c>, which vanilla's water shader samples as
    /// <c>texUnitNoise</c>. Shares <see cref="SceneDepth"/>'s unit on <see cref="GrassPush"/>'s argument --
    /// the particle pass is a different program in a later bracket, and both are sampler2D so even a missed
    /// rebind reads a wrong texture rather than dropping the draw.
    /// <para>NOT on the map block, and this is the whole reason the unit is written down: the colour loop's
    /// slot binds run for any batch that is not a merged bindless lit one, and a water batch is never one --
    /// so units 3..9 are overwritten with texture 0 on the first water draw. The first cut put the noise on
    /// unit 3 and got a perfectly flat sea: every noise read returned 0, which drives the wave amplitude to
    /// exactly zero.</para></summary>
    internal const uint WaterNoise = SceneDepth;
    /// <summary>#243: the hi-Z pyramid, read by the CULL DISPATCH only. Shares <see cref="Probe"/>'s unit
    /// for <see cref="PostHdr"/>'s reason and one more: the dispatch is a compute program that runs BETWEEN
    /// two draw passes and samples nothing else, and every draw program rebinds its own probe at the program
    /// switch that opens each pass. The dispatch still leaves texture 0 on the unit rather than its pyramid,
    /// so a consumer that somehow did not rebind samples nothing instead of a 2D texture through a cube
    /// sampler. 0..15 is the whole budget this renderer allows itself (GL guarantees only 16 per stage), so
    /// a seventeenth unit is not available to spend here.</summary>
    internal const uint HiZ = Probe;

    /// <summary>The map samplers, in unit order from <see cref="MapBase"/>. This array IS the bind order the
    /// lit program's link-time <c>glUniform1i</c> loop walks -- there is no second copy of it.</summary>
    internal static readonly string[] MapSamplers =
        { "uNrmTex", "uSpecTex", "uRoughTex", "uHeightTex", "uIllumTex", "uFlowTex", "uAoTex" };

    /// <summary>Assert the table against the GLSL that actually compiled: every slot's sampler must be
    /// declared by the stage that reads it, and the units must not overlap. False = do not arm -- a slot
    /// disagreement renders as dropped draws or a diffuse sampled as a normal map, neither of which says
    /// anything at pixel time, so the honest failure is to stand down and name the slot.</summary>
    internal static bool Check(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string slot) { if (!c) { ok = false; log.Info($"TextureSlots: slot disagrees -- {slot}"); } }

        // Each unit against the sampler declaration in the stage that consumes it -- the copy that is
        // independent of the consts above, which now all alias this table. Rename or drop a declaration and
        // glUniform1i binds a -1 location: a legal no-op that leaves the sampler on unit 0.
        E(AlloyRenderer.OwnedFs.Contains("uniform sampler2D uTex;")
          && AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uTex;")
          && GrassField.GrassFsHead.Contains("uniform sampler2D uTex;")
          && Particles.ParticleFsHead.Contains("uniform sampler2D uTex;"), $"{Diffuse} uTex (diffuse)");
        E(AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunShadow;"),
          $"{SunShadow} uSunShadow");
        E(AlloyLights.LocalShadowLib.Contains("uniform samplerCubeArrayShadow uLocalShadow;"),
          $"{LocalShadow} uLocalShadow");
        E(AlloyLights.LocalShadowLib.Contains("uniform samplerCubeArrayShadow uLocalDynShadow;"),
          $"{LocalShadowDyn} uLocalDynShadow");
        E(AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunDynShadow;"),
          $"{SunShadowDyn} uSunDynShadow");
        for (int i = 0; i < MapSamplers.Length; i++)
            E(AlloyRenderer.OwnedLitFsHead.Contains($"uniform sampler2D {MapSamplers[i]};"),
              $"{MapBase + i} {MapSamplers[i]}");
        E(PostChain.ResolveFs.Contains("uniform sampler2D uHdr;"), $"{PostHdr} uHdr");
        E(PostChain.ResolveFs.Contains("uniform sampler2D uBloom;"), $"{PostBloom} uBloom");
        // #375: the post stack reuses the resolve's two units, after it and in the same bracket.
        E(PostStack.PostStageHead.Contains("uniform sampler2D uSrc;"), $"{PostHdr} uSrc (post stack)");
        E(PostStack.PostStageHead.Contains("uniform sampler2D uDepth;"), $"{PostBloom} uDepth (post stack)");
        E(SmaaStage.WeightFs.Contains("uniform sampler2D uEdges;"), $"{AaEdges} uEdges (aa weights)");
        E(SmaaStage.WeightFs.Contains("uniform sampler2D uArea;"), $"{AaArea} uArea (aa weights)");
        E(SmaaStage.WeightFs.Contains("uniform sampler2D uSearch;"), $"{AaSearch} uSearch (aa weights)");
        E(SmaaStage.BlendStageFs.Contains("uniform sampler2D uBlend;"), $"{AaBlend} uBlend (aa stage)");
        E(AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uSsao;"), $"{Ssao} uSsao (lit program)");
        E(AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uSceneDepth;"), $"{SsaoDepth} uSceneDepth (lit program)");
        E(GrassField.GrassVs.Contains("uniform sampler2D uPushTex;"), $"{GrassPush} uPushTex (grass program)");
        E(GrassField.GrassFsHead.Contains("uniform sampler2D uGround;"), $"{GrassGround} uGround (grass program)");
        // #339: the water stage no longer samples the probe -- vanilla's reflection falls back to the
        // shaded surface, not to a cube -- so the lit stage is the only consumer left.
        E(AlloyRenderer.OwnedLitFsHead.Contains("uniform samplerCube uProbe;"), $"{Probe} uProbe");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uNoise;"), $"{WaterNoise} uNoise (water program)");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uEnvMap;"), $"{WaterEnv} uEnvMap (water program)");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbColor;"), $"{SnapshotColor} uFbColor");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbDepth;"), $"{SnapshotDepth} uFbDepth");
        E(AlloyRenderer.OitOutFs.Contains("uniform sampler2D uOitDepth;"), $"{OitDepth} uOitDepth (blended output)");
        E(AlloyRenderer.OitOutFs.Contains("uniform ivec2 uOitDim;") && AlloyRenderer.OitOutFs.Contains("uniform float uOitBias;") && AlloyRenderer.OitOutFs.Contains("uniform int uOitGob;"), "uOitDim/uOitBias/uOitGob are what OitPass.Upload and IssueColour write");
        E(Particles.ParticleFsHead.Contains("uniform sampler2D uSceneDepth;"), $"{SceneDepth} uSceneDepth");

        // ...and the units themselves. MapSamplers is what the lit program's bind loop walks, so a name added
        // there without a unit to put it on would silently land on the probe's.
        E(MapSamplers.Length == MapCount, $"MapSamplers has {MapSamplers.Length} names for {MapCount} units");
        E(MapBase > LocalShadow && MapBase + MapCount <= PostHdr, $"{MapBase}..{MapBase + MapCount - 1} map block overlaps its neighbours");
        E(PostHdr < PostBloom && PostBloom < Probe && Probe < SnapshotColor
          && SnapshotColor < SnapshotDepth && SnapshotDepth < SceneDepth, "the units above the map block are not distinct and ascending");
        return ok;
    }
}
