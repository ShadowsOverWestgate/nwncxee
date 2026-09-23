using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-24: which atlas cells draw as procedural blades. Servers and builders swap grass textures at
/// will (<c>AddGrassOverride</c>, per-area tilesets) and NWN does not say which is grass and which is a
/// flower, so each live atlas is classified from its own pixels (<see cref="GrassAtlas"/>) once per upload;
/// the verdict and the cells' colour ramps fill the material table.
/// <para>ponytail: no hak 2DA override of the verdict yet. It needs a <c>C2DA</c> read (new symbols, both
/// platforms); <c>grass.style</c> is the A/B, and a per-resref override is the upgrade if a real atlas
/// misclassifies.</para></summary>
internal static unsafe partial class GrassField
{
    /// <summary>One material slot's verdict, keyed by the texture it was read from: the GL name and a hash of
    /// the resref. Either changing (an override, an area transition re-uploading under a recycled name)
    /// re-classifies; <c>Done</c> with no blade bits is also the failure latch, so an unreadable atlas is asked
    /// once, not every frame (the <see cref="HasMips"/> per-frame-rebuild trap).</summary>
    private struct Cls { public int Tid; public uint Name; public int Kinds; public bool Done; }
    private static readonly Cls[] _cls = new Cls[MaxGrassMaterials];
    /// <summary>Root/mid/tip per cell per material, display space.</summary>
    private static readonly Vector3[] _ramp = new Vector3[MaxGrassMaterials * 12];

    /// <summary>Every live material's BLADE cell bits this frame: none under the texture style, or without
    /// the blade program.</summary>
    private static void ClassifyAll(int* tids, bool* live, int* bits)
    {
        bool blades = Grass.Style.V == Grass.StyleBlades && _progBlade != 0;
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            byte* info = live[m] ? GrassInfo(m) : null;
            bits[m] = blades && info != null ? Classified(m, tids[m], info) : 0;
        }
    }

    /// <summary>The material's BLADE cell bits, classifying its atlas when it is new. Cold on a miss: one
    /// level-0 readback per atlas upload, which allocates.</summary>
    private static int Classified(int m, int tid, byte* info)
    {
        uint name = 2166136261u;
        for (int i = 0; i < 32 && info[InfoName + i] != 0; i++) name = (name ^ info[InfoName + i]) * 16777619u;
        ref Cls c = ref _cls[m];
        if (c.Done && c.Tid == tid && c.Name == name) return c.Kinds;
        if (tid == 0) return 0;   // not uploaded yet: a card this frame (which skips it too), asked again next
        c = new Cls { Tid = tid, Name = name, Done = true };
        int w = 0, h = 0;
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h);
        if (Gl.GetTexImage == null || w < 8 || h < 8 || (long)w * h > 4L << 20)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _log?.Info($"[grass] material {m} tid {tid}: atlas unreadable ({w}x{h}) -- drawn as cards");
            return 0;
        }
        var px = new byte[w * h * 4];
        Span<GrassAtlas.Cell> cells = stackalloc GrassAtlas.Cell[4];
        fixed (byte* p = px)
        {
            Gl.GetTexImage(Gl.TEXTURE_2D, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
            GrassAtlas.Classify(p, w, h, cells);
        }
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        for (int k = 0; k < 4; k++)
        {
            if (cells[k].Blade) c.Kinds |= 1 << k;
            _ramp[m * 12 + k * 3] = cells[k].Root;
            _ramp[m * 12 + k * 3 + 1] = cells[k].Mid;
            _ramp[m * 12 + k * 3 + 2] = cells[k].Tip;
        }
        string v = "";
        for (int k = 0; k < 4; k++) v += (c.Kinds >> k & 1) != 0 ? " BLADE" : " CARD";
        _log?.Info($"[grass] material {m} tid {tid} {w}x{h} classified:{v}");
        return c.Kinds;
    }
}
