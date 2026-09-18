using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>gi</c> group (#643): the storage <see cref="GiProbes"/> reads. Master = the grid; off is the
/// constant area ambient exactly as before. Touched from <see cref="RenderCommands.Register"/>.</summary>
internal static class Gi
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Gain, Bias, Sky, Spacing, SpacingZ, Hysteresis;
    internal static readonly Knob<int> PerFrame, Idle;
    internal static readonly Knob<bool> Cache;

    static Gi()
    {
        G = KnobGroup.Declare("gi", "Graphics", "Indirect light", master: "Indirect light from a probe grid",
            masterHelp: "Shadows are filled by light bounced off nearby surfaces, computed from the scene as you walk. Off is the area's flat ambient.",
            parent: "alloy.enabled",
            header: "Experimental (#643): a coarse world-space irradiance grid that follows the player.");
        G.Status = GiProbes.Describe;
        Gain = G.Slider("gain", "Strength", 0f, 4f, 0.05f, 1f,
                        help: "Scales the bounced light. 1 makes an unoccluded probe match the area ambient.");
        Sky = G.Slider("sky", "Sky brightness", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                       help: "What a probe sees where nothing is drawn, as a multiple of the area ambient.");
        Bias = G.Slider("bias", "Leak bias", 0f, 1f, 0.05f, 0.3f, "m", Tier.Advanced,
                        help: "How far off the surface the probes are read from. Higher hides seams; lower stops light leaking through walls.");
        Spacing = G.Slider("spacing", "Probe spacing", 2f, 10f, 0.5f, 5f, "m", Tier.Advanced,
                           help: "Distance between probes; the grid is 16 x 16 x 4 probes.", changed: GiProbes.Invalidate);
        SpacingZ = G.Slider("spacingz", "Vertical spacing", 1f, 6f, 0.5f, 3f, "m", Tier.Advanced, changed: GiProbes.Invalidate);
        PerFrame = G.Slider("perframe", "Probes per frame", 1, 8, 1, 2, tier: Tier.Advanced,
                            help: "More converges faster and costs more; each probe is six small draws of the scene.");
        Idle = G.Slider("idle", "Idle refresh", 0, 60, 1, 0, "frames", Tier.Advanced,
                        help: "Frames between probe refreshes once every probe has a value. 0, the default, is free: a settled area re-uses its bake and draws no probe at all.");
        Cache = G.Toggle("cache", "Keep bakes on disk", true, Tier.Advanced,
                         help: "Remember each lighting state's bake between sessions, so a place you have been lights up at once. Off keeps the bake in memory only.");
        Hysteresis = G.Slider("hysteresis", "Smoothing", 0f, 0.95f, 0.05f, 0.85f, tier: Tier.Advanced,
                              help: "How much of a probe's previous value survives a re-capture. Hides the round-robin update and averages torch flicker.");
    }
}

