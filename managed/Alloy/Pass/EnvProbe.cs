using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #121 (map #68, spec #114 M3): the per-area image-based lighting probe -- a small prefiltered colour
/// cubemap captured from the Alloy scene draw, standing in for the engine's 2002 pseudo-spheremap as the
/// ambient-specular source of every Alloy lit part.
/// <para>What it replaces: <c>inc_envmap.shd</c>'s sphere branch is not a reflection at all (an octahedral
/// squash of the VIEW-SPACE normal plus a screen-position fudge, biased to (0.5,0.25)), and the texture it
/// squashes is the AREA's authored env map -- which is why indoor metal reflects an outdoor sky. The scene
/// env map at <c>scene+0x1e8</c> is therefore deliberately never bound by the Alloy pass: this cube is what
/// the area actually looks like from where the player stands.</para>
/// <para>Cost: capture is the Alloy draw again, six times, into <see cref="Size"/>-square faces -- and it
/// runs on an area change, not per frame (<see cref="Wanted"/>). It happens inside the colour bracket the
/// Alloy pass already opened, so it costs no additional GL bracket.</para>
/// <para>The cube stores LINEAR radiance in RGBA16F: <c>glGenerateMipmap</c> box-filters whatever is in the
/// texture, and filtering display-space values darkens them. The capture stage skips its display-space
/// encode for exactly this reason (<c>uCapture</c>), and the sampler needs no linearisation.</para>
/// <para>ponytail: the roughness blur is the plain mip chain, not a GGX-importance-sampled prefilter. It is
/// the classic cheap split-sum stand-in and already strictly better than the stock path (which clamps cube
/// sampling to LOD 0 and gets no blur at all). Upgrade path: a prefilter pass over the chain if rough metal
/// reads too sharp.</para>
/// </summary>
internal static unsafe class EnvProbe
{
    /// <summary>Face resolution. Small on purpose -- this is an irradiance-ish source, not a mirror, and the
    /// whole cube is one 6 x 128^2 RGBA16F allocation (~0.8MB with mips). Config <c>probe_size</c> overrides:
    /// it sizes an allocation, so it is a boot setting rather than a <c>/probe</c> key.</summary>
    internal static int Size { get; private set; } = 128;
    /// <summary>Levels in the mip chain: roughness 1 maps onto the last one.</summary>
    internal static int Mips { get; private set; }
    /// <summary>The cube, or 0 when it has not been built. Sampled by the Alloy lit stage on
    /// <see cref="ProbeUnit"/>.</summary>
    internal static uint Tex { get; private set; }
    /// <summary>A capture has COMPLETED. Until then the Alloy pass feeds no ambient specular at all, which
    /// is exactly its pre-#121 behaviour -- never a half-written cube.</summary>
    internal static bool Ready { get; private set; }
    /// <summary>World point the live cube was captured from. The reflection vector is not parallax-corrected
    /// against it, so it is also the centre the "has the player walked out of this probe" test measures from.</summary>
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

    private static uint _fbo;
    private static bool _on = true;
    private static readonly StandDown _down = new("EnvProbe");
    private static IntPtr _bsp;
    private static int _lastFrame = -MinFrames;
    private static ExtenderLog? _log;
    private static float _far = 500f;

    /// <summary><c>/probe gain</c>: how much presence the ambient-specular term gets over the engine's own
    /// weighting. 1.0 IS the engine's weighting, and higher is a deliberate departure -- the term multiplies
    /// the light already arriving at the fragment, so raising it far past 1 puts the wet-stone look back.</summary>
    internal static float Gain { get; private set; } = 1f;

    /// <summary><c>/probe areawide 1</c>: also feed the probe to parts whose material declares NO env map, in
    /// areas that set one on the Scene (<c>scene+0x1e8</c>) -- which is where <c>RenderFlat</c> binds the
    /// scene's env map to the sampler, so it is the vanilla-faithful reading of the term.
    /// <para>OFF by default, on live evidence: the engine's authored spheremap is a dim texel that reads as
    /// nothing on ordinary geometry, while the probe carries real scene radiance -- so being faithful to the
    /// BINDING made city cobblestone reflect the sky, which vanilla plainly does not do. What is left is the
    /// case the reflection is actually for: a material that declares an env map (81% of weapon models).</para></summary>
    internal static bool AreaWide { get; private set; }

