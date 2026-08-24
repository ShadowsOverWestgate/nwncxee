using System.Numerics;

namespace Sowx.Services.Shadow;

/// <summary>
/// Latest shadow-pipeline inputs captured from the engine, refreshed every frame by
/// <see cref="ShadowCaptureService"/> and read by the shadow-map renderer. Sun direction and camera
/// pose both change continuously (time-of-day, movement), so these are live values, not one-shot
/// latches. Capture and consumption are both on the render thread, so plain fields suffice.
/// </summary>
public static class ShadowInputs
{
    /// <summary>True once the directional sun light has been seen this session.</summary>
    public static bool HasSun;
    /// <summary>Normalized world-space (Z-up) direction the sunlight travels (from <c>PartLight+0xac</c>).</summary>
    public static Vector3 SunDir;

    /// <summary>True once the camera view transform has been seen.</summary>
    public static bool HasCamera;
    /// <summary>World-space camera position (the <c>Vector&amp;</c> arg to SetViewTransform).</summary>
    public static Vector3 CameraPos;
    /// <summary>Camera orientation (the <c>Quaternion&amp;</c> arg to SetViewTransform).</summary>
    public static Quaternion CameraRot;

    /// <summary>True once the SCENE camera has been latched off the engine matrix stacks this session.</summary>
    public static bool HasSceneCam;
    /// <summary>World-space position and forward of the camera the SCENE was drawn with, latched mid-frame by
    /// <c>OwnedDrawService.DrawOwned</c>. Distinct from <see cref="CameraPos"/>/<see cref="CameraRot"/>, which
    /// are captured through SetViewTransform and at the present belong to whichever pass set a view last -- a
    /// UI/ortho one as often as the scene's (live-observed as (0, 0, 742)). This pair is read inside the colour
    /// pass, where the stacks hold the real thing, and only from a pass that passes
    /// <c>OwnedLights.TryPerspective</c>, so an ortho/UI pass cannot seed it (#99).</summary>
    public static Vector3 SceneCamPos, SceneCamFwd;
    /// <summary>The latched pass's projection <c>p11</c> (<c>1 / tan(fovy/2)</c>). Taken from the same
    /// projection <c>TryPerspective</c> accepted, so the field of view is the SCENE's -- <see cref="CamFovy"/>
    /// is captured through SetPerspectiveTransform and carries the same last-pass-wins hazard as
    /// <see cref="CameraRot"/>.</summary>
    public static float SceneCamP11;
    /// <summary>Viewport area of the pass that currently holds the latch this frame. Largest wins: a
    /// water-reflection subframe is perspective too and would otherwise take the latch by going last, but it
    /// renders into a smaller target than the scene's full backbuffer.
    /// <para>ponytail: an equal-area tie goes to the later pass, so a FULL-resolution reflection subframe would
    /// take the latch. Water does not render at all today (#90), so it cannot happen; discriminate on the
    /// bound FBO if it ever does.</para></summary>
    private static int _sceneCamArea;
    /// <summary>Frame the current contest belongs to. The reset is driven off the CALLER's own frame clock,
    /// not another service's roll-call: the producer runs whether or not the shadow feed is armed, and a
    /// contest that never re-opened would turn <see cref="_sceneCamArea"/> into a session-wide high-water
    /// mark -- one that pins the latch to a pre-resize camera forever the moment the window shrinks.</summary>
    private static int _sceneCamFrame = -1;

    /// <summary>Offer a perspective pass's camera as this frame's scene camera; the largest viewport of the
    /// frame keeps it. A new <paramref name="frameSeq"/> re-opens the contest, while the latched values
    /// themselves persist -- so a frame that draws no perspective pass keeps the last good camera rather than
    /// reverting to nothing. Hot (per owned colour pass) and alloc-free.</summary>
    public static void LatchSceneCam(Vector3 pos, Vector3 fwd, float p11, int viewportArea, int frameSeq)
    {
        if (frameSeq != _sceneCamFrame) { _sceneCamFrame = frameSeq; _sceneCamArea = 0; }
        else if (viewportArea < _sceneCamArea) return;
        _sceneCamArea = viewportArea;
        SceneCamPos = pos; SceneCamFwd = fwd; SceneCamP11 = p11; HasSceneCam = true;
    }

    /// <summary>True once the camera perspective has been seen (needed to restore it after the light pass).</summary>
    public static bool HasPerspective;
    /// <summary>Camera perspective params (the doubles to SetPerspectiveTransform): fovy, aspect, near, far.</summary>
    public static float CamFovy, CamAspect, CamNear, CamFar;

    /// <summary>Largest far plane set this / last presented frame. The light-cull must use LAST frame's max
    /// (rolled over in the present roll-call): the walk runs at frame start (ADR 0007), before this frame's
    /// main projection is set, and the raw <see cref="CamFar"/> can then belong to a small-far auxiliary pass
    /// (reflection subframe) -- which culled the carried torch ("momentarily shuts off").</summary>
    public static float FarMaxThisFrame, FarMaxLastFrame;

