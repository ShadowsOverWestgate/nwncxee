using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The frame constants, READ: camera, area terms, sun and fog come out of the engine's own state
/// once per world frame, at a slot Alloy picks -- not harvested off a rendering call site (ADR 0018 M5
/// strand 1 step 2, #257). The engine still owns the simulation; nothing on this path is hooked and there is
/// no last-writer-wins.
/// <para><b>Where the values live</b> (all Ghidra-verified against the client):
/// <list type="bullet">
/// <item><c>CurrentCamera</c> -- the global the engine writes at <c>Camera::RenderScene</c> @0x4a822c and
/// clears at @0x4a8324, so it holds exactly the camera whose scene is rendering: read inside the world scene's
/// render, this IS the scene camera, and a GUI 3D panel's camera can only be here while its own scene
/// renders.</item>
/// <item><c>Camera</c> fields, from the engine's own accessors: position <c>+0xb0</c>
/// (<c>Camera::GetPosition</c>), orientation <c>+0xbc</c> (<c>GetOrientation</c>, stored (w, x, y, z)),
/// view angle <c>+0x4c4</c> (<c>GetViewAngle</c>), near/far <c>+0x4cc</c>/<c>+0x4d0</c>
/// (<c>GetClipDist</c>), viewport w/h <c>+0x4dc</c>/<c>+0x4e0</c> (<c>SetViewPort</c>). These are the same
/// four fields <c>Camera::RenderScene</c> @0x4a7d55..0x4a7db1 passes to <c>SetPerspectiveTransform</c>; the
/// upscale multiplies w and h by the same factor (@0x4a7cbe..0x4a7cff), so the aspect is the raw pair's.</item>
/// <item>Area ambient and diffuse -- <c>LightManager+0xa0</c> and <c>+0x98</c>, the two <c>PartLight*</c>
/// <c>LightManager::ControlLightsFade</c> @0x4c2050 pushes through <c>SetLightGL(_, -1, fade)</c>; the fade
/// weight is <c>PartLight+0x90</c>.</item>
/// <item>Fog -- <c>Scene+0x140</c> enable, <c>+0x144</c> start, <c>+0x148</c> end, <c>+0x14c</c> colour,
/// exactly what <c>Scene::DoFog</c> @0x45f1b0 pushes into <c>GLRender::m_fog</c>, including its
/// <c>LightManager::m_pOnlyGob</c> branch (a lit-gob preview forces the fog colour to black).</item>
/// </list></para>
/// <para>Hot-path rules apply: once per world frame in engine context, allocates nothing.</para>
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
        var sc = new Scene(scene);
        ReadFog(sc);
        ReadAreaTerms(sc);
    }

    private static void ReadCamera()
    {
        var cam = new Camera(AlloyCamera.World);
        // No camera bound means we are outside a scene render; the last frame's constants stand rather than
        // collapsing to zero, which is what every consumer's Has* flag already expects.
        if (cam.IsNull) return;
        FrameInputs.CameraPos = cam.Pos;
        // Quat holds the engine's (w, x, y, z) and converts on assignment to Numerics' (x, y, z, w) -- and
        // the matrix MakeMatrix4x4 @0x5b8ce0 builds from it is Numerics' CreateFromQuaternion term for term.
        FrameInputs.CameraRot = cam.Rot;
        FrameInputs.HasCamera = true;

        FrameInputs.CamViewW = cam.ViewW;
        FrameInputs.CamViewH = cam.ViewH;
        FrameInputs.CamFovy = cam.ViewAngle;
        FrameInputs.CamAspect = FrameInputs.CamViewH > 0f ? FrameInputs.CamViewW / FrameInputs.CamViewH : 1f;
        FrameInputs.CamNear = cam.Near;
        FrameInputs.CamFar = cam.Far;
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

    private static void ReadFog(Scene scene)
    {
        bool on = scene.FogEnable != 0;
        FrameInputs.FogOn = on;
        if (!on) return;
        FrameInputs.FogStart = scene.FogStart;
        FrameInputs.FogEnd = scene.FogEnd;
        // DoFog's own branch: while a single gob is being previewed in isolation the fog colour is forced
        // to black rather than the area's.
        if (_onlyGob != IntPtr.Zero && *(IntPtr*)_onlyGob != IntPtr.Zero) { FrameInputs.FogColor = default; return; }
        FrameInputs.FogColor = scene.FogColor;
    }

    /// <summary>The two index -1 lights, read where they live instead of intercepted where they are pushed.
    /// Colours land LINEAR, the same curve <c>RenderInterface::GetColorSpaceFromSRGB</c> applies before the
    /// engine's own uniform, because the Alloy pass shades in linear.
    /// <para>CX-108: the pair comes from <see cref="LightRegistry"/>, which picks them the way
    /// <c>LightManager::UpdateLights</c> @0x4c06c0 does -- a light of the area's own set with
    /// <c>range &gt;= 1000</c>, split on <c>kind</c> -- rather than out of <c>LightManager+0x98/+0xa0</c>.
    /// The engine's day/night blend needs no port: <c>CNWCArea::UpdateDayNightCycle</c> @0x706a20 lerps the
    /// area's day and night colour sets and drives these very <c>PartLight</c>s through
    /// <c>CNWCArea::SetGlobalLightAmbient</c>/<c>Diffuse</c>, which is game-layer simulation upstream of the
    /// field, so what is read here already carries the transition.</para></summary>
    private static void ReadAreaTerms(Scene sc)
    {
        if (LightRegistry.Live)
        {
            var ra = new PartLight(LightRegistry.AreaAmbient);
            var rd = new PartLight(LightRegistry.AreaDiffuse);
            if (!ra.IsNull) ApplyAreaTerm(ra);
            if (!rd.IsNull) ApplyAreaTerm(rd);
            return;
        }
        // light.registry 0: the engine's own two fields, which is where CX-85 read them.
        LightManager lm = sc.LightManager;
        if (lm.IsNull) return;
        PartLight amb = lm.AreaAmbient, dif = lm.AreaDiffuse;
        if (!amb.IsNull) ApplyAreaTerm(amb);
        if (!dif.IsNull) ApplyAreaTerm(dif);
    }

    /// <summary>Route one index -1 light to the term its own <c>type@0x80</c> says it drives -- ambient,
    /// or the directional diffuse, which when it reaches far enough is also the sun.</summary>
    private static void ApplyAreaTerm(PartLight light)
    {
        float fade = light.Fade;
        Vector3 c = light.Colour;
        var col = new Vector3(MathF.Pow(MathF.Max(c.X * fade, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Y * fade, 0f), AlloyLights.SrgbGamma),
                              MathF.Pow(MathF.Max(c.Z * fade, 0f), AlloyLights.SrgbGamma));
        if (light.Kind != 0)
        {
            FrameInputs.AreaAmbient = col;
            FrameInputs.HasAreaAmbient = true;
            return;
        }
        Vector3 toLight = light.Pos;
        if (toLight.LengthSquared() <= 1e-8f) return;
        FrameInputs.AreaDiffuse = col;
        FrameInputs.AreaDiffuseDir = Vector3.Normalize(toLight);
        FrameInputs.HasAreaDiffuse = true;

        // ...and the same light IS the sun, when it reaches like one. +0xac points TO the light; the light
        // TRAVELS the other way, which is the direction the cascades render along. Set, never cleared: an
        // interior has no sun and CasterPass gates on FrameInputs.Interior for exactly that reason, so
        // clearing here would only make an exterior's first frames shadowless.
        if (light.Range <= EngineLayout.DirectionalRange) return;
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