/// <summary>#643: a DDGI-lite irradiance probe grid. A 16 x 16 x 4 lattice of world-space probes centred on
/// the player; each probe stores an octahedral irradiance map (8x8 + border) and a depth / depth-squared map
/// (16x16 + border) in two atlases, and is refreshed round-robin, <c>gi.perframe</c> a frame, by rasterising
/// Alloy's own opaque set into a tiny cube from its position (six more <c>IssueBatches</c> passes, lit by the
/// same sun and clusters) and convolving that cube. The lit stage then reads the eight surrounding probes,
/// trilinear and Chebyshev-visibility weighted, in place of the constant area ambient.
/// <para>A capture happens ONCE per cell: every convolve is read back into the bake ledger
/// (<c>GiProbes.Cache.cs</c>), and a cell that re-enters the window is re-uploaded from it. A settled area
/// therefore captures nothing -- see the ledger's own summary for what makes a bake stale.</para>
/// <para>The grid is toroidal: a cell's atlas slot is <c>cell mod N</c>, so walking shifts the window and
/// invalidates only the face of probes that left it; the rest keep converging. A probe that sees mostly
/// back faces (inside a wall) marks itself invalid and its neighbours cover for it.</para>
/// <para>ponytail: no probe relocation, no classification of probes as idle, no view-direction bias in the
/// receiver read. Upgrade path is the DDGI paper's, one term at a time, once the look is judged.</para></summary>
internal static unsafe partial class GiProbes
{
    /// <summary>Probes per axis. 1024 probes; the irradiance atlas is 160 x 640 RGBA16F, the depth atlas
    /// 288 x 1152 RG16F. Fixed: the lit stage's sampler carries the same constants.</summary>
    internal const int Nx = 16, Ny = 16, Nz = 4, Count = Nx * Ny * Nz;
    /// <summary>Interior texels per probe map; the tile is that plus a 1-texel wrapped border for bilinear reads.</summary>
    internal const int IrrRes = 8, DepRes = 16, IrrTile = IrrRes + 2, DepTile = DepRes + 2;
    /// <summary>Capture cube face size. Sixteen: the convolve integrates 6 x 16 x 16 samples per output texel.</summary>
    internal const int CapSize = 16;
    private const float FaceNear = 0.1f, FaceFar = 60f;
    /// <summary>The depth map's ceiling, in cell diagonals: a receiver only ever asks about the eight probes
    /// around it, and RG16F holds distance-squared to a useful precision only when it is bounded.</summary>
    private const float DepthReach = 1.5f;
    private const long NoKey = long.MinValue;
    /// <summary>Bits per axis in a packed cell key: 20, so a cell coordinate of a million tiles still packs.</summary>
    private const int KeyBits = 20;
    private const long KeyMask = (1L << KeyBits) - 1;

    private static uint _fbo, _cube, _cubeDepth, _irr, _dep, _prog;
    private static int _locRad, _locDep, _locPlane, _locTile, _locRes, _locMode, _locCap;
    private static bool _built;
    private static readonly StandDown _down = new("GiProbes");
    private static ExtenderLog? _log;
    /// <summary>Per slot: the packed cell it holds, or <see cref="NoKey"/>.</summary>
    private static readonly long[] _key = new long[Count];
    /// <summary>Per slot: its tiles hold a value. False = cleared to alpha 0 (a reader ignores it), and the
    /// next capture takes it whole rather than blending into nothing.</summary>
    private static readonly bool[] _holds = new bool[Count];
    private static int _cursor;
    private static (int x, int y, int z) _cell0;
    private static bool _haveCell0;
    internal static int Captures { get; private set; }
    /// <summary>Captures this Stats period.</summary>
    private static int _captures;

    internal static bool On => Gi.G.Master!.V && !_down.Failed;
    /// <summary>The lit stage may read the atlases: built, and at least one probe captured.</summary>
    internal static bool Live => On && _irr != 0 && Captures > 0;
    /// <summary>Every slot holds its cell: the capture budget drops from <c>gi.perframe</c> to <c>gi.idle</c>.</summary>
    internal static bool Full => _haveCell0 && _keyless == 0;
    private static int _keyless = Count;
    /// <summary>This frame's capture budget: <c>gi.perframe</c> while any slot is keyless, else one probe
    /// every <c>gi.idle</c> frames -- 0, the default, so a settled area rasterises nothing at all. What
    /// carries a light change into the grid is the fingerprint in <see cref="Tick"/>, not this.</summary>
    internal static int Budget()
    {
        if (!Full) return Gi.PerFrame.V;
        int refresh = RefreshBudget();
        if (refresh > 0) return refresh;
        if (Gi.Idle.V <= 0) return 0;
        return ++_idleTick >= Gi.Idle.V ? (_idleTick = 0) + 1 : 0;
    }
    private static int _idleTick;

    internal static void Attach(ExtenderLog log) => _log = log;