    private static float _bootGain;
    private static bool _bootAreaWide;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        Size = Settings.Int("probe_size", Size, 16, 1024);
        _bootGain = Gain; _bootAreaWide = AreaWide;
    }

    internal static string Describe()
        => $"probe {(_on ? "on" : "off")}  gain {Gain:F2}  areawide {(AreaWide ? 1 : 0)}  " +
           $"size {Size}px  captures {Captures}";

    /// <summary>Live tuning behind <c>/probe</c>; returns the old value, NaN for an unknown key. Same
    /// no-lock reasoning as <see cref="PostChain.Tune"/>: independent values, written from the input thread,
    /// re-read per capture. <c>on</c> gates <see cref="Wanted"/>, which is re-asked every frame, so the probe
    /// idles rather than un-arming and a re-arm captures at the next edge.</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "on":       old = _on ? 1f : 0f; _on = v > 0f; break;
            case "gain":     old = Gain; Gain = v; break;
            case "areawide": old = AreaWide ? 1f : 0f; AreaWide = v > 0f; break;
            default: return float.NaN;
        }
        _log?.Info($"EnvProbe: /probe {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    /// <summary>Put every <c>/probe</c> dial back to its boot value. Size is not among them: it is the cube's
    /// allocation, and the config file owns it.</summary>
    internal static void ResetTuning()
    {
        _on = true; Gain = _bootGain; AreaWide = _bootAreaWide;
        _log?.Info($"EnvProbe: /probe reset -> {Describe()}");
    }

    /// <summary>Off-device (run-demo.sh): the dial table is reachable, an unknown key is reported, and every
    /// knob the check drove is restored -- a missed one ships its test value as the boot value.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"EnvProbe.SelfCheck FAIL: {w}"); } }

        float gain = Gain; bool wide = AreaWide, on = _on;
        try
        {
            E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            foreach (string key in new[] { "on", "gain", "areawide" })
                E(!float.IsNaN(Tune(key, 1f)), $"knob \"{key}\" is reachable");
            E(float.IsNaN(Tune("size", 256f)),
              "size is NOT tunable: it allocates the cube, so it is the config setting probe_size");
            E(Tune("on", 0f) == 1f && !_on, "/probe on 0 idles the probe rather than un-arming it");
            float boot = _bootGain; _bootGain = 0.25f;
            ResetTuning();
            E(Gain == 0.25f && _on, "reset honours the BOOT value and re-arms");
            _bootGain = boot;

            // #209: the world edge, which the probe had no slot for at all. Both halves matter -- dropping
            // Ready without the key would re-bake once and then trust the stale key again, and dropping the
            // key without Ready leaves the cube readable until the player walks MoveDist.
            Tex = 9; Ready = true; _bsp = (IntPtr)0x1234;
            Revoke();
            E(!Ready && _bsp == IntPtr.Zero,
              "the world edge drops the cube's freshness AND its BSP key -- a recycled BSP address must not read as the same area");
            E(Tex == 9, "...and keeps the GL objects: they belong to the context, not to the world");
            Tex = 0;
        }
        finally
        {
            // Driven through Tune, so the knobs stamped come from the KEY TABLE rather than from a list
            // written beside the restore -- an assertion built from the restore's own list passes whatever
            // that list forgets. Describe prints every knob, so a missed one surfaces as its own sentinel.
            const float Sentinel = -12345f;
            foreach (string key in new[] { "on", "gain", "areawide" }) Tune(key, Sentinel);
            Gain = gain; AreaWide = wide; _on = on;
            E(!Describe().Contains("-12345"), "the check restores every knob it drove");
            _log = quiet;
        }
        log.Info(ok ? "EnvProbe.SelfCheck: passed" : "EnvProbe.SelfCheck: FAILED");
    }

    /// <summary>Is a capture due? True only on the edges that change what the probe should contain: never
    /// captured, a different area (the Scene's static tile BSP root is per-area -- <c>Scene::InitBSP</c>
    /// builds one per area load), or the player has walked <see cref="MoveDist"/> out of the last one. The
    /// frame floor sits in front of all three.</summary>
    internal static bool Wanted(Vector3 eye, IntPtr bspRoot, int frame)
    {
        if (!_on || _down.Failed || Gl.GenerateMipmap == null || Gl.ClearColor == null) return false;
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
        Gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
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
                                Gl.TEXTURE_CUBE_MAP_POSITIVE_X + (uint)face, Tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Fail("the probe framebuffer is incomplete"); return false; }
        Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        return true;
    }

    /// <summary>Close the capture: build the mip chain (the roughness blur) and publish the cube.</summary>
    internal static void End()
    {
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, Tex);
        Gl.GenerateMipmap(Gl.TEXTURE_CUBE_MAP);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Ready = true;
        Captures++;
        _log?.Info($"EnvProbe: captured area probe #{Captures} at ({Origin.X:F0},{Origin.Y:F0},{Origin.Z:F0}) " +
                   $"-- {Size}px x 6 faces, {Mips} mips, area bsp 0x{(ulong)_bsp:x}");
    }

    /// <summary>The 4.6 context went away with the objects in it. Nothing is deleted here -- the context that
    /// owned them is gone -- the names are simply dropped so the next capture builds fresh ones.</summary>
    internal static void Forget()
    {
        Tex = 0; _fbo = 0; _down.Forget();
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

    private static bool Ensure()
    {
        if (_down.Failed) return false;
        if (Tex != 0) return true;
        if (Gl.GenTextures == null || Gl.TexImage2D == null || Gl.GenFramebuffers == null
            || Gl.FramebufferTexture2D == null || Gl.GenerateMipmap == null || Gl.ClearColor == null)
        { Fail("a required GL entry point for the probe is missing"); return false; }

        Mips = 1;
        for (int s = Size; s > 1; s >>= 1) Mips++;

        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, t);
        while (Gl.GetError() != 0) { }   // an earlier pass's error must not be read as this allocation's
        for (uint f = 0; f < 6; f++)
            Gl.TexImage2D(Gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, 0, (int)Gl.RGBA16F, Size, Size, 0,
                          Gl.RGBA, Gl.HALF_FLOAT, null);
        if (Gl.GetError() != 0)
        {
            Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);
            Gl.DeleteTextures(1, &t);
            Fail($"a {Size}x{Size} RGBA16F cube could not be allocated (out of video memory?)");
            return false;
        }
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_WRAP_R, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_CUBE_MAP, Gl.TEXTURE_MAX_LEVEL, Mips - 1);
        // Global state, not a texture parameter: without it a roughness mip fetch near a face border blends
        // against the edge clamp instead of the neighbouring face, which reads as a seam in the reflection.
        Gl.Enable(Gl.TEXTURE_CUBE_MAP_SEAMLESS);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, 0);

        uint d; Gl.GenTextures(1, &d);
        RenderTarget.AllocDepth(d, Gl.DEPTH_COMPONENT24, Size, Size);

        uint f2; Gl.GenFramebuffers(1, &f2);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, f2);
        // One depth buffer for all six faces: they are rendered one at a time and each clears it. Only the
        // colour attachment changes per face.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, d, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);

        Tex = t; _fbo = f2;
        return true;
    }

    /// <summary>Stand the probe down by name for the session. The Alloy pass keeps drawing; it simply feeds
    /// no ambient specular, which is its pre-#121 behaviour rather than a broken frame.</summary>
    private static void Fail(string why)
    {
        Ready = false;
        _down.Fail(_log, why, "no IBL probe this session (Alloy parts keep direct lighting only)");
    }
}
