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
            SelfCheck = l => { OwnedTextures.SelfCheck(l); AlloyRenderer.SelfCheckHandles(l); },
            // #259: no Revoke. Its resources are keyed on CONTENT, so a part-table wipe or a /vanilla toggle
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
            SelfCheck = l => OwnedBuffers.SelfCheck(l),
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
        r.Register(new PassLifecycle
        {
            Name = "waterbodies",
            Attach = WaterBodies.Attach,
            SelfCheck = WaterBodies.SelfCheck,
            // #147/#209: keyed on PART POINTERS, so it dies on the world edge. Called by hand from
            // RevokeAll until the walk itself moved above that method's empty-table guard.
            Revoke = WaterBodies.Clear,
            Describe = WaterBodies.Describe,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "sky",
            // The sky's own capture holds no GL objects -- it replays captured parts through the Alloy draw
            // service's programs and VAOs. #222's scratch target DOES, so ContextLost is no longer null.
            Attach = SkyComposite.Configure,
            ContextLost = SkyComposite.ContextLost,
            SelfCheck = l => { SkyBox.SelfCheck(l); SkyComposite.SelfCheck(l); },
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
        // #179: the Alloy world pass's own dials, so /draw reaches them through the one command
        // implementation. No Attach/Install slots -- AlloyRenderer IS the service that drives this walk, and
        // it owns those edges directly. ContextLost is in the table anyway (#209): driving the walk is no
        // reason to be absent from it, and being absent is how this pass kept its own GL names through a
        // context loss while every pass it calls dropped theirs. No Revoke -- RevokeAll IS its world edge.
        r.Register(new PassLifecycle
        {
            Name = "draw",
            ContextLost = AlloyRenderer.ContextLost,
            SelfCheck = AlloyRenderer.TuneSelfCheck,
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