    internal static string Describe()
        => $"gi {(_down.Failed ? "FAILED: " + _down.Reason : !Gi.G.Master!.V ? "off" : Live ? "live" : "warming up")}  "
         + $"gain {Gi.Gain.V:F2}  spacing {Gi.Spacing.V:F1}x{Gi.SpacingZ.V:F1}m  perframe {Gi.PerFrame.V}  bias {Gi.Bias.V:F2}  "
         + $"sky {Gi.Sky.V:F2}  hysteresis {Gi.Hysteresis.V:F2}  captures {Captures}  valid {Valid()}/{Count}  " + CacheNote();

    internal static string Stats(int frames)
    {
        int n = _captures; _captures = 0;
        return !On ? "" : $"gi {(float)n / MathF.Max(frames, 1):F2} probes/frame, {Valid()}/{Count} valid, " + CacheNote();
    }

    private static int Valid() { int n = 0; for (int i = 0; i < Count; i++) if (_key[i] != NoKey) n++; return n; }

    /// <summary>Context loss: the names are gone; the bookkeeping restarts with them.</summary>
    internal static void Forget()
    {
        _fbo = 0; _cube = 0; _cubeDepth = 0; _irr = 0; _dep = 0; _prog = 0; _built = false; _down.Forget();
        Invalidate();
    }

    /// <summary>World edge, and the spacing knobs' Changed: every slot describes somewhere else now. The bake
    /// ledger goes with it -- its cells were baked for a world that has gone, or at a spacing that no longer
    /// names them. <see cref="Revoke"/> is the world-edge form, which writes the ledger out first.</summary>
    internal static void Invalidate()
    {
        for (int i = 0; i < Count; i++) _key[i] = NoKey;   // _holds stays: ClearDropped wipes the tiles next frame
        _keyless = Count; _haveCell0 = false; Captures = 0;
        ResetCache();
    }

    private static long Key(int x, int y, int z) => ((x & KeyMask) << (2 * KeyBits)) | ((y & KeyMask) << KeyBits) | (z & KeyMask);
    private static int Slot(int x, int y, int z)
    {
        int sx = ((x % Nx) + Nx) % Nx, sy = ((y % Ny) + Ny) % Ny, sz = ((z % Nz) + Nz) % Nz;
        return sx + Nx * (sy + Ny * sz);
    }
    private static Vector3 Spacing => new(Gi.Spacing.V, Gi.Spacing.V, Gi.SpacingZ.V);

    /// <summary>Recentre the window on <paramref name="centre"/>: the min cell puts the player mid-grid in xy
    /// and one layer up in z. Slots whose cell fell out of the window lose their key; their tiles are cleared
    /// at the next <see cref="Capture"/>. Returns how many were dropped (the self-check's number).</summary>
    internal static int Shift(Vector3 centre)
    {
        Vector3 s = Spacing;
        var c0 = ((int)MathF.Floor(centre.X / s.X) - Nx / 2, (int)MathF.Floor(centre.Y / s.Y) - Ny / 2, (int)MathF.Floor(centre.Z / s.Z) - 1);
        if (_haveCell0 && c0 == _cell0) return 0;
        _cell0 = c0; _haveCell0 = true;
        int dropped = 0;
        for (int z = 0; z < Nz; z++)
            for (int y = 0; y < Ny; y++)
                for (int x = 0; x < Nx; x++)
                {
                    int cx = c0.Item1 + x, cy = c0.Item2 + y, cz = c0.Item3 + z;
                    int slot = Slot(cx, cy, cz);
                    if (_key[slot] != NoKey && _key[slot] != Key(cx, cy, cz)) { _key[slot] = NoKey; _holds[slot] = false; dropped++; _keyless++; }
                }
        return dropped;
    }

    /// <summary>Pick the next slot to capture: a keyless one first (never captured, or dropped by a shift),
    /// else the round-robin cursor. Returns the slot and the cell in the current window that maps onto it.</summary>
    private static int Next(out int cx, out int cy, out int cz)
    {
        int slot = -1;
        for (int i = 0; i < Count && slot < 0; i++)
        {
            int s = (_cursor + i) % Count;
            if (_key[s] == NoKey) slot = s;
        }
        if (slot < 0) slot = _cursor;
        _cursor = (slot + 1) % Count;
        CellOfSlot(slot, out cx, out cy, out cz);
        return slot;
    }

