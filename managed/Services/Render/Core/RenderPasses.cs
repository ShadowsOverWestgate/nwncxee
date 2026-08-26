using Sowx.Framework;

namespace Sowx.Services.Render;

/// <summary>
/// Every owned pass's lifecycle, declared once (#175). This is the file the acceptance criterion means by
/// "adding a pass requires touching one file": a new pass writes whatever methods it actually has and adds
/// one entry here -- instead of writing up to eight methods and remembering to wire each into four separate
/// hardcoded call lists, which is the convention that produced the gaps below.
/// <para>Order is <see cref="FrameGraph"/>'s. A <c>null</c> slot is a pass that genuinely has no such
/// concern; where a slot is missing and probably should not be, it says so on the line.</para>
/// </summary>
internal static class RenderPasses
{
    /// <summary>Fill the registry and seal it. Once, from <c>OwnedDrawService.Initialize</c>.</summary>
    internal static void Register(ExtenderLog log)
    {
        var r = PassRegistry.Global;
        if (r.Sealed) return;

        // #129: FrameSnapshot first, because FrameGraph's own check asserts that the ownable-bucket list AGREES
        // with whether owned water can draw -- so water's boot knobs have to be read before that check runs.
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
            Describe = WaterBodies.Describe,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "sky",
            // No ContextLost: the sky holds no GL objects of its own -- it replays captured parts through
            // the owned draw service's programs and VAOs. The null slot is the honest answer here.
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
            Attach = OwnedLights.Attach,
            SelfCheck = l => OwnedLights.SelfCheck(l),
            ContextLost = OwnedLights.Forget,
            Describe = OwnedLights.Describe,
            Tune = OwnedLights.Tune,
            Reset = OwnedLights.ResetTuning,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "probe",
            Attach = EnvProbe.Attach,
            SelfCheck = EnvProbe.SelfCheck,
            ContextLost = EnvProbe.Forget,
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
        // #179: the owned world pass's own dials, so /draw reaches them through the one command
        // implementation. No Attach/Install/ContextLost slots -- OwnedDrawService IS the service that drives
        // this walk, and it owns those edges directly.
        r.Register(new PassLifecycle
        {
            Name = "draw",
            SelfCheck = OwnedDrawService.TuneSelfCheck,
            Describe = OwnedDrawService.DescribeTuning,
            Tune = OwnedDrawService.Tune,
            Reset = OwnedDrawService.ResetTuning,
        }, log);
        // CasterPass is deliberately NOT here yet. It runs in the SHADOW bracket, installs through its own
        // service, and its stand-down is a latched Disarm rather than a context edge -- so it does not fit
        // these slots without changing what they mean. Folding the shadow pass in is #176's work, which is
        // where the folder seam between it and the colour passes goes away.

        r.Seal();
    }
}
