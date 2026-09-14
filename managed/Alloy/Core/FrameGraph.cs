using CX.Framework;

namespace CX.Alloy;

/// <summary>The GL brackets (#80 context enter/leave) the Alloy frame is allowed per frame. ADR 0011 costs
/// the M3 graph at exactly two, so a third one appearing is a design change, not an implementation detail.
/// Named by role, not by anchor: the M3 exit step moves where each bracket hangs off the engine.</summary>
internal enum Bracket
{
    /// <summary>World casters: sun cascades + local light cubes. <see cref="CasterPass.OnPassSlot"/>.</summary>
    Shadow,
    /// <summary>The Alloy colour pass, at #132's frame anchor -- <c>Scene::FadeDoors</c>, after-original.</summary>
    Colour,
}

/// <summary>One pass of the Alloy frame. Declaration order IS ADR 0011's graph order -- new M3 passes are
/// inserted at their graph position, never appended for convenience.</summary>
internal enum PassId
{
    SunCascades,
    LocalLightCubes,
    DepthPrepass,
    /// <summary>#379: screen-space ambient occlusion from the prepass depth, BEFORE the colour pass so the lit
    /// stage reads it as a lighting input (its ambient term) rather than a post stage darkening the finished
    /// frame the way vanilla's FB effect does (ADR 0021).</summary>
    AmbientOcclusion,
    OpaqueOwned,
    /// <summary>#125: the per-area skybox, replayed from <see cref="SkyBox"/>'s capture of the engine's own
    /// draw. Here because ADR 0011 puts it AFTER the opaque set -- so the prepass depth rejects every sky
    /// pixel the world covered before it shades -- and BEFORE the BLENDING classes, cutout
    /// included (#357), so anything seen against the sky composites over a finished one. Vanilla draws it
    /// between buckets 0 and 1, which gets the second half right and the first wrong.</summary>
    SkyOwned,
    /// <summary>#357: the cutout class -- the alpha discard, depth writes ON, no blending and no sort, which
    /// is the whole of its order-independence (<see cref="AlloyClasses.Blends"/> carries why the blend went
    /// away). Split out of <see cref="OpaqueOwned"/> to sit where the engine draws bucket 1, immediately
    /// after <c>RenderSkyBoxes</c>; unblended it would be correct on either side of the sky, and this side
    /// costs only the sky pixels the foliage then covers.</summary>
    CutoutOwned,
    /// <summary>#127: Alloy grass -- GPU-instanced blades over the engine's own bin geometry, replacing
    /// <c>Scene::DoBinBucket</c> outright. A cutout class: alpha discard, depth write ON, GL_BLEND OFF, so
    /// no grass fragment is ever partially transparent AND depth-writing and #106's punch-through cannot
    /// occur. Draws after the opaque set (early-Z has the world's depth in hand) and after the cutout class:
    /// its root blend reads a copy of the frame under each blade, and a ground tile is a cutout when its
    /// texture carries alpha -- taken before that pass, the copy under those roots held the clear colour.
    /// BEFORE the blended class, because <see cref="OitPass"/> writes no depth: grass drawn after it would
    /// paint over every translucent object standing in it. The cost is a tile fading under a close camera
    /// (a blended part while it fades), whose root copy now misses the fade.</summary>
    GrassOwned,
    /// <summary>#126: the blended class -- genuine alpha, sorted back-to-front per part, depth writes off.
    /// Sits here because ADR 0011 puts blended after the opaque set and BEFORE the framebuffer copy the
    /// FB-sampling and water classes will read (#128): fade is a blended-class member, and moving it ahead
    /// of the copy is what lets one copy serve both classes instead of the engine's two.</summary>
    BlendedOwned,
    /// <summary>#129: the ONE framebuffer copy of the Alloy frame -- colour and depth of everything drawn so
    /// far, snapshotted into <see cref="FrameSnapshot"/>'s own pair of textures so the water stage can march them.
    /// <para>Here, and exactly once, because of what is on either side. Behind it: the opaque set, the sky
    /// and the blended class, fade included -- so water loses nothing the engine's own pre-water copy would
    /// have given it. Ahead of it: additive. That is vanilla's placement and it is deliberate, not an
    /// oversight to fix -- additive does not write depth and water does, so drawing the effects first would
    /// mean water paints over any torch glow standing in front of it. You would gain a reflection of the
    /// glow and lose the glow. A second copy taken after the effects is circular.</para>
    /// <para>The engine takes TWO snapshots per frame (<c>Camera::CopyRenderSubframe</c> before bucket 5 and
    /// again before bucket 6, <c>Scene::RenderSinglePass</c> @0x46e5f0) because its bucket order is fixed,
    /// not because two are needed. The Alloy frame takes one; the engine's water copy disappears with the
    /// bucket-6 walk it belongs to (<see cref="BucketGate"/>'s <c>HasItemsOfType</c> gate). The
    /// bucket-5 copy stays the engine's until the framebuffer-sampling class is owned.</para></summary>
    FbCopy,
    /// <summary>#129: water, drawn from the copy above -- screen-space refraction and reflection with the
    /// #121 probe standing in wherever the march misses. Depth writes ON.</summary>
    WaterOwned,
    /// <summary>#126: additive last of the Alloy classes, as it is last of the engine's buckets. Unsorted --
    /// addition commutes -- and no depth write.</summary>
    AdditiveOwned,
    /// <summary>#130: the volumetric light beams -- bucket 15, which the engine's own bucket walk never
    /// touches, replayed from <see cref="Beams"/>'s capture of <c>Scene::RenderBeams</c>. Here because that
    /// is vanilla's own placement: step 27 of <c>Scene::RenderSinglePass</c>, immediately after the additive
    /// bucket and before the particles. It is an additive class in every respect -- (SRC_ALPHA, ONE), no
    /// depth write, no sort needed -- and only sits in its own pass rather than in
    /// <see cref="AdditiveOwned"/> because its geometry is extruded by a program of its own.</summary>
    BeamsOwned,
    /// <summary>#131: particles -- <c>Scene::DoEmitterBucket</c>'s emitters, rebuilt rather than replayed.
    /// Last of the Alloy content classes, matching vanilla's own step 28. The engine keeps spawn, movement
    /// and life; what is owned is the vertex build, which is the only part of an emitter that was ever a
    /// draw. Three sub-orders inside the one pass -- punch-through first (it writes depth, so the others can
    /// test against it), then the alpha-blended remainder in the engine's own back-to-front order, then
    /// additive, batched by texture across the screen. <see cref="Particles"/> spells out which of those two
    /// reorderings is free and which one is visible.
    /// <para><b>Position in the list is not submission order against the ENGINE.</b> The whole Alloy bracket
    /// runs at the #132 frame anchor, so Alloy particles are submitted before everything the engine still draws
    /// in the rest of <c>RenderSinglePass</c> -- step 28 included. That matters here and nowhere else in
    /// this list, because this is the only class that declines PER ITEM: a fire whose smoke layer is owned
    /// and whose flame layer is declined (custom material shader, no texture, fading tile) has the declined
    /// layer composite over the Alloy one unconditionally, whichever is actually nearer. It is the price of
    /// the per-emitter fallback, and it is worth paying -- the alternative is an all-or-none rule that hands
    /// back a whole screen of effects because one of them named a custom shader.</para></summary>
    ParticlesOwned,
    /// <summary>Procedural light halos (<see cref="Halos"/>): vanilla's lens flares, step 29, right after the
    /// particles. Additive, no depth write; occlusion is the shader's own depth read.</summary>
    HalosOwned,
    /// <summary>#121: the per-area IBL probe capture. Last in the colour bracket by necessity -- it draws
    /// the same staged batches six more times and rebuilds the light clusters per face, so it has to run
    /// after every on-screen pass that reads them. Skipped on all but the handful of frames
    /// <see cref="EnvProbe.Wanted"/> admits.</summary>
    ProbeCapture,
    /// <summary>#139/#132: resolve the HDR colour target -- bloom, tonemap, and the frame's ONE encode --
    /// into the engine's own attachment. Last because it consumes the whole frame's colour, and after the
    /// probe capture rather than before it for the same reason the capture is where it is: the capture draws
    /// the staged batches again into its own framebuffer, so it is not part of the image being resolved and
    /// must not be resolved into. Since #132 it cannot stand down on its own: every Alloy stage writes
    /// linear, so a pass PostChain refuses to arm has no Alloy frame at all -- the whole pass falls back to
    /// vanilla before anything is staged.</summary>
    PostProcess,
    /// <summary>#375: the display-space post stack -- the client's video options the engine ran as
    /// <c>Camera::DoFrameBufferEffects</c> (gamma first; #352's hosted chain plugs in here) over the resolved
    /// frame, last stage into the engine's target. After the resolve because it consumes the resolve's
    /// output; its stand-down is per stage and never reaches the frame: an empty stack is the resolve
    /// writing straight to the engine, exactly as before it existed.</summary>
    PostStack,
}

