using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 (seam-scene): the DB-mode SOURCE for every area setting Alloy reads off the engine's
/// <c>Scene</c> today. One accessor per input, over <see cref="SceneDb"/>'s scene columns, plus the
/// <see cref="Tick"/> that fills <see cref="FrameInputs"/> the way <c>FrameRead.Tick</c> fills it from the
/// engine object. Nothing here reads engine memory.
///
/// <para><b>What reads what.</b> Each entry names the Alloy consumer whose engine read this replaces, so the
/// retarget is a one-line change in that consumer (seam-feed's half of CX-120):</para>
/// <list type="table">
/// <item><term>fog</term><description><c>Scene+0xf4/0xf8/0xfc</c> (<c>FogEnable/Start/End/Color</c>) read by
/// <c>FrameRead.ReadFog</c> into <see cref="FrameInputs"/>, consumed by <c>SceneFog</c>,
/// <c>AlloyRenderer.Uniforms</c> and <c>CullPass</c>. ->
/// <see cref="Tick"/>.</description></item>
/// <item><term>ambient</term><description><c>Scene+0x168</c>, which <c>Scene::Render</c> hands
/// <c>SetLightModel</c>. Alloy takes its area terms from the database's own area light records instead, so in
/// DB mode the ambient column is the fallback when no area light set exists -> <see cref="Ambient"/>.</description></item>
/// <item><term>sky / skybox</term><description><c>Scene+0x1c0..</c> (<c>SkyGobs</c>, <c>SkyFadeGob</c>,
/// <c>SkyOff</c>, <c>SkyT/Cur/Desired/Pos</c>) read by <c>Pass/SkyBox.cs</c> and <c>Scene/SkyTick.cs</c>, which
/// also WRITE those fields (they own the sky clock, CX-107) -> <see cref="SkyBoxSlot"/>,
/// <see cref="SkyFade"/>, <see cref="SkyEnabled"/>, <see cref="SkyAlpha"/>.</description></item>
/// <item><term>wind</term><description><c>Scene+0x160</c>'s <c>WindManager</c>, which <c>AlloyWind</c> already
/// replaced (CX-110) except for the GLOBAL wind the area sets -> <see cref="GlobalWind"/>.</description></item>
/// <item><term>grass</term><description><c>GrassIsEnabled</c>/<c>GrassFarRenderDistance</c> and the engine's
/// grass info list, read by <c>Pass/GrassField.cs</c>. The info list arrives through the object-free
/// <c>AurGrassAddInfo</c>/<c>AurGrassClearInfo</c> setters (seam-obj), and the per-scene enable is here ->
/// <see cref="GrassEnabled"/>, <see cref="GrassStamp"/>.</description></item>
/// <item><term>highlight</term><description><c>Scene+0x1a0..</c> (<c>HiliteGobs</c>, <c>HiliteColors</c>,
/// <c>HiliteCount</c>, <c>HiliteTexture</c>) read by <c>Highlight.cs</c> and <c>Pass/HighlightGlow.cs</c> ->
/// <see cref="HighlightCount"/>, <see cref="HighlightAt"/>, <see cref="HighlightTexture"/>.</description></item>
/// <item><term>blind lights / shadow gob</term><description>read by <c>Pass/AlloyLights.cs</c> and the caster
/// gather -> <see cref="BlindLights"/>, <see cref="ShadowGob"/>.</description></item>
/// <item><term>draw order</term><description>the engine's gob list order, which <c>SceneWalk</c> observed ->
/// <see cref="DrawOrder"/>, the scene's member list, which the four <c>RenderGob*</c> slots maintain.</description></item>
/// <item><term>geometry fade / tile fade / door fade</term><description><c>SetGeomFadeProperties</c> and
/// <c>ResetTileFading</c>/<c>FadeDoors</c>, read by <c>TileFadeRamp.cs</c> and <c>DoorFade.cs</c> ->
/// <see cref="GeomFade"/>, <see cref="FadeStamp"/>.</description></item>
/// <item><term>static lighting</term><description><c>ComputeStaticLighting</c>/<c>ComputeStaticProjections</c>.
/// Alloy owns both as the content-keyed lightmap bake (CX-105), so these are stale STAMPS the bake compares,
/// never a synchronous bake inside a seam call -> <see cref="BakeStamp"/>, <see cref="ProjStamp"/>.</description></item>
/// <item><term>lines to render</term><description><c>AddLinesToRender</c>/<c>ClearLinesToRender</c>, which no
/// Alloy pass draws today and no game-layer caller reaches on this build -> <see cref="Lines"/>, recorded and
/// available, drawn by nobody.</description></item>
/// </list></summary>
public static class SceneFeed
{
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Monotonic stamps a consumer compares against its own last-seen value: a change is one
    /// increment on the game thread, and the consumer rebuilds on its own schedule rather than inside the
    /// seam call. Scene-major, one per kind -- the cheapest change notice that cannot be missed.</summary>
    private static readonly int[] _sky = new int[SceneDb.MaxScenes], _grass = new int[SceneDb.MaxScenes],
                                  _wind = new int[SceneDb.MaxScenes], _hl = new int[SceneDb.MaxScenes],
                                  _fade = new int[SceneDb.MaxScenes], _bake = new int[SceneDb.MaxScenes],
                                  _proj = new int[SceneDb.MaxScenes], _lights = new int[SceneDb.MaxScenes];