    /// <summary>The cell in the current window that maps onto <paramref name="slot"/> -- the inverse of
    /// <see cref="Slot"/> over the window, which is what lets the ledger be asked about a keyless slot.</summary>
    private static void CellOfSlot(int slot, out int cx, out int cy, out int cz)
    {
        int sx = slot % Nx, sy = (slot / Nx) % Ny, sz = slot / (Nx * Ny);
        cx = _cell0.x + ((sx - _cell0.x) % Nx + Nx) % Nx;
        cy = _cell0.y + ((sy - _cell0.y) % Ny + Ny) % Ny;
        cz = _cell0.z + ((sz - _cell0.z) % Nz + Nz) % Nz;
    }

    /// <summary>Open the capture of one probe: bind the scratch cube, hand back its world position and the
    /// slot. The caller draws the six faces between <see cref="Face"/> calls, then <see cref="Convolve"/>.</summary>
    internal static bool Begin(Vector3 clear, out Vector3 pos, out int slot)
    {
        pos = default; slot = -1;
        if (!On || !_haveCell0 || !Ensure()) return false;
        slot = Next(out int cx, out int cy, out int cz);
        Vector3 s = Spacing;
        pos = new Vector3(cx * s.X, cy * s.Y, cz * s.Z);
        if (_key[slot] == NoKey) _keyless--;
        _key[slot] = Key(cx, cy, cz);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Viewport(0, 0, CapSize, CapSize);
        // alpha 1: the sky is not a back face
        Gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
        new PassState(depth: true, depthWrite: true, cull: false, srgb: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        return true;
    }

    internal static bool Face(Vector3 pos, int face, out Matrix4x4 vp)
    {
        vp = ShadowMath.PointLightFaceViewProj(pos, face, FaceNear, FaceFar);
        uint f = Gl.TEXTURE_CUBE_MAP_POSITIVE_X + (uint)face;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, f, _cube, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, f, _cubeDepth, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Fail("the probe capture framebuffer is incomplete"); return false; }
        Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        return true;
    }

    internal static (float near, float far) FacePlanes => (FaceNear, FaceFar);

