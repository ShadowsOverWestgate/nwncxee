using Sowx.Framework;

namespace Sowx.Alloy;

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
            Attach = OwnedBuffers.Attach,
            Install = OwnedBuffers.Install,
            SelfCheck = l => { OwnedBuffers.SelfCheck(l); StagePool.SelfCheck(l); AlloyRenderer.SelfCheckItemDirty(l); },
            Revoke = OwnedBuffers.Revoke,
            ContextLost = OwnedBuffers.ContextLost,
            Stats = OwnedBuffers.Stats,
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
            Describe = FrameSnapshot.Describe,
            Tune = FrameSnapshot.Tune,
            Reset = FrameSnapshot.ResetTuning,
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
            Describe = DanglyPhysics.Describe,
            Tune = DanglyPhysics.Tune,
            Reset = DanglyPhysics.ResetTuning,
        }, log);
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
            Describe = GrassField.Describe,
            Tune = GrassField.Tune,
            Reset = GrassField.ResetTuning,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "border",
            Install = AreaBorder.Install,
            SelfCheck = AreaBorder.SelfCheck,
            Stats = _ => AreaBorder.Stats(),
            Describe = AreaBorder.Describe,
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
            Name = "particles",
            Install = Particles.Install,
            SelfCheck = Particles.SelfCheck,
            ContextLost = Particles.ContextLost,
            Revoke = Particles.Forget,
            Stats = _ => Particles.Stats(),
            Describe = Particles.Describe,
            Tune = Particles.Tune,
            Reset = Particles.ResetTuning,
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
            Describe = AlloyLights.Describe,
            Tune = AlloyLights.Tune,
            Reset = AlloyLights.ResetTuning,
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
            Describe = EnvProbe.Describe,
            Tune = EnvProbe.Tune,
            Reset = EnvProbe.ResetTuning,
        }, log);
        // #341: the fog's TARGET, which is a look decision and therefore a set of dials -- the ramp itself
        // stays the area author's. No frame slot: every stage uploads it through UploadFeatureTerms.
        r.Register(new PassLifecycle
        {
            Name = "fog",
            Attach = SceneFog.Attach,
            SelfCheck = SceneFog.SelfCheck,
            Revoke = SceneFog.Revoke,
            Describe = SceneFog.Describe,
            Tune = SceneFog.Tune,
            Reset = SceneFog.ResetTuning,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "post",
            Attach = PostChain.Attach,
            SelfCheck = l => PostChain.SelfCheck(l),
            ContextLost = PostChain.Forget,
            Stats = f => "post " + PostChain.Stats(f),
            Describe = PostChain.Describe,
            Tune = PostChain.Tune,
            Reset = PostChain.ResetTuning,
        }, log);
        // #375: the display-space stack after the resolve. No Describe/Tune slots of its own: /post is the
        // one post command and PostChain's Describe/Tune/Reset fall through to it. No Revoke -- it captures
        // nothing from the world; its inputs are the engine's option globals, read per frame.
        r.Register(new PassLifecycle
        {
            Name = "ao",
            Attach = SsaoPass.Attach,
            SelfCheck = l => SsaoPass.SelfCheck(l),
            ContextLost = SsaoPass.Forget,
            Stats = SsaoPass.Stats,
            Describe = SsaoPass.Describe,
            Tune = SsaoPass.Tune,
            Reset = SsaoPass.ResetTuning,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "poststack",
            Attach = PostStack.Attach,
            SelfCheck = l => PostStack.SelfCheck(l),
            ContextLost = PostStack.Forget,
            Stats = PostStack.Stats,
        }, log);
        // #179: the Alloy world pass's own dials, so /draw reaches them through the one command
        // implementation. No Attach/Install slots -- AlloyRenderer IS the service that drives this walk, and
        // it owns those edges directly. ContextLost is in the table anyway (#209): driving the walk is no
        // reason to be absent from it, and being absent is how this pass kept its own GL names through a
        // context loss while every pass it calls dropped theirs. No Revoke -- RevokeAll IS its world edge.
        r.Register(new PassLifecycle
        {
            Name = "draw",
            ContextLost = AlloyRenderer.ContextLost,
            SelfCheck = l => { AlloyRenderer.TuneSelfCheck(l); AlloyRenderer.EnrollSelfCheck(l); },
            Describe = AlloyRenderer.DescribeTuning,
            Tune = AlloyRenderer.Tune,
            Reset = AlloyRenderer.ResetTuning,
        }, log);
        // CasterPass is deliberately NOT here yet. It runs in the SHADOW bracket, installs through its own
        // service, and its stand-down is a latched Disarm rather than a context edge -- so it does not fit
        // these slots without changing what they mean. Folding the shadow pass in is #176's work, which is
        // where the folder seam between it and the colour passes goes away.

        r.Seal();
    }
}