    // ---- change notices (called from the slots) ------------------------------------------------------

    internal static void FogChanged(DbId s) => SceneDb.TouchScene(s, Dirty.Material);
    internal static void SkyChanged(DbId s) { if (Ok(s)) { _sky[s.Index]++; SceneDb.TouchScene(s, Dirty.Visible); } }
    internal static void GrassChanged(DbId s) { if (Ok(s)) _grass[s.Index]++; }
    internal static void WindChanged(DbId s) { if (Ok(s)) _wind[s.Index]++; }
    internal static void HighlightChanged(DbId s) { if (Ok(s)) { _hl[s.Index]++; SceneDb.TouchScene(s, Dirty.Material); } }
    internal static void GeomFadeChanged() { _geomFade++; }
    internal static void FadeReset(DbId s) { if (Ok(s)) _fade[s.Index]++; }
    internal static void LightsChanged(DbId s) { if (Ok(s)) _lights[s.Index]++; }
    internal static void StaticLightingStale(DbId s) { if (Ok(s)) _bake[s.Index]++; }
    internal static void StaticProjectionsStale(DbId s) { if (Ok(s)) _proj[s.Index]++; }

    private static bool Ok(DbId s) => SceneDb.Scenes.Valid(s);

    // ---- the stamps a consumer polls ----------------------------------------------------------------

    public static int SkyStamp(DbId s) => Ok(s) ? _sky[s.Index] : 0;
    public static int GrassStamp(DbId s) => Ok(s) ? _grass[s.Index] : 0;
    public static int WindStamp(DbId s) => Ok(s) ? _wind[s.Index] : 0;
    public static int HighlightStamp(DbId s) => Ok(s) ? _hl[s.Index] : 0;
    public static int FadeStamp(DbId s) => Ok(s) ? _fade[s.Index] : 0;
    public static int BakeStamp(DbId s) => Ok(s) ? _bake[s.Index] : 0;
    public static int ProjStamp(DbId s) => Ok(s) ? _proj[s.Index] : 0;
    public static int LightStamp(DbId s) => Ok(s) ? _lights[s.Index] : 0;

    // ---- the inputs themselves ----------------------------------------------------------------------

