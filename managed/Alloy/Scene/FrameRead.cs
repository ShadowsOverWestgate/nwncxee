using System;
using System.Numerics;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>#257 (M5 strand 1 step 2): the frame constants, READ. Camera, area terms, sun and fog come out
/// of the engine's own state once per world frame, at a slot Alloy picks -- not harvested off a rendering
/// call site and disambiguated with a largest-viewport contest (ADR 0018; census E7/E9/E12's feed).
/// <para>The engine still owns the simulation -- that binds permanently. What went is the interception:
/// <c>SetViewTransform</c>, <c>SetPerspectiveTransform</c> and <c>SetLightGL</c> are no longer hooked, and
/// there is no last-writer-wins anywhere on this path.</para>
/// <para><b>Where the values live</b> (all Ghidra-verified against the client):
/// <list type="bullet">
/// <item><c>CurrentCamera</c> -- the global the engine writes at <c>Camera::RenderScene</c> @0x4a822c and
/// clears at @0x4a8324, i.e. it holds exactly the camera whose scene is rendering. That is what makes the
/// contest unnecessary rather than merely cheaper: read inside the world scene's render, this IS the scene
/// camera, and a GUI 3D panel's camera can only be here while its own scene renders.</item>
/// <item><c>Camera</c> fields, from the engine's own accessors: position <c>+0xb0</c>
/// (<c>Camera::GetPosition</c>), orientation <c>+0xbc</c> (<c>GetOrientation</c>, stored (w, x, y, z)),
/// view angle <c>+0x4c4</c> (<c>GetViewAngle</c>), near/far <c>+0x4cc</c>/<c>+0x4d0</c>
/// (<c>GetClipDist</c>), viewport w/h <c>+0x4dc</c>/<c>+0x4e0</c> (<c>SetViewPort</c>). The arguments
/// <c>Camera::RenderScene</c> @0x4a7d55..0x4a7db1 passes to <c>SetPerspectiveTransform</c> are these same
/// four fields, and its aspect is the upscaled viewport's w/h -- the upscale multiplies both by the same
/// factor (@0x4a7cbe..0x4a7cff), so it cancels and the ratio is the raw pair's.</item>
/// <item>Area ambient and diffuse -- <c>LightManager+0xa0</c> and <c>+0x98</c>, the two
/// <c>PartLight*</c> <c>LightManager::ControlLightsFade</c> @0x4c2050 pushes through
/// <c>SetLightGL(_, -1, fade)</c>; the fade weight is <c>PartLight+0x90</c>.</item>
/// <item>Fog -- <c>Scene+0x140</c> enable, <c>+0x144</c> start, <c>+0x148</c> end, <c>+0x14c</c> colour,
/// exactly what <c>Scene::DoFog</c> @0x45f1b0 pushes into <c>GLRender::m_fog</c>, including its
/// <c>LightManager::m_pOnlyGob</c> branch (a lit-gob preview forces the fog colour to black).</item>
/// </list></para>
/// <para>Hot-path rules apply: this runs once per world frame in engine context and allocates nothing.</para>
/// </summary>
internal static unsafe class FrameRead
{
    /// <summary>Accept only clearly downward-travelling light as the sun (NWN is Z-up). A horizontal or
    /// below-horizon directional would light undersides and read as garbage in the cascades.</summary>
    private const float DownwardZ = -0.05f;

    private static IntPtr _currentCamera;
    private static IntPtr _onlyGob;
    private static ExtenderLog? _log;

    /// <summary>Resolved enough to read a camera. False leaves every constant at its last value, which for a
    /// fresh session is "none seen".</summary>
    internal static bool Ready { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _currentCamera = Native.Sym(SymbolMap.CurrentCamera);
        _onlyGob = Native.Sym(SymbolMap.LightManagerOnlyGob);
        Ready = _currentCamera != IntPtr.Zero;
        if (!Ready) log.Info("FrameRead: CurrentCamera unresolved -- Alloy has no camera, no sun and no fog");
        SelfCheck(log);
    }

    /// <summary>Read this frame's constants. Called once per world frame from the frame anchor, after the
    /// engine's <c>EnableLights</c> has advanced the fade weights and before anything draws.</summary>
    internal static void Tick(IntPtr scene)
    {
        // The main menu's 3D vista renders through a camera too, and seeding the constants off it would
        // hand the first in-game frame a camera and an area colour that belong to nothing (CameraCapture
        // gated its captures the same way, for the same reason).
        if (!Ready || scene == IntPtr.Zero || !GameGate.InModule) return;
        ReadCamera();
        ReadFog(scene);
        ReadAreaTerms(*(IntPtr*)(scene + SceneLightManager));
    }