/// <summary>
/// #117 (map #68, spec #114 M3): the Alloy frame's pass list, declared once instead of implied by the order
/// statements happen to appear in. Each bracket site walks this and switches on the id, so adding an M3 pass
/// is one entry here plus one arm there -- and an ordering change is a diff in this file rather than a
/// rendering bug nobody can bisect.
/// <para>Since #118 this is wall-clock order too: the shadow bracket fires at the pass slot
/// (<c>Scene::AddPartsToDrawBuckets</c>, after-original), ahead of the colour bracket that samples it. The one
/// remaining lag is creature POSES, captured during the colour pass and therefore a frame old at the next
/// bake -- ADR 0011's named approximation, upgrade path filed as #113.</para>
/// <para><b>What the engine still draws, and why (#130).</b> The list above is what the Alloy frame draws;
/// this is the rest of <c>Scene::RenderSinglePass</c> @0x46e5f0, so that "engine-drawn" is a decision on
/// record rather than a gap. #106 enumerated eleven draws outside the bucket walk. Five are owned or
/// scheduled: the sky (<see cref="PassId.SkyOwned"/>), grass (<see cref="PassId.GrassOwned"/>), beams
/// (<see cref="PassId.BeamsOwned"/>) and <c>Scene::RenderDynamicGeometry</c>, which is not an overlay at all
/// -- it is the entry point that walks buckets 2 and 3, i.e. the creatures and placeables the Alloy classes
/// already carry. A fifth joined them in #131: <c>Scene::DoEmitterBucket</c> @0x476f00 (step 28), the
/// particles -- see <see cref="PassId.ParticlesOwned"/>. The remaining six stay the engine's, each for a
/// reason:
/// <list type="bullet">
/// <item><c>Scene::DoHighlightGob</c> @0x470fc0 (step 11) -- the selection outline. UI feedback, not scene
/// content: it manages its own depth mask and draws whatever the mouse is over. A modern replacement is a
/// post-process edge pass, not a re-draw, so owning it here would mean porting the technique we intend to
/// throw away.</item>
/// <item><c>Scene::DoTriggerBucket</c> @0x46b860 (step 12) -- trigger/AoE wireframes. DM and debug
/// visualisation, drawn only when the client asks for it, and content-level rather than renderer-level.</item>
/// <item><c>Scene::DoHighlightTriggers</c> (steps 13 and 24, the same function twice) -- the same class as
/// above, and the second call is what puts a highlighted trigger in front of the water.</item>
/// <item><c>Scene::RenderLensFlares</c> @0x471860 (step 29) -- screen-space additive quads with the depth
/// test off. No world geometry and no VDO to bind, so "owning" it is a from-scratch reimplementation with no
/// capture to replay; it is a look change, and look changes get their own ticket.</item>
/// <item><c>RenderDrawableSquares()</c> @0x463880 (step 31) -- the walkmesh/tile square overlay, pool-backed
/// and depth-test-off. A debug surface, not scene content.</item>
/// <item><c>Scene::RenderLines</c> @0x46be40 (step 32) -- the <c>AddLinesToRender</c> clients: debug and aux
/// line drawing, depth test off.</item>
/// </list>
/// None of the six is a colour-pass shading class, none has a VDO the Alloy pass could bind, and every one
/// of them draws AFTER the Alloy bracket has resolved -- so leaving them with the engine costs correctness
/// nothing and the one-owner audit nothing: no part of any of them ever enters the ownership table.</para>
/// </summary>
internal static class FrameGraph
{
    /// <summary>ADR 0011 §"The graph", in order, each with the bracket it runs inside.</summary>
    internal static readonly (PassId Id, Bracket Bracket)[] Passes =
    {
        (PassId.SunCascades,     Bracket.Shadow),
        (PassId.LocalLightCubes, Bracket.Shadow),
        (PassId.DepthPrepass,    Bracket.Colour),
        (PassId.AmbientOcclusion, Bracket.Colour),
        (PassId.OpaqueOwned,     Bracket.Colour),
        (PassId.SkyOwned,        Bracket.Colour),
        (PassId.CutoutOwned,     Bracket.Colour),
        (PassId.GrassOwned,      Bracket.Colour),
        (PassId.BlendedOwned,    Bracket.Colour),
        (PassId.FbCopy,          Bracket.Colour),
        (PassId.WaterOwned,      Bracket.Colour),
        (PassId.AdditiveOwned,   Bracket.Colour),
        (PassId.BeamsOwned,      Bracket.Colour),
        (PassId.ParticlesOwned,  Bracket.Colour),
        (PassId.HalosOwned,      Bracket.Colour),
        (PassId.ProbeCapture,    Bracket.Colour),
        (PassId.PostProcess,     Bracket.Colour),
        (PassId.PostStack,       Bracket.Colour),
    };

