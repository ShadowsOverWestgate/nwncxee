using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>
/// Every Alloy pass's lifecycle, declared once (#175). This is the file the acceptance criterion means by
/// "adding a pass requires touching one file": a new pass writes whatever methods it actually has and adds
/// one entry here -- instead of writing up to eight methods and remembering to wire each into four separate
/// hardcoded call lists, which is the convention that produced the gaps below.
/// <para>Order is <see cref="FrameGraph"/>'s. A <c>null</c> slot is a pass that genuinely has no such
/// concern; where a slot is missing and probably should not be, it says so on the line.</para>
/// </summary>
internal static class RenderPasses
{
    /// <summary>Fill the registry and seal it. Once, from <c>AlloyRenderer.Initialize</c>.</summary>
    internal static void Register(ExtenderLog log)
    {
        var r = PassRegistry.Global;
        if (r.Sealed) return;

        // #211: not a pass -- the texture table every pass draws from. Registered here anyway, because what
        // it needs IS a pass lifecycle: both edges, an Attach for its boot gate, and a SelfCheck.
        r.Register(new PassLifecycle
        {
            Name = "ownedtex",
            Attach = l => { OwnedTextures.Attach(l); KnobSurface.Note(AlloyRenderer.VramNote); },
            Install = OwnedTextures.Install,
            SelfCheck = l => { OwnedTextures.SelfCheck(l); BcnEncode.SelfCheck(l); AlloyRenderer.SelfCheckHandles(l); AlloyRenderer.SelfCheckNameRetired(l); OwnedTextures.SelfCheckCache(l); },
            // #259: no Revoke. Its resources are keyed on CONTENT, so a part-table wipe
            // invalidates nothing in them -- and since they are only ever populated at the engine's upload
            // funnel, wiping them on an edge the engine does not rebuild across would leave every texture
            // borrowed for the rest of the session. The engine's own DeleteTextures IS the world edge here.
            ContextLost = OwnedTextures.ContextLost,
        }, log);

        // #212: nor is this one -- the vertex/index buffers the static-opaque class draws from. Same shape
        // as ownedtex, one level down: a boot gate, two engine funnels, and both lifetime edges.
        r.Register(new PassLifecycle
        {
            Name = "ownedgeom",
            Attach = l => { OwnedBuffers.Attach(l); NormalWeld.Attach(l); },
            Install = OwnedBuffers.Install,
            SelfCheck = l => { OwnedBuffers.SelfCheck(l); NormalWeld.SelfCheck(l); StagePool.SelfCheck(l); AlloyRenderer.SelfCheckItemDirty(l); },
            Revoke = OwnedBuffers.Revoke,
            ContextLost = OwnedBuffers.ContextLost,
            Stats = OwnedBuffers.Stats,
        }, log);

        // Automatic mesh LOD: not a draw pass either -- a generated index run per eligible mesh, one shared
        // element buffer, and a background worker. Its Revoke is the world edge (every key is a VDO slot and
        // an item); its ContextLost forgets the buffer name and keeps the mirror the arena is rebuilt from.
        r.Register(new PassLifecycle
        {
            Name = "meshlod",
            Attach = MeshLod.Attach,
            SelfCheck = l => MeshLod.SelfCheck(l),
            Revoke = MeshLod.Revoke,
            ContextLost = MeshLod.ContextLost,
            Stats = MeshLod.Stats,
        }, log);

        // #673: the charter -- MeshLod's sibling with its own vertex arena and a disk ledger. Same three edges.
        r.Register(new PassLifecycle
        {
            Name = "meshchart",
            Attach = MeshChart.Attach,
            SelfCheck = l => MeshChart.SelfCheck(l),
            Revoke = MeshChart.Revoke,
            ContextLost = MeshChart.ContextLost,
            Stats = MeshChart.Stats,
        }, log);

        // #674: the lightmap bake -- the pages, the scratch pair and the per-slot records. Revoke rewinds the
        // packing (every rect names an instance of an area that is gone); ContextLost drops the names.
        r.Register(new PassLifecycle
        {
            Name = "lightmap",
            Attach = LightmapBake.Attach,
            Install = LightmapBake.Install,
            SelfCheck = l => { LightmapBake.SelfCheck(l); LightmapBake.HoldSelfCheck(l); BakeCache.SelfCheck(l); },
            Revoke = LightmapBake.Revoke,
            ContextLost = LightmapBake.ContextLost,
            Stats = LightmapBake.Stats,
        }, log);

        // #129: FrameSnapshot first, because FrameGraph's own check asserts that the ownable-bucket list AGREES
        // with whether Alloy water can draw -- so water's boot knobs have to be read before that check runs.
        r.Register(new PassLifecycle
        {
            Name = "water",
            Attach = FrameSnapshot.Attach,
            SelfCheck = l => FrameSnapshot.SelfCheck(l),
            ContextLost = FrameSnapshot.Forget,
            Stats = FrameSnapshot.Stats,
        }, log);
        // not a draw pass either -- it replaces the engine's per-frame dangly SOLVE, which is a
        // separate pass from the dangly draw. No GL objects, so no ContextLost; no captured world state,
        // so no Revoke; its only resources are the engine's own buffers.
        r.Register(new PassLifecycle
        {
            Name = "dangly",
            Install = DanglyPhysics.Install,
            SelfCheck = DanglyPhysics.SelfCheck,
            Stats = DanglyPhysics.Stats,
        }, log);
        // CX-29: not a pass and not Alloy's -- foot placement runs under either renderer. Registered for its
        // clause on the [alloy] period line only.
        r.Register(new PassLifecycle { Name = "footik", Stats = FootIkService.Stats }, log);
        r.Register(new PassLifecycle
        {
            Name = "sky",
            // #261 deleted SkyComposite with its scratch target, so the sky holds no GL objects again: it
            // draws enrolled parts through the Alloy draw service's own programs and VAOs.
            SelfCheck = SkyBox.SelfCheck,
            Revoke = SkyBox.Forget,
            Stats = _ => SkyBox.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "grass",
            Install = GrassField.Install,
            ContextLost = GrassField.ContextLost,
            SelfCheck = GrassField.SelfCheck,
            // #175 found this one: GrassField.Forget had NO caller anywhere, so grass carried its patch
            // table and slot map straight across a part-table wipe. The registry is what makes that visible.
            Revoke = GrassField.Forget,
            Stats = _ => GrassField.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "border",
            Install = AreaBorder.Install,
            SelfCheck = AreaBorder.SelfCheck,
            Stats = _ => AreaBorder.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "beams",
            Install = Beams.Install,
            ContextLost = Beams.ContextLost,
            SelfCheck = Beams.SelfCheck,
            Revoke = Beams.Forget,
            Stats = _ => Beams.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "halos",
            Install = Halos.Install,
            ContextLost = Halos.ContextLost,
            Stats = _ => Halos.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "particles",
            Install = Particles.Install,
            SelfCheck = Particles.SelfCheck,
            ContextLost = Particles.ContextLost,
            Revoke = Particles.Forget,
            Stats = _ => Particles.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "weather",
            Install = Weather.Install,
            SelfCheck = Weather.SelfCheck,
            ContextLost = Weather.ContextLost,
            Stats = _ => Weather.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "scenedepth",
            // Attached by Particles, its only consumer, rather than here: it is a resource the particle pass
            // owns the timing of, not a pass of its own. ponytail: left alone; if a second consumer appears
            // it moves to the Attach slot above.
            ContextLost = SceneDepth.Forget,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "light",
            Attach = AlloyLights.Attach,
            SelfCheck = l => AlloyLights.SelfCheck(l),
            ContextLost = AlloyLights.Forget,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "probe",
            Attach = EnvProbe.Attach,
            SelfCheck = EnvProbe.SelfCheck,
            ContextLost = EnvProbe.Forget,
            // #209: was missing. The re-bake keys on a BSP POINTER, so a recycled address carried the
            // previous area's cube into the new one.
            Revoke = EnvProbe.Revoke,
        }, log);
        // #341: the fog's TARGET, which is a look decision and therefore a set of dials -- the ramp itself
        // stays the area author's. No frame slot: every stage uploads it through UploadFeatureTerms.
        r.Register(new PassLifecycle
        {
            Name = "fog",
            Attach = SceneFog.Attach,
            SelfCheck = SceneFog.SelfCheck,
            Revoke = SceneFog.Revoke,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "post",
            Attach = PostChain.Attach,
            SelfCheck = l => PostChain.SelfCheck(l),
            ContextLost = PostChain.Forget,
            Stats = f => "post " + PostChain.Stats(f),
        }, log);
        // #375: the display-space stack after the resolve; its dials are the post group's. No Revoke -- it
        // captures nothing from the world; its inputs are the engine's option globals, read per frame.
        // #388: the camera velocity target; post.motion and post.viz 4 reach it. Revoke resets the previous
        // view-projection so the first frame after an area transition paints zero rather than a jump.
        r.Register(new PassLifecycle
        {
            Name = "motion",
            Attach = MotionVectors.Attach,
            SelfCheck = l => MotionVectors.SelfCheck(l),
            ContextLost = MotionVectors.Forget,
            Revoke = MotionVectors.Revoke,
            Stats = MotionVectors.Stats,
        }, log);
        // #493: the vis-ID target the prepass writes beside its depth; a frame-sized GL object, so it goes
        // with the context.
        r.Register(new PassLifecycle
        {
            Name = "idtarget",
            Attach = IdTarget.Attach,
            SelfCheck = l => IdTarget.SelfCheck(l),
            ContextLost = IdTarget.Forget,
            Stats = IdTarget.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "oit",
            Attach = OitPass.Init,
            ContextLost = OitPass.ContextLost,
            Stats = OitPass.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "ao",
            Attach = SsaoPass.Attach,
            SelfCheck = l => SsaoPass.SelfCheck(l),
            ContextLost = SsaoPass.Forget,
            // #390: the visibility history is another area's after a transition.
            Revoke = SsaoPass.Revoke,
            Stats = SsaoPass.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "poststack",
            Attach = PostStack.Attach,
            SelfCheck = l => PostStack.SelfCheck(l),
            ContextLost = PostStack.Forget,
            // #389: the TAA history is another area's after a transition; the first frame resolves plain.
            Revoke = TaaStage.Reset,
            Stats = PostStack.Stats,
        }, log);
        // #179: the Alloy world pass. Its dials are the alloy group's (ADR 0025), read straight off the
        // knobs. No Attach/Install slots -- AlloyRenderer IS the service that drives this walk, and
        // it owns those edges directly. ContextLost is in the table anyway (#209): driving the walk is no
        // reason to be absent from it, and being absent is how this pass kept its own GL names through a
        // context loss while every pass it calls dropped theirs. No Revoke -- RevokeAll IS its world edge.
        r.Register(new PassLifecycle
        {
            Name = "draw",
            ContextLost = AlloyRenderer.ContextLost,
            SelfCheck = l => { CullPass.SelfCheck(l); AlloyRenderer.EnrollSelfCheck(l); },
        }, log);
        // CasterPass is deliberately NOT here yet. It runs in the SHADOW bracket, installs through its own
        // service, and its stand-down is a latched Disarm rather than a context edge -- so it does not fit
        // these slots without changing what they mean. Folding the shadow pass in is #176's work, which is
        // where the folder seam between it and the colour passes goes away.

        r.Seal();
    }
}
