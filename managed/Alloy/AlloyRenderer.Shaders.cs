using System;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#171: the Alloy pass's embedded GLSL and the predicates that vet a content override against it.
/// Split out of the render service so the control flow there reads as control flow; the constants are
/// <c>partial</c>-class members of the same type, so every call site and every
/// <see cref="ShaderStages"/> arm still names them unqualified.
/// <para>The offline checker (<c>check-shaders.py</c>) scrapes <c>const string</c> raw literals from this
/// file, so the declaration shape is load-bearing: a constant assembled any other way is not compiled by
/// the build gate.</para></summary>
public sealed unsafe partial class AlloyRenderer
{

    /// <summary>Resrefs the Alloy colour pass looks up before falling back to the GLSL below (#92). A
    /// content author drops <c>&lt;name&gt;.shd</c> in a hak, <c>override/</c> or NWSync and gets that stage
    /// instead; the lit fragment resref replaces the whole composed stage, <see cref="AlloyLights.FragmentLib"/>
    /// included, so an override must carry its own clustered-lighting helpers -- and may spell their sizing
    /// constants as the <see cref="ShaderResource.Tokens"/> the embedded source uses.</summary>
    internal const string ShdFullbrightVs = "cx_own_vs", ShdFullbrightFs = "cx_own_fs";
    internal const string ShdLitVs = "cx_ownlit_vs", ShdLitFs = "cx_ownlit_fs";
    /// <summary>#129's water stage. Its own pair rather than the lit one's, so an author can rewrite the
    /// water surface without forking every lit part in the world.</summary>
    internal const string ShdWaterVs = "cx_water_vs", ShdWaterFs = "cx_water_fs";

    /// <summary>#84: the per-draw block, reached through <c>uDrawIdx[uDrawBase + gl_DrawID]</c>. This is what makes one
    /// multidraw able to cover a whole group -- everything that varies per part and used to be a uniform lives
    /// here instead. <c>binding = 4</c> must stay in step with <see cref="BindDraw"/>.
    /// <para><c>rot</c> is (cos, sin) of the tile texture rotation: the engine's own vertex stage does
    /// <c>vVertexTexCoords = mat2(m_texture) * vTcIn.xy</c> (base_shaders.bif), and m_texture is
    /// PartTriMesh::TransformTextureMatrix's Z-rotation -- columns (c,s) and (-s,c). The shaders below are
    /// that mat2. <c>amb</c>/<c>dif</c>/<c>emi</c> are the material colours, written only for lit draws.</para>
    /// <para>#120: <c>pbr</c> is (Specularity, Roughness, Metallicness, live slot mask) and <c>spc</c> is
    /// (CustomSpecularColor.rgb, declared?) -- the four stock .mtr parameters, carried per draw so one batch
    /// can still span materials that declare different ones.</para>
    /// <para>#123: <c>feat</c> is (<see cref="ShaderFeature"/>, and the three parameters that feature reads).
    /// Same reason it is per draw and not a uniform: one batch spans materials asking for different
    /// shaders.</para>
    /// <para>#392: <c>prev</c> is the model matrix of the PREVIOUS frame -- rolled forward on every frame the
    /// record is built, so it equals <c>model</c> for everything standing still and lags it by one frame on a
    /// mover. Only the mover velocity stage (<see cref="MotionVs"/>) reads it.</para>
    /// <para>#242: the extra indirection is what lets the records be PERSISTENT -- they are indexed by
    /// ownership slot and written only when the part moves, while the draw order is per pass. The index
    /// array is the only per-draw thing the frame still uploads whole, at four bytes each.</para>
    /// <para>ponytail: both vertex stages read this block, so the whole Alloy pass -- fullbright included --
    /// now needs vertex-stage SSBO access. GL 4.6 only REQUIRES
    /// <c>MAX_VERTEX_SHADER_STORAGE_BLOCKS >= 0</c> (it is the fragment/compute minimums that are 8), so a
    /// strictly-conformant driver may refuse the link, which disarms #82's and #83's paths along with #84's.
    /// Every desktop driver in practice reports >= 8, and the failure is loud (program build fails -> DISARMED,
    /// by name). Upgrade path if a real client ever hits it: probe the limit and put the fullbright variant's
    /// records in a UBO, which has a required vertex-stage minimum of 12 blocks.</para></summary>
    internal const string DrawBufDecl = """
        struct SowxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; mat4 prev; };
        layout(std430, binding = 4) readonly buffer SowxDrawBuf { SowxDraw uDraw[]; };
        layout(std430, binding = 9) readonly buffer SowxDrawIdxBuf { int uDrawIdx[]; };
        // #242: the record slot draw n reads. Records are indexed by OWNERSHIP slot and persist across
        // frames; draw order is per pass, so the two are different numbers and this is the map between them.
        int SowxSlot(int draw) { return uDrawIdx[draw]; }

        """;

    /// <summary>#123: the feature ids, as GLSL. One contract with <see cref="ShaderFeature"/> -- the C# value
    /// is staged into <c>d.feat.x</c> and compared against these, so <see cref="SelfCheck"/> asserts the two
    /// agree rather than trusting a comment to keep them in step.</summary>
    internal const string FeatDecl = """
        #define SOWX_FEAT_WINDGRASS 1
        #define SOWX_FEAT_WINDLEAF  2
        #define SOWX_FEAT_WINDPLANT 3
        #define SOWX_FEAT_ROOFKN    4
        #define SOWX_FEAT_ROOFKNM   5
        #define SOWX_FEAT_FLOWMAP   7
        #define SOWX_FEAT_SKIN      8
        #define SOWX_FEAT_NOIRWET   9
        #define SOWX_FEAT_NOIRGLOSS 10
        #define SOWX_FEAT_GROUNDWEATHER 11
        uniform int uTimeOfDay;        // worldtimerTimeOfDay, the engine's own int
        uniform float uLodBias;        // #391: log2(render scale), <= 0; every material sample takes it
        uniform vec3 uAreaWind;        // areaGlobalWind
        uniform int uAreaFlags;        // areaFlags
        uniform vec3 uCamPos;          // cameraPosition
        uniform vec3 uPlayerPos;       // playerPosition
        uniform vec3 uCamFocus;        // cameraFocus -- the camera PIVOT (runtime +0x90), not uCamPos

        """;

    /// <summary>#123: the vertex-side half of the feature switches -- the sine sway of the three wind shaders
    /// and the camera-height branch of the two roof toggles, replacing seven forked vertex stages.
    /// <para>The arithmetic is the community shaders' verbatim, constants and quirks included:
    /// <c>sqrt(dot(wind.x, wind.y))</c> (a one-component dot, i.e. x*y), the 0.356 no-wind floor, the
    /// per-foliage denominators (grass and leaf /100, plant /1000) and speed multipliers (grass 20000, plant
    /// and leaf 7000), the doubled plant frequency, the root-anchor gradient vs the leaf's flat 5.0, and the
    /// roof's four odd-value <c>areaFlags</c> comparisons. Bug-for-bug is the acceptance criterion here: the
    /// content is tuned against these numbers.</para>
    /// <para><c>aPos</c> IS the shaders' <c>vPos</c> and <c>d.model</c> their <c>m_m</c> -- the Alloy pass
    /// stages the node matrix for matrix-class parts and identity for the rest, which is exactly the model
    /// matrix the engine had bound when it drew them. Skinning is absent because a skinned part is not
    /// ownable at all (<c>TryOwnCandidate</c>'s mode-1 VDO test).</para>
    /// <para>Returns the displaced LOCAL position; <c>anchorZ</c> comes back as the UNDISPLACED world height
    /// the roof fragment stage discards the toolset vertex anchor by.</para></summary>
    /// <summary>#137: the skinning half of the Alloy vertex stages. Vertex-side only -- the fragment stages
    /// never see a bone -- and shared by both stages verbatim so a robe shades identically lit or unlit.
    /// <para><c>binding = 7</c> must stay in step with <see cref="BindBones"/>, and the two attribute
    /// locations with <see cref="LocBoneIdx"/>/<see cref="LocBoneW"/>. A std430 <c>mat4</c> array has a
    /// 64-byte stride, which IS the engine's own <c>m_bones</c> layout, so the CPU side is a memcpy.</para>
    /// <para>The <c>weight &gt; 0.0</c> guards are not a micro-optimisation: an unused lane carries a junk
    /// bone index, and an SSBO read tolerates that far less gracefully than the engine's fixed-size uniform
    /// array did. Same guards, same reason, as <c>CasterPass.CastVs</c>.</para>
    /// <para>One blended MATRIX rather than a blended position, because the normal needs it too: skinning the
    /// position and leaving the normal in bind space lights a moving cloak off a stationary surface.</para>
    /// </summary>
    internal const string BoneVs = """
        layout(location = 8) in vec4 aBoneIdx;
        layout(location = 9) in vec4 aBoneWeight;
        layout(std430, binding = 7) readonly buffer SowxBoneBuf { mat4 uBone[]; };

        // -1 for every draw that is not a posed skinmesh, which is nearly all of them.
        int SowxBoneBase(SowxDraw d) { return int(d.feat.x) == SOWX_FEAT_SKIN ? int(d.feat.y) : -1; }

        mat4 SowxSkinMat(int bb)
        {
            mat4 m = mat4(0.0);
            if (aBoneWeight.x > 0.0) m  = uBone[bb + int(aBoneIdx.x)] * aBoneWeight.x;
            if (aBoneWeight.y > 0.0) m += uBone[bb + int(aBoneIdx.y)] * aBoneWeight.y;
            if (aBoneWeight.z > 0.0) m += uBone[bb + int(aBoneIdx.z)] * aBoneWeight.z;
            if (aBoneWeight.w > 0.0) m += uBone[bb + int(aBoneIdx.w)] * aBoneWeight.w;
            return m;
        }

        """;