    /// <summary>Off-device (run-demo.sh): the list schedules every declared pass exactly once, in ADR 0011's
    /// order, and costs two brackets. Ids ascending is what ties the walk to the ADR -- the enum carries the
    /// graph order, so a pass appended out of position fails here instead of rendering wrong. Returns false to
    /// keep the Alloy frame from arming at all -- a mis-ordered graph is a misrender, not a bad dial.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // ...and every declared bracket is one the frame actually opens: a third Bracket member is the design
        // change ADR 0011 costs at two, so it fails here rather than quietly becoming a third Enter/Leave.
        bool ok = Passes.Length == System.Enum.GetValues<PassId>().Length
                  && System.Enum.GetValues<Bracket>().Length == 2;
        int brackets = 0;
        for (int i = 0; i < Passes.Length; i++)
        {
            if (i > 0)
            {
                if ((int)Passes[i].Id <= (int)Passes[i - 1].Id) ok = false;             // ADR order
                if (Passes[i].Bracket < Passes[i - 1].Bracket) ok = false;              // brackets never re-open
                if (Passes[i].Bracket != Passes[i - 1].Bracket) brackets++;
            }
            else brackets++;
        }
        if (brackets != 2) ok = false;
        // #375: the post GPU bracket opens at PostProcess and closes at PostStack, so they must be adjacent --
        // a pass slotted between them would be timed as post and run on a half-finished frame.
        for (int i = 1; i < Passes.Length; i++)
            if (Passes[i].Id == PassId.PostStack && Passes[i - 1].Id != PassId.PostProcess) ok = false;
        log.Info(ok ? $"FrameGraph.SelfCheck: passed ({Passes.Length} passes, {brackets} brackets)"
                    : $"FrameGraph.SelfCheck: FAILED ({Passes.Length} passes, {brackets} brackets)");
        return ok;
    }
}