    private static void ReadCamera()
    {
        IntPtr cam = *(IntPtr*)_currentCamera;
        // No camera bound means we are outside a scene render; the last frame's constants stand rather than
        // collapsing to zero, which is what every consumer's Has* flag already expects.
        if (cam == IntPtr.Zero) return;
        float* p = (float*)(cam + CamPos);
        float* q = (float*)(cam + CamRot);
        FrameInputs.CameraPos = new Vector3(p[0], p[1], p[2]);
        // (w, x, y, z) in the engine, (x, y, z, w) in System.Numerics -- and the rotation matrix
        // MakeMatrix4x4 @0x5b8ce0 builds from it is Numerics' CreateFromQuaternion term for term.
        FrameInputs.CameraRot = new Quaternion(q[1], q[2], q[3], q[0]);
        FrameInputs.HasCamera = true;

        FrameInputs.CamViewW = *(float*)(cam + CamViewW);
        FrameInputs.CamViewH = *(float*)(cam + CamViewH);
        FrameInputs.CamFovy = *(float*)(cam + CamViewAngle);
        FrameInputs.CamAspect = FrameInputs.CamViewH > 0f ? FrameInputs.CamViewW / FrameInputs.CamViewH : 1f;
        FrameInputs.CamNear = *(float*)(cam + CamNear);
        FrameInputs.CamFar = *(float*)(cam + CamFar);
        FrameInputs.HasPerspective = FrameInputs.CamFovy > 0f;

        // The scene camera the shadow fit rides (#99) is now the same read, so there is nothing left to
        // latch: SceneCamFwd is the camera's own -Z, and p11 is the projection term its view angle gives.
        FrameInputs.SceneCamPos = FrameInputs.CameraPos;
        FrameInputs.SceneCamFwd = ViewForward(FrameInputs.CameraRot);
        FrameInputs.SceneCamP11 = P11(FrameInputs.CamFovy);
        FrameInputs.HasSceneCam = true;
    }

    /// <summary>World-space direction the camera looks along: view space looks down -Z (OpenGL), and the
    /// camera quaternion is the view-&gt;world rotation (<c>SetViewTransform</c> feeds
    /// <c>MakeMatrix4x4</c> the CONJUGATE, <c>operator-</c> @0x6b5d50 negating only the scalar).</summary>
    internal static Vector3 ViewForward(Quaternion rot) => Vector3.Transform(new Vector3(0f, 0f, -1f), rot);

    /// <summary>The projection's <c>p11</c> = <c>1 / tan(fovy / 2)</c>, from a view angle in DEGREES (which
    /// is what <c>Camera::GetViewAngle</c> holds -- <c>SetPerspectiveTransform</c> takes degrees).</summary>
    internal static float P11(float fovyDegrees)
    {
        float half = fovyDegrees * (MathF.PI / 360f);
        float t = MathF.Tan(half);
        return t > 1e-6f ? 1f / t : 0f;
    }

    private static void ReadFog(IntPtr scene)
    {
        byte* s = (byte*)scene;
        bool on = *(int*)(s + SceneFogEnable) != 0;
        FrameInputs.FogOn = on;
        if (!on) return;
        FrameInputs.FogStart = *(float*)(s + SceneFogStart);
        FrameInputs.FogEnd = *(float*)(s + SceneFogEnd);
        // DoFog's own branch: while a single gob is being previewed in isolation the fog colour is forced
        // to black rather than the area's.
        if (_onlyGob != IntPtr.Zero && *(IntPtr*)_onlyGob != IntPtr.Zero) { FrameInputs.FogColor = default; return; }
        float* c = (float*)(s + SceneFogColor);
        FrameInputs.FogColor = new Vector3(c[0], c[1], c[2]);
    }

    /// <summary>The two index -1 lights, read where they live instead of intercepted where they are pushed.
    /// Colours land LINEAR, the same curve <c>RenderInterface::GetColorSpaceFromSRGB</c> applies before the
    /// engine's own uniform, because the Alloy pass shades in linear.</summary>
    private static void ReadAreaTerms(IntPtr lm)
    {
        if (lm == IntPtr.Zero) return;
        IntPtr amb = *(IntPtr*)((byte*)lm + LmAreaAmbient);
        IntPtr dif = *(IntPtr*)((byte*)lm + LmAreaDiffuse);
        if (amb != IntPtr.Zero) ApplyAreaTerm((byte*)amb);
        if (dif != IntPtr.Zero) ApplyAreaTerm((byte*)dif);
    }