    internal const string FeatVs = """
        // MUST mirror ShaderFeatures.Displaces: this predicate decides which parts take the displaced clip
        // expression, and that one decides which parts the CPU keeps out of the prepass. Disagree and a part
        // sits in the prepass computing a clip expression the prepass never compiled.
        bool SowxDisplaces(int feat) { return feat >= SOWX_FEAT_WINDGRASS && feat <= SOWX_FEAT_ROOFKNM; }

        vec4 SowxFeatPos(SowxDraw d, vec3 aPos, out float anchorZ)
        {
            vec4 pos = vec4(aPos, 1.0);
            anchorZ = (d.model * pos).z;
            int feat = int(d.feat.x);
            if (feat >= SOWX_FEAT_WINDGRASS && feat <= SOWX_FEAT_WINDPLANT)
            {
                float wind = sqrt(dot(uAreaWind.x, uAreaWind.y));
                if (wind == 0.0) wind = 0.356;
                bool plant = feat == SOWX_FEAT_WINDPLANT;
                float strength = d.feat.z / (plant ? 1000.0 : 100.0);
                float speed = d.feat.y * (feat == SOWX_FEAT_WINDGRASS ? 20000.0 : 7000.0);
                float phase = (d.model * pos).y * (plant ? 2.0 : 1.0) * float(uTimeOfDay) / speed;
                float force = sin(phase) * strength;
                // leaves have no roots to anchor, so a flat multiplier where the others take a gradient
                force *= feat == SOWX_FEAT_WINDLEAF ? 5.0 : clamp(aPos.z, 0.0, 1.0) * 10.0;
                pos.x += force * wind;
                pos.y += force * wind;
                if (plant) pos.z += force * wind / 4.0;
                else if (feat == SOWX_FEAT_WINDLEAF) pos.z += force * wind / 2.0;
                // the tileset-mesh lift: grass and plant meshes are authored under the walkmesh so the
                // toolset will not collide with them. The leaf shader declares no such uniform, and its
                // record's w carries nothing.
                if (!(feat == SOWX_FEAT_WINDLEAF) && d.feat.w != 0.0) pos.z += d.feat.w;
            }
            else if (feat == SOWX_FEAT_ROOFKN || feat == SOWX_FEAT_ROOFKNM)
            {
                // roofknm measures the clip height from the PLAYER, roofkn from the world
                float clip = d.feat.z + (feat == SOWX_FEAT_ROOFKNM ? uPlayerPos.z : 0.0);
                bool interior = uAreaFlags == 1 || uAreaFlags == 3 || uAreaFlags == 5 || uAreaFlags == 7;
                if ((uCamPos.z < clip || d.feat.w == 1.0) && interior) pos.z += d.feat.y;
                else pos.z -= 500.0;   // out of sight under the floor, for pits and open water
            }
            return pos;
        }

        """;
    /// <summary>#124: the uniforms behind the camera dissolve and scene fog, in both fragment stages. All
    /// four come straight out of the engine's own globals -- the same values <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 hands its own shaders -- so nothing here is a reconstruction of engine state.</summary>
    /// <summary>#341: the fog block on its own, so a stage can fog without declaring the keyhole's
    /// uniforms. <c>uEyePos</c> lives here rather than in each stage's own head because the fog target is
    /// direction-dependent now and every fogging stage needs the same view ray.</summary>
    internal const string FogDecl = """
        uniform vec4 uFogParams;       // (enabled, start, end, 1/(end-start)) -- the engine's fogParams
        uniform vec4 uFogColor;        // #132: LINEAR -- the C# side applies the engine's own sRGB curve
                                       // to the display-space value before uploading it
        uniform vec3 uEyePos;          // #120: this pass's camera, for the view vector
        uniform vec3 uFogSky;          // #341: (sky mix, height falloff per unit, layer floor world Z)
        uniform vec3 uFogSkyColor;     // #341: LINEAR sky colour the fog converges toward
        uniform vec4 uFogSun;          // #341: (world direction toward the sun, in-scatter exponent)
        uniform vec3 uFogSunColor;     // #341: LINEAR sun colour, already scaled by the in-scatter strength

        """;

    internal const string KeyFogDecl = FogDecl + """
        uniform vec2 uKeyholeDia;      // (min, max) screen-relative aperture diameters
        uniform vec2 uKeyholeCam;      // (playerCameraDist, cameraPitch)
        uniform vec3 uPixel;           // pixelParams: (1/width, 1/height, width/height)

        """;

    /// <summary>#147: Ashima's simplex noise on its own, so the tessellation-evaluation stage can
    /// have it without dragging in the keyhole and fog block it used to live inside. Same text, one
    /// home -- two copies of a noise function is two different dissolve edges waiting to happen.</summary>
    internal const string NoiseLib = """
        // Ashima Arts' simplex noise, as the engine ships it in inc_common.shd -- the dissolve edge IS this
        // pattern, so an approximation reads as a different effect rather than a slightly different one.
        vec4 SowxPermute(vec4 x) { return mod((x * 34.0 + 1.0) * x, 289.0); }

        float SowxNoise3D(vec3 v)
        {
            const vec2 C = vec2(0.16666667, 0.33333333);
            const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
            vec3 i  = floor(v + dot(v, C.yyy));
            vec3 x0 = v - i + dot(i, C.xxx);
            vec3 g = step(x0.yzx, x0.xyz);
            vec3 l = 1.0 - g;
            vec3 i1 = min(g.xyz, l.zxy);
            vec3 i2 = max(g.xyz, l.zxy);
            vec3 x1 = x0 - i1 + 1.0 * C.xxx;
            vec3 x2 = x0 - i2 + 2.0 * C.xxx;
            vec3 x3 = x0 - 1.0 + 3.0 * C.xxx;
            i = mod(i, 289.0);
            vec4 p = SowxPermute(SowxPermute(SowxPermute(
                       i.z + vec4(0.0, i1.z, i2.z, 1.0))
                     + i.y + vec4(0.0, i1.y, i2.y, 1.0))
                     + i.x + vec4(0.0, i1.x, i2.x, 1.0));
            const float n_ = 1.0 / 7.0;
            vec3 ns = n_ * D.wyz - D.xzx;
            vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
            vec4 x_ = floor(j * ns.z);
            vec4 y_ = floor(j - 7.0 * x_);
            vec4 x = x_ * ns.x + ns.yyyy;
            vec4 y = y_ * ns.x + ns.yyyy;
            vec4 h = 1.0 - abs(x) - abs(y);
            vec4 b0 = vec4(x.xy, y.xy);
            vec4 b1 = vec4(x.zw, y.zw);
            vec4 s0 = floor(b0) * 2.0 + 1.0;
            vec4 s1 = floor(b1) * 2.0 + 1.0;
            vec4 sh = -step(h, vec4(0.0));
            vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
            vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
            vec3 p0 = vec3(a0.xy, h.x);
            vec3 p1 = vec3(a0.zw, h.y);
            vec3 p2 = vec3(a1.xy, h.z);
            vec3 p3 = vec3(a1.zw, h.w);
            vec4 norm = inversesqrt(vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
            p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
            vec4 m = max(0.6 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
            m = m * m;
            return 42.0 * dot(m * m, vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
        }
        """;

    /// <summary>#124: the fragment half of the dissolve and the fog, ported from the generation of
    /// <c>inc_keyhole.shd</c> / <c>inc_standard.shd</c> the installed client actually runs -- the monolithic
    /// <c>base_shaders.bif</c> set, not the split-include fork (#107 s11). Constants, the simplex noise and
    /// the punch-through/border split are verbatim; what changed is only where the inputs come from.
    /// <para>The engine computes <c>fFragCamOffset</c> in its VERTEX stage and interpolates it. Recomputing
    /// it per fragment from <c>vDepth</c> is the same value: both are perspective-correct interpolations of
    /// a linear function of the view position, and <c>gl_Position.w</c> IS <c>-vPosView.z</c>.</para>
    /// <para>The engine reconstructs the world position with <c>m_view_inv * vPosView</c> purely because its
    /// stage does not carry one. Ours does, and it is the same point.</para></summary>
    internal const string KeyholeFs = NoiseLib + """

        // inc_keyhole.shd's own constants. FLOOR_OFFSET is the height above the player below which nothing
        // dissolves -- it is what stops the floor under the PC being punched out along with the wall.
        const float SOWX_KH_FLOOR = 1.0, SOWX_KH_BLACK = 0.8, SOWX_KH_NOISE = 0.35,
                    SOWX_KH_PUNCH = 0.3, SOWX_KH_SIZE = 12.5;

        /// Returns the LINEAR colour multiplier the dissolve border wants, or a negative value meaning
        /// "punch through" -- the caller discards. 1.0 = this fragment is not in the aperture at all.
        float SowxKeyhole(float dissolve, vec3 world, float depth)
        {
            // vDepth is -vPosView.z, so every sign below is the engine's with that substitution made.
            float off = depth - uKeyholeCam.x + (90.0 - uKeyholeCam.y) / 45.0;
            if (dissolve == 0.0 || off >= 0.0) return 1.0;
            float r = clamp((uKeyholeDia.y - uKeyholeDia.x) * uKeyholeDia.x * SOWX_KH_SIZE / depth
                            + uKeyholeDia.x, uKeyholeDia.x, uKeyholeDia.y);
            float r2 = r * r;
            vec2 cv = vec2(2.0 * gl_FragCoord.x * uPixel.x - 1.0, 2.0 * gl_FragCoord.y * uPixel.y - 1.0);
            if (uPixel.x < uPixel.y) cv.x *= uPixel.z; else cv.y /= uPixel.z;
            float d2 = dot(cv, cv);
            if (d2 >= r2) return 1.0;
            float noise = SowxNoise3D(world) * 0.5 + 0.5;
            float lvl = 1.0 - d2 / r2 - noise * SOWX_KH_NOISE + 0.5 / off;
            lvl *= clamp(world.z - SOWX_KH_FLOOR - uPlayerPos.z, 0.0, 1.0);
            if (lvl >= SOWX_KH_PUNCH) return -1.0;
            if (lvl > 0.0) return 1.0 - SOWX_KH_BLACK * (lvl * lvl / (SOWX_KH_PUNCH * SOWX_KH_PUNCH));
            return 1.0;
        }

        """;

    /// <summary>#341: the fog, split out of the keyhole so a stage can have one without the other (the
    /// particle stage fogs and never dissolves; the depth prepass dissolves and never fogs).
    /// <para>The ramp is untouched engine: <c>GetFog</c>'s linear lerp between the area author's start and
    /// end, in view depth. What #341 changes is the two ends of it -- the target colour is built per ray
    /// instead of being one constant, and the factor can be thinned by height.</para></summary>
    internal const string FogFs = """

        /// How fogged this fragment is, 0..1. The area author's ramp, times the height layer.
        float SowxFogFactor(vec3 world, float depth)
        {
            if (uFogParams.x == 0.0) return 0.0;
            float f = (depth - uFogParams.y) * uFogParams.w;
            if (f <= 0.0) return 0.0;
            // #341: fog as a LAYER, thinning above uFogSky.z. Off (uFogSky.y == 0) is the boot default and
            // the author's ramp alone -- this is the one term here that changes density rather than colour.
            if (uFogSky.y > 0.0) f *= exp(-max(world.z - uFogSky.z, 0.0) * uFogSky.y);
            return min(f, 1.0);
        }

        /// The LINEAR colour a fogged fragment mixes toward along this ray. Three weights, all of them
        /// direction or depth -- none touches the ramp:
        ///  - the sky, full at and above the horizon (where the seam against the skybox is) and falling off
        ///    looking steeply DOWN, where there is no sky to match and the author's colour is the honest one;
        ///  - the FOG ITSELF. #341's first cut pulled the target all the way to the sky at every depth, so a
        ///    lightly hazed building took a sky-coloured haze rather than the author's. Scaling by `f` keeps
        ///    the near and middle of the ramp on the colour the builder picked and lets the sky win only
        ///    where the fog is total, which is the one place the seam exists;
        ///  - the sun, added rather than mixed: looking into the sun through fog is BRIGHTER than the fog,
        ///    and a mix could only ever tint it.
        vec3 SowxFogColourLin(vec3 world, float f)
        {
            vec3 dir = normalize(world - uEyePos);
            float w = uFogSky.x * smoothstep(-0.35, 0.0, dir.z) * f;
            vec3 c = mix(uFogColor.rgb, uFogSkyColor, w);
            return c + uFogSunColor * (f * pow(max(dot(dir, uFogSun.xyz), 0.0), uFogSun.w));
        }

        /// The lerp itself. The engine fogs in DISPLAY space (fslit's tail is ApplyKeyhole,
        /// RevertColorSpace, ApplyFog): mixing in linear against the same endpoints is a different CURVE --
        /// it builds up late and then slams, where the engine's ramp is even. #132 moved this across the
        /// encode and #336 moved it back; #341 keeps it there and fixes what batch 2 left half-done, which
        /// was a display-space source mixed toward a LINEAR target. Both ends are encoded here, once.
        vec3 SowxFog(vec3 lin, vec3 world, float depth)
        {
            float f = SowxFogFactor(world, depth);
            if (f <= 0.0) return lin;
            vec3 g = vec3(1.0 / $SRGB$);
            vec3 disp = mix(pow(max(lin, vec3(0.0)), g),
                            pow(max(SowxFogColourLin(world, f), vec3(0.0)), g), f);
            return pow(max(disp, vec3(0.0)), vec3($SRGB$));
        }

        """;

