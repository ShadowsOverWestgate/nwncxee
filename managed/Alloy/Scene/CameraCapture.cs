using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// Slice 2/3 of the shadow-map replacement (#65): continuously captures the pipeline's live inputs
/// into <see cref="FrameInputs"/> for the renderer, and (under <c>/shadow diag 1</c>) logs a
/// one-time human-readable dump. Hooks the camera view set and the light push, both **call-through**
/// (observe, not replace). Always runs <see cref="ShadowMath.SelfCheck"/> (headless-safe).
/// </summary>
/// <remarks>
/// <para>ABIs (both static free/GLRender functions -- no <c>this</c>): SetViewTransform is
/// <c>(Vector&amp; pos /*rdi*/, Quaternion&amp; rot /*rsi*/)</c>; SetLightGL is
/// <c>(PartLight* /*rdi*/, int idx /*esi*/, float scale /*xmm0*/)</c>.</para>
/// <para>The sun (directional diffuse light) is <c>idx==-1 &amp;&amp; type@0x80==0 &amp;&amp;
/// range@0x70 huge</c>; its direction is the normalized Vector at <c>PartLight+0xac</c> (<c>+0x64</c>
/// is the colour). Capture is continuous and alloc-free (pure struct stores) -- sun and camera move
/// every frame; the one-time formatted log is diag-only. Captures gate on <see cref="GameGate.InModule"/>
/// so the main-menu 3D vista doesn't seed them.</para>
/// </remarks>
public sealed unsafe class CameraCapture : IExtenderService
{
    private static ExtenderLog? _log;

    private static delegate* unmanaged<IntPtr, IntPtr, void> _viewOrig;
    private static bool _armed;
    private static delegate* unmanaged<double, double, double, double, void> _perspOrig;

    private static bool _camLogged, _sunLogged, _perspLogged;
    private static int _lightsLogged;
    private const int LightLogCount = 6;

    // The PartLight colour SetLightGL reads (verified by disasm, research doc §6.4 + slice-2 diag); the
    // range/kind/direction triple it reads alongside is shared, in SceneLayout.
    private const int LightColor = 0x64;
    private const float DirectionalRange = 1000f;   // sun/moon carry range ~100000 (infinite); points ~10.
    private const float DownwardZ = -0.05f;          // accept only clearly downward-travelling light (Z-up world).

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        ShadowMath.SelfCheck(log);   // pure math, runs headless

        if (!Settings.Bool("shadows", true)) return;   // one kill switch for the pipeline

        _viewOrig = (delegate* unmanaged<IntPtr, IntPtr, void>)hooks.Install(
            SymbolMap.SetViewTransform, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnSetViewTransform);
        _lightSlot = hooks.Chain(SymbolMap.SetLightGL,   // shared symbol (#81/#88)
            (IntPtr)(delegate* unmanaged<IntPtr, int, float, void>)&OnSetLightGL, LightPriority);
        _armed = true;
        _perspOrig = (delegate* unmanaged<double, double, double, double, void>)hooks.Install(
            SymbolMap.SetPerspectiveTransform,
            (IntPtr)(delegate* unmanaged<double, double, double, double, void>)&OnSetPerspective);

