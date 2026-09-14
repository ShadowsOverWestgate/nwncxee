using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>
/// This frame's CONSTANTS: the engine state every Alloy pass shades and fits against -- camera, area
/// terms, sun, fog and the light snapshot. #257 made it a block that is READ, once per world frame, by
/// <see cref="FrameRead"/> and <see cref="SceneInputs"/>; nothing here is captured off a rendering call
/// site any more and no value is decided by a contest between passes. Sun direction and camera pose both
/// change continuously (time-of-day, movement), so these are live values, not one-shot latches. The read
/// and every consumer are on the render thread, so plain fields suffice.
/// </summary>
public static class FrameInputs
{
    /// <summary>True once the directional sun light has been seen this session.</summary>
    public static bool HasSun;
    /// <summary>Normalized world-space (Z-up) direction the sunlight travels (from <c>PartLight+0xac</c>).</summary>
    public static Vector3 SunDir;

    /// <summary>True once a camera has been read this session.</summary>
    public static bool HasCamera;
    /// <summary>World-space camera position (<c>*CurrentCamera + 0xb0</c>).</summary>
    public static Vector3 CameraPos;
    /// <summary>Camera orientation (<c>*CurrentCamera + 0xbc</c>, re-ordered to Numerics' (x, y, z, w)).</summary>
    public static Quaternion CameraRot;

    /// <summary>True once the scene camera has been read this session.</summary>
    public static bool HasSceneCam;
    /// <summary>World-space position and forward of the camera the SCENE is drawn with. Since #257 this is
    /// the SAME read as <see cref="CameraPos"/>/<see cref="CameraRot"/> -- <c>CurrentCamera</c> inside the
    /// world scene's render IS the scene camera -- so the pair is kept only because the shadow path names
    /// it (#99), not because the two can disagree.</summary>
    public static Vector3 SceneCamPos, SceneCamFwd;
    /// <summary>The scene projection's <c>p11</c> (<c>1 / tan(fovy/2)</c>), from <see cref="CamFovy"/>.</summary>
    public static float SceneCamP11;

    /// <summary>True once a camera with a real view angle has been read.</summary>
    public static bool HasPerspective;
    /// <summary>Camera perspective params, the same four the engine hands SetPerspectiveTransform: view
    /// angle in degrees (<c>+0x4c4</c>), viewport aspect (<c>+0x4dc / +0x4e0</c>), near and far
    /// (<c>+0x4cc</c>/<c>+0x4d0</c>).</summary>
    public static float CamFovy, CamAspect, CamNear, CamFar;
    /// <summary>The camera's own viewport width and height (<c>+0x4dc</c>/<c>+0x4e0</c>). Not a GL
    /// viewport -- the engine upscales both by the same factor before glViewport -- but it moves exactly
    /// when the GL one does, which is what makes it a usable generation key for the frame's target.</summary>
    public static float CamViewW, CamViewH;


    /// <summary>Scene fog, read straight off <c>Scene+0x140..0x14c</c> (#257) rather than out of
    /// <c>GLRender::m_fog</c> after asking the engine to fill it. The colour is DISPLAY space, as the block
    /// stores it -- the stages that fog linearise it themselves.</summary>
    public static bool FogOn;
    /// <summary>Fog start and end distance, and its display-space colour. Meaningful only while
    /// <see cref="FogOn"/>.</summary>
    public static float FogStart, FogEnd;
    /// <summary>Fog colour, display space (see <see cref="FogOn"/>).</summary>
    public static Vector3 FogColor;

    // --- Area ambient / directional diffuse (#83): the engine's own area terms, the two index -1 lights
    // LightManager::ControlLightsFade pushes -- read since #257 where they live (LightManager +0xa0 / +0x98)
    // rather than intercepted at SetLightGL. Colours are stored LINEAR (the engine runs them through
    // GetColorSpaceFromSRGB before its own uniform); the direction is world-space surface->light.

    /// <summary>True once each area term has been read this session.</summary>
    public static bool HasAreaAmbient, HasAreaDiffuse;
    /// <summary>Linear area ambient / directional diffuse colour, and the world-space surface-&gt;light dir.</summary>
    public static Vector3 AreaAmbient, AreaDiffuse, AreaDiffuseDir;

    // --- The light snapshot: every active point light in the area, read from the LightManager MASTER list
    // once per world frame by SceneInputs. Values are COPIED, never the PartLight* (the engine frees it on
    // area/module boundaries -- the AurTexture lifetime lesson); the id is kept for equality only.
    //
    // #257: no fixed worst-case array survives here. Both sets grow to whatever the master list holds, so
    // there is no ceiling to overflow, no overflow counter to read, and no nearest-K eviction to churn --
    // CasterPass::CubePass does its own nearest-first pick against its cube budget, which is the only
    // selection that was ever load-bearing.

    /// <summary>Shadow-worthy point lights recorded this frame.</summary>
    public static int LocalLightCount;
    /// <summary>Engine light identity (the <c>PartLight*</c> from the master-list walk) -- the slot-binding
    /// key. NEVER dereferenced outside the walk; used only for equality, where a recycled pointer simply
    /// reads as a new light.</summary>
    public static IntPtr[] LocalLightId = new IntPtr[InitialLights];
    /// <summary>World-space light position (<c>PlPos</c>, +0xac) -- the cube's bake origin. It is the SAME
    /// value <see cref="AreaLightPosRange"/> shades from, deliberately: a cube baked from anywhere else
    /// computes occlusion for a light that is not where the BRDF thinks it is.</summary>
    public static Vector3[] LocalLightPos = new Vector3[InitialLights];
    /// <summary>Light reach (cube far plane) = <c>GetLightAdjustedRadius</c>, the engine's own lit radius off
    /// <c>PartLight+0x70</c>. #186: exactly the sphere the receiver's attenuation has support over, so nothing
    /// outside it is worth baking.</summary>
    public static float[] LocalLightRange = new float[InitialLights];
    /// <summary>Light RGB (<c>+0x64</c>) -- for light-&gt;slot matching / debug, not the cube itself.</summary>
    public static Vector3[] LocalLightColor = new Vector3[InitialLights];