    public static Vector3 Ambient(DbId s) => Ok(s) ? SceneDb.SceneAmbient[s.Index] : Vector3.Zero;
    public static DbId BlindLights(DbId s) => Ok(s) ? SceneDb.SceneBlindLights[s.Index] : default;
    public static DbId ShadowGob(DbId s) => Ok(s) ? SceneDb.SceneShadowGob[s.Index] : default;
    /// <summary>The GLOBAL geometry-fade mode (1, 2 or 4; 0 = never set). Not per scene -- the engine's
    /// setter and getter are both static over one int.</summary>
    public static int GeomFade => SceneDb.GeomFade;
    /// <summary>Bumped when the global mode changes.</summary>
    public static int GeomFadeStamp => _geomFade;
    private static int _geomFade;
    /// <summary>Is the sky drawn? The engine keeps the OFF byte, so this is its negation -- and note that a
    /// scene with the sky off still REMEMBERS its four objects.</summary>
    public static bool SkyEnabled(DbId s) => Ok(s) && !SceneDb.SceneSkyOff[s.Index];
    /// <summary>The cross-fade: the blend 0..1, the mode being shown, and the one being faded to.</summary>
    public static void SkyBlend(DbId s, out float t, out int cur, out int desired)
    {
        if (!Ok(s)) { t = 0f; cur = 0; desired = 0; return; }
        t = SceneDb.SceneSkyT[s.Index];
        cur = SceneDb.SceneSkyCur[s.Index];
        desired = SceneDb.SceneSkyDesired[s.Index];
    }
    public static DbId SkyFade(DbId s) => Ok(s) ? SceneDb.SceneSkyFade[s.Index] : default;
    public static string HighlightTexture(DbId s) => Ok(s) ? SceneDb.SceneHighlightTex[s.Index] : "";
    public static bool RenderAabb(DbId s) => Ok(s) && SceneDb.SceneRenderAabb[s.Index];
    public static DbLines? Lines(DbId s) => Ok(s) ? SceneDb.SceneLines[s.Index] : null;

    /// <summary>One of the four skybox objects (<c>Scene::SetSkyBox</c>'s argument order).</summary>
    public static DbId SkyBoxSlot(DbId s, int slot) => Ok(s) ? SceneDb.SceneSky(s, slot) : default;

    /// <summary>The area's global wind: direction, magnitude, gustiness and the update timeout, as
    /// <c>Scene::SetGlobalWind</c> and <c>SetWindUpdateTimeout</c> left them. <c>AlloyWind</c>'s own noise
    /// field runs off these instead of <c>WindManager::GetGlobalWind</c>.</summary>
    public static void GlobalWind(DbId s, out Vector3 dir, out float magnitude, out float gustiness, out float timeout)
    {
        if (!Ok(s)) { dir = Vector3.Zero; magnitude = 0f; gustiness = 0f; timeout = 0f; return; }
        int i = s.Index;
        dir = SceneDb.SceneWindDir[i];
        magnitude = SceneDb.SceneWindMag[i];
        gustiness = SceneDb.SceneWindGust[i];
        timeout = SceneDb.SceneWindTimeout[i];
    }

    /// <summary>Grass is on for this scene. The engine answers this off its own grass info list being
    /// non-empty, which is the same question as "did the area add any grass info"; in DB mode the list is the
    /// object-free <c>AurGrassAddInfo</c> setters' and a world scene with no tiles cannot have grass.</summary>
    public static bool GrassEnabled(DbId s) => Ok(s) && SceneDb.SceneIsWorld[s.Index];

    /// <summary>The scene's members in DRAW ORDER -- what the renderer walks in place of the engine's gob
    /// list, and what the four <c>RenderGob*</c> slots reorder. Null when the scene has no members.</summary>
    public static DbList? DrawOrder(DbId s) => Ok(s) ? SceneDb.SceneObjects[s.Index] : null;

    public static int HighlightCount(DbId s) => Ok(s) ? SceneDb.SceneHighlights[s.Index]?.Count ?? 0 : 0;

    /// <summary>Highlight entry <paramref name="k"/>: the object and its colour.</summary>
    public static bool HighlightAt(DbId s, int k, out DbId obj, out Vector3 colour)
    {
        obj = default; colour = Vector3.One;
        DbList? hl = Ok(s) ? SceneDb.SceneHighlights[s.Index] : null;
        if (hl == null || (uint)k >= (uint)hl.Count) return false;
        obj = hl[k];
        Vector4 a = hl.Aux(k);
        colour = new Vector3(a.X, a.Y, a.Z);
        return true;
    }

