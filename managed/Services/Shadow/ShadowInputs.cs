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

    /// <summary>True once the camera perspective has been seen (needed to restore it after the light pass).</summary>
    public static bool HasPerspective;
    /// <summary>Camera perspective params (the doubles to SetPerspectiveTransform): fovy, aspect, near, far.</summary>
    public static float CamFovy, CamAspect, CamNear, CamFar;

    /// <summary>Set by the depth pass while it drives the engine's transform setters into light space, so the
    /// capture hooks ignore those (else they'd record the light pose as the camera). Same render thread, so a
    /// plain bool is a correct guard.</summary>
    public static bool InDepthPass;

    // --- Local (point) light shadows (slice 6): the engine's per-frame, prioritized shadow-light set,
    // recorded by ShadowService as Scene::DoGobShadows fires -- one call per shadow-worthy point light,
    // already capped by LightManager::m_nMaxShadowLights. Values are COPIED, never the PartLight* (the engine
    // frees it on area/module boundaries -- the AurTexture lifetime lesson). Reset once per presented frame in
    // the roll-call; accumulated across every RenderShadows sub-frame, deduped by position.

    /// <summary>Max point lights we build shadow cubes for -- sized to cover every shadow-worthy light a
    /// player can see at once; areas with more keep the NEAREST ones (see <see cref="RecordLocalLight"/>).
    /// All faces live in one TEXTURE_2D_ARRAY (6*Max layers) on a single unit, so raising this costs only
    /// VRAM (~1.5MB/light at 256px) + receiver loop iterations. SOWX_LOCAL_MAX in inc_common.shd must match.</summary>
    public const int MaxLocalLights = 32;
    /// <summary>Point lights recorded this frame (0..<see cref="MaxLocalLights"/>).</summary>
    public static int LocalLightCount;
    /// <summary>Shadow-worthy point lights past the cap this frame (diagnostic).</summary>
    public static int LocalLightOverflow;
    /// <summary>World-space light position, already Z-lifted (<c>+0xb4 + +0x7c</c>) -- the shadow origin.</summary>
    public static readonly Vector3[] LocalLightPos = new Vector3[MaxLocalLights];
    /// <summary>Light reach (cube far plane): <c>+0x78</c> when &gt; 0, else <c>+0x70</c>.</summary>
    public static readonly float[] LocalLightRange = new float[MaxLocalLights];
    /// <summary>Light RGB (<c>+0x64</c>) -- for light-&gt;slot matching / debug, not the cube itself.</summary>
    public static readonly Vector3[] LocalLightColor = new Vector3[MaxLocalLights];

    /// <summary>Two lights within this world distance are the same slot (dedup across sub-frames).</summary>
    private const float LocalMinSep = 0.5f;

    /// <summary>True once <see cref="Focus"/> holds this frame's shadow focus (the player anchor).</summary>
    public static bool HasFocus;
    /// <summary>The shadow focus (player position) -- the nearest-K key for overflow areas.</summary>
    public static Vector3 Focus;

    // --- Persistent light registry: the engine's per-frame shadow-light submission is its old
    // stencil-volume prioritizer -- camera/visibility-dependent, so in light-heavy areas the submitted set
    // CHURNS as the player moves, and the slot machinery downstream thrashed (evict/re-claim/re-bake) until
    // most cubes sat inert. Lights are therefore accumulated here and only EXPIRE after a hold-down, and the
    // per-frame arrays above become a stable nearest-K view over the registry. Cost: a genuinely removed
    // light's shadow lingers up to the hold-down. Same render thread throughout; alloc-free.

    /// <summary>Registry capacity -- comfortably above the largest per-area point-light population seen (119).</summary>
    private const int RegistryMax = 256;
    /// <summary>Presented frames a registry entry survives without being re-submitted (~10s at 60fps).</summary>
    private const int RegistryHoldFrames = 600;
    private static int _frame;
    private static int _regCount;
    private static readonly Vector3[] _regPos = new Vector3[RegistryMax];
    private static readonly float[] _regRange = new float[RegistryMax];
    private static readonly Vector3[] _regColor = new Vector3[RegistryMax];
    private static readonly int[] _regSeen = new int[RegistryMax];

    /// <summary>Record one shadow-casting point light (world pos already Z-lifted, reach, RGB): upsert into
    /// the persistent registry, keyed by position within <see cref="LocalMinSep"/> (a carried torch moves
    /// less than that per frame, so it keeps its entry). The per-frame set is NOT touched here -- it is
    /// rebuilt from the registry once per presented frame in <see cref="ResetLocalLights"/>, so a light the
    /// engine skips this frame stays tracked.</summary>
    public static void RecordLocalLight(Vector3 pos, float range, Vector3 color)
    {
        int stale = -1, stalest = _frame + 1;
        for (int i = 0; i < _regCount; i++)
        {
            if ((_regPos[i] - pos).LengthSquared() < LocalMinSep * LocalMinSep)
            { _regPos[i] = pos; _regRange[i] = range; _regColor[i] = color; _regSeen[i] = _frame; return; }
            if (_regSeen[i] < stalest) { stalest = _regSeen[i]; stale = i; }
        }
        int at = _regCount < RegistryMax ? _regCount++ : stale;
        if (at < 0) return;
        _regPos[at] = pos; _regRange[at] = range; _regColor[at] = color; _regSeen[at] = _frame;
    }

    /// <summary>Advance the frame and rebuild the per-frame nearest-K set from the live (unexpired) registry
    /// (called once per presented frame in the roll-call). Insertion into a K-slot nearest list --
    /// O(registry * K) with K=32, alloc-free.</summary>
    public static void ResetLocalLights()
    {
        _frame++;
        LocalLightCount = 0; LocalLightOverflow = 0;
        for (int i = 0; i < _regCount; i++)
        {
            if (_frame - _regSeen[i] > RegistryHoldFrames)
            {   // expire: swap-remove; re-check this index
                _regCount--;
                _regPos[i] = _regPos[_regCount]; _regRange[i] = _regRange[_regCount];
                _regColor[i] = _regColor[_regCount]; _regSeen[i] = _regSeen[_regCount];
                i--; continue;
            }
            float d = HasFocus ? (_regPos[i] - Focus).LengthSquared() : i;
            if (LocalLightCount >= MaxLocalLights)
            {
                LocalLightOverflow++;
                int worst = -1; float worstD = d;
                for (int k = 0; k < MaxLocalLights; k++)
                {
                    float kd = HasFocus ? (LocalLightPos[k] - Focus).LengthSquared() : k;
                    if (kd > worstD) { worstD = kd; worst = k; }
                }
                if (worst < 0) continue;
                LocalLightPos[worst] = _regPos[i]; LocalLightRange[worst] = _regRange[i]; LocalLightColor[worst] = _regColor[i];
            }
            else
            {
                int at = LocalLightCount++;
                LocalLightPos[at] = _regPos[i]; LocalLightRange[at] = _regRange[i]; LocalLightColor[at] = _regColor[i];
            }
        }
    }

    /// <summary>Drop every registry entry (area transition: positions from the old area must not bleed in).</summary>
    public static void ClearLightRegistry() { _regCount = 0; }
}
