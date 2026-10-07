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
/// <c>AlloyRenderer.Uniforms</c>, <c>CullPass</c> and <c>LightRegistry</c>'s setup radius. ->
/// <see cref="Tick"/>.</description></item>
/// <item><term>ambient</term><description><c>Scene+0x168</c>, which <c>Scene::Render</c> hands
/// <c>SetLightModel</c>. Alloy takes its area terms from <c>LightRegistry</c>'s index -1 lights instead, so in
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

    /// <summary>Fill <see cref="FrameInputs"/> for a DB-mode frame: the fog block and the focus, from the
    /// scene's columns, and the camera block from the view <see cref="DbCamera.BuildView"/> computed. The
    /// counterpart of <c>FrameRead.Tick(IntPtr scene)</c>, which does the same from the engine object -- same
    /// fields, same meaning, so every consumer downstream is unchanged.
    /// <para>The area light sets are NOT filled here: they come from the scene's light PARTS, which is the
    /// light registry's walk over the DB (seam-feed), not a scene column.</para></summary>
    internal static void Tick(DbId scene, DbId camera, in AlloyView view)
    {
        if (!Ok(scene)) return;
        SceneTerms(scene);
        if (!SceneDb.Cameras.Valid(camera) || !view.Valid) { FrameInputs.HasSceneCam = false; return; }
        CamTerms(DbCamera.PosOf(camera), DbCamera.RotOf(camera),
                 view.Fovy, view.Aspect, view.Near, view.Far, view.W, view.H, view.Perspective);
    }

    /// <summary>The same fill from a published <c>RenderSnapshot.ViewRec</c>: the camera's state AS RECORDED,
    /// which is what the renderer must shade with. Reading the camera columns again here would hand frame N's
    /// geometry frame N+1's camera whenever the render thread is a frame behind.</summary>
    internal static void TickFromView(DbId scene, DbId camera, in RenderSnapshot.ViewRec v)
    {
        // CX-120: the renderer's own view latch, from the SNAPSHOT's matrices. Fed before anything else this
        // view does, because DrawOwned refuses a frame with no measured world camera -- and that refusal is
        // terminal for the session, not for the frame.
        AlloyCamera.NoteDbView(in ViewOf(in v), v.World);
        if (!Ok(scene)) return;
        SceneTerms(scene);
        CamTerms(v.CamPos, v.CamRot, v.Fovy, v.Aspect, v.Near, v.Far, v.W, v.H, v.Perspective);
        if (v.World) RuntimeWind(scene);
    }

    /// <summary>The area wind in the engine's shader block, as <c>GLRender::CheckAndUpdateUniformDirtyDataForFrame</c>
    /// @0x5d4e50 copies it (<c>*(currentscene+0x160)+0x20</c> into block <c>+0x28</c>). That copy runs only from
    /// <c>Scene::Render</c>, which DB mode never enters, so the rain fell straight down whatever the area's wind.
    /// The game's <c>setglobalwind</c> still lands in the shell's own <c>WindManager</c> (<c>SceneFns.SeedWind</c>).</summary>
    private static void RuntimeWind(DbId scene)
    {
        if (AlloyRenderer.Rt.Ptr == 0) return;
        nint sc = SceneDb.SceneFacade[scene.Index];
        if (sc == 0) return;
        var wm = new WindManager(new Scene(sc).WindManager);
        if (wm.IsNull) return;
        AlloyRenderer.Rt.AreaWind = wm.BaseWind;
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

    private static void SceneTerms(DbId scene)
    {
        int i = scene.Index;
        FogTerms(i);
        // FrameInputs.Focus is NOT SceneDb.SceneFocus. It is the SHADOW MAP's centre, and mode 0 has exactly
        // one writer for it: SceneInputs.ShadowFocus, the camera-follow target. SceneFocus is the
        // Scene::SetSceneFocus column -- a different question with the same word in it. Publishing it here put
        // the cascades at the origin while the PC stood elsewhere, and it ran per VIEW, so it also overwrote
        // whatever the shadow slot had just published.
        AreaTerms();
    }

    /// <summary>Fog, read off the scene SHELL at the engine's own offsets rather than out of a DB column.
    /// <para>The shell is the truth for fog because the game does not set it through a <c>CAurScene</c> slot:
    /// <c>CNWCArea::SetFog</c>/<c>SetFogAmount</c> emit <c>FuncInterp("mainscene.fog*")</c> TEXT, which
    /// resolves the scene by name through <c>FindScene</c> and then writes the words directly. Those writes
    /// land in the shell, and a column would not see them. <c>FrameRead.ReadFog</c> reads the same four words
    /// off the engine's <c>Scene</c> in mode 0, so the two modes read one layout.</para>
    /// <para>No shell (a fixture scene) falls back to the columns the slots keep.</para></summary>
    private static void FogTerms(int i)
    {
        _fogPath = 0;
        FogRead(i);
        FogNote(i);
    }

    /// <summary>Which source <see cref="FogRead"/> took: 1 the area, 2 the scene columns, 3 the scene shell.</summary>
    private static int _fogPath;
    private static int _fogSaidPath = -1;
    private static bool _fogSaidOn;
    private static Vector3 _fogSaidCol;
    private static float _fogSaidEnd;
    private static bool _fogSaidSky;

    /// <summary>One line whenever the fog the frame will draw changes -- path, switch, colour, range, the
    /// area's day/night state and the engine's clear colour -- so a wrong fog in a player's session names its
    /// source from cx.log. Cold: a still scene compares four values and writes nothing.</summary>
    private static void FogNote(int i)
    {
        bool on = FrameInputs.FogOn;
        Vector3 col = FrameInputs.FogColor;
        float end = FrameInputs.FogEnd;
        bool haveSky = SceneFog.TrySky(out Vector3 sky);
        if (_fogPath == _fogSaidPath && on == _fogSaidOn && col == _fogSaidCol && end == _fogSaidEnd
            && haveSky == _fogSaidSky) return;
        _fogSaidPath = _fogPath; _fogSaidOn = on; _fogSaidCol = col; _fogSaidEnd = end; _fogSaidSky = haveSky;
        CNWCArea a = SceneInputs.CurrentArea();
        float[] clr = ScreenOwner.ClearRgba;
        string area = a.IsNull ? "no area"
            : $"area night={a.IsNight} day=({a.DayFog[0]:F3},{a.DayFog[1]:F3},{a.DayFog[2]:F3}) "
            + $"nightfog=({a.NightFog[0]:F3},{a.NightFog[1]:F3},{a.NightFog[2]:F3}) scene={(a.Scene.IsNull ? "null" : "set")}";
        _log?.Always($"SceneFeed: fog scene={i} path={_fogPath} on={on} col=({col.X:F3},{col.Y:F3},{col.Z:F3}) "
                   + $"start={FrameInputs.FogStart:F1} end={end:F1} | {area} | clear=({clr[0]:F3},{clr[1]:F3},{clr[2]:F3})"
                   + (haveSky ? $" | sky term ({sky.X:F3},{sky.Y:F3},{sky.Z:F3}) mix={SceneFog.SkyMix:F2}" : " | sky term OFF"));
    }

    private static void FogRead(int i)
    {
        // CX-120: the AREA's own scene first. The game writes the fog words into whatever object sits at
        // CNWCArea+0x248 -- that is the pointer SetFog/SetFogAmount refuse without and the one LoadArea hands
        // the sun model -- so reading them there needs no name lookup, and in particular does not depend on
        // FindScene answering to "mainscene". The scene facade below stays as the fallback for a view whose
        // scene is not the area's (a GUI panel's), where the area has nothing to say.
        CNWCArea cur = SceneInputs.CurrentArea();
        // The area pointer is null at the main menu and between modules, and `.Scene` is a raw load off it:
        // asking an absent area for its scene reads address 0x248 and takes the process with it.
        Scene area = cur.IsNull ? default : cur.Scene;
        if (!area.IsNull && AreaFog(cur, out Vector3 col))
        {
            _fogPath = 1;
            FrameInputs.FogOn = true;
            // The RANGE is already right on the area's own scene: CNWCModule::AdjustCameraClipRange @0x8d8ad0
            // drives SetFogRange through its vtable, and a vtable call reaches a CX shell. Measured live: a DB
            // shell carries mode 0's 30/45 before anything here runs.
            FrameInputs.FogStart = area.FogStart;
            FrameInputs.FogEnd = area.FogEnd;
            FrameInputs.FogColor = col;
            return;
        }
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0)
        {
            _fogPath = 2;
            FrameInputs.FogOn = SceneDb.SceneFogOn[i];
            if (!FrameInputs.FogOn) return;
            FrameInputs.FogStart = SceneDb.SceneFogNear[i];
            FrameInputs.FogEnd = SceneDb.SceneFogFar[i];
            FrameInputs.FogColor = SceneDb.SceneFogColor[i];
            return;
        }
        _fogPath = 3;
        var sc = new Scene(shell);
        bool on = sc.FogEnable != 0;
        FrameInputs.FogOn = on;
        if (!on) return;
        FrameInputs.FogStart = sc.FogStart;
        FrameInputs.FogEnd = sc.FogEnd;
        FrameInputs.FogColor = sc.FogColor;
    }

    /// <summary>Is there fog in this area, and in what colour -- asked of the AREA rather than of its scene.
    /// <para>The ENABLE word on the scene is the one area term that does not reach a CX shell: the game turns
    /// fog on as console TEXT (<c>LoadArea</c> emits <c>FuncInterp("mainscene.fog 1")</c>, and
    /// <c>SetFog</c>/<c>SetFogAmount</c> emit <c>mainscene.fogcolor %f %f %f</c>), which resolves the scene by
    /// NAME. Measured: a DB shell has the right fog RANGE (that one arrives through a vtable call) and
    /// <c>fogEnable = 0</c>, so the by-name path is not landing. The game layer's own answer needs no lookup
    /// -- <c>SetAsDay</c>/<c>SetAsNight</c> call the area's <c>SetFog</c> with the palette's fog triple and
    /// its amount byte -- so that is what is read here.</para>
    /// <para><c>ponytail:</c> the ceiling is a script that changes fog mid-area through the same TEXT path;
    /// it would move the scene's words and not these. The upgrade is making the by-name lookup answer, which
    /// is <c>FindScene</c>'s business, not this function's.</para></summary>
    private static bool AreaFog(CNWCArea a, out Vector3 colour)
    {
        bool night = a.IsNight != 0;
        Span<float> c = night ? a.NightFog : a.DayFog;
        colour = new Vector3(c[0], c[1], c[2]);
        // ON, unconditionally, because that is what loading an area does: LoadArea emits
        // FuncInterp("mainscene.fog 1") with no test beyond "the sun model was created". The amount byte is
        // not the switch -- SetAsDay/SetAsNight pass it to SetFog ALONGSIDE this triple, and it reads 0 on an
        // area whose mode-0 frame fogs -- so it is not consulted here.
        return true;
    }

    /// <summary>The area's own flags, from the one place vanilla learns them. <c>SceneIsWorld</c> is NOT this
    /// question -- it means "the scene has a tile grid", so every outdoor area read as INTERIOR and lost its
    /// sun, its weather and its wetness. Interior is <c>CNWCArea</c> flags bit 0, which is what
    /// <c>SceneInputs.FrameStart</c> reads in mode 0; this is the same read, so the two modes agree by
    /// construction rather than by coincidence.
    /// <para>Latched on the area POINTER rather than asked per view: the walk is three guarded derefs and the
    /// answer only changes at an area edge.</para></summary>
    private static void AreaTerms()
    {
        CNWCArea a = SceneInputs.CurrentArea();
        // CX-120: read EVERY frame rather than latch on the pointer. The area is allocated before LoadArea
        // fills it, so a latch taken on the first frame that sees the pointer reads a zero Flags word and
        // calls every interior exterior for the life of the area -- which is what DB mode did. Mode 0 latches
        // too, but only from its stage 3, after the area is up. Three guarded derefs is not worth a latch.
        bool interior = !a.IsNull && (a.Flags & InteriorFlag) != 0;
        _extent = a.IsNull ? default
            : new Vector2(a.TileWidth * AreaBorder.TileSize, a.TileHeight * AreaBorder.TileSize);
        if (a.Ptr != _areaPtr || interior != _interior)
        {
            _areaPtr = a.Ptr; _interior = interior;
            _log?.Info($"SceneFeed: area is {(_interior ? "INTERIOR -- no sun shadows" : "exterior -- sun shadows on")}");
        }
        FrameInputs.Interior = interior;
        FrameInputs.AreaPtr = a.Ptr;
        FrameInputs.AreaExtent = _extent;
    }

    /// <summary><c>CNWCArea</c> flags bit 0: the area is interior/underground, so it has no sun.</summary>
    private const uint InteriorFlag = 1;

    private static IntPtr _areaPtr;
    private static bool _interior;
    private static Vector2 _extent;

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