    /// <summary>The light reaching <paramref name="obj"/>'s position in <paramref name="scene"/> --
    /// <c>Scene::GetLightingValueAtFocus</c>'s answer. The scene ambient plus every area light whose radius
    /// reaches the object, attenuated linearly, which is the shape of the engine's own
    /// <c>LightManager</c> sample; with no object it answers the ambient alone.
    /// <para><c>ponytail:</c> the engine's exact falloff is the vanilla point-light curve
    /// (<c>vanilla-point-light-curve-re</c>), and the three game callers use this to TINT a GUI portrait --
    /// a term the player compares against nothing. A closer port is a measurement away if a builder ever
    /// notices.</para></summary>
    public static Vector3 LightingAt(DbId scene, DbId obj)
    {
        if (!Ok(scene)) return Vector3.Zero;
        Vector3 c = SceneDb.SceneAmbient[scene.Index];
        if (!SceneDb.Objects.Valid(obj)) return c;
        Vector3 p = SceneDb.ObjPos[obj.Index];
        for (int k = 0; k < FrameInputs.LightCount; k++)
        {
            Vector4 pr = FrameInputs.AreaLightPosRange[k];
            float r = pr.W;
            if (r <= 0f) continue;
            float d = Vector3.Distance(new Vector3(pr.X, pr.Y, pr.Z), p);
            if (d >= r) continue;
            c += FrameInputs.AreaLightColor[k] * (1f - d / r);
        }
        return c;
    }

    /// <summary>The same fill from a published <c>RenderSnapshot.ViewRec</c>: the camera's state AS RECORDED,
    /// which is what the renderer must shade with. Reading the camera columns again here would hand frame N's
    /// geometry frame N+1's camera whenever the render thread is a frame behind.</summary>
    /// <param name="h">CX-188: the frame's hoisted ENGINE terms. Nothing under this call reads engine
    /// memory, which is what lets the per-view draw run on <c>RenderThread</c>.</param>
    internal static void TickFromView(DbId scene, DbId camera, in RenderSnapshot.ViewRec v, in FrameHoist h)
    {
        // CX-120: the renderer's own view latch, from the SNAPSHOT's matrices. Fed before anything else this
        // view does, because DrawOwned refuses a frame with no measured world camera -- and that refusal is
        // terminal for the session, not for the frame.
        AlloyCamera.NoteDbView(in ViewOf(in v), v.World);
        if (!Ok(scene)) return;
        SceneTerms(scene, in v, in h);
        CamTerms(v.CamPos, v.CamRot, v.Fovy, v.Aspect, v.Near, v.Far, v.W, v.H, v.Perspective);
    }

    /// <summary>The area wind in the engine's shader block, as <c>GLRender::CheckAndUpdateUniformDirtyDataForFrame</c>
    /// @0x5d4e50 copies it (<c>*(currentscene+0x160)+0x20</c> into block <c>+0x28</c>). That copy runs only from
    /// <c>Scene::Render</c>, which DB mode never enters, so the rain fell straight down whatever the area's wind.
    /// The game's <c>setglobalwind</c> still lands in the shell's own <c>WindManager</c> (<c>SceneFns.SeedWind</c>).
    /// <para>CX-188: called at the FOLD, from <see cref="FoldWind"/>, not from the per-view draw. It reads the
    /// engine scene's <c>WindManager</c> and WRITES the engine's shader block -- a write to engine memory,
    /// which is the one thing a render thread may never do.</para></summary>
    private static void RuntimeWind(DbId scene)
    {
        if (AlloyRenderer.Rt.Ptr == 0) return;
        nint sc = SceneDb.SceneFacade[scene.Index];
        if (sc == 0) return;
        var wm = new WindManager(new Scene(sc).WindManager);
        if (wm.IsNull) return;
        AlloyRenderer.Rt.AreaWind = wm.BaseWind;
    }