    /// <summary>The keyhole and the fog, in the order every fragment stage wants them. Declared after
    /// both halves: <c>check-shaders.py</c> inlines only constants it has already read.</summary>
    internal const string KeyFogFs = KeyholeFs + FogFs;

    /// <summary>#347: the VANILLA ENVIRONMENT -- everything a spliced stock include would have got from
    /// the <c>#include</c> lines <see cref="VanillaShader"/> drops, in one block ahead of every marked
    /// region so that two spliced files cannot each declare it. That collision is not hypothetical: both
    /// <c>inc_noir.shd</c> and <c>inc_vision.shd</c> include <c>inc_scriptable</c>, and a uniform declared
    /// twice in one compilation unit is a compile error, not a warning.
    /// <para>Macros where Alloy already carries the value under its own name, so a splice costs no extra
    /// upload; uniforms only where it does not.</para>
    /// <para><b><c>SOWX_ALLOY</c> is the contract with the content repo.</b> A stock file that needs to do
    /// something different when Alloy is the renderer -- because Alloy replaced the engine machinery it
    /// would otherwise read -- branches on this. Old content that has never heard of it compiles exactly as
    /// it does for the engine, which is why the extender and the hak can land in either order.</para>
    /// <para><c>LIGHTING 0</c> is that case's first customer. The engine's forward light arrays
    /// (<c>numLights</c>, <c>lightPosition[]</c>) do not exist here -- Alloy's lights are an SSBO the
    /// clustered pass walks -- so every stock block guarded on them compiles out, and where a file wants a
    /// light term anyway it asks <see cref="SowxVisionLight"/> for Alloy's.</para></summary>
    internal const string VanillaEnvDefs = """

        #define SOWX_ALLOY 1
        #define POSITION_WORLD 1
        #define POSITION_VIEW  0
        #define LIGHTING 0
        #define PI 3.14159265359
        #define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
        // inc_standard.shd:64's own value under VERTEX_COLOR 0, inc_common's default; Alloy carries no
        // per-vertex colour into the lit path.
        #define VertexColor COLOR_WHITE
        #define vPosWorld vWorld
        #define playerPosition uPlayerPos
        // The camera PIVOT, not the eye: inc_uniforms' `cameraFocus`, which the bridge never follows, so
        // Alloy owes the value. Content measures a sight range against it because in chase cam it sits on
        // the controlled character (inc_vision).
        #define cameraFocus uCamFocus
        #define worldtimerTimeOfDay uTimeOfDay
        // The engine's compatibility preamble, the part of it a spliced file can reach. GLRender builds
        // this ahead of every stage it compiles (@0x12f3fd0) and content is written against it.
        #define texture2D texture
        #define textureCube texture
        #define GAMMA_CORRECTION 1
        // ...and the one a STAGE declares rather than the preamble: 1 = vertex, 2 = fragment, as every
        // stock stage sets for itself (`vsparticle.shd` 1, `fsparticle.shd` 2) and `inc_common.shd` defaults
        // to 2 for a file that forgot. This block stands in for the stage a spliced file thinks it is in,
        // and that stage is `cx_ownlit_fs` -- so 2. Undefined it is not a compile error, which is the whole
        // hazard: `#if SHADER_TYPE == 2` silently reads 0 and the file's fragment half vanishes. `inc_vision`
        // is written that way (its body is FRAGMENT ONLY because it reads gl_FragCoord, with an empty
        // `ApplyRacialVision` as the #else), so without this the racial-vision hook compiled to a no-op and
        // SoW's darkness limiting simply did not happen under Alloy -- no error, no effect.
        #define SHADER_TYPE 2
        #define vTexCoords vUV
        #define texUnit0 uTex
        // Material texture slot 6. Stock names it by unit; Alloy binds it as the flow map's sampler, and
        // which of the two a material means is its own business -- the slot is the contract, not the name.
        #define texUnit6 uFlowTex
        // The area's fog colour as the engine's stages see it: DISPLAY space. Alloy linearises it at upload
        // (#132), so this undoes that rather than carrying the same number twice.
        #define fogColor vec4(pow(max(uFogColor.rgb, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0)
        // The renderer's own encode exponent, for content that has a display-space number to apply to a
        // linear value.
        #define SOWX_GAMMA $SRGB$
        // #350: inc_material's own resolution of the material terms. A stage that declares one as a
        // constant shadows the macro for its own text; a stage that declares none reads and WRITES the
        // globals, which is what td_ground_fs's min()/max() idiom does.
        #define MATERIAL_ROUGHNESS_VALUE fRoughness
        #define MATERIAL_SPECULARITY_VALUE fSpecularity
        #define MATERIAL_METALLICNESS_VALUE fMetallicness
        #define MATERIAL_SPECULAR_COLOR_VALUE SpecularColor

        """;

    /// <summary>#350: what a hosted stage gets in place of <c>inc_standard</c>. The two calls every stock
    /// fragment stage wraps its own work in are Alloy's: the normal is final before the stage's body runs
    /// and the lighting after it is the clustered pass. The setup call is NOT empty, and that was measured:
    /// a stock main opens with <c>FragmentColor = vec4(1.0)</c> and it is <c>SetupStandardShaderInputs</c>
    /// that multiplies the texel in (<c>inc_standard.shd</c>, <c>FragmentColor *= MainTextureColor</c>),
    /// so an empty stand-in hands every hosted body a WHITE surface -- the weather ground rendered as a
    /// snowfield with the snow at 0. The scriptable block still has to come from somewhere, and stock gets
    /// it through this include.</summary>
    internal const string VanillaStandardFs = """
        #ifndef SOWX_INC_STANDARD
        #define SOWX_INC_STANDARD
        #include "inc_scriptable"
        void SetupStandardShaderInputs() { FragmentColor *= SowxHostTexel; }
        void ApplyStandardShader() { }
        #endif

        """;

    /// <summary>The declarations half of the environment, after <see cref="VanillaEnvDefs"/>. The macro
    /// half is separate because a hosted stage re-issues it after scoping out its own defines.</summary>
    internal const string VanillaEnvFs = VanillaEnvDefs + """

        // The engine's own projection, off the same matrix stack Alloy reads its own from. Only the
        // DIAGONAL is ever read by the files spliced here (m_proj[3][3] separates the orthographic 2D UI
        // from the perspective world), and a diagonal element is the same in either transpose convention --
        // which matters, because Alloy stages hold the transpose of what the engine's do.
        uniform mat4 m_proj;
        // The scriptable block is NOT declared here. Every file spliced into this stage includes
        // inc_scriptable for it, and the bridge resolves that include through the resource chain, so
        // declaring it too is a redefinition and a dead stage. What Alloy owes is the VALUES:
        // UploadVanillaEnv and UploadTargetShape write them under those names out of the engine's own
        // tables (scriptableInt(n) = i[10+n], scriptableFloat(n) = f[23+n]), and a name the stage does not
        // use resolves to -1, which GL ignores.

        // inc_common's colour-space pair, and its exact curve. ApplyColorSpace goes display -> WORKING
        // space and RevertColorSpace comes back. The Alloy frame is linear, which is what working space is
        // on a gamma-correcting client, so content that converts either way gets what it expects -- and
        // that is also why a stock stage's FragmentColor is linear where it is handed to a hosted body.
        vec3 ApplyColorSpace(vec3 c)  { return sign(c) * pow(abs(c), vec3($SRGB$)); }
        vec3 RevertColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3(1.0 / $SRGB$)); }
        // #350: the stock stage's working colour, and the texel its setup call multiplies into it --
        // Alloy's linear albedo with the coverage in .a, sampled before the body runs. .rgb is read back
        // as the albedo.
        vec4 FragmentColor;
        vec4 SowxHostTexel;
        // ...and the material terms as inc_material's generated globals: filled from Alloy's synthesis
        // before a hosted body runs, read back after it (see the MATERIAL_*_VALUE macros above).
        float fRoughness, fSpecularity, fMetallicness;
        vec3 SpecularColor;

        // #348: what inc_ripple.shd's TiltNormal writes through, and the two matrices it converts with.
        // The helper tilts a VIEW-space normal in the surface's world xy plane; Alloy's normal is already
        // world-space, so the matrices are IDENTITY and the conversion collapses to the world-plane tilt the
        // helper is written to perform. That is exact, not an approximation -- read the function.
        vec3 vFragmentNormal;
        const mat4 m_view = mat4(1.0);
        const mat4 m_view_inv = mat4(1.0);
        // The GEOMETRY normal (before any normal map) and the eye-to-fragment direction, both under the
        // same identity view: stock reads them in view space and only ever takes a dot or a Z off them,
        // neither of which a rotation changes.
        vec3 vSurfaceNormal;
        vec3 vViewToSurface_n;

        /// #347: how lit this fragment is, for a stock file that would have asked the engine's light arrays.
        /// The RAW measurement -- the caller applies its own floor/full curve to it, which is where those
        /// constants belong.
        /// <para>This is <c>inc_vision.shd</c>'s <c>VisionLightHere</c> term for term: the strongest single
        /// light reaching this point, by the engine's own attenuation, with NO N.L and NO shadow. That is not
        /// an approximation of the stock loop, it IS the stock loop -- it asks "is this spot inside a light",
        /// and max() rather than a sum because two torches do not make that twice as true.</para>
        /// <para>It used to answer with the luma of Alloy's clustered DIFFUSE, which is N.L-weighted, cube-
        /// shadowed and summed. Every one of those makes the answer smaller than the stock loop's, and the
        /// answer holds a distance veil OPEN -- so a lit tavern read as an unlit one and the veil crushed it
        /// to black. A floor at grazing incidence to a wall lantern, or anything the lantern's own cube
        /// shadows, lost the light entirely.</para>
        float SowxVisionLight()
        {
            uint cl = SowxCluster(vDepth);
            uint n = uClCount[cl];
            uint base = cl * SOWX_CLUSTER_CAP;
            vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
            float lit = 0.0;
            for (uint k = 0u; k < n; k++)
            {
                uint li = uClIdx[base + k];
                vec4 pr = uLights[li * 2u];
                float rr = pr.w * pr.w;
                vec3 dl = pr.xyz - vWorld;
                float dd = dot(dl, dl);
                if (dd > rr) continue;
                float f2 = dd / rr;
                float att = (1.0 - f2) / (at.x + at.y * f2);
                vec3 c = SowxLinear(uLights[li * 2u + 1u].rgb);
                lit = max(lit, att * dot(c, vec3(0.299, 0.587, 0.114)));
            }
            return lit;
        }

        """;

    /// <summary>#347/#348: the CONTENT HOOK. A function the lit stage calls and does not implement, so
    /// that display-space shading a content repo invented -- SoW's noir desaturation and racial vision --
    /// lives in that repo and not in this one. (#350 deleted the surface hook beside it: a material's own
    /// custom FS states the surface work already, and <see cref="HostFs"/> runs that file instead of
    /// asking content to restate it.)
    /// <para><b>Why the extender carries none of it.</b> The bridge exists so one copy of a shader runs, and
    /// the copy that runs should be the one its author edits. A stock file (<c>inc_target.shd</c>) belongs
    /// to the ENGINE, so splicing it here is faithful; <c>inc_noir</c> and <c>inc_vision</c> are SoW's, and
    /// an extender that embedded even their stubs would be asserting what a content repo's shaders are
    /// called. This extender is optional and drop-in: run it against any module and these two hooks are
    /// no-ops, which is exactly the right answer for content that never asked for them.</para>
    /// <para>The content file is <c>cx_lit_hooks</c> -- one resref, composed by the content out of its own
    /// includes (which <see cref="VanillaShader"/> resolves through the resource chain). The signature
    /// below is the whole contract, and it is generic on purpose: nothing in it names a feature. It stays
    /// an adapter deliberately: "which display filters, in which order" is stated in no stock file, and
    /// deriving it would mean Alloy naming SoW's functions.</para>
    /// <para>It runs after fog, in DISPLAY space, where <c>inc_standard.shd</c> puts the desaturation and
    /// the vision passes.</para></summary>
    internal const string HookFs = """

        // $VANILLA cx_lit_hooks
        vec3 SowxDisplayHook(vec3 c) { return c; }
        // $VANILLA-END

        """;