    /// <summary>Current area carries the ARE interior flag -- no sun, so nothing directional is worth
    /// rendering there. Recomputed once per frame by <see cref="SceneInputs"/> (which walks the engine's
    /// own area chain); false until an area is known, which is the exterior-shaped default.</summary>
    public static bool Interior;

    /// <summary>True once <see cref="Focus"/> holds this frame's shadow focus (the player anchor).</summary>
    public static bool HasFocus;
    /// <summary>The shadow focus (player position) -- the nearest-K key for overflow areas.</summary>
    public static Vector3 Focus;

    /// <summary>Starting capacity of both snapshots. Not a ceiling: a denser area grows them once and keeps
    /// the larger arrays for the session. A dense live area runs ~120 lights.</summary>
    private const int InitialLights = 256;

    /// <summary>Record one shadow-casting point light (identity, world pos, reach, RGB). Dedup by IDENTITY,
    /// so a repeat walk in the same frame refreshes the entry rather than doubling it. Alloc-free in the
    /// steady state -- <see cref="ResetLocalLights"/> has already sized them off the master list, so the
    /// growth branch below is a guard rather than a path.</summary>
    public static void RecordLocalLight(IntPtr id, Vector3 pos, float range, Vector3 color)
    {
        for (int i = 0; i < LocalLightCount; i++)
            if (LocalLightId[i] == id)
            { LocalLightPos[i] = pos; LocalLightRange[i] = range; LocalLightColor[i] = color; return; }
        if (LocalLightCount == LocalLightId.Length)
        {
            int n = LocalLightCount * 2;
            Array.Resize(ref LocalLightId, n); Array.Resize(ref LocalLightPos, n);
            Array.Resize(ref LocalLightRange, n); Array.Resize(ref LocalLightColor, n);
        }
        int at = LocalLightCount++;
        LocalLightId[at] = id; LocalLightPos[at] = pos; LocalLightRange[at] = range; LocalLightColor[at] = color;
    }

    /// <summary>Clear the per-frame shadow-candidate set (top of each master-list walk). <paramref
    /// name="expect"/> is the master list's own count, which the recorded set is a subset of -- so sizing
    /// off it here is what keeps <see cref="RecordLocalLight"/> from ever allocating on the render thread.</summary>
    public static void ResetLocalLights(int expect)
    {
        LocalLightCount = 0;
        if (expect <= LocalLightId.Length) return;
        Array.Resize(ref LocalLightId, expect); Array.Resize(ref LocalLightPos, expect);
        Array.Resize(ref LocalLightRange, expect); Array.Resize(ref LocalLightColor, expect);
    }

    // --- Extender-owned point lighting: EVERY active point light in the area (shadow-casting or not),
    // snapshotted from the same master-list walk and fed to the Alloy pass's clustered light SSBO (#83).

    /// <summary>Point lights snapshotted this walk -- the Alloy path's set. The clustered path has no light
    /// ceiling by design (#83), and since #257 neither has the snapshot that feeds it.</summary>
    public static int LightCount;
    /// <summary>World pos (xyz) + lit range (w), packed for a single glUniform4fv.</summary>
    public static Vector4[] AreaLightPosRange = new Vector4[InitialLights];
    /// <summary>Light RGB (sRGB as stored in PartLight; the shader linearizes).</summary>
    public static Vector3[] AreaLightColor = new Vector3[InitialLights];
    /// <summary>PartLight identity per area light (equality only) -- keys the CPU-side light-&gt;cube-slot
    /// bind.</summary>
    public static IntPtr[] AreaLightId = new IntPtr[InitialLights];
    /// <summary>Cube slot per area light (-1 = casts no shadow / no cube) -- resolved at the pass slot right
    /// after the bake (#118), so the mapping and the cube contents are the same frame's, and read by the owned
    /// lit stage.</summary>
    public static int[] AreaLightSlot = new int[InitialLights];

    /// <summary>Area lights that resolved to a real cube slot on the last walk. Zero while cubes render is
    /// exactly the "shadows are on but invisible" state -- the receiver's no-slot path returns full light
    /// and says nothing.</summary>
    public static int LightsWithCube;

    /// <summary>Restart the snapshot (top of each master-list walk -- the walk is idempotent, so the latest
    /// walk simply wins). <paramref name="expect"/> is the master list's own count: sizing off it means a
    /// dense area grows the arrays once, at the top of its first walk, instead of doubling mid-walk.</summary>
    public static void BeginAreaLights(int expect)
    {
        LightCount = 0;
        if (expect > AreaLightPosRange.Length) GrowArea(expect);
    }

    private static void GrowArea(int n)
    {
        Array.Resize(ref AreaLightPosRange, n); Array.Resize(ref AreaLightColor, n);
        Array.Resize(ref AreaLightId, n); Array.Resize(ref AreaLightSlot, n);
    }

    public static void RecordAreaLight(IntPtr id, Vector3 pos, float range, Vector3 color)
    {
        if (LightCount == AreaLightPosRange.Length) GrowArea(LightCount * 2);
        AreaLightPosRange[LightCount] = new Vector4(pos, range);
        AreaLightColor[LightCount] = color;
        AreaLightId[LightCount] = id;
        AreaLightSlot[LightCount++] = -1;
    }
}