    /// <summary>CX-188: the frame's wind, on the GAME thread. Every WORLD view of the published frame, which
    /// is where the per-view draw used to do it -- the value lands in the engine's shader block either way, so
    /// the last world view of the frame wins exactly as it did before.</summary>
    internal static void FoldWind(RenderSnapshot.Frame f)
    {
        for (int k = 0; k < f.ViewCount; k++)
            if (f.Views[k].World && Ok(f.Views[k].Scene)) RuntimeWind(f.Views[k].Scene);
    }

    /// <summary>A snapshot view record as the renderer's own <c>AlloyView</c>. Both already hold the same
    /// facts in the same convention (row-vector <c>v * M</c>); this is the field-for-field hand-over.</summary>
    private static ref AlloyView ViewOf(in RenderSnapshot.ViewRec v)
    {
        _dbView.View = v.View; _dbView.Proj = v.Proj;
        _dbView.X = v.X; _dbView.Y = v.Y; _dbView.W = v.W; _dbView.H = v.H;
        _dbView.Aspect = v.Aspect; _dbView.Near = v.Near; _dbView.Far = v.Far; _dbView.Fovy = v.Fovy;
        _dbView.DepthNear = v.DepthNear; _dbView.DepthFar = v.DepthFar;
        _dbView.Perspective = v.Perspective;
        // A view the seam published is a view it measured: the only invalid one is a view nobody drew.
        _dbView.Valid = v.W > 0 && v.H > 0;
        return ref _dbView;
    }

    private static AlloyView _dbView;

    private static void SceneTerms(DbId scene, in RenderSnapshot.ViewRec v, in FrameHoist h)
    {
        FogTerms(scene.Index, in v, in h);
        // FrameInputs.Focus is NOT SceneDb.SceneFocus. It is the SHADOW MAP's centre, and mode 0 has exactly
        // one writer for it: SceneInputs.ShadowFocus, the camera-follow target. SceneFocus is the
        // Scene::SetSceneFocus column -- a different question with the same word in it. Publishing it here put
        // the cascades at the origin while the PC stood elsewhere, and it ran per VIEW, so it also overwrote
        // whatever the shadow slot had just published.
        AreaTerms(in h);
    }

    /// <summary>This view's fog into <see cref="FrameInputs"/>. CX-188: the three sources -- the AREA's own
    /// scene, the scene SHELL's words, the scene columns -- are all engine or game-thread state, so the
    /// choice between them is made at the fold (<see cref="FrameHoist.FoldFog"/>) and all that happens here
    /// is the copy into the frame's inputs. The ordering argument that picked the area first is that
    /// function's and is written there.</summary>
    private static void FogTerms(int i, in RenderSnapshot.ViewRec v, in FrameHoist h)
    {
        _fogPath = v.Fog.Path;
        FrameInputs.FogOn = v.Fog.On;
        if (v.Fog.On)
        {
            FrameInputs.FogStart = v.Fog.Start;
            FrameInputs.FogEnd = v.Fog.End;
            FrameInputs.FogColor = v.Fog.Colour;
        }
        FogNote(i, in h);
    }

    /// <summary>Which source the fold took: 1 the area, 2 the scene columns, 3 the scene shell.</summary>
    private static int _fogPath;
    private static int _fogSaidPath = -1;
    private static bool _fogSaidOn;
    private static Vector3 _fogSaidCol;
    private static float _fogSaidEnd;
    private static bool _fogSaidSky;