    /// <summary>#350: the HOSTED content stages -- one region per <see cref="ShaderFeature"/> that names a
    /// custom FS (<see cref="ShaderFeatures.FsNames"/>; <c>SelfCheck</c> holds the two lists together). Each
    /// region is the material's own <c>customShaderFS</c>, run as a function of this program: see
    /// <see cref="VanillaShader.HostText"/> for the rewrite and <see cref="VanillaStandardFs"/> for what
    /// stands in for <c>inc_standard</c>. The stub is the wrapper's own empty signature, so a feature whose
    /// file is missing, stale or broken draws the plain lit path -- and the log names it.
    /// <para>The dispatcher is what the lit main calls, at the point a stock stage runs its body: after the
    /// texel is sampled and the normal is final, before anything lights either. It returns whether a body
    /// ran, so the read-back costs nothing on the parts that have none.</para></summary>
    internal const string HostFs = """

        // $VANILLA-HOST fs_noirwet
        void SowxHost_fs_noirwet() { }
        // $VANILLA-END
        // $VANILLA-HOST fs_noirgloss
        void SowxHost_fs_noirgloss() { }
        // $VANILLA-END
        // $VANILLA-HOST td_ground_fs
        void SowxHost_td_ground_fs() { }
        // $VANILLA-END

        bool SowxHostSurface(int feat)
        {
            if (feat == SOWX_FEAT_NOIRWET) SowxHost_fs_noirwet();
            else if (feat == SOWX_FEAT_NOIRGLOSS) SowxHost_fs_noirgloss();
            else if (feat == SOWX_FEAT_GROUNDWEATHER) SowxHost_td_ground_fs();
            else return false;
            return true;
        }

        """;

    /// <summary>#346: the SPELL-TARGETING SHAPE -- the red circle (or cone, or rectangle) a caster sees on
    /// the ground while picking a target. Vanilla draws it as arithmetic in <c>inc_standard.shd</c>'s tail,
    /// not as a draw call, which is why the M4 cutover lost it: no engine draw went missing, an engine
    /// SHADER did. <see cref="RspShim.EngineOverlays"/> is therefore the wrong tool here and was not used --
    /// there is nothing to replay.
    /// <para><b>The shape's code is not written here.</b> It is the engine's own <c>inc_target.shd</c>,
    /// spliced in at compile time by <see cref="VanillaShader"/> from whatever the resource chain supplies
    /// -- a hak, <c>override/</c>, NWSync, or the stock <c>base_shaders.bif</c>. That is the point of the
    /// bridge: <c>sow-topdata</c> customises this file (a Reach-promoted range, a Widen-grown shape) and
    /// nothing in the extender has to know.</para>
    /// <para><b>Between the markers is a STUB, not a snapshot.</b> A copy would be the second
    /// implementation this bridge exists to delete, and it would be the worst kind: one that runs only when
    /// the splice failed, so its drift from the content would never be seen. What the fallback owes is the
    /// world, not the feature -- the lit program has to link whatever the resource chain hands it, because
    /// a program that fails to build reverts every Alloy part to engine drawing. So the stub answers "no
    /// shape here" and the frame is otherwise untouched. No file, no overlay, and the log says so.</para>
    /// <para>Everything the file wants from its own includes is in <see cref="VanillaEnvFs"/>. The uniforms
    /// it declares ITSELF (<c>targetShape</c> and friends) are not repeated there --
    /// <c>UploadTargetShape</c> writes them under the engine's own names, which is what lets the stock text
    /// drop in unedited.</para></summary>
    internal const string TargetFs = """

        // $VANILLA inc_target
        float GetTargetGroundHighlightStrength() { return 0.0; }
        void ApplyTargetGroundHighlight(inout vec4 FragmentColor, float fStrength) { }
        // $VANILLA-END

        /// #346/#347: the three display-space overlays, in ONE encode bracket and in inc_standard.shd's own
        /// order -- noir desat, then racial vision, then the targeting shape. The engine applies all three
        /// after RevertColorSpace; the same arithmetic on linear values is a different, stronger effect
        /// (#222's trap), and bracketing them together costs one encode instead of three.
        /// <para>The shape goes last on purpose, and the content says why: it is UI drawn on the ground, and
        /// a caster has to read its colour over a monochrome scene.</para>
        vec3 SowxDisplayOverlays(vec3 lin)
        {
            float strength = GetTargetGroundHighlightStrength();
            vec4 c = vec4(pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0);
            // The content's display-space passes first (SoW puts its desaturation and racial vision here),
            // then the ENGINE's targeting shape last -- inc_standard.shd's own order, and its own reason:
            // the shape is UI drawn on the ground, and a caster has to read its colour over a monochrome
            // scene.
            c.rgb = SowxDisplayHook(c.rgb);
            ApplyTargetGroundHighlight(c, strength);
            return pow(max(c.rgb, vec3(0.0)), vec3($SRGB$));
        }

        /// #347: the CONTENT half alone, for a stage whose stock counterpart runs the hooks but not the
        /// targeting shape. `fswater.shd` is the case and the reason: its tail calls ApplyNoirDesat and
        /// ApplyRacialVision and stops there, so adding the shape to Alloy's water would paint a reticle
        /// vanilla never painted. Same encode bracket, one term short.
        vec3 SowxDisplayContent(vec3 lin)
        {
            vec3 c = pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$));
            c = SowxDisplayHook(c);
            return pow(max(c, vec3(0.0)), vec3($SRGB$));
        }

        """;

    /// <summary>#136's known ceiling, closed: the hosted sky program is the ENGINE's and writes DISPLAY space
    /// into Alloy's LINEAR target, so the resolve encoded it a second time and the sky read brighter than
    /// vanilla. This runs right after the hosted draw, over the pixels it wrote (the depth range and LEQUAL
    /// test the sky pass already holds: only where nothing wrote depth), reading each texel back through a
    /// texture barrier and decoding it once. The frame stays linear everywhere, which is the contract the
    /// resolve's own guard enforces -- the fix could not live there.</summary>
    internal const string SkyDecodeFs = """
        #version 460 core
        layout(location = 0) out vec4 frag;
        layout(location = 1) out float oReveal;
        // OIT (OitPass): 1 = write the weighted-blended accumulation pair instead of a composited colour.
        uniform int uOit;
        vec4 SowxOitOut(vec4 c)
        {
            if (uOit == 0) { oReveal = 1.0; return c; }
            float a = clamp(c.a, 0.0, 1.0);
            // McGuire & Bavoil eq. 9: near-and-opaque weighs most; clamped so fp16 neither under- nor overflows.
            float w = clamp(pow(min(1.0, a * 10.0) + 0.01, 3.0) * 1e8 * pow(1.0 - gl_FragCoord.z * 0.9, 3.0), 1e-2, 3e3);
            oReveal = a;
            return vec4(c.rgb * a, a) * w;
        }
        uniform sampler2D uHdr;
        void main()
        {
            vec4 c = texelFetch(uHdr, ivec2(gl_FragCoord.xy), 0);
            frag = vec4(pow(max(c.rgb, vec3(0.0)), vec3($SRGB$)), c.a);
        }
        """;

    /// <summary>The one clip-space expression EVERY Alloy vertex stage computes, character for character.
    /// #119's prepass writes the depth the colour pass then tests LEQUAL against, and <c>invariant
    /// gl_Position</c> only guarantees a bit-identical result across programs for the same sequence of
    /// operations -- the lit stage's former <c>uVP * (model * pos)</c> association is a different sequence,
    /// and a ULP of drift the wrong way punches holes in lit surfaces. A content override (#92) that rewrites
    /// this expression, or drops the invariant qualifier, reintroduces exactly that.</summary>
    internal const string ClipExpr = "gl_Position = uVP * d.model * vec4(aPos, 1.0);";

    internal const string OwnedVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;

        """ + DrawBufDecl + FeatDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec2 vUV;
        flat out float vAlpha;         // #126: the per-draw alpha multiplier, forwarded rather than re-read

        """ + FeatVs + BoneVs + """
        void main()
        {
            SowxDraw d = uDraw[SowxSlot(uDrawBase + gl_DrawID)];
            float anchorZ;
            vec4 pos = SowxFeatPos(d, aPos, anchorZ);
            int bb = SowxBoneBase(d);
            if (bb >= 0) pos = SowxSkinMat(bb) * vec4(aPos, 1.0);
            // The skinned arm takes the DISPLACED expression, not a third one, and takes it first: a skinned
            // position cannot be the literal clip expression the prepass compiled, which is why #137 keeps
            // this class out of the prepass (ShaderFeatures.OutOfPrepass says so in one place for both).
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else if (!SowxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
            else gl_Position = uVP * d.model * pos;
            vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            vAlpha = d.rot.w;
        }
        """;
    /// <summary>The unlit variant: the engine draws these parts with lighting disabled (MatStatic's flat-white
    /// branch -- cap 5 off, or a <c>mat+0x98</c> material), so flat texture IS their vanilla appearance.</summary>
    internal const string OwnedFs = """
        #version 460 core
        in vec2 vUV;
        flat in float vAlpha;
        uniform sampler2D uTex;
        uniform float uLodBias;        // #391
        uniform int uViz;
        // #126: the engine's fAlphaDiscardValue -- 0.2 on the classes it draws under Enable(1), -1.0 (i.e.
        // no test at all) on the rest. A uniform here because the class is a per-batch fact.
        uniform float uAlphaCut;
        // #222: the constant colour PartTriMesh::Mat sets before the draw. White for every ordinary unlit
        // part; the SCENE'S FOG COLOUR for a part whose gob carries the +0x19 tint byte, which is what the
        // skyfade gob is. A uniform rather than a per-draw record field because the whole unlit class shares
        // one value and the sky changes it at most once per role.
        uniform vec3 uTint;
        layout(location = 0) out vec4 frag;
        layout(location = 1) out float oReveal;
        // OIT (OitPass): 1 = write the weighted-blended accumulation pair instead of a composited colour.
        uniform int uOit;
        vec4 SowxOitOut(vec4 c)
        {
            if (uOit == 0) { oReveal = 1.0; return c; }
            float a = clamp(c.a, 0.0, 1.0);
            // McGuire & Bavoil eq. 9: near-and-opaque weighs most; clamped so fp16 neither under- nor overflows.
            float w = clamp(pow(min(1.0, a * 10.0) + 0.01, 3.0) * 1e8 * pow(1.0 - gl_FragCoord.z * 0.9, 3.0), 1e-2, 3e3);
            oReveal = a;
            return vec4(c.rgb * a, a) * w;
        }
        void main()
        {
            if (uViz == 4) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
            vec4 t = texture(uTex, vUV, uLodBias);
            if (uAlphaCut >= 0.0 && t.a <= uAlphaCut) discard;
            // #132: the target is LINEAR, so the texel's display-space encode comes off here too -- the
            // probe and the frame want the same thing now. Same exponent AlloyLights.SowxLinear uses,
            // spelled out because this stage does not include that lib.
            vec3 tinted = t.rgb * uTint;
            vec3 rgb = pow(max(tinted, vec3(0.0)), vec3($SRGB$));
            frag = SowxOitOut(vec4(rgb, t.a * vAlpha));
        }
        """;

