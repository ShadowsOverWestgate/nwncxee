using System;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Perf;

/// <summary>#336 step 5: split the [zones] line's <c>other</c> column. Four timing hooks on the engine main
/// loop's non-render steps (see <see cref="FrameZones.Zone.Net"/>); each calls the original through its
/// trampoline between two timestamps and returns whatever it returned. The handlers forward the argument
/// register untouched -- one integer or float each, no by-value structs (the mangled names say so) -- and
/// return <c>long</c> so a void, int or pointer result rides through rax intact. Hot, allocation-free.</summary>
public sealed unsafe class EngineZonesService : IExtenderService
{
    private static delegate* unmanaged<IntPtr, int, long> _net;
    private static delegate* unmanaged<IntPtr, uint, long> _res;
    private static delegate* unmanaged<IntPtr, long> _ai;
    private static delegate* unmanaged<IntPtr, float, long> _gui;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!Settings.Bool("enginezones", true)) { log.Info("EngineZones: off (config enginezones=0)"); return; }
        _net = (delegate* unmanaged<IntPtr, int, long>)hooks.Install(
            SymbolMap.NetProcessReceivedFrames, (IntPtr)(delegate* unmanaged<IntPtr, int, long>)&OnNet);
        _res = (delegate* unmanaged<IntPtr, uint, long>)hooks.Install(
            SymbolMap.ResManUpdate, (IntPtr)(delegate* unmanaged<IntPtr, uint, long>)&OnRes);
        _ai = (delegate* unmanaged<IntPtr, long>)hooks.Install(
            SymbolMap.ClientAiUpdateState, (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnAi);
        _gui = (delegate* unmanaged<IntPtr, float, long>)hooks.Install(
            SymbolMap.GuiUpdateAndRender, (IntPtr)(delegate* unmanaged<IntPtr, float, long>)&OnGui);
        log.Info("EngineZones: timing the engine loop's net / resman / AI / GUI steps into [zones] (config enginezones=0 disables)");
    }

    [UnmanagedCallersOnly]
    private static long OnNet(IntPtr self, int a)
    {
        long t = FrameZones.Now;
        long r = _net != null ? _net(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Net, t);
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnRes(IntPtr self, uint a)
    {
        long t = FrameZones.Now;
        long r = _res != null ? _res(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Res, t);
        return r;
    }

    /// <summary>The first live read had <c>Ai</c> at 10ms with <c>other</c> at -7: the AI update is where
    /// the engine renders the scene from, so every renderer bracket sits inside it. Their ticks are
    /// subtracted, and what is left is the update itself.</summary>
    [UnmanagedCallersOnly]
    private static long OnAi(IntPtr self)
    {
        long t = FrameZones.Now;
        long inner0 = Inner();
        long r = _ai != null ? _ai(self) : 0;
        FrameZones.Add(FrameZones.Zone.Ai, t, Inner() - inner0);
        return r;
    }

    private static long Inner()
        => FrameZones.Ticks(FrameZones.Zone.Enroll) + FrameZones.Ticks(FrameZones.Zone.Shadow)
           + FrameZones.Ticks(FrameZones.Zone.Shim) + FrameZones.Ticks(FrameZones.Zone.Alloy)
           + FrameZones.Ticks(FrameZones.Zone.Ui) + FrameZones.Ticks(FrameZones.Zone.Present);

    [UnmanagedCallersOnly]
    private static long OnGui(IntPtr self, float dt)
    {
        long t = FrameZones.Now;
        long r = _gui != null ? _gui(self, dt) : 0;
        FrameZones.Add(FrameZones.Zone.Gui, t);
        return r;
    }
}
