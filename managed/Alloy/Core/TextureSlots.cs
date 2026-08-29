using Sowx.Framework;

namespace Sowx.Alloy;

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
    /// <summary>The environment probe cube -- above the whole map block for the type-collision reason.</summary>
    internal const uint Probe = 12;
    /// <summary>The frame snapshot the water stage refracts through: colour then depth.</summary>
    internal const uint SnapshotColor = 13, SnapshotDepth = 14;
    /// <summary>The scene depth the soft-particle fade reads.</summary>
    internal const uint SceneDepth = 15;
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
        for (int i = 0; i < MapSamplers.Length; i++)
            E(AlloyRenderer.OwnedLitFsHead.Contains($"uniform sampler2D {MapSamplers[i]};"),
              $"{MapBase + i} {MapSamplers[i]}");
        E(PostChain.ResolveFs.Contains("uniform sampler2D uHdr;"), $"{PostHdr} uHdr");
        E(PostChain.ResolveFs.Contains("uniform sampler2D uBloom;"), $"{PostBloom} uBloom");
        E(GrassField.GrassVs.Contains("uniform sampler2D uPushTex;"), $"{GrassPush} uPushTex (grass program)");
        E(AlloyRenderer.OwnedLitFsHead.Contains("uniform samplerCube uProbe;")
          && FrameSnapshot.FsHead.Contains("uniform samplerCube uProbe;"), $"{Probe} uProbe");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbColor;"), $"{SnapshotColor} uFbColor");
        E(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbDepth;"), $"{SnapshotDepth} uFbDepth");
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