    /// <summary>#119's depth-only stage. Deliberately has NO resref: a stage that writes nothing but depth
    /// has nothing an author could restyle, and an override is one more copy of <see cref="ClipExpr"/> to
    /// keep bit-identical. The fragment stage is empty -- colour writes are masked off, and a program with no
    /// fragment stage at all is a portability gamble for nothing.</summary>
    internal const string DepthVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + FeatDecl + BoneVs + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        void main()
        {
            SowxDraw d = uDraw[SowxSlot(uDrawBase + gl_DrawID)];
            // #262: the skinned arm, and it goes through the same `pos` TEMP the colour stages use so the
            // assigning statement is character for character theirs -- ShaderContract asserts that one
            // literal against all four stages. #119's `invariant gl_Position` is the only reason this stage
            // exists to be careful about, and everywhere else in this renderer that promise is kept by
            // identical TEXT rather than by an equivalent expression tree.
            vec4 pos = vec4(aPos, 1.0);
            int bb = SowxBoneBase(d);
            if (bb >= 0) pos = SowxSkinMat(bb) * vec4(aPos, 1.0);
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else gl_Position = uVP * d.model * vec4(aPos, 1.0);
        }
        """;
    internal const string DepthFs = """
        #version 460 core
        void main() { }
        """;

    /// <summary>The feature prepass's fragment stage: the cutout test the colour stages make, and nothing else.
    /// Same texel (unit 0, the batch's diffuse, the lit stage's rotated <c>vUV</c>), same threshold
    /// (<c>AlloyClasses.Discard</c>), same env exemption (an env-mapped material's alpha is reflectivity, not
    /// coverage -- <c>SOWX_SLOT_ENV</c>). A negative cut (an opaque displaced batch) tests nothing. Flowmap
    /// batches slide their diffuse uv and stay out on the CPU side, so the fetch here is never wrong for them.</summary>
    internal const string DepthFeatFs = """
        #version 460 core
        in vec2 vUV;
        flat in int vDraw;

        """ + DrawBufDecl + """
        uniform sampler2D uTex;
        uniform float uAlphaCut;
        uniform float uLodBias;        // #391: the SAME bias as the colour cutout, or the two tests disagree (#119)
        void main()
        {
            if (uAlphaCut < 0.0) return;
            if ((int(uDraw[vDraw].pbr.w) & 64) != 0) return;
            if (texture(uTex, vUV, uLodBias).a <= uAlphaCut) discard;
        }
        """;

    /// <summary>#124: the depth stage for a batch the camera dissolves. Without it those batches leave the
    /// prepass entirely, and <c>Camera::GetCanKeyholeDissolve</c> answers "CAN this dissolve" -- a near-static
    /// property of a third of the Alloy set in a tiled interior -- while the aperture that actually discards
    /// is a small circle around the PC. Excluding them measured x8,12 -> x15,19 overdraw and doubled the
    /// colour pass, i.e. it handed back most of #119.
    /// <para>So the prepass runs the SAME discard the colour pass will, and writes exactly the holes it is
    /// going to punch. Also has no resref, and for a stronger reason than <see cref="DepthVs"/>: an override
    /// here that drifted from the colour stage's aperture by one constant would write depth over fragments
    /// the colour pass then discards -- the room behind the wall culled, silently.</para></summary>
    internal const string DepthKeyVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + FeatDecl + BoneVs + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec3 vWorld;
        out float vDepth;
        flat out int vDraw;
        void main()
        {
            int id = SowxSlot(uDrawBase + gl_DrawID);
            SowxDraw d = uDraw[id];
            vec4 pos = vec4(aPos, 1.0);
            int bb = SowxBoneBase(d);   // #262: a dissolving skinmesh takes the same arm DepthVs does
            if (bb >= 0) pos = SowxSkinMat(bb) * vec4(aPos, 1.0);
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else gl_Position = uVP * d.model * vec4(aPos, 1.0);
            // vWorld feeds the APERTURE, and it has to be the position the colour stage discards on or the
            // two passes punch different holes. That was a ponytail note while only rigid parts reached this
            // stage -- a sub-pixel speck at the threshold. #262 let skinmeshes in and a cloak's vertices sit
            // METRES from bind pose, so it stops being a speck: the prepass would keep a fragment the colour
            // pass discards, write cloak depth over it, and LEQUAL would cull the room behind the character.
            // Hence `pos`, which is the skinned position when there is one and aPos when there is not.
            // The remaining gap is the DISPLACED case (SowxFeatPos), and those never reach this stage.
            vWorld = (d.model * pos).xyz;
            vDepth = gl_Position.w;
            vDraw = id;
        }
        """;
    internal const string DepthKeyFs = """
        #version 460 core
        in vec3 vWorld;
        in float vDepth;
        flat in int vDraw;

        """ + DrawBufDecl + FeatDecl + KeyFogDecl + """

        """ + KeyholeFs + """
        void main()
        {
            if (SowxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
        }
        """;

    /// <summary>The lit variant's vertex stage. Beyond the fullbright one it carries world position, world
    /// normal and the view depth: <c>gl_Position.w</c> IS the linear view depth under a perspective
    /// projection, so the fragment stage finds its froxel slice without a second matrix.</summary>
    internal const string OwnedLitVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aNrm;

        """ + DrawBufDecl + FeatDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec2 vUV;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vDepth;
        out float vAnchorZ;            // #123: undisplaced world height, for the roof anchor discard
        flat out int vDraw;

        """ + FeatVs + BoneVs + """
        void main()
        {
            int id = SowxSlot(uDrawBase + gl_DrawID);
            SowxDraw d = uDraw[id];
            vec4 pos = SowxFeatPos(d, aPos, vAnchorZ);
            // #137: a posed skinmesh REPLACES the feature position -- the two never coexist (see
            // ShaderFeature.Skin) -- and the same blended matrix then carries the normal, so a cloak is lit
            // off the surface it actually has rather than the one it had in bind pose.
            int bb = SowxBoneBase(d);
            mat4 skin = bb >= 0 ? SowxSkinMat(bb) : mat4(1.0);
            if (bb >= 0) pos = skin * vec4(aPos, 1.0);
            vec4 world = d.model * pos;
            // The undisplaced branch is written out in full rather than folded into the line below, because
            // #119's prepass re-tests this exact expression and `invariant` only holds over an identical
            // SEQUENCE of operations -- not merely identical values. So the test is "does this part displace",
            // NOT "does it have a feature": a nokeyhole or flowmap part does not move, therefore stays in the
            // prepass, and therefore must reach the literal expression the prepass compiled. Only a displacing
            // part takes the other branch, and the CPU excludes exactly those from the prepass with the same
            // predicate -- no text check can catch the two disagreeing, so they mirror each other by
            // construction and SelfCheck holds it.
            // The skinned arm takes the DISPLACED expression, not a third one, and takes it first -- see
            // the fullbright stage for why, and ShaderFeatures.OutOfPrepass for the prepass side of it.
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else if (!SowxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
            else gl_Position = uVP * d.model * pos;
            vDepth = gl_Position.w;
            // shaded at the DISPLACED position, which is where the vanilla shaders light a swaying plant too
            vWorld = world.xyz;
            vNrm = mat3(d.model) * (bb >= 0 ? mat3(skin) * aNrm : aNrm);
            vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            vDraw = id;
        }
        """;

