using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-52: cube textures, the one class of engine texture the 2D upload funnel never sees
/// (<c>glImageBasic</c> routes <c>flagCube</c> to <c>CreateCubeTexture</c>). Owned the way <see cref="Adopt"/>
/// owns a 2D one under <c>texsingle</c>: an Alloy-minted <c>GL_TEXTURE_CUBE_MAP</c> name is bound where the
/// engine bound its own, the engine's upload runs into it (face cut, rotations, sampler state and mips are all
/// the engine's code), and the cell array is handed Alloy's name while the engine's dies unused. Nothing in
/// Alloy samples a cube map today -- the IBL probe and the 2D scene env map stand in -- so this is ownership
/// of the object for its own sake: the engine's draws bind Alloy's cube. No scan and no BCn transcode.</summary>
internal static unsafe partial class OwnedTextures
{
    private static RiCreateCubeTextureFn _origCreateCube;
    private static long _cubeUploads, _cubeRefused;
    /// <summary><see cref="Measure"/>'s DSA level queries answer for one face of a cube.</summary>
    private const int CubeFaces = 6;

    /// <summary>The engine is about to upload cell <paramref name="cell"/> of a cube texture into the name it has
    /// bound to <c>GL_TEXTURE_CUBE_MAP</c>. Cold: once per cube per (re)build.</summary>
    [UnmanagedCallersOnly]
    private static void OnCreateCubeTexture(IntPtr tex, byte useSubImage, int cell)
    {
        long t0 = FrameZones.Now;
        if (!(_single && AdoptCube(tex, cell))) _origCreateCube(tex, useSubImage, cell);
        FrameZones.Add(FrameZones.Zone.Tex, t0);
        FrameZones.Count(FrameZones.Ev.TexUpload);
    }

    /// <summary>True when Alloy's cube stands in for the engine's; false leaves the upload to the caller.</summary>
    private static bool AdoptCube(IntPtr tex, int cell)
    {
        if (!_armed || !AurTex.IsBasic(tex) || !GlContextService.Available) return false;
        var b = new CAurTextureBasic(tex);
        uint* cells = (uint*)b.CellNames;
        int n = b.NumX * b.NumY;
        if (cells == null || cell < 0 || cell >= n || n > MaxCells) return false;
        int etid = (int)cells[cell];
        if (etid <= 0) return false;
        FreshName((uint)etid);

        uint dst;
        Gl.CreateTextures(Gl.TEXTURE_CUBE_MAP, 1, &dst);
        if (dst == 0) return false;
        // Raw bind, as in Adopt: the engine's upload writes through the cube binding on unit 0, and its bind
        // cache must go on naming what it bound.
        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_CUBE_MAP, &savedBind);
        Gl.DrainErrors(8);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, dst);
        _origCreateCube(tex, 0, cell);   // the allocating form: a fresh name has no storage
        uint err = Gl.GetError();
        long bytes = err == 0 ? Measure(dst) * CubeFaces : 0;
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, (uint)savedBind);
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            if (++_cubeRefused <= 3) _log?.Always($"OwnedTextures: cube tid {etid} left to the engine -- its upload into Alloy's cube gave 0x{err:x}");
            return false;
        }

        cells[cell] = dst;
        if (cell == 0) b.GlId = (int)dst;
        uint old = (uint)etid;
        if (!_owned.Contains(old) && Gl.DeleteTextures != null) { Gl.DeleteTextures(1, &old); ScrubEngineBoundCache(old); }
        else _engineHeld.Remove(old);
        _engineHeld.Add(dst);
        _borrowed.Remove(etid);
        _stats.Remove(dst);
        Replace(Key(tex, cell), (int)dst, dst, bytes);
        HighWater();
        _cubeUploads++;
        return true;
    }
}