    /// <summary>Fold the captured cube into the slot's two atlas tiles. A slot with no previous value takes
    /// the capture whole; one that had one blends by <c>gi.hysteresis</c>.</summary>
    internal static void Convolve(int slot)
    {
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, _cube);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, _cubeDepth);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false, cull: false, srgb: false).Apply();
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locRad, 0); Gl.Uniform1i(_locDep, 1);
        Vector3 s = Spacing;
        Gl.Uniform3f(_locPlane, FaceNear, FaceFar, DepthReach * s.Length());
        Gl.Uniform1i(_locCap, CapSize);
        bool blend = _holds[slot] && Gi.Hysteresis.V > 0f;
        if (blend)
        {
            Gl.Enable(Gl.BLEND);
            Gl.BlendColor(0f, 0f, 0f, 1f - Gi.Hysteresis.V);
            Gl.BlendFunc(Gl.CONSTANT_ALPHA, Gl.ONE_MINUS_CONSTANT_ALPHA);
        }
        else Gl.Disable(Gl.BLEND);
        int sx = slot % Nx, row = slot / Nx;
        int cell = Reserve(_key[slot]);
        Tile(_irr, sx * IrrTile, row * IrrTile, IrrTile, IrrRes, 0);
        ReadBack(cell, sx * IrrTile, row * IrrTile, IrrTile, Gl.RGBA, 0);
        Tile(_dep, sx * DepTile, row * DepTile, DepTile, DepRes, 1);
        ReadBack(cell, sx * DepTile, row * DepTile, DepTile, Gl.RG, IrrBytes);
        if (cell >= 0) _stores++;
        Gl.Disable(Gl.BLEND);
        Gl.BindVertexArray(0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _holds[slot] = true;
        Captures++; _captures++;
    }

    private static void Tile(uint atlas, int x, int y, int tile, int res, int mode)
    {
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, atlas, 0);
        Gl.Viewport(x, y, tile, tile);
        Gl.Uniform2i(_locTile, x, y);
        Gl.Uniform1i(_locRes, res);
        Gl.Uniform1i(_locMode, mode);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
    }

    /// <summary>Clear the tiles of every slot a shift dropped, so the lit stage's <c>irr.a</c> read ignores
    /// them until they are re-captured. Runs before the frame's captures, inside the colour bracket.</summary>
    internal static void ClearDropped()
    {
        if (_irr == 0) return;
        bool any = false;
        for (int i = 0; i < Count && !any; i++) any = _key[i] == NoKey && _holds[i];
        if (!any) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.ClearColor(0f, 0f, 0f, 0f);
        Gl.Enable(Gl.SCISSOR_TEST);
        for (int pass = 0; pass < 2; pass++)
        {
            int tile = pass == 0 ? IrrTile : DepTile;
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, pass == 0 ? _irr : _dep, 0);
            for (int i = 0; i < Count; i++)
            {
                if (_key[i] != NoKey || !_holds[i]) continue;
                Gl.Scissor((i % Nx) * tile, (i / Nx) * tile, tile, tile);
                Gl.Clear(Gl.COLOR_BUFFER_BIT);
            }
        }
        Gl.Disable(Gl.SCISSOR_TEST);
        for (int i = 0; i < Count; i++) if (_key[i] == NoKey) _holds[i] = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>From every lit-program bind: the atlases on their units and the grid's terms. <c>uGi.x</c> 0
    /// is the constant-ambient path, bit-for-bit the pre-#643 shader.</summary>
    internal static void Upload(int locGi, int locCell)
    {
        if (locGi < 0) return;
        bool live = Live;
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GiIrr); Gl.BindTexture(Gl.TEXTURE_2D, live ? _irr : 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GiDepth); Gl.BindTexture(Gl.TEXTURE_2D, live ? _dep : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform4f(locGi, live ? Gi.Gain.V : 0f, Gi.Bias.V, Gi.Spacing.V, Gi.SpacingZ.V);
        Gl.Uniform3i(locCell, _cell0.x, _cell0.y, _cell0.z);
    }

    private static bool Ensure()
    {
        if (_down.Failed) return false;
        if (_built) return true;
        _built = true;
        int units = 0; Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
        if (Gl.BlendColor == null || Gl.Uniform2i == null) { Fail("glBlendColor or glUniform2i is missing"); return false; }
        if (units <= (int)TextureSlots.GiDepth) { Fail($"{units} fragment texture units, needs {TextureSlots.GiDepth + 1}"); return false; }
        string vs = ShaderStages.Source(PostChain.ShdPostVs);
        _prog = GlProgram.Graphics(vs, ConvolveFs, _log, "GI probe convolve");
        if (_prog == 0) { Fail("the convolve program did not build"); return false; }
        _locRad = GlProgram.Loc(_prog, "uRad"); _locDep = GlProgram.Loc(_prog, "uDep"); _locPlane = GlProgram.Loc(_prog, "uPlane");
        _locTile = GlProgram.Loc(_prog, "uTile"); _locRes = GlProgram.Loc(_prog, "uRes"); _locMode = GlProgram.Loc(_prog, "uMode");
        _locCap = GlProgram.Loc(_prog, "uCap");

        uint* t = stackalloc uint[4];
        Gl.GenTextures(4, t);
        _cube = t[0]; _cubeDepth = t[1]; _irr = t[2]; _dep = t[3];
        while (Gl.GetError() != 0) { }
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, _cube);
        for (uint f = 0; f < 6; f++)
            Gl.TexImage2D(Gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, 0, (int)Gl.RGBA16F, CapSize, CapSize, 0, Gl.RGBA, Gl.HALF_FLOAT, null);
        CubeParams();
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, _cubeDepth);
        for (uint f = 0; f < 6; f++)
            Gl.TexImage2D(Gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, 0, (int)Gl.DEPTH_COMPONENT24, CapSize, CapSize, 0, Gl.DEPTH_COMPONENT, Gl.UNSIGNED_INT, null);
        CubeParams();
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, _irr);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA16F, Nx * IrrTile, Ny * Nz * IrrTile, 0, Gl.RGBA, Gl.HALF_FLOAT, null);
        RenderTarget.Clamp(Gl.LINEAR);
        Gl.BindTexture(Gl.TEXTURE_2D, _dep);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RG16F, Nx * DepTile, Ny * Nz * DepTile, 0, Gl.RG, Gl.HALF_FLOAT, null);
        RenderTarget.Clamp(Gl.LINEAR);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        if (Gl.GetError() != 0) { Fail("the atlases could not be allocated"); return false; }
        uint f2; Gl.GenFramebuffers(1, &f2); _fbo = f2;
        Invalidate();
        // every slot starts cleared: the atlases are fresh allocations, whose contents are undefined
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.ClearColor(0f, 0f, 0f, 0f);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _irr, 0);
        Gl.Viewport(0, 0, Nx * IrrTile, Ny * Nz * IrrTile); Gl.Clear(Gl.COLOR_BUFFER_BIT);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _dep, 0);
        Gl.Viewport(0, 0, Nx * DepTile, Ny * Nz * DepTile); Gl.Clear(Gl.COLOR_BUFFER_BIT);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _log?.Info($"GiProbes: {Count} probes, {Nx * IrrTile}x{Ny * Nz * IrrTile} irradiance + {Nx * DepTile}x{Ny * Nz * DepTile} depth atlases, {CapSize}px capture cube");
        return true;
    }


    private static void CubeParams()
    {
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_R, Gl.CLAMP_TO_EDGE);
    }

    private static void Fail(string why) => _down.Fail(_log, why, "no probe grid this session (the constant area ambient stays)");

    /// <summary>Off-device: the toroidal bookkeeping. A slot is a function of the cell alone, a shift drops
    /// exactly the face that left the window, and the next capture goes to a keyless slot first.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"GiProbes.SelfCheck FAIL: {w}"); } }
        Invalidate();
        E(Slot(3, 4, 1) == Slot(3 + Nx, 4 - Ny, 1 + 2 * Nz), "a cell's slot does not depend on the window");
        E(Slot(-1, 0, 0) == Slot(Nx - 1, 0, 0), "negative cells wrap");
        Vector3 s = new(Gi.Spacing.V, Gi.Spacing.V, Gi.SpacingZ.V);
        Shift(new Vector3(100f, 100f, 10f));
        for (int i = 0; i < Count; i++) { Next(out int cx, out int cy, out int cz); _key[Slot(cx, cy, cz)] = Key(cx, cy, cz); _keyless--; }
        E(Valid() == Count && Full, "Next visits every slot once per cycle, and the grid is then full");
        int cx0 = _cell0.x, cy0 = _cell0.y, cz0 = _cell0.z;
        Next(out int nx, out int ny, out int nz);
        E(nx >= cx0 && nx < cx0 + Nx && ny >= cy0 && ny < cy0 + Ny && nz >= cz0 && nz < cz0 + Nz, "Next names a cell inside the window");
        int dropped = Shift(new Vector3(100f + s.X, 100f, 10f));
        E(dropped == Ny * Nz, $"one cell east drops one face of probes ({dropped} vs {Ny * Nz})");
        E(Valid() == Count - Ny * Nz && !Full && _keyless == Ny * Nz, "...and keeps the rest");
        Next(out nx, out ny, out nz);
        E(_key[Slot(nx, ny, nz)] == NoKey && nx == _cell0.x + Nx - 1, "the next capture is a dropped slot, on the new east face");
        E(Shift(new Vector3(100f + s.X, 100f, 10f)) == 0, "a shift to the same window drops nothing");
        bool inverse = true;
        for (int i = 0; i < Count; i++) { CellOfSlot(i, out int qx, out int qy, out int qz); inverse &= Slot(qx, qy, qz) == i; }
        E(inverse, "CellOfSlot is the inverse of Slot over the window -- the ledger asks about the right cell");
        Invalidate();
        log.Info(ok ? "GiProbes.SelfCheck: passed" : "GiProbes.SelfCheck: FAILED");
    }

    /// <summary>The convolve stage: one fragment per atlas texel of the probe's tile, integrating the
    /// capture cube. Mode 0 = irradiance / pi (rgb; a uniform environment of radiance L reads back as L, so
    /// the constant ambient is the unoccluded answer) with the back-face verdict in alpha; mode 1 = the
    /// cos^50-weighted mean distance and squared distance along each direction, for the receiver's Chebyshev
    /// test. Border texels take the wrapped octahedral direction, which is what a copied border would hold.</summary>
    internal const string ConvolveFs = """
        #version 460 core
        uniform samplerCube uRad;
        uniform samplerCube uDep;
        uniform vec3 uPlane;      // face frustum near, far; the stored distance's ceiling
        uniform ivec2 uTile;      // this tile's origin in the atlas, pixels
        uniform int uRes;         // interior texels per side
        uniform int uMode;
        uniform int uCap;         // capture face size
        layout(location = 0) out vec4 frag;

        vec3 CxOctDecode(vec2 f)
        {
            vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
            float t = max(-n.z, 0.0);
            n.xy += vec2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
            return normalize(n);
        }
        // any bijection onto six faces will do: the cube sampler picks the face from the direction
        vec3 CxFaceDir(int face, vec2 uv)
        {
            switch (face)
            {
                case 0: return vec3( 1.0, uv.x, uv.y);
                case 1: return vec3(-1.0, uv.x, uv.y);
                case 2: return vec3(uv.x,  1.0, uv.y);
                case 3: return vec3(uv.x, -1.0, uv.y);
                case 4: return vec3(uv.x, uv.y,  1.0);
                default: return vec3(uv.x, uv.y, -1.0);
            }
        }
        void main()
        {
            vec2 px = gl_FragCoord.xy - vec2(uTile);
            vec2 oct = (px - 1.0) / float(uRes) * 2.0 - 1.0;
            if (abs(oct.x) > 1.0) { oct.x = sign(oct.x) * 2.0 - oct.x; oct.y = -oct.y; }
            if (abs(oct.y) > 1.0) { oct.y = sign(oct.y) * 2.0 - oct.y; oct.x = -oct.x; }
            vec3 d = CxOctDecode(oct);
            float step = 2.0 / float(uCap);
            vec3 sum = vec3(0.0); float wsum = 0.0, back = 0.0, tot = 0.0;
            vec2 dm = vec2(0.0); float dw = 0.0;
            for (int f = 0; f < 6; f++)
                for (int i = 0; i < uCap; i++)
                    for (int j = 0; j < uCap; j++)
                    {
                        vec2 uv = vec2(-1.0 + (float(i) + 0.5) * step, -1.0 + (float(j) + 0.5) * step);
                        float r2 = 1.0 + dot(uv, uv), r = sqrt(r2);
                        float sa = step * step / (r2 * r);   // the texel's solid angle
                        vec3 dir = CxFaceDir(f, uv) / r;
                        float c = dot(d, dir);
                        if (uMode == 0)
                        {
                            vec4 s = texture(uRad, dir);
                            float w = max(c, 0.0) * sa;
                            sum += s.rgb * w; wsum += w;
                            back += (1.0 - s.a) * sa; tot += sa;
                        }
                        else if (c > 0.0)
                        {
                            float sd = texture(uDep, dir).r;
                            float axis = uPlane.x * uPlane.y / (uPlane.y - sd * (uPlane.y - uPlane.x));
                            float dist = min(axis * r, uPlane.z);
                            float w = pow(c, 50.0) * sa;
                            dm += vec2(dist, dist * dist) * w; dw += w;
                        }
                    }
            if (uMode == 0) frag = vec4(sum / max(wsum, 1e-6), back > tot * 0.25 ? 0.0 : 1.0);
            else frag = vec4(dm / max(dw, 1e-6), 0.0, 1.0);
        }
        """;
}