    /// <summary>The lit variant's fragment stage: the engine's area ambient + directional diffuse, then every
    /// point light the froxel holds, then the material terms -- the combine order of inc_lighting.shd's
    /// ComputeLighting (ambient scaled by the material ambient and a sun-facing wrap, diffuse by the material
    /// diffuse, both multiplied onto the LINEARIZED texture).
    /// <para>The sun term is attenuated by <see cref="AlloyLights.SunShadowLib"/>'s cascade lookup (#94);
    /// ambient deliberately is not, it is the term that stands for light arriving from everywhere else.</para>
    /// <para>#120 folds the material read in: GGX specular on every lit part (vanilla runs one on all of them
    /// and the Alloy pass dropped it on 100%), the five extra stock .mtr texture slots, and the four stock
    /// parameter names honoured as declared.</para>
    /// <para>#121 folds the last of the <c>ENVIRONMENT_MAP == 1</c> branches in: ambient specular off the
    /// per-area <see cref="EnvProbe"/> cube on every lit part (the engine gates that term on the specular
    /// define, not on an env-map define, so vanilla feeds it everywhere), plus the diffuse alpha read as
    /// legacy content's reflectivity mask.</para>
    /// <para>ponytail: no synthesis beyond what the stock shaders already do for a declared-nothing material
    /// (#122).</para></summary>
    internal const string OwnedLitFsHead = """
        #version 460 core
        in vec2 vUV;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vDepth;
        in float vAnchorZ;             // #123: undisplaced world height, for the roof anchor discard
        flat in int vDraw;             // this fragment's SowxDraw record: the material colours live there now

        """ + DrawBufDecl + FeatDecl + KeyFogDecl + """
        uniform sampler2D uTex;
        uniform sampler2D uFlowTex;    // #123 slot 6: RG flow direction, only ever bound on a flowmap part
        // #120: the five stock .mtr slots beyond the diffuse, on units 3..7 (1 and 2 are the shadow maps).
        // Sampled only where the record's slot mask says the material actually bound one.
        uniform sampler2D uNrmTex;     // slot 1: tangent-space normal, xy in rg
        uniform sampler2D uSpecTex;    // slot 2: specularity in r
        uniform sampler2D uRoughTex;   // slot 3: roughness in r
        uniform sampler2D uHeightTex;  // slot 4: height, read for surface occlusion
        uniform sampler2D uIllumTex;   // slot 5: self-illumination
        uniform sampler2D uAoTex;      // #146 slot 7: baked ambient occlusion in r
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;         // world-space surface->light for the area directional (sun/moon)
        uniform int uViz;              // /draw viz, see OwnedLitFsMain
        // #121: the per-area IBL probe, holding LINEAR radiance (EnvProbe captures with uCapture = 1, which
        // is what skips the display-space encode below). uProbeMips 0 = nothing captured yet, so no ambient
        // specular at all -- the pre-#121 behaviour, never a half-written cube.
        uniform samplerCube uProbe;
        uniform float uProbeMips;
        uniform float uProbeGain;      // /probe gain, the ambient-specular dial
        uniform int uSceneEnv;         // does the AREA declare an env map (scene+0x1e8)? see below
        uniform int uCapture;          // 1 = this draw IS the probe capture: stay linear, never sample uProbe
        uniform sampler2D uSsao;       // #379: this frame's screen-space visibility (SsaoPass), at gl_FragCoord
        uniform int uSsaoOn;           // >0 when this BATCH is in the depth SsaoPass read; <0 names why not (viz 35)
        uniform sampler2D uSceneDepth; // the prepass depth SsaoPass read, to check the pixel is OURS (viz 37)
        uniform float uAoDirect;       // /ao direct: how much of the occlusion the DIRECT terms take (0 = ADR 0021)
        // #126: the engine's fAlphaDiscardValue, per batch -- 0.2 on the cutout and blended classes,
        // -1.0 (no test) on opaque and additive.
        uniform float uAlphaCut;
        layout(location = 0) out vec4 frag;
        layout(location = 1) out float oReveal;
        // OIT (OitPass): 1 = write the weighted-blended accumulation pair instead of a composited colour.
        uniform int uOit;
        vec4 SowxOitOut(vec4 c)
        {
            if (uOit == 0) { oReveal = 1.0; return c; }
            float a = clamp(c.a, 0.0, 1.0);
            // McGuire & Bavoil eq. 9: near-and-opaque weighs most; clamped so fp16 neither under- nor overflows.
            float w = clamp(pow(min(1.0, a * 10.0) + 0.01, 3.0) * 1e8 * pow(1.0 - gl_FragCoord.z * 0.9, 3.0), 1e-2, 3e3);
            oReveal = a;
            return vec4(c.rgb * a, a) * w;
        }

        // Slot mask bits, matching AlloyRenderer's 1 << (slot - 1).
        const int SOWX_SLOT_NORMAL = 1, SOWX_SLOT_SPEC = 2, SOWX_SLOT_ROUGH = 4,
                  SOWX_SLOT_HEIGHT = 8, SOWX_SLOT_ILLUM = 16;
        // #146: bit 6, texture slot 7 -- the AO map. It multiplies the SAME occlusion factor the height
        // map's local term feeds, so a material carrying both gets one combined ambient scale.
        const int SOWX_SLOT_AO = 128;
        /// the depth encode's own rounding, nothing more: #119 makes a prepassed fragment's depth EQUAL
        const float SOWX_AO_DEPTH_EPS = 1e-5;
        // Synthesis floors for a material that declares nothing, read straight off the OLD-generation
        // inc_material.shd the installed client actually runs (#107 §11): fSpecularityMin 0.04 (the old gen's
        // name -- the split-include fork calls it fGeneratedSpecularityMin), fGeneratedRoughnessMin 0.125
        // unconditionally, fGeneratedRoughnessMax 0.6 at SHADER_QUALITY_MODE > 1 (0.65 below it, and the
        // Alloy lobe is GGX + Schlick geometric shadowing unconditionally, which IS that setting), and the
        // specular-map metallicness curve (fMetallicnessModifier 3.0 * fGeneratedMetallicnessOffset 0.2 = 0.6).
        const float SOWX_SPEC_MIN = 0.04, SOWX_ROUGH_MAX = 0.6, SOWX_ROUGH_MIN = 0.125;
        // metallicness curve, both of the old gen's branches: scale * specularity - scale * offset, where the
        // offset is 0.2 with a specular map and fGeneratedSpecularityMin without one.
        const float SOWX_METAL_SCALE_MAP = 3.0, SOWX_METAL_BIAS_MAP = 0.6,
                    SOWX_METAL_SCALE_BARE = 10.0, SOWX_METAL_BIAS_BARE = 0.4;
        // ApplyColorSpace(0.5) at the engine's 2.2 gamma -- the mid-grey inc_material.shd divides the
        // albedo's peak channel by when it synthesises a specular colour.
        const float SOWX_MID_GREY = 0.2176;
        // #121: bit 5 of the same slot mask -- the material declares an env map (Material::HasEnvMap, i.e.
        // mat+0x40). NO texture is bound for it: the probe replaces what it held. What survives is the
        // engine's own "this is shiny" flag on 2002 content, which drives the synthesis below.
        const int SOWX_SLOT_ENV = 64;
        // #123: bit 5, texture slot 6 -- the flow map, and the only slot outside the stock five. Enrolled
        // only on a flowmap part, so on everything else this bit is clear and uFlowTex is never sampled.
        const int SOWX_SLOT_FLOW = 32;
        // inc_material.shd:143-144 -- an env-mapped material with no specular map takes its specularity from
        // the diffuse alpha (fEnvMapLevel = 1 - alpha), and its roughness down toward the floor with it.
        // TWO modifiers, not one: fLegacyEnvMapSpecularityModifier 8.0 and fLegacyEnvMapRoughnessModifier
        // 3.0 (:60-62). Sharing 8.0 would saturate the roughness curve at envLevel 0.125 and render every
        // legacy shiny surface at the floor. 3.0 is the OLD generation's value, which is the one this client
        // runs (#107 s11); the split-include fork in sow-topdata says 2.5.
        const float SOWX_ENV_SPEC_MAX = 0.98, SOWX_ENV_SPEC_SCALE = 8.0, SOWX_ENV_ROUGH_SCALE = 3.0;
        // inc_standard.shd:250-252: the albedo is desaturated toward this grey in proportion to reflectivity,
        // so the reflection is not counted twice. DISPLAY space -- the engine does it before the :253 linearise.
        const vec3 SOWX_ENV_GREY = vec3(0.666667);
        """;
    /// <summary>#122: material synthesis -- what a part gets for every stock .mtr parameter it does NOT
    /// declare. Lifted out of the fragment main so it is a pure function of already-sampled values: that is
    /// what lets <c>check-shaders.py</c> fold it to constants off-device and check "declared beats
    /// synthesised" against the engine's own generators without a GPU.
    /// <para>The reference is <c>inc_material.shd</c>'s <c>SetupSpecularity</c>, from the OLD generation the
    /// installed client actually loads (<c>data/base_shaders.bif</c>) -- NOT the split-include fork, which
    /// differs here (roughness max 0.55, legacy env roughness modifier 2.5).</para>
    /// <para>Two deliberate deviations from stock, both because the Alloy pass has no permutation defines:
    /// the quality-gated constants are taken at the high setting (the Alloy lobe is GGX + Schlick geometric
    /// shadowing unconditionally, which IS <c>SHADER_QUALITY_MODE &gt; 1</c>), and a map's presence is read
    /// off the live slot mask where stock reads a compile-time <c>#if</c>.</para></summary>
    internal const string MaterialLib = """

        /// (Specularity, Roughness, Metallicness) -- declared wins, then the map, then the generated floor.
        vec3 SowxSynthPbr(vec3 declared, int slots, float envLevel, float specTexel, float roughTexel)
        {
            float envShiny = min(envLevel * SOWX_ENV_SPEC_SCALE, 1.0);
            float envRough = min(envLevel * SOWX_ENV_ROUGH_SCALE, 1.0);
            bool sm = (slots & SOWX_SLOT_SPEC) != 0;
            float spec = declared.x;
            if (spec <= 0.0)
            {
                if (sm) spec = specTexel;
                // #121: env map + no specular map = legacy "shiny" content, and this is the engine's own
                // translation of it (inc_material.shd, "mainly for the sake of enabling specularity on
                // legacy content").
                else if (envShiny > 0.0) spec = mix(SOWX_SPEC_MIN, SOWX_ENV_SPEC_MAX, envShiny);
                else spec = SOWX_SPEC_MIN;
            }
            float rough = declared.y;
            if (rough <= 0.0)
            {
                // A ROUGHNESS MAP IS DECLARED CONTENT: it is taken whole, and the env curve below never
                // touches it -- stock applies that curve only where the roughness was generated
                // (inc_material.shd puts the min() inside the specular-map branch, not after it).
                if ((slots & SOWX_SLOT_ROUGH) != 0) rough = roughTexel;
                else
                {
                    // a specular map with no roughness map is pre-PBR content: translate rather than trust it
                    rough = sm ? mix(SOWX_ROUGH_MIN, SOWX_ROUGH_MAX, (1.0 - spec) * (1.0 - spec))
                               : SOWX_ROUGH_MAX;
                    // ...and the env map drives it toward the same floor, on TOP of that translation. With
                    // no maps at all the min() is stock's bare assignment: the mix never exceeds the max.
                    if (envLevel > 0.0)
                        rough = min(rough, mix(SOWX_ROUGH_MAX, SOWX_ROUGH_MIN, envRough));
                }
            }
            float metal = declared.z;
            // the old gen keys this curve on whether a SPECULAR map is present -- 3.0/0.2 with one, 10.0/0.04
            // without (fMetallicnessModifier * fGeneratedMetallicnessOffset). Collapsing them to one branch
            // would generate visibly different metal for content that declares Specularity but ships no map.
            if (metal <= 0.0)
                metal = clamp((sm ? SOWX_METAL_SCALE_MAP : SOWX_METAL_SCALE_BARE) * spec
                              - (sm ? SOWX_METAL_BIAS_MAP : SOWX_METAL_BIAS_BARE), 0.0, 1.0);
            return vec3(spec, rough, metal);
        }

        /// The material's specular colour: CustomSpecularColor where declared (w = the CPU-side "not black"
        /// test, which is stock's own sentinel), else generated off the albedo.
        vec3 SowxSpecColor(vec4 declared, vec3 base, float metal, int slots)
        {
            if (declared.w > 0.0) return SowxLinear(declared.rgb);
            // stock takes plain white as the non-metal base once a height map is bound -- the peak-channel
            // rescue below is for flat content, and a height-mapped material already carries its crevices
            if ((slots & SOWX_SLOT_HEIGHT) != 0) return mix(vec3(1.0), base, metal);
            // non-metals should be white, but dark diffuse texels stand in for crevices, so the peak channel
            // scales it -- inc_material.shd's own reasoning, in its own words
            return mix(vec3(min(1.0, max(base.r, max(base.g, base.b)) / SOWX_MID_GREY)), base, metal);
        }
        """;