    /// <summary>One line whenever the fog the frame will draw changes -- path, switch, colour, range, the
    /// area's day/night state and the engine's clear colour -- so a wrong fog in a player's session names its
    /// source from cx.log. Cold: a still scene compares four values and writes nothing.</summary>
    private static void FogNote(int i, in FrameHoist h)
    {
        bool on = FrameInputs.FogOn;
        Vector3 col = FrameInputs.FogColor;
        float end = FrameInputs.FogEnd;
        bool haveSky = SceneFog.TrySky(out Vector3 sky);
        if (_fogPath == _fogSaidPath && on == _fogSaidOn && col == _fogSaidCol && end == _fogSaidEnd
            && haveSky == _fogSaidSky) return;
        _fogSaidPath = _fogPath; _fogSaidOn = on; _fogSaidCol = col; _fogSaidEnd = end; _fogSaidSky = haveSky;
        float[] clr = ScreenOwner.ClearRgba;
        // CX-188: the area's terms as the FOLD read them, not a fresh walk. This line runs inside the draw,
        // which may be the render thread, where CurrentArea() is a read of the game thread's memory.
        string area = !h.HaveArea ? "no area"
            : $"area night={h.Night} fog=({h.FogColour.X:F3},{h.FogColour.Y:F3},{h.FogColour.Z:F3}) "
            + $"scene={(h.HaveAreaScene ? "set" : "null")} range={h.AreaFogStart:F1}/{h.AreaFogEnd:F1}";
        _log?.Always($"SceneFeed: fog scene={i} path={_fogPath} on={on} col=({col.X:F3},{col.Y:F3},{col.Z:F3}) "
                   + $"start={FrameInputs.FogStart:F1} end={end:F1} | {area} | clear=({clr[0]:F3},{clr[1]:F3},{clr[2]:F3})"
                   + (haveSky ? $" | sky term ({sky.X:F3},{sky.Y:F3},{sky.Z:F3}) mix={SceneFog.SkyMix:F2}" : " | sky term OFF"));
    }

    /// <summary>The area's own flags, from the one place vanilla learns them. <c>SceneIsWorld</c> is NOT this
    /// question -- it means "the scene has a tile grid", so every outdoor area read as INTERIOR and lost its
    /// sun, its weather and its wetness. Interior is <c>CNWCArea</c> flags bit 0, which is what
    /// <c>SceneInputs.FrameStart</c> reads in mode 0; this is the same read, so the two modes agree by
    /// construction rather than by coincidence.
    /// <para>Latched on the area POINTER rather than asked per view: the walk is three guarded derefs and the
    /// answer only changes at an area edge.</para></summary>
    private static void AreaTerms(in FrameHoist h)
    {
        // CX-188: the flags and the extent come off the FOLD, which read them every frame on the game thread
        // -- read EVERY frame rather than latched on the pointer, because the area is allocated before
        // LoadArea fills it and a latch taken on the first frame that sees the pointer reads a zero Flags
        // word and calls every interior exterior for the life of the area (which is what DB mode did).
        if (h.Area != _areaPtr || h.Interior != _interior)
        {
            _areaPtr = h.Area; _interior = h.Interior;
            _log?.Info($"SceneFeed: area is {(_interior ? "INTERIOR -- no sun shadows" : "exterior -- sun shadows on")}");
        }
        FrameInputs.Interior = h.Interior;
        FrameInputs.AreaPtr = h.Area;
        FrameInputs.AreaExtent = h.Extent;
    }

    private static IntPtr _areaPtr;
    private static bool _interior;

    private static void CamTerms(Vector3 pos, Quaternion rot, float fovy, float aspect, float near, float far,
                                 int w, int h, bool perspective)
    {
        FrameInputs.CameraPos = pos;
        FrameInputs.CameraRot = rot;
        FrameInputs.HasCamera = true;
        FrameInputs.SceneCamPos = pos;
        FrameInputs.SceneCamFwd = FrameRead.ViewForward(rot);
        FrameInputs.CamFovy = fovy;
        FrameInputs.CamAspect = aspect;
        FrameInputs.CamNear = near;
        FrameInputs.CamFar = far;
        FrameInputs.CamViewW = w;
        FrameInputs.CamViewH = h;
        FrameInputs.HasPerspective = perspective;
        FrameInputs.SceneCamP11 = FrameRead.P11(fovy);
        FrameInputs.HasSceneCam = true;
    }
}
