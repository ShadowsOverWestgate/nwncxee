using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>PLT, the engine's palette-composited skins: g_bGeneratePLTOnGPU is held at zero so every character
/// comes down the upload funnel, and a GPU composite that slips past is copied inside the 4.6 bracket.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The engine's GPU-PLT switch, held at zero so PLT textures come through the upload funnel as
    /// decoded pixels. Null when the symbol did not resolve, which leaves PLT content borrowed.</summary>
    private static int* _pltOnGpu;
    private static bool _pltWanted;
    private static long _pltForced;

    /// <summary><c>glImageBasic</c> @0x48bd67 takes the GPU-composite branch on
    /// <see cref="CAurTextureBasic.PltKind"/> <c>== 4</c> and nothing else, so 4 is the PLT marker and this is
    /// the ONLY test that separates a build Alloy must copy from one it has already uploaded.</summary>
    private const int PltKind = 4;

    /// <summary>One finished GPU composite, waiting for a context that may copy it. Deliberately holds NO
    /// <c>CAurTexture*</c>: the identity is already resolved to a content key here, so nothing in the queue
    /// can be a pointer into a world that moved on before the bracket.</summary>
    private readonly struct PltCopy
    {
        public readonly uint Src; public readonly ulong Key;
        public PltCopy(uint src, ulong key) { Src = src; Key = key; }
    }
    private static readonly List<PltCopy> _pltQ = new();
    private static long _pltCopied, _pltCopyRefused, _pltCopyDied;
    private static int _pltCopyDiagLeft = 8;
    private static bool _pltNoCopyLogged;

    /// <summary>Enrol every cell of a finished GPU-PLT build for adoption by copy. Queued rather than copied
    /// here: <c>glCopyImageSubData</c> is GL 4.3 and this runs in the ENGINE's context, where the source is
    /// only guaranteed visible after the flush the frame's own present provides. No dedup -- the engine builds
    /// a texture exactly twice and both must produce a new copy, or a re-composite serves a stale skin.</summary>
    private static void QueuePltCopy(IntPtr tex)
    {
        if (!_armed || !AurTex.IsBasic(tex)) return;
        var b = new CAurTextureBasic(tex);
        if (b.PltKind != PltKind) return;
        uint* cells = (uint*)b.CellNames;
        if (cells == null) return;
        int n = b.NumX * b.NumY;
        if (n > MaxCells) return;
        for (int cell = 0; cell < n; cell++)
        {
            int etid = (int)cells[cell];
            if (etid <= 0) continue;
            FreshName((uint)etid);
            _pltQ.Add(new PltCopy((uint)etid, Key(tex, cell)));
        }
    }

    /// <summary>The engine is retiring this GL name; anything queued against it describes a texture that no
    /// longer exists. Dropped rather than copied-and-discarded, because the copy would read a recycled name.
    /// <para>The queue holds at most a few entries between brackets, so the scan is over nothing.</para></summary>
    private static void DropQueued(uint name)
    {
        for (int i = _pltQ.Count - 1; i >= 0; i--)
            if (_pltQ[i].Src == name) { _pltQ.RemoveAt(i); _pltCopyDied++; }
    }

    /// <summary>Hold <c>g_bGeneratePLTOnGPU</c> at zero. Re-asserted per frame rather than stored once: the
    /// engine writes it from a settings callback that can run after our install and again whenever the option
    /// is applied, and a single frame with it set is a character created outside the upload funnel and
    /// borrowed until something re-creates it. The count is reported: forcing a global is not silent.</summary>
    internal static void KeepPltOnCpu()
    {
        if (_pltOnGpu == null || *_pltOnGpu == 0) return;
        *_pltOnGpu = 0;
        // #358: the [vram] line carries the count, but it only prints when the high-water GROWS -- so after
        // an area has settled the count reads stale, and "re-cleared 1x" looked like proof the flag never
        // came back when it was only proof nobody had looked since. The re-clears that matter are the ones
        // AFTER Attach's, they are cold, and there are a handful of them: say each one out loud.
        if (_pltForced++ > 0 && _pltDiagLeft > 0)
        {
            _pltDiagLeft--;
            _log?.Info($"OwnedTextures: g_bGeneratePLTOnGPU was set back to 1 -- re-cleared ({_pltForced}x). "
                     + (_origConstructImage != null && _origGlImageBasic != null
                        ? "Held at the readers, so no PLT texture is rebuilt outside the upload funnel (#358)"
                        : "The readers are NOT hooked, so a rebuild in this same engine step is GPU-composited "
                        + "and its character stays BORROWED until something re-creates it (#358)"));
        }
    }

    /// <summary>A small budget of named re-clears: the population is what matters and it is small.</summary>
    private static int _pltDiagLeft = 8;

    /// <summary>Adopt every queued GPU composite. Runs from <see cref="Drain"/>, inside the 4.6 bracket --
    /// the only place <c>glCopyImageSubData</c> is legal and the first place the source is guaranteed
    /// published across the share-list. Keyed on content, as every upload is. Sampler state does NOT ride
    /// storage: filter, wrap, the mip range and the component swizzle are carried across explicitly.</summary>
    private static void DrainPltCopies()
    {
        if (_pltQ.Count == 0) return;
        if (Gl.CopyImageSubData == null || Gl.TextureStorage2D == null)
        {
            foreach (PltCopy q in _pltQ) { _borrowed.Add((int)q.Src); _pltCopyRefused++; }
            _pltQ.Clear();
            // Its own latch, not the per-texture budget: a driver with no copy path refuses EVERY composite,
            // and sharing the budget would spend it all on the first character and hide the second class of
            // failure behind the first.
            if (!_pltNoCopyLogged)
            {
                _pltNoCopyLogged = true;
                _log?.Info("OwnedTextures: GPU-PLT copy unavailable -- this driver has no glCopyImageSubData "
                         + "or no glTexStorage2D, so every GPU composite stays BORROWED (#360)");
            }
            return;
        }

        for (int i = 0; i < _pltQ.Count; i++) Copy(_pltQ[i]);
        _pltQ.Clear();
    }
}