    internal const string OwnedLitFsMain = """
        void main()
        {
            vec3 N = normalize(vNrm);
            // /draw viz: attribute a dim frame to a TERM instead of guessing from the composite.
            // 1 = point-light diffuse alone (torch bright here but the frame dark => the torch is fine and
            //     something else lost intensity); 2 = cluster occupancy, green empty -> red full (a black
            //     froxel where the torch stands means the binning, not the light, is at fault).
            // 4 = coverage: every Alloy draw flat, so what STAYS textured is what the engine still draws.
            //     Cyan lit / magenta fullbright, so the two Alloy variants are told apart too.
            if (uViz == 4) { frag = vec4(0.0, 1.0, 1.0, 1.0); return; }
            // 5 = the LOCAL cube term alone: white lit, black occluded, flat white where no cube-bearing
            //     light reaches. A pillar that casts nothing here is not writing depth; one that is black
            //     here but bright in the composite is being washed out by a second light.
            if (uViz == 5) { frag = vec4(vec3(SowxLocalShadowViz(vWorld, N, vDepth)), 1.0); return; }
            if (uViz == 1) { frag = vec4(SowxClusteredDiffuse(vWorld, N, vDepth), 1.0); return; }
            if (uViz == 2)
            {
                float n = float(uClCount[SowxCluster(vDepth)]) / float(SOWX_CLUSTER_CAP);
                frag = vec4(n, 1.0 - n, 0.0, 1.0);
                return;
            }
            SowxDraw d = uDraw[vDraw];
            int feat = int(d.feat.x);
            // #123, shd_roofkn's fragment half: the toolset "vertex anchor" is a scrap of mesh authored high
            // above the walkmesh with its normals pointing down, so the toolset can transform the roof
            // without letting the builder collide with it. It must never be seen in game. roofknm dropped it.
            if (feat == SOWX_FEAT_ROOFKN && vAnchorZ > 100.0) discard;
            // #330: the keyhole verdict FIRST. It used to sit at the very end, so every fragment inside the
            // aperture paid the whole lit stage -- the cluster loop and the cube taps -- and then discarded;
            // under a keyhole through stacked roofs that was every layer at every pixel of the hole. The
            // probe capture never dissolves (a hole baked into the cube is permanent in every reflection).
            float kh = uCapture != 0 ? 1.0 : SowxKeyhole(d.rot.z, vWorld, vDepth);
            if (kh < 0.0) discard;
            int slots = int(d.pbr.w);
            // 22 = normal-map coverage: magenta where the material's map REACHED the shader, grey where it
            // did not. The one thing viz 7 cannot say, because a mesh normal and a mapped one paint the same
            // way there -- and a map that dropped out at the per-slot re-fetch looks exactly like content
            // that never declared one.
            if (uViz == 22) { frag = vec4((slots & SOWX_SLOT_NORMAL) != 0 ? vec3(1.0, 0.0, 1.0) : vec3(0.25), 1.0); return; }
            vec2 uv = vUV;
            // #123, shd_flowmap: the RG map is a per-texel flow DIRECTION, and the diffuse slides along it in
            // two half-offset phases blended by their own distance from the midpoint -- the blend is what
            // stops each phase's wrap showing as a flash. fMixingTime 5.0 and fFlowMapMod 1.0 are the
            // shader's own hardcoded constants; only fSlideSpeed comes from the .mtr.
            // These are DIFFUSE-only coordinates, deliberately kept off `uv`: the ticket scopes the feature
            // to "offsets diffuse UVs", and the maps below must go on sampling the unslid vUV. Sliding them
            // to phase 1 alone -- which is what reassigning `uv` here would do -- is worse than not sliding
            // them at all, because an unblended phase SNAPS at every wrap while the diffuse over it does not.
            // ponytail: vanilla slides the normal and height maps too, blending both phases the same way.
            // That is two more taps on flowmap fragments; do it here if static ripples ever read wrong.
            vec2 uvA = uv, uvB = uv;
            float flowMix = 0.0;
            if (feat == SOWX_FEAT_FLOWMAP && (slots & SOWX_SLOT_FLOW) != 0)
            {
                vec2 flow = texture(uFlowTex, uv, uLodBias).rg * 2.0 - 1.0;
                float t1 = (float(uTimeOfDay) / 1000.0) / 5.0;
                float c1 = fract(t1), c2 = fract(t1 + 2.5);
                uvA = uv - d.feat.y * flow * c1;
                uvB = uv - d.feat.y * flow * c2;
                flowMix = abs(c1 - 0.5) * 2.0;
            }
            vec3 V = normalize(uEyePos - vWorld);
            float occl = 1.0;
            // #146: outside the normal/height branch below -- a material may declare an AO map and nothing
            // else, and the engine's screen-space SSAO (an FB effect, downstream of this pass entirely) does
            // not see the contact detail a baked map carries.
            if ((slots & SOWX_SLOT_AO) != 0) occl *= texture(uAoTex, uv, uLodBias).r;
            // #379: ...and the screen-space term beside it, a lighting input like the map (ADR 0021)
            // ...and only where the visibility at this pixel was computed from THIS surface. The buffer comes
            // from the depth prepass, so a fragment the prepass never drew -- a blended door, anything the
            // sweep skipped, anything drawn over a hole -- would otherwise wear the occlusion of whatever
            // stands behind it. #119 makes the two passes agree on gl_Position character-for-character, so
            // for a prepassed fragment this equality is exact and the epsilon is only for the depth encode.
            float aoDz = abs(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r - gl_FragCoord.z);
            bool aoMine = uSsaoOn > 0 && aoDz < SOWX_AO_DEPTH_EPS;
            if (aoMine) occl *= texelFetch(uSsao, ivec2(gl_FragCoord.xy), 0).r;
            if ((slots & (SOWX_SLOT_NORMAL | SOWX_SLOT_HEIGHT)) != 0)
            {
                // Cotangent frame from screen-space derivatives (Mikkelsen), NOT the engine's vTangent /
                // fHandedness streams at attribute locations 4/5 (GLRender::ConnectShaderData's bind table).
                // Those would mean two more harvested attributes on every Alloy VAO plus a per-VDO "does this
                // mesh even carry them" branch, and the derivative frame gets mirrored UVs right for free. It
                // also picks the tile texture rotation up automatically, because vUV arrives already rotated
                // -- the stock stage has to undo it by hand (inc_standard.shd, transpose(mat2(m_texture))).
                // ponytail: four derivatives on normal-mapped fragments. Harvest locations 4/5 into the owned
                // VAO if the quality ever asks for it.
                vec3 dp1 = dFdx(vWorld), dp2 = dFdy(vWorld);
                vec2 du1 = dFdx(uv), du2 = dFdy(uv);
                vec3 p2 = cross(dp2, N), p1 = cross(N, dp1);
                vec3 T = p2 * du1.x + p1 * du2.x;
                vec3 B = p2 * du1.y + p1 * du2.y;
                // The Mikkelsen frame comes out as det * (dP/du, dP/dv), where det is the screen-space UV
                // determinant -- so on a mirrored UV island BOTH axes come back negated, and normalizing by
                // a magnitude alone keeps that 180-degree rotation. The engine never has it: its tangent is
                // dP/du unconditionally (MdlNodeTriMesh::CalculateTangentSpaceBasis, nm 0x457710, divides the
                // edge combination BY the determinant) and its handedness is sign(det), which SetupTSB spends
                // on the bitangent alone. Carry the sign here and the two frames agree on every face.
                float uvDet = du1.x * du2.y - du1.y * du2.x;
                float inv = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-12)) * (uvDet < 0.0 ? -1.0 : 1.0);
                if ((slots & SOWX_SLOT_HEIGHT) != 0)
                {
                    // inc_material.shd's SetupOcclusion: the height against a blurrier tap of itself IS the
                    // local self-occlusion (fOcclusionVicinityRange 2.0, base 1.0, modifier 0.0).
                    // ponytail: no parallax displacement of the UVs -- that is the same map read again, per
                    // fragment, in a loop. #122 owns what else the height map is worth.
                    float o = texture(uHeightTex, uv, 2.0 + uLodBias).r - texture(uHeightTex, uv, uLodBias).r;
                    occl *= 1.0 - clamp(o, 0.0, 0.99);
                }
                if ((slots & SOWX_SLOT_NORMAL) != 0)
                {
                    vec3 nm;
                    nm.xy = texture(uNrmTex, uv, uLodBias).rg * 2.0 - 1.0;
                    nm.z = sqrt(max(1.0 - dot(nm.xy, nm.xy), 0.0));
                    // 23 = the TANGENT-SPACE normal as sampled, before the frame touches it. Flat lavender
                    //      means the map carries no signal (or is not the map we think it is), which viz 7
                    //      cannot tell apart from a frame throwing the perturbation away.
                    if (uViz == 23) { frag = vec4(nm * 0.5 + 0.5, 1.0); return; }
                    // Green is NOT inverted here, and that was measured, not assumed: a lit frame rendered
                    // both ways against a vanilla frame of the same shot, correlated over the pixels the
                    // choice actually moves, matches vanilla UNFLIPPED (+0.50 against +0.34, and the gap
                    // widens the more the flip matters). Which is what the RE says too -- SetupTSB's
                    // bitangent works out to +dP/dv, the same axis the frame below builds.
                    N = normalize(mat3(T * inv, B * inv, N) * nm);
                }
            }
            // 7 = the SHADING NORMAL as colour, AFTER the normal map. Flat wedges with straight edges
            //     here are the mesh's own normals: no diffuse term, #143's wrap included, can hide a
            //     DISCONTINUITY in N, and nothing else tells that apart from a terminator crossing a facet.
            if (uViz == 7) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
            // 8 = the occlusion factor: white unoccluded. #146's map and the height term are the only two
            //     things that write it, so a frame that is white everywhere means no material in sight
            //     declared either -- which is the state shipped content is in today.
            if (uViz == 8) { frag = vec4(vec3(occl), 1.0); return; }
            if (uViz == 38)
            {   // lights per pixel, from the froxel this fragment reads: black 0, blue 8, green 16, yellow 24, red 32+
                float t = clamp(float(uClCount[SowxCluster(vDepth)]) / 32.0, 0.0, 1.0);
                vec3 h = t < 0.25 ? mix(vec3(0.0), vec3(0.0, 0.0, 1.0), t * 4.0)
                       : t < 0.5 ? mix(vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), (t - 0.25) * 4.0)
                       : t < 0.75 ? mix(vec3(0.0, 1.0, 0.0), vec3(1.0, 1.0, 0.0), (t - 0.5) * 4.0)
                       : mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 0.0), (t - 0.75) * 4.0);
                frag = vec4(h, 1.0); return;
            }
            // 35 = why this fragment has no screen-space AO. GREEN in the prepass depth and taking it;
            //      RED excluded as a cutout, BLUE by an out-of-prepass feature, YELLOW as a blended batch,
            //      MAGENTA as a keyhole batch the keyed depth sweep did not run for.
            //      Grey means the pass is not running at all.
            // 36 = the SCREEN-SPACE term alone, straight off SsaoPass, with neither the #146 map nor the
            //      height term in it. viz 8 cannot answer "does the AO buffer know this object is there",
            //      because a material that ships an AO map reads occluded in it either way.
            // 37 = whose occlusion is at this pixel. GREEN this surface's own (the prepass drew it here),
            //      RED another surface's -- the AO buffer holds geometry BEHIND this fragment, which is what
            //      "AO renders through doors" looks like from the inside. Grey where the pass is not running.
            if (uViz == 37)
            {
                frag = vec4(uSsaoOn <= 0 ? vec3(0.25) : aoMine ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), 1.0);
                return;
            }
            if (uViz == 36)
            {
                frag = vec4(vec3(aoMine ? texelFetch(uSsao, ivec2(gl_FragCoord.xy), 0).r : 1.0), 1.0);
                return;
            }
            if (uViz == 35)
            {
                vec3 c = uSsaoOn > 0 ? vec3(0.0, 1.0, 0.0)
                       : uSsaoOn == -1 ? vec3(1.0, 0.0, 0.0)
                       : uSsaoOn == -2 ? vec3(0.0, 0.0, 1.0)
                       : uSsaoOn == -3 ? vec3(1.0, 1.0, 0.0)
                       : uSsaoOn == -4 ? vec3(1.0, 0.0, 1.0) : vec3(0.25);
                frag = vec4(c, 1.0); return;
            }
            float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
            float sunNdl = SowxWrap(dot(N, uAreaDir), uWrapN);
            // the wrapped value, not the raw one -- a fragment the wrap has just lit must still be able to
            // be in shadow, or the band the wrap opens up glows through every caster
            float sunVis = SowxSunShadow(vWorld, N, sunNdl, sunRaw);
            // 3 = sun visibility alone: white lit, black shadowed. A frame that looks unshadowed reads here
            //     as "the cascades are empty" or "nothing selected a cascade", which the composite cannot say.
            if (uViz == 3) { frag = vec4(vec3(sunVis), 1.0); return; }
            // Material colours go in RAW: inc_lighting.shd uses materialFrontAmbient (517), materialFrontDiffuse
            // (529) and materialFrontEmissive (625) with no ApplyColorSpace. Only the TEXTURE is linearised
            // (inc_standard.shd:253). Linearising the material terms too double-darkened every non-white material.
            vec3 matAmbient = d.amb.rgb;
            vec3 matDiffuse = d.dif.rgb;
            vec4 texel = texture(uTex, uvA, uLodBias);
            if (flowMix > 0.0) texel = mix(texel, texture(uTex, uvB, uLodBias), flowMix);
            // #294: what the diffuse alpha MEANS here, asked once and spent once. On an env-mapped material
            // it is REFLECTIVITY, not coverage -- it becomes envLevel four lines down -- and the engine is
            // explicit about not spending it twice: inc_standard.shd:289 multiplies only `.rgb` through on
            // the ENVIRONMENT_MAP branch where :293 multiplies all four components on the other, and its
            // AlphaDiscard (:175) tests the MATERIAL alpha there, never the texel's. Alloy spent it as
            // reflectivity AND again as opacity, so the more reflective a surface was authored the more
            // invisible it drew -- the #294 crystal reads bright and solid in vanilla and as a ghost here.
            // CasterPass already knew this and lowered its own env threshold for the same reason; the
            // colour path's turn.
            float coverage = (slots & SOWX_SLOT_ENV) != 0 ? 1.0 : texel.a;
            // #126: the cutout, on the FINAL diffuse alpha and ahead of every other branch -- a discarded
            // fragment must cost neither the material synthesis nor a probe sample, and the probe capture
            // has to punch the same holes the screen does or the cube reflects solid foliage.
            if (uAlphaCut >= 0.0 && coverage <= uAlphaCut) discard;
            // #121: the diffuse ALPHA is the reflectivity mask on env-mapped content (inc_standard.shd:248,
            // fEnvMapLevel = 1 - alpha). A diffuse with no alpha channel reads 1.0, i.e. level 0 -- which is
            // why this costs nothing on the parts that are not env-mapped.
            float envLevel = (slots & SOWX_SLOT_ENV) != 0 ? 1.0 - texel.a : 0.0;
            texel.rgb = mix(texel.rgb, SOWX_ENV_GREY, envLevel);
            vec3 albedo = SowxLinear(texel.rgb);
            // #122: the maps are sampled here and the synthesis itself is a pure function -- it has to fold
            // to a constant off-device (check-shaders.py) for the declared-beats-synthesised rule to be
            // checkable at all. A slot with nothing bound is not sampled, exactly as before.
            float specTexel = (slots & SOWX_SLOT_SPEC) != 0 ? texture(uSpecTex, uv, uLodBias).r : 0.0;
            float roughTexel = (slots & SOWX_SLOT_ROUGH) != 0 ? texture(uRoughTex, uv, uLodBias).r : 0.0;
            vec3 pbr = SowxSynthPbr(d.pbr.xyz, slots, envLevel, specTexel, roughTexel);
            // #350: the material's own custom FS, hosted, AFTER synthesis -- the point its stock body runs,
            // with SetupStandardShaderInputs's outputs in the engine's own names. The material globals are
            // filled first because the weather ground READS them before it writes them
            // (`min(MATERIAL_ROUGHNESS_VALUE, WET_ROUGHNESS)` is a comparison); the specular colour is
            // synthesised from the PRE-body albedo because SetupSpecularity runs before the body in stock.
            SowxHostTexel = vec4(albedo, coverage);
            FragmentColor = vec4(1.0);      // stock main's own opener, for a body that skips it
            vFragmentNormal = N;
            vSurfaceNormal = normalize(vNrm);
            vViewToSurface_n = normalize(vWorld - uEyePos);
            fSpecularity = pbr.x; fRoughness = pbr.y; fMetallicness = pbr.z;
            SpecularColor = SowxSpecColor(d.spc, albedo * matDiffuse, pbr.z, slots);
            // 30 = the hosted body BYPASSED: the plain lit path for every hosted material, the A/B for
            //      "the look is wrong" vs "the handoff is wrong".
            if (uViz != 30 && SowxHostSurface(feat))
            {
                albedo = FragmentColor.rgb;
                N = normalize(vFragmentNormal);
                pbr = vec3(fSpecularity, fRoughness, fMetallicness);
            }
            // 31/32/33 = the surface this fragment shades with -- albedo, shading normal,
            // (spec, rough, metal) -- against 7 (the normal before any body). Emitted AFTER the branch so
            // they describe EVERY material: inside it they answered only on a hosted one and read as
            // identical to viz 0 everywhere else, which is a viz that cannot be trusted to say "no
            // difference".
            if (uViz == 31) { frag = vec4(albedo, 1.0); return; }
            if (uViz == 32) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
            if (uViz == 33) { frag = vec4(pbr, 1.0); return; }
            float spec = pbr.x, rough = pbr.y, metal = pbr.z;
            vec3 base = albedo * matDiffuse;
            vec3 specColor = SpecularColor;
            vec3 F0 = specColor * spec;

            vec3 sun = uAreaDiffuse * (sunNdl * sunVis);
            // #143's wrap is a DIFFUSE fix -- it stands in for light bleeding across a low-poly terminator,
            // which is a subsurface/interreflection effect. A specular highlight has no such excuse: fed the
            // wrapped dot, a glossy face angled away from the sun picks up a lobe it cannot geometrically
            // see. The lobe takes the raw dot, the way the ambient hemisphere already does.
            vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);
            vec3 sunSss = uAreaDiffuse * (sunVis * max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y);
            vec3 pointSpec;
            // #308 follow-up: 1 on a creature fragment (skinmesh, or the dangly marker in feat.w) -- the
            // local receiver's carrier-band gate, so a creature takes no dyn-pool self-shadow up close.
            float onMover = (int(d.feat.x) == SOWX_FEAT_SKIN || d.feat.w < 0.0) ? 1.0 : 0.0;
            vec3 point = SowxClusteredLight(vWorld, N, V, vDepth, onMover, rough, spec, pointSpec);
            // The engine's split: every light accumulates its own lobe, then ONE view-side normalisation
            // (fSpecularBaseModifier) and the material's specular colour scale the sum
            // (inc_lighting.shd:456 and :615). fSpecularity is already inside the lobe via the Fresnel term.
            // ADR 0021 gives occlusion to the ambient term alone, which is right and is invisible wherever
            // the area ambient is dark -- every interior, where the light is lamps and the corners are
            // exactly what wants occluding. /ao direct is the dial back toward vanilla's post-multiply:
            // 0 is ADR 0021 untouched, 1 occludes the direct terms outright. The probe term below picks it
            // up through `diffuse` at no extra cost.
            float dirOcc = mix(1.0, occl, uAoDirect);
            vec3 specLight = (sunSpec * SowxSpecLobe(N, V, uAreaDir, rough, spec) + pointSpec)
                             * (specColor * SowxSpecBase(max(dot(N, V), 0.0), rough)) * dirOcc;
            vec3 diffuse = (sun + sunSss + point) * dirOcc;
            // the 0.15 ambient floor and the sun-facing wrap are inc_lighting.shd:679 verbatim; the height
            // map's occlusion scales the AMBIENT, which is the term it is actually occluding
            vec3 ambient = uAreaAmbient * matAmbient * mix(0.15, 1.0, sunRaw) * occl;
            // 34 = the ONLY term occlusion can move inside `total` (the (1-spec) share of the ambient).
            //      Black at both /ao on 0 and /ao on 1 means AO has nothing to scale here, however bright
            //      viz 8 reads -- which no other view in the ladder can say.
            if (uViz == 34) { frag = vec4(ambient * (1.0 - spec), 1.0); return; }
            // #121: ambient specular -- the term vanilla feeds from the area env map on EVERY lit part
            // (inc_lighting.shd:456-512 gates it on SPECULAR_LIGHT, not on ENVIRONMENT_MAP) and the owned
            // pass dropped on all of them. Weighted the ENGINE's way: the env sample TINTS the light already
            // arriving at this fragment -- `EnvironmentColor = (AmbientLight + DiffuseLight) * envTexel`,
            // then `* fEnvironmentSpecularity` -- rather than adding radiance on top of it. Added radiance
            // (the split-sum form this first shipped as) makes a brick road reflect the sky like wet stone
            // and lights unlit corners out of the probe; this form cannot exceed the light that is there.
            // ponytail: no split-sum BRDF and no specular Fresnel (inc_lighting.shd:463, SPECULAR_FRESNEL).
            // /probe gain is the dial if the term wants more presence than the engine gave it.
            // ...and fed only where the material DECLARES an env map. RenderFlat @0x450eb0 also binds the
            // area's own env map (currentscene+0x1e8, verified) for plain parts, so being faithful to the
            // BINDING would put this term on everything in such an area -- and live that reflects the sky off
            // city cobblestone, which vanilla plainly does not do: its authored spheremap is a dim texel,
            // while the probe carries real scene radiance. /probe areawide 1 opts back into the engine's
            // rule (uSceneEnv); the default is the case the reflection exists for.
            if (uCapture == 0 && uProbeMips > 0.0 && (uSceneEnv != 0 || (slots & SOWX_SLOT_ENV) != 0))
            {
                vec3 env = textureLod(uProbe, reflect(-V, N), rough * (uProbeMips - 1.0)).rgb;
                specLight += (ambient + diffuse) * env * F0 * uProbeGain;
            }
            // 6 = the specular lobe alone: exactly what #120 adds. A part that reads flat in the composite is
            //     either black here (no lobe at all) or washed out by the diffuse, and nothing else tells the
            //     two apart.
            if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
            // energy split: what the specular lobe reflects, the diffuse does not -- inc_lighting.shd's own
            // `DiffuseLight * (1.0 - fSpecularity)` and `AmbientLight * (1.0 - fSpecularity)`
            vec3 total = (ambient + diffuse * matDiffuse) * (1.0 - spec) + d.emi.rgb;
            // 9 = the LIGHT arriving here, with no albedo on it: vanilla's TotalLight, the value
            //     inc_lighting.shd multiplies ColorIO by. Paired with 31 it splits "the light is short"
            //     from "the surface is dark", which no composite frame can do.
            if (uViz == 9) { frag = vec4(total, 1.0); return; }
            // Specular and self-illumination land AFTER the albedo multiply: a highlight is light off the
            // surface, not light through it (inc_lighting.shd:615 `ColorIO.rgb += SpecularColor *
            // SpecularLight`, and the texUnit5 add just above it).
            // inc_lighting.shd:672-677: "Specularity ignores material transparency" -- vanilla pre-divides the
            // specular by the alpha it is about to be blended by, so a ghosted creature keeps its highlights.
            float partA = coverage * d.rot.w;
            if (partA < 1.0 && partA > 0.001) specLight /= max(partA, 0.1);
            vec3 lit = albedo * total + specLight;
            if ((slots & SOWX_SLOT_ILLUM) != 0) lit += SowxLinear(texture(uIllumTex, uv, uLodBias).rgb);
            // #132: everything from here down stays LINEAR -- the screen and the probe want the same values
            // now, and PostChain's resolve does the frame's one encode. The probe never wanted anything else
            // (#121: glGenerateMipmap filters what is STORED, and filtering display-space values darkens
            // them), so what changed is the screen path catching up to it.
            // The dissolve and the fog are both linear multiplies/lerps here. #124 matched the fslit tail's
            // own order (ApplyKeyhole, RevertColorSpace, ApplyFog) and #132 knowingly drops the encode out of
            // the middle of it, which moves fog into linear -- a deliberate look change on foggy frames.
            // Both terms are camera-dependent, so the probe capture takes neither: a dissolve baked into the
            // cube is a permanent hole in every reflection the area ever shows.
            if (uCapture != 0) { frag = vec4(lit, 1.0); return; }
            // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
            // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
            // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
            // #294: times COVERAGE, not the raw texel alpha -- on an env-mapped material that channel is
            // reflectivity and the engine leaves the fragment alpha to the material alone.
            // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
            // #346/#347: the display-space overlays go on LAST, after the fog, exactly where
            // inc_standard.shd puts them -- noir, racial vision, then the targeting shape.
            frag = SowxOitOut(vec4(SowxDisplayOverlays(SowxFog(lit * kh, vWorld, vDepth)), coverage * d.rot.w));
        }
        """;
}