    // --- Local (point) light shadows (slice 6 / ADR 0005 phase 1): fed each frame from the LightManager
    // MASTER light list (ShadowFeedService's GetShadowLights call-through walks it) -- the persistent ground
    // truth, so the set is deterministic and area-stable, not the engine's camera-prioritized pick. Values are
    // COPIED, never the PartLight* (the engine frees it on area/module boundaries -- the AurTexture lifetime
    // lesson). Reset once per presented frame in the roll-call; deduped by position across sub-frame fires.

    /// <summary>Shadow-worthy point lights this snapshot can hold -- the candidate pool
    /// <see cref="CasterPass"/> picks its cube set out of, nearest to the camera first (see
    /// <see cref="RecordLocalLight"/>). Deliberately at least as large as <c>CasterPass.MaxCubes</c> so the
    /// pool is never what limits the cube count; the interim receiver's own 32-light ceiling came from
    /// unrolling one sampler per slot, and a cube array has no such thing.</summary>
    /// <summary>#151: was 64, which #149's ceiling sweep missed -- and the live log's "64 flagged
    /// castShadows" was this cap masquerading as a fact about the area. A torch-lit district flags more;
    /// the arrays are a few KB.</summary>
    public const int MaxLocalLights = 256;
    /// <summary>Point lights recorded this frame (0..<see cref="MaxLocalLights"/>).</summary>
    public static int LocalLightCount;
    /// <summary>Shadow-worthy point lights past the cap this frame (diagnostic).</summary>
    public static int LocalLightOverflow;
    /// <summary>Engine light identity (the <c>PartLight*</c> from the master-list walk) -- the slot-binding
    /// key. NEVER dereferenced outside the walk (the engine frees it on area/module boundaries); used only
    /// for equality, where a recycled pointer simply reads as a new light.</summary>
    public static readonly IntPtr[] LocalLightId = new IntPtr[MaxLocalLights];
    /// <summary>World-space light position (<c>PlPos</c>, +0xac) -- the cube's bake origin. It is the SAME
    /// value <see cref="AreaLightPosRange"/> shades from, deliberately: a cube baked from anywhere else
    /// computes occlusion for a light that is not where the BRDF thinks it is.</summary>
    public static readonly Vector3[] LocalLightPos = new Vector3[MaxLocalLights];
    /// <summary>Light reach (cube far plane): <c>+0x78</c> when &gt; 0, else <c>+0x70</c>.</summary>
    public static readonly float[] LocalLightRange = new float[MaxLocalLights];
    /// <summary>Light RGB (<c>+0x64</c>) -- for light-&gt;slot matching / debug, not the cube itself.</summary>
    public static readonly Vector3[] LocalLightColor = new Vector3[MaxLocalLights];

    /// <summary>Two lights within this world distance are the same slot (dedup across sub-frames).</summary>
    private const float LocalMinSep = 0.5f;

    /// <summary>Current area carries the ARE interior flag -- no sun, so nothing directional is worth
    /// rendering there. Recomputed once per frame by <see cref="ShadowFeedService"/> (which walks the engine's
    /// own area chain); false until an area is known, which is the exterior-shaped default.</summary>
    public static bool Interior;

    /// <summary>True once <see cref="Focus"/> holds this frame's shadow focus (the player anchor).</summary>
    public static bool HasFocus;
    /// <summary>The shadow focus (player position) -- the nearest-K key for overflow areas.</summary>
    public static Vector3 Focus;

    /// <summary>Record one shadow-casting point light (identity, world pos, reach, RGB) into this frame's
    /// nearest-K set: dedup by IDENTITY (a repeat walk in the same frame refreshes the entry), then keep the
    /// <see cref="MaxLocalLights"/> nearest to the CAMERA -- the only anchor that is always live; a carried
    /// torch is by construction among the nearest. The feed is the LightManager master list, walked whole
    /// every frame, so membership is deterministic. Alloc-free, O(K).</summary>
    public static void RecordLocalLight(IntPtr id, Vector3 pos, float range, Vector3 color)
    {
        for (int i = 0; i < LocalLightCount; i++)
            if (LocalLightId[i] == id)
            { LocalLightPos[i] = pos; LocalLightRange[i] = range; LocalLightColor[i] = color; return; }
        if (LocalLightCount < MaxLocalLights)
        {
            int at = LocalLightCount++;
            LocalLightId[at] = id; LocalLightPos[at] = pos; LocalLightRange[at] = range; LocalLightColor[at] = color;
            return;
        }
        LocalLightOverflow++;
        // Swap margin: displace the farthest incumbent only when this light is >=20% nearer (1.44 = 1.2^2 on
        // squared distances) -- at the exact nearest-K boundary, walking otherwise oscillates membership every
        // few steps and each swap evicts + full-rebakes a slot (the "all shadows jump around" churn, #67).
        float d = HasCamera ? (pos - CameraPos).LengthSquared() : float.MaxValue;
        int worst = -1; float worstD = d * 1.44f;
        for (int k = 0; k < MaxLocalLights; k++)
        {
            float kd = HasCamera ? (LocalLightPos[k] - CameraPos).LengthSquared() : -1f;
            if (kd > worstD) { worstD = kd; worst = k; }
        }
        if (worst < 0) return;
        LocalLightId[worst] = id; LocalLightPos[worst] = pos; LocalLightRange[worst] = range; LocalLightColor[worst] = color;
    }