        log.Info("CameraCapture: capturing camera + sun + perspective into FrameInputs");
    }

    [UnmanagedCallersOnly]
    private static void OnSetViewTransform(IntPtr pos, IntPtr rot)
    {
        if (GameGate.InModule && pos != IntPtr.Zero && rot != IntPtr.Zero)
        {
            float* v = (float*)pos;
            float* q = (float*)rot;
            FrameInputs.CameraPos = new Vector3(v[0], v[1], v[2]);
            FrameInputs.CameraRot = new Quaternion(q[0], q[1], q[2], q[3]);
            FrameInputs.HasCamera = true;
            if (SceneInputs.Diag && !_camLogged)
            {
                _camLogged = true;
                _log?.Info($"[shadowdiag] camera pos=({v[0]:F2},{v[1]:F2},{v[2]:F2}) " +
                           $"quat=({q[0]:F3},{q[1]:F3},{q[2]:F3},{q[3]:F3})");
            }
        }
        if (_viewOrig != null) _viewOrig(pos, rot);
    }

    [UnmanagedCallersOnly]
    private static void OnSetPerspective(double fovy, double aspect, double near, double far)
    {
        if (GameGate.InModule)
        {
            FrameInputs.CamFovy = (float)fovy;
            FrameInputs.CamAspect = (float)aspect;
            FrameInputs.CamNear = (float)near;
            FrameInputs.CamFar = (float)far;
            if ((float)far > FrameInputs.FarMaxThisFrame) FrameInputs.FarMaxThisFrame = (float)far;
            FrameInputs.HasPerspective = true;
            if (SceneInputs.Diag && !_perspLogged)
            {
                _perspLogged = true;
                _log?.Info($"[shadowdiag] perspective fovy={fovy:F1} aspect={aspect:F3} near={near:F2} far={far:F1}");
            }
        }
        if (_perspOrig != null) _perspOrig(fovy, aspect, near, far);
    }

    /// <summary>Chain priority on SetLightGL: pure observer, order among observers is irrelevant.</summary>
    private const int LightPriority = 50;
    private static IntPtr* _lightSlot;

    /// <summary>Post-call observer: the engine's bind runs first, we only read the light.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetLightGL(IntPtr light, int idx, float scale)
    {
        var next = (delegate* unmanaged<IntPtr, int, float, void>)*_lightSlot;
        if (next != null) next(light, idx, scale);

        if (_armed && GameGate.InModule && light != IntPtr.Zero)
        {
            byte* p = (byte*)light;
            int type = *(int*)(p + LightKind);
            float range = *(float*)(p + LightRange);
            if (idx == -1 && type == 0 && range > DirectionalRange)   // the directional diffuse light = the sun
            {
                float* d = (float*)(p + LightDir);
                // +0xac is the direction TO the light (the sun/moon high in the sky, Z-up +); the light
                // TRAVELS the opposite way, so negate to get the travel direction the shadow map renders along.
                Vector3 dir = new(-d[0], -d[1], -d[2]);
                if (dir.LengthSquared() > 1e-8f) dir = Vector3.Normalize(dir);
                // NWN is Z-up; a shadow-casting sun/moon must travel DOWNWARD. Reject a horizontal or
                // below-horizon light -- its map would look at undersides / read as garbage.
                if (dir.Z < DownwardZ)
                {
                    FrameInputs.SunDir = dir;
                    FrameInputs.HasSun = true;
                }
                if (SceneInputs.Diag && !_sunLogged)
                {
                    _sunLogged = true;
                    _log?.Info($"[shadowdiag] SUN dir@0xac=({dir.X:F3},{dir.Y:F3},{dir.Z:F3}) range={range:F0} " +
                               $"{(dir.Z < DownwardZ ? "accepted" : "REJECTED (not downward)")}");
                }
            }
            if (idx == -1) CaptureAreaTerm(p, type, scale);
            if (SceneInputs.Diag && _lightsLogged < LightLogCount)
            {
                _lightsLogged++;
                float* dp = (float*)(p + LightDir);
                float* col = (float*)(p + LightColor);
                _log?.Info($"[shadowdiag] light#{_lightsLogged} idx={idx} type={type} range={range:F0} " +
                           $"dirpos@0xac=({dp[0]:F3},{dp[1]:F3},{dp[2]:F3}) col@0x64=({col[0]:F3},{col[1]:F3},{col[2]:F3})");
            }
        }
    }

    /// <summary>sRGB -&gt; linear exponent: <c>RenderInterface::GetColorSpaceFromSRGB</c> is
    /// <c>powf(c, 2.2)</c> per channel (identity at 0 and 1), and the engine runs the area colours through it
    /// before the uniform. The Alloy pass shades in linear, so the snapshot stores the converted value; the
    /// Alloy shader's <c>SowxLinear</c> is the same curve, from the same constant.</summary>
    private const float SrgbGamma = AlloyLights.SrgbGamma;

    /// <summary>#83: mirror <c>SetLightGL</c>'s index -1 path (Ghidra @0x4c0e60) into <see cref="FrameInputs"/>
    /// so the Alloy pass has the engine's own area terms. type != 0 -&gt; SetAreaAmbientLight(colour * fade);
    /// type == 0 -&gt; SetAreaDiffuseLight(colour * fade, normalize(+0xac)), the world-space surface-&gt;light
    /// direction. Colours are linearized here, matching the engine's own sRGB conversion.</summary>
    private static void CaptureAreaTerm(byte* p, int type, float fade)
    {
        float* c = (float*)(p + LightColor);
        var col = new Vector3(MathF.Pow(MathF.Max(c[0] * fade, 0f), SrgbGamma),
                              MathF.Pow(MathF.Max(c[1] * fade, 0f), SrgbGamma),
                              MathF.Pow(MathF.Max(c[2] * fade, 0f), SrgbGamma));
        if (type != 0)
        {
            FrameInputs.AreaAmbient = col;
            FrameInputs.HasAreaAmbient = true;
            return;
        }
        float* d = (float*)(p + LightDir);
        var dir = new Vector3(d[0], d[1], d[2]);
        if (dir.LengthSquared() <= 1e-8f) return;
        FrameInputs.AreaDiffuse = col;
        FrameInputs.AreaDiffuseDir = Vector3.Normalize(dir);
        FrameInputs.HasAreaDiffuse = true;
    }
}