    /// <summary>Route one index -1 light to the term its own <c>type@0x80</c> says it drives -- ambient,
    /// or the directional diffuse, which when it reaches far enough is also the sun.</summary>
    private static void ApplyAreaTerm(byte* p)
    {
        int type = *(int*)(p + LightKind);
        float fade = *(float*)(p + LightFade);
        float* c = (float*)(p + LightColour);
        var col = new Vector3(MathF.Pow(MathF.Max(c[0] * fade, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c[1] * fade, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c[2] * fade, 0f), AlloyLights.SrgbGamma));
        if (type != 0)
        {
            FrameInputs.AreaAmbient = col;
            FrameInputs.HasAreaAmbient = true;
            return;
        }
        float* d = (float*)(p + LightDir);
        var toLight = new Vector3(d[0], d[1], d[2]);
        if (toLight.LengthSquared() <= 1e-8f) return;
        FrameInputs.AreaDiffuse = col;
        FrameInputs.AreaDiffuseDir = Vector3.Normalize(toLight);
        FrameInputs.HasAreaDiffuse = true;

        // ...and the same light IS the sun, when it reaches like one. +0xac points TO the light; the light
        // TRAVELS the other way, which is the direction the cascades render along. Set, never cleared: an
        // interior has no sun and CasterPass gates on FrameInputs.Interior for exactly that reason, so
        // clearing here would only make an exterior's first frames shadowless.
        if (*(float*)(p + LightRange) <= DirectionalRange) return;
        Vector3 travel = -Vector3.Normalize(toLight);
        if (travel.Z < DownwardZ) { FrameInputs.SunDir = travel; FrameInputs.HasSun = true; }
    }

    /// <summary>The engine's OWN area-light uniforms, straight out of the globals
    /// <c>SetAreaDiffuseLight</c>/<c>SetAreaAmbientLight</c> store into (verified in asm: three movss and a
    /// dirty-sequence bump, no scale). What <see cref="ApplyAreaTerm"/> derives should equal these, and the
    /// two are printed side by side rather than trusted.
    /// <para>Reads nothing of Alloy's, so it answers in a <c>vanilla=1</c> session too -- which is the point:
    /// SoW drives area lighting from a server-side day/night palette, so an Alloy frame and a vanilla frame
    /// captured in different sessions are not comparable until these numbers say they are.</para></summary>
    internal static string EngineAreaTerms()
    {
        float* dif = (float*)Native.Sym(SymbolMap.LightAreaDiffuse);
        float* amb = (float*)Native.Sym(SymbolMap.LightAreaAmbient);
        float* dir = (float*)Native.Sym(SymbolMap.LightAreaDiffuseDir);
        if (dif == null || amb == null || dir == null) return "area uniforms unresolved";
        return $"diffuse=({dif[0]:F3},{dif[1]:F3},{dif[2]:F3}) ambient=({amb[0]:F3},{amb[1]:F3},{amb[2]:F3}) " +
               $"dir=({dir[0]:F3},{dir[1]:F3},{dir[2]:F3})";
    }

    /// <summary>Pure-math check of the two derivations that have no other witness: the view forward the
    /// camera quaternion gives, and the projection term its view angle gives. Headless-safe.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        // Identity: the camera looks down -Z, i.e. straight down in NWN's Z-up world.
        Vector3 f = ViewForward(Quaternion.Identity);
        ok &= MathF.Abs(f.Z + 1f) < 1e-4f && MathF.Abs(f.X) < 1e-4f && MathF.Abs(f.Y) < 1e-4f;
        // A quarter turn about X tips that forward onto +Y (right-handed, Z-up).
        Vector3 t = ViewForward(Quaternion.CreateFromAxisAngle(new Vector3(1f, 0f, 0f), MathF.PI / 2f));
        ok &= MathF.Abs(t.Y - 1f) < 1e-4f && MathF.Abs(t.Z) < 1e-4f;
        // 90 degrees of vertical FOV puts tan(45) = 1 in the denominator.
        ok &= MathF.Abs(P11(90f) - 1f) < 1e-4f;
        ok &= P11(0f) == 0f;
        log.Info(ok ? "FrameRead.SelfCheck: ok (view forward, p11)"
                    : "FrameRead.SelfCheck: FAILED -- the camera quaternion or FOV convention drifted");
    }
}