    /// <summary>Clear the per-frame set (called once per presented frame in the roll-call); the master-list
    /// walk repopulates it during the next frame's render.</summary>
    public static void ResetLocalLights()
    {
        LocalLightCount = 0; LocalLightOverflow = 0;
    }

    // --- Extender-owned point lighting: EVERY active point light in the area (shadow-casting or not),
    // snapshotted from the same master-list walk and fed to the owned pass's clustered light SSBO (#83).

    /// <summary>Shadow-candidate ceiling: how far down the snapshot the cube-slot budget looks. The owned
    /// pass (#83) lights from the whole snapshot and is not bound by it.</summary>
    public const int MaxAreaLights = 1024;
    /// <summary>Snapshot capacity. The owned clustered path has no light ceiling by design (#83), so the
    /// snapshot holds far more than the ~120 a dense master list runs; the surplus is counted, not dropped
    /// silently. <para>ponytail: a fixed array rather than a growable one -- 1024 is ~8x the densest live
    /// area; if content ever passes it, grow this const (the overflow counter is the signal).</para></summary>
    public const int MaxLights = 8192;
    /// <summary>Point lights snapshotted this walk (0..<see cref="MaxLights"/>) -- the owned path's set.</summary>
    public static int LightCount;
    /// <summary>Lights the snapshot could not hold this walk (diagnostic; 0 on all real content).</summary>
    public static int LightOverflow;
    /// <summary>The snapshot clamped to <see cref="MaxAreaLights"/> -- the shadow-candidate window.</summary>
    public static int AreaLightCount => LightCount < MaxAreaLights ? LightCount : MaxAreaLights;

    // --- Area ambient / directional diffuse (#83): the engine's own area terms, captured from SetLightGL's
    // index -1 path (Ghidra @0x4c0e60: type != 0 -> SetAreaAmbientLight(colour*fade); type == 0 ->
    // SetAreaDiffuseLight(colour*fade, normalize(+0xac))). Colours are stored LINEAR (the engine runs them
    // through GetColorSpaceFromSRGB before the uniform); the direction is world-space surface->light.

    /// <summary>True once both area terms have been seen this session.</summary>
    public static bool HasAreaAmbient, HasAreaDiffuse;
    /// <summary>Linear area ambient / directional diffuse colour, and the world-space surface-&gt;light dir.</summary>
    public static Vector3 AreaAmbient, AreaDiffuse, AreaDiffuseDir;
    /// <summary>World pos (xyz) + lit range (w), packed for a single glUniform4fv.</summary>
    public static readonly Vector4[] AreaLightPosRange = new Vector4[MaxLights];
    /// <summary>Light RGB (sRGB as stored in PartLight; the shader linearizes).</summary>
    public static readonly Vector3[] AreaLightColor = new Vector3[MaxLights];
    /// <summary>PartLight identity per area light (equality only) -- keys the CPU-side light-&gt;cube-slot
    /// bind.</summary>
    public static readonly IntPtr[] AreaLightId = new IntPtr[MaxLights];
    /// <summary>Cube slot per area light (-1 = casts no shadow / no cube) -- resolved at the pass slot right
    /// after the bake (#118), so the mapping and the cube contents are the same frame's, and read by the owned
    /// lit stage.</summary>
    public static readonly int[] AreaLightSlot = new int[MaxLights];

    /// <summary>Area lights that resolved to a real cube slot on the last walk. Zero while cubes render is
    /// exactly the "shadows are on but invisible" state -- the receiver's no-slot path returns full light
    /// and says nothing.</summary>
    public static int LightsWithCube;

        /// <summary>Restart the snapshot (top of each master-list walk -- the walk is idempotent, so the latest
    /// walk simply wins).</summary>
    public static void BeginAreaLights() { LightCount = 0; LightOverflow = 0; }

    public static void RecordAreaLight(IntPtr id, Vector3 pos, float range, Vector3 color)
    {
        if (LightCount >= MaxLights) { LightOverflow++; return; }
        AreaLightPosRange[LightCount] = new Vector4(pos, range);
        AreaLightColor[LightCount] = color;
        AreaLightId[LightCount] = id;
        AreaLightSlot[LightCount++] = -1;
    }
}
