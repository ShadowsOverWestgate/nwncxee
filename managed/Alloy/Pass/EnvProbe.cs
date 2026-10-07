using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The per-area image-based lighting probe (ADR 0011 M3, #121): a small prefiltered colour cubemap captured
/// from the Alloy scene draw, the ambient-specular source of every Alloy lit part. It replaces the engine's
/// pseudo-spheremap: <c>inc_envmap.shd</c>'s sphere branch is not a reflection at all (an octahedral squash
/// of the VIEW-SPACE normal plus a screen-position fudge, biased to (0.5,0.25)), and the texture it squashes
/// is the AREA's authored env map -- which is why indoor metal reflects an outdoor sky. The scene env map at
/// <c>scene+0x1e8</c> is therefore never bound by the Alloy pass: this cube is what the area actually looks
/// like from where the player stands.
/// <para>Cost: capture is the Alloy draw again, six times, into <see cref="Size"/>-square faces -- on an
/// area change, not per frame (<see cref="Wanted"/>), inside the colour bracket the Alloy pass already
/// opened. The cube stores LINEAR radiance in RGBA16F: filtering display-space values darkens them, so the
/// capture stage skips its display-space encode (<c>uCapture</c>) and the sampler needs no linearisation.</para>
/// <para>CX-46: the capture lands in <see cref="_cap"/>, whose box mip chain only feeds two compute
/// dispatches after it: <c>cx_probe_ggx</c> GGX-prefilters it into the published <see cref="Tex"/> (level i
/// = perceptual roughness i / (Mips - 1), the inverse of every sample site's <c>sqrt(rough)</c> mapping) and
/// <c>cx_probe_sh</c> projects SH9 irradiance into <see cref="_sh"/>, the lit stage's ambient shape. A
/// driver that cannot run them stands the whole probe down: no silent box-filtered fallback.</para>
/// <para>CX-51: creatures are left out of the capture, and its alpha is 1 / distance, which CxProbeFetch
/// (cx_ssr.glsl) uses to parallax-correct every lookup against <see cref="Origin"/> -- what an SSR miss falls
/// back to then stands where the screen would have shown it. ponytail: one probe per area, captured at the
/// camera; what the camera's position cannot see (an awning's underside) is not in it either.</para>
/// </summary>
internal static unsafe class EnvProbe
{
    /// <summary>Face resolution. Small on purpose -- this is an irradiance-ish source, not a mirror, and the
    /// whole cube is one 6 x 128^2 RGBA16F allocation (~0.8MB with mips). <c>light.probesize</c> sets it: it
    /// sizes an allocation, so it is a Restart knob read through Boot.</summary>
    internal static int Size { get; private set; } = 128;
    /// <summary>Levels in the mip chain: roughness 1 maps onto the last one.</summary>
    internal static int Mips { get; private set; }
    /// <summary>The GGX-prefiltered cube, or 0 when it has not been built. Sampled by the Alloy lit stage on
    /// <see cref="ProbeUnit"/>.</summary>
    internal static uint Tex { get; private set; }
    /// <summary>A capture has COMPLETED. Until then the Alloy pass feeds no ambient specular at all, which
    /// is exactly its pre-#121 behaviour -- never a half-written cube.</summary>
    internal static bool Ready { get; private set; }
    /// <summary>World point the live cube was captured from: the parallax correction's centre, and the one the
    /// "has the player walked out of this probe" test measures from.</summary>
    internal static Vector3 Origin { get; private set; }
    /// <summary>Captures this session -- the [alloy] line prints it, and a number that climbs with the frame
    /// count is the failure this ticket's third acceptance criterion is about.</summary>
    internal static int Captures { get; private set; }

    /// <summary>Texture unit the probe binds on: above the material slots (3..9 since #146 added the AO
    /// map), which are above the two shadow maps (1/2) and the diffuse (0). It must stay above them -- a
    /// samplerCube and a sampler2D on ONE unit is INVALID_OPERATION and the draw is dropped, silently as far
    /// as the frame is concerned. Above the grass push map and the #139 resolve as well: those are other
    /// programs, but a cube target sharing a unit with them is exactly the mistake this alias exists to
    /// prevent. See <see cref="TextureSlots"/> for the whole ladder, which asserts that ordering.</summary>
    internal const uint ProbeUnit = TextureSlots.Probe;

    /// <summary>Near plane of a face frustum. Small: the probe sits at the camera, and the geometry that
    /// matters (the room around it) starts close.</summary>
    private const float FaceNear = 0.25f;

    /// <summary>Frames between captures. Not a quality dial -- a floor that keeps a pathological dirty
    /// signal (a scene pointer that churns, a player running a boundary) from turning the probe into a
    /// per-frame pass.</summary>
    private const int MinFrames = 120;
    /// <summary>World distance from <see cref="Origin"/> that re-captures. Areas are large and one probe
    /// cannot serve all of one, so the probe follows the player in coarse steps.</summary>
    private const float MoveDist = 30f;

    /// <summary>UBO binding of the SH9 block (<c>cx_material</c>'s <c>CxProbeSh</c>); 0 is the pass block.</summary>
    internal const uint ShBinding = 1;
    /// <summary>SSBO binding <c>cx_probe_sh</c> writes the same buffer through: above every other SSBO binding
    /// (8 is the bindless handle table, 27 the dangly history), so the dispatch clobbers nothing.</summary>
    private const uint ShOutBinding = 28;
    /// <summary>9 vec4 of SH plus the capture origin, std140 = std430 for vec4s.</summary>
    private const int ShBytes = 10 * 16;

    private static uint _fbo;
    /// <summary>The capture target: raw radiance plus its box chain, read only by the two dispatches.</summary>
    private static uint _cap;
    /// <summary>The SH9 buffer: written as SSBO <see cref="ShOutBinding"/>, read as UBO <see cref="ShBinding"/>.</summary>
    private static uint _sh;
    /// <summary>The two CX-46 compute programs.</summary>
    private static uint _progGgx, _progSh;
    /// <summary><c>cx_probe_ggx</c>'s <c>uLevel</c>: the level (so the roughness) being written.</summary>
    private static int _locLevel;
    /// <summary><c>cx_probe_sh</c>'s <c>uOrigin</c>: the capture point it publishes beside the SH.</summary>
    private static int _locOrigin;
    /// <summary><c>light.probe</c>: gates <see cref="Wanted"/>, which is re-asked every frame, so the probe
    /// idles rather than un-arming and a re-enable captures at the next edge.</summary>
    private static bool _on => Lighting.Probe.V;
    private static readonly StandDown _down = new("EnvProbe");
    private static IntPtr _bsp;
    private static int _lastFrame = -MinFrames;
    private static ExtenderLog? _log;
    private static float _far = 500f;

    /// <summary><c>/light probegain</c>: how much presence the ambient-specular term gets over the engine's own
    /// weighting. 1.0 IS the engine's weighting, and higher is a deliberate departure -- the term multiplies
    /// the light already arriving at the fragment, so raising it far past 1 puts the wet-stone look back.</summary>
    internal static float Gain => Lighting.ProbeGain.V;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        Size = Lighting.ProbeSize.Boot;
    }

    /// <summary>Off-device (run-demo.sh): the world edge drops what it must and keeps what it must.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);

        try
        {
            // #209: the world edge, which the probe had no slot for at all. Both halves matter -- dropping
            // Ready without the key would re-bake once and then trust the stale key again, and dropping the
            // key without Ready leaves the cube readable until the player walks MoveDist.
            // CX-46: every probe fetch maps roughness the way cx_probe_ggx baked it -- level i is perceptual
            // roughness i / (levels - 1), so a site on any other curve samples the wrong lobe.
            string ggx = ShaderSource.Get("cx_probe_ggx.glsl");
            check.Expect(ggx.Contains("float perceptual = float(uLevel) / float(max(levels - 1, 1));")
                         && ggx.Contains("float a = perceptual * perceptual"), "the prefilter bakes perceptual roughness per level");
            foreach (string stage in new[] { "cx_ownlit_tail.glsl", "cx_ownlit_light.glsl", "cx_grass_fmain.glsl", "cx_gblade_fmain.glsl" })
            {
                string src = ShaderSource.Get(stage);
                // the one exemption: the legacy env pattern's MEAN, read at the top mip on purpose
                int fetches = Count(src, "uProbe,"), mapped = Count(src, "CxProbeFetch(uProbe, vWorld, R, sqrt(")
                              + Count(src, "CxProbeFetch(uProbe, vWorld, reflect(-V, N), sqrt("),
                    mean = Count(src, "CxProbeFetch(uProbe, vWorld, R, uProbeMips - 1.0)");
                check.Expect(fetches > 0 && fetches == mapped + mean && Count(src, "* (uProbeMips - 1.0))") == mapped,
                             $"{stage}: every probe fetch is parallax-corrected on the prefilter's sqrt(rough) * (uProbeMips - 1) mapping");
            }
            string mat = ShaderSource.Get("cx_material.glsl"), sh = ShaderSource.Get("cx_probe_sh.glsl"),
                   ssr = ShaderSource.Get("cx_ssr.glsl");
            check.Expect(!mat.Contains("textureLod(uProbe"), "the ambient shape reads the SH9 block, not the cube");
            check.Expect(ssr.Contains($"layout(std140, binding = {ShBinding}) uniform CxProbeSh {{ vec4 uProbeSh[9]; vec4 uProbeAt; }}")
                         && sh.Contains("buffer CxProbeShOut { vec4 uShOut[9]; vec4 uShAt; }")
                         && sh.Contains($"layout(std430, binding = {ShOutBinding}) writeonly buffer"),
                         "the SH9 block's two bindings match the C# side");

            Tex = 9; Ready = true; _bsp = (IntPtr)0x1234;
            Revoke();
            check.Expect(!Ready && _bsp == IntPtr.Zero,
              "the world edge drops the cube's freshness AND its BSP key -- a recycled BSP address must not read as the same area");
            check.Expect(Tex == 9, "...and keeps the GL objects: they belong to the context, not to the world");
            Tex = 0;

            check.Expect(Finite(stackalloc float[] { 0f, 0.5f, 16f }) && !Finite(stackalloc float[] { 0.2f, float.NaN })
                         && !Finite(stackalloc float[] { float.PositiveInfinity }), "a NaN or Inf capture is not published");
            string sky = ShaderSource.Get("cx_sky_f.glsl");
            check.Expect(sky.Contains("mix(clamp(FragmentColor, 0.0, 1.0), vec4(0.0), isnan(FragmentColor))")
                         && sky.Contains("CxOitOut(vec4(rgb, c.a))"),
                         "a hosted sky stage is clamped as the engine's UNORM target clamps it, NaN to 0");
        }
        finally { _log = quiet; }
        log.Info(check.Ok ? "EnvProbe.SelfCheck: passed" : "EnvProbe.SelfCheck: FAILED");
    }

    /// <summary>Is a capture due? True only on the edges that change what the probe should contain: never
    /// captured, a different area (the Scene's static tile BSP root is per-area -- <c>Scene::InitBSP</c>
    /// builds one per area load), or the player has walked <see cref="MoveDist"/> out of the last one. The
    /// frame floor sits in front of all three.</summary>
    internal static bool Wanted(Vector3 eye, IntPtr bspRoot, int frame)
    {
        if (!_on || _down.Failed || Gl.ClearColor == null) return false;
        if (frame - _lastFrame < MinFrames) return false;
        return !Ready || bspRoot != _bsp || Vector3.DistanceSquared(eye, Origin) > MoveDist * MoveDist;
    }

    /// <summary>Open a capture: build the cube if needed and latch what this one is of. The caller then draws
    /// each face between <see cref="Face"/> and closes with <see cref="End"/>.
    /// <para><see cref="Ready"/> drops for the duration: the lit stage must not sample a cube that is
    /// currently an FBO attachment, and the capture draws through that same stage.</para></summary>
    internal static bool Begin(Vector3 eye, IntPtr bspRoot, int frame, float far, Vector3 clear)
    {
        if (!Ensure()) return false;
        Origin = eye; _bsp = bspRoot; _lastFrame = frame;
        _far = far > FaceNear * 4f ? far : FaceNear * 4f;
        Ready = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Viewport(0, 0, Size, Size);
        // alpha 0 = nothing there, infinitely far: the parallax correction leaves the direction alone
        Gl.ClearColor(clear.X, clear.Y, clear.Z, 0f);
        // srgb off: the cube holds linear radiance, and an sRGB-encoding target would encode it a second
        // time on write.
        // #292: cull ON, same as the screen pass -- a probe capture re-enters the SAME batch loop six more
        // times (see AlloyRenderer._capturing), so its faces get the per-material decision per batch and this
        // only states the default underneath them. It is not "camera-facing by construction and therefore
        // exempt": a cube face sees the world's back faces exactly as the screen does, and leaving culling
        // off here would bake a solid cave ceiling into the reflection the screen pass no longer shows.
        new PassState(depth: true, depthWrite: true, cull: true, srgb: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        return true;
    }

    /// <summary>Attach face <paramref name="face"/> (0..5 = +X,-X,+Y,-Y,+Z,-Z) and clear it, handing back the
    /// exactly-90&deg; view-projection to draw it with. Same face basis the shadow cubes render and sample
    /// with, so the reflection vector round-trips.</summary>
    internal static bool Face(int face, out Matrix4x4 vp)
    {
        vp = ShadowMath.PointLightFaceViewProj(Origin, face, FaceNear, _far);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0,
                                Gl.TEXTURE_CUBE_MAP_POSITIVE_X + (uint)face, _cap, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Fail("the probe framebuffer is incomplete"); return false; }
        Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        return true;
    }

    /// <summary>Close the capture: box-mip it, prefilter it into <see cref="Tex"/> and project its SH9, then
    /// publish.</summary>
    internal static void End()
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.GenerateTextureMipmap(_cap);
        Prefilter();
        Captures++;
        // the roughest level's six faces, one texel each: a black probe shows here and nowhere else, and a
        // NaN anywhere in the capture has reached this level through the box chain
        float* px = stackalloc float[24];
        Gl.MemoryBarrier(Gl.TEXTURE_UPDATE_BARRIER_BIT);
        if (Gl.GetTextureImage != null) Gl.GetTextureImage(Tex, Mips - 1, Gl.RGBA, Gl.FLOAT, 24 * sizeof(float), px);
        Ready = Finite(new ReadOnlySpan<float>(px, 24));
        _log?.Info($"EnvProbe: captured area probe #{Captures} at ({Origin.X:F0},{Origin.Y:F0},{Origin.Z:F0}) " +
                   $"-- {Size}px x 6 faces, {Mips} mips, area bsp 0x{(ulong)_bsp:x}; " +
                   $"up ({px[16]:F2},{px[17]:F2},{px[18]:F2}) down ({px[20]:F2},{px[21]:F2},{px[22]:F2})");
        if (!Ready && _poisoned++ == 0)
            _log?.Always($"EnvProbe: capture #{Captures} holds NaN/Inf -- NOT PUBLISHED, the area has no ambient " +
                         "specular or ambient direction until a clean capture (a stage in the capture wrote a " +
                         "non-finite colour)");
    }

    /// <summary>Non-finite captures so far; only the first is logged loudly (a refused probe re-captures every
    /// <see cref="MinFrames"/>).</summary>
    private static int _poisoned;

    /// <summary>Every value a real number? A NaN texel reads back as 16 through <c>CxProbeFetch</c>'s cap on
    /// NVIDIA (<c>min</c> returns the non-NaN operand), which is a white sheen on every lit surface.</summary>
    internal static bool Finite(ReadOnlySpan<float> v)
    {
        foreach (float f in v) if (!float.IsFinite(f)) return false;
        return true;
    }

    /// <summary>The 4.6 context went away with the objects in it. Nothing is deleted here -- the context that
    /// owned them is gone -- the names are simply dropped so the next capture builds fresh ones.</summary>
    internal static void Forget()
    {
        Tex = _cap = _sh = _progGgx = _progSh = _fbo = 0; _down.Forget();
        Revoke();   // a cube built by a dead context is stale for the world reason too
    }

    /// <summary>#209: the world edge. The cube describes an area that has gone away, and <see cref="_bsp"/>
    /// -- the only thing that would ask for a re-bake -- is a BARE POINTER used as an equality key. The
    /// allocator recycles those, so a new area landing on the old BSP address kept <see cref="Ready"/> set
    /// and lit the new interior with the previous one's env map. The GL objects survive: they are the
    /// context's, not the world's, and the next capture reuses them.</summary>
    internal static void Revoke()
    {
        Ready = false; _bsp = IntPtr.Zero;
    }

    /// <summary>The view the probe is captured with, for the light-cluster build: the FACE's view, not the
    /// player camera's. Reusing the main pass's clusters would light every face with the froxels binned for
    /// the screen -- wrong point lights in exactly the interiors the probe exists for.</summary>
    internal static Matrix4x4 FaceView(int face) => ShadowMath.CubeFaceView(Origin, face);

    internal static (float near, float far) FacePlanes => (FaceNear, _far);

    /// <summary>The SH9 block onto <see cref="ShBinding"/>, from every lit-program bind. Created here on first
    /// use (zeroed) so the block is backed even before a capture; <c>uProbeMips</c> 0 keeps it unread till then.</summary>
    internal static void BindSh()
    {
        if (_sh == 0)
        {
            uint b; Gl.CreateBuffers(1, &b);
            byte* zero = stackalloc byte[ShBytes];
            new Span<byte>(zero, ShBytes).Clear();
            Gl.NamedBufferStorage(b, (IntPtr)ShBytes, zero, 0);
            _sh = b;
        }
        Gl.BindBufferBase(Gl.UNIFORM_BUFFER, ShBinding, _sh);
    }

    /// <summary>The two CX-46 dispatches over the fresh capture: GGX into every level of <see cref="Tex"/>, SH9
    /// into <see cref="_sh"/>. Compute, so no raster state is touched; the program and the unit are put back.</summary>
    private static void Prefilter()
    {
        int prev; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, _cap);
        Gl.UseProgram(_progGgx);
        for (int i = 0; i < Mips; i++)
        {
            uint groups = (uint)(Math.Max(Size >> i, 1) + 7) / 8;
            Gl.Uniform1i(_locLevel, i);
            Gl.BindImageTexture(0, Tex, i, 1, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
            Gl.DispatchCompute(groups, groups, 6);
        }
        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
        BindSh();
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, ShOutBinding, _sh);
        Gl.UseProgram(_progSh);
        Gl.Uniform3f(_locOrigin, Origin.X, Origin.Y, Origin.Z);
        Gl.DispatchCompute(1, 1, 1);
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.UNIFORM_BARRIER_BIT);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, ShOutBinding, 0);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
        Gl.UseProgram((uint)prev);
    }

    private static bool Ensure()
    {
        if (_down.Failed) return false;
        if (Tex != 0) return true;
        if (Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null || Gl.ClearColor == null)
        { Fail("a required GL entry point for the probe is missing"); return false; }
        if (Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.MemoryBarrier == null || Gl.CreateBuffers == null)
        { Fail("compute, image stores or buffer creation are missing, so the probe cannot be prefiltered"); return false; }
        if (_progGgx == 0)
        {
            _progGgx = GlProgram.Compute("cx_probe_ggx", ShaderSource.Get("cx_probe_ggx.glsl"), _log, "EnvProbe GGX prefilter");
            _progSh = GlProgram.Compute("cx_probe_sh", ShaderSource.Get("cx_probe_sh.glsl"), _log, "EnvProbe SH9 projection");
            if (_progGgx == 0 || _progSh == 0)
            { Fail("the prefilter or SH9 program did not build"); return false; }
            _locLevel = GlProgram.Loc(_progGgx, "uLevel");
            _locOrigin = GlProgram.Loc(_progSh, "uOrigin");
        }

        Mips = 1;
        for (int s = Size; s > 1; s >>= 1) Mips++;

        uint cap = Cube(), t = Cube();
        if (cap == 0 || t == 0)
        {
            if (cap != 0) Gl.DeleteTextures(1, &cap);
            if (t != 0) Gl.DeleteTextures(1, &t);
            Fail($"two {Size}x{Size} RGBA16F cubes could not be allocated (out of video memory?)");
            return false;
        }
        // Global state, not a texture parameter: without it a roughness mip fetch near a face border blends
        // against the edge clamp instead of the neighbouring face, which reads as a seam in the reflection.
        Gl.Enable(Gl.TEXTURE_CUBE_MAP_SEAMLESS);

        uint d = 0;
        RenderTarget.AllocDepth(ref d, Gl.DEPTH_COMPONENT24, Size, Size);

        uint f2; Gl.GenFramebuffers(1, &f2);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, f2);
        // One depth buffer for all six faces: they are rendered one at a time and each clears it. Only the
        // colour attachment changes per face.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, d, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);

        Tex = t; _cap = cap; _fbo = f2;
        return true;
    }

    /// <summary>One <see cref="Size"/>-square RGBA16F cube with the full chain and trilinear clamp, or 0 when
    /// the allocation raised.</summary>
    private static uint Cube()
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_CUBE_MAP, 1, &t);
        Gl.DrainErrors();   // an earlier pass's error must not be read as this allocation's
        Gl.TextureStorage2D(t, Mips, Gl.RGBA16F, Size, Size);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_R, Gl.CLAMP_TO_EDGE);
        return t;
    }

    /// <summary>Occurrences of <paramref name="what"/> in <paramref name="s"/>, for SelfCheck's source contracts.</summary>
    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Stand the probe down by name for the session. The Alloy pass keeps drawing; it simply feeds
    /// no ambient specular, which is its pre-#121 behaviour rather than a broken frame.</summary>
    private static void Fail(string why)
    {
        Ready = false;
        _down.Fail(_log, why, "no IBL probe this session (Alloy parts keep direct lighting only)");
    }
}
