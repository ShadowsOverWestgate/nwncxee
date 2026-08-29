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
    internal const string ShdWaterVs = "cx_water_vs", ShdWaterFs = "cx_water_fs",
                         ShdWaterTcs = "cx_water_tcs", ShdWaterTes = "cx_water_tes";

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
        struct SowxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; };
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
        uniform int uTimeOfDay;        // worldtimerTimeOfDay, the engine's own int
        uniform vec3 uAreaWind;        // areaGlobalWind
        uniform int uAreaFlags;        // areaFlags
        uniform vec3 uCamPos;          // cameraPosition
        uniform vec3 uPlayerPos;       // playerPosition

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
    internal const string KeyFogDecl = """
        uniform vec4 uFogParams;       // (enabled, start, end, 1/(end-start)) -- the engine's fogParams
        uniform vec4 uFogColor;        // #132: LINEAR -- the C# side applies the engine's own sRGB curve
                                       // to the display-space value before uploading it
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
    internal const string KeyFogFs = NoiseLib + """

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

        /// Scene fog: GetFog + the ApplyFog lerp. Two uniforms and a mix, which is all the old generation's
        /// inc_fog.shd is -- the shaped fog in sow-topdata's fork is a look decision that belongs in that
        /// fork's override, not in the embedded text.
        /// #132 moved this lerp across the encode: the engine fogs in DISPLAY space (fslit's tail is
        /// ApplyKeyhole, RevertColorSpace, ApplyFog) and the Alloy frame no longer has a display-space point
        /// to fog at. Both ends of the mix are linear here instead, which is the physically right space for
        /// it and a knowingly different look on a foggy frame -- #124 welded the engine's order and this
        /// ticket unwelds it.
        vec3 SowxFog(vec3 lin, float depth)
        {
            float f = uFogParams.x == 0.0 ? 0.0 : (depth - uFogParams.y) * uFogParams.w;
            return mix(lin, uFogColor.rgb, clamp(f, 0.0, 1.0));
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
        uniform int uViz;
        // #126: the engine's fAlphaDiscardValue -- 0.2 on the classes it draws under Enable(1), -1.0 (i.e.
        // no test at all) on the rest. A uniform here because the class is a per-batch fact.
        uniform float uAlphaCut;
        // #222: the constant colour PartTriMesh::Mat sets before the draw. White for every ordinary unlit
        // part; the SCENE'S FOG COLOUR for a part whose gob carries the +0x19 tint byte, which is what the
        // skyfade gob is. A uniform rather than a per-draw record field because the whole unlit class shares
        // one value and the sky changes it at most once per role.
        uniform vec3 uTint;
        out vec4 frag;
        void main()
        {
            if (uViz == 4) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
            vec4 t = texture(uTex, vUV);
            if (uAlphaCut >= 0.0 && t.a <= uAlphaCut) discard;
            // #132: the target is LINEAR, so the texel's display-space encode comes off here too -- the
            // probe and the frame want the same thing now. Same exponent AlloyLights.SowxLinear uses,
            // spelled out because this stage does not include that lib.
            vec3 tinted = t.rgb * uTint;
            vec3 rgb = pow(max(tinted, vec3(0.0)), vec3($SRGB$));
            frag = vec4(rgb, t.a * vAlpha);
        }
        """;

    /// <summary>#119's depth-only stage. Deliberately has NO resref: a stage that writes nothing but depth
    /// has nothing an author could restyle, and an override is one more copy of <see cref="ClipExpr"/> to
    /// keep bit-identical. The fragment stage is empty -- colour writes are masked off, and a program with no
    /// fragment stage at all is a portability gamble for nothing.</summary>
    internal const string DepthVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        void main()
        {
            SowxDraw d = uDraw[SowxSlot(uDrawBase + gl_DrawID)];
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
        }
        """;
    internal const string DepthFs = """
        #version 460 core
        void main() { }
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

        """ + DrawBufDecl + """
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
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            // ponytail: vWorld is the same product the colour stage forms, but it reaches it through `pos`
            // (SowxFeatPos's undisplaced return), so the two are not guaranteed bit-identical the way
            // gl_Position is. Worst case is one fragment at the punch-through threshold deciding differently
            // between the passes -- a sub-pixel speck on an edge the noise already makes ragged. Only a
            // displacing part could make it worse, and those never reach this stage at all. If it ever shows,
            // the fix is to give this stage SowxFeatPos too and mirror the expression character for character.
            vWorld = (d.model * vec4(aPos, 1.0)).xyz;
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

        """ + KeyFogFs + """
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
        uniform vec3 uEyePos;          // #120: this pass's camera, for the specular view vector
        uniform int uViz;              // /draw viz, see OwnedLitFsMain
        // #121: the per-area IBL probe, holding LINEAR radiance (EnvProbe captures with uCapture = 1, which
        // is what skips the display-space encode below). uProbeMips 0 = nothing captured yet, so no ambient
        // specular at all -- the pre-#121 behaviour, never a half-written cube.
        uniform samplerCube uProbe;
        uniform float uProbeMips;
        uniform float uProbeGain;      // /probe gain, the ambient-specular dial
        uniform int uSceneEnv;         // does the AREA declare an env map (scene+0x1e8)? see below
        uniform int uCapture;          // 1 = this draw IS the probe capture: stay linear, never sample uProbe
        // #126: the engine's fAlphaDiscardValue, per batch -- 0.2 on the cutout and blended classes,
        // -1.0 (no test) on opaque and additive.
        uniform float uAlphaCut;
        out vec4 frag;

        // Slot mask bits, matching AlloyRenderer's 1 << (slot - 1).
        const int SOWX_SLOT_NORMAL = 1, SOWX_SLOT_SPEC = 2, SOWX_SLOT_ROUGH = 4,
                  SOWX_SLOT_HEIGHT = 8, SOWX_SLOT_ILLUM = 16;
        // #146: bit 6, texture slot 7 -- the AO map. It multiplies the SAME occlusion factor the height
        // map's local term feeds, so a material carrying both gets one combined ambient scale.
        const int SOWX_SLOT_AO = 128;
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
                vec2 flow = texture(uFlowTex, uv).rg * 2.0 - 1.0;
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
            if ((slots & SOWX_SLOT_AO) != 0) occl *= texture(uAoTex, uv).r;
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
                    float o = texture(uHeightTex, uv, 2.0).r - texture(uHeightTex, uv).r;
                    occl *= 1.0 - clamp(o, 0.0, 0.99);
                }
                if ((slots & SOWX_SLOT_NORMAL) != 0)
                {
                    vec3 nm;
                    nm.xy = texture(uNrmTex, uv).rg * 2.0 - 1.0;
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
            float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
            float sunNdl = SowxWrap(dot(N, uAreaDir), uWrapN);
            // the wrapped value, not the raw one -- a fragment the wrap has just lit must still be able to
            // be in shadow, or the band the wrap opens up glows through every caster
            float sunVis = SowxSunShadow(vWorld, N, sunNdl);
            // 3 = sun visibility alone: white lit, black shadowed. A frame that looks unshadowed reads here
            //     as "the cascades are empty" or "nothing selected a cascade", which the composite cannot say.
            if (uViz == 3) { frag = vec4(vec3(sunVis), 1.0); return; }
            // Material colours go in RAW: inc_lighting.shd uses materialFrontAmbient (517), materialFrontDiffuse
            // (529) and materialFrontEmissive (625) with no ApplyColorSpace. Only the TEXTURE is linearised
            // (inc_standard.shd:253). Linearising the material terms too double-darkened every non-white material.
            vec3 matAmbient = d.amb.rgb;
            vec3 matDiffuse = d.dif.rgb;
            vec4 texel = texture(uTex, uvA);
            if (flowMix > 0.0) texel = mix(texel, texture(uTex, uvB), flowMix);
            // #126: the cutout, on the FINAL diffuse alpha and ahead of every other branch -- a discarded
            // fragment must cost neither the material synthesis nor a probe sample, and the probe capture
            // has to punch the same holes the screen does or the cube reflects solid foliage.
            if (uAlphaCut >= 0.0 && texel.a <= uAlphaCut) discard;
            // #121: the diffuse ALPHA is the reflectivity mask on env-mapped content (inc_standard.shd:248,
            // fEnvMapLevel = 1 - alpha). A diffuse with no alpha channel reads 1.0, i.e. level 0 -- which is
            // why this costs nothing on the parts that are not env-mapped.
            float envLevel = (slots & SOWX_SLOT_ENV) != 0 ? 1.0 - texel.a : 0.0;
            texel.rgb = mix(texel.rgb, SOWX_ENV_GREY, envLevel);
            vec3 albedo = SowxLinear(texel.rgb);
            // #122: the maps are sampled here and the synthesis itself is a pure function -- it has to fold
            // to a constant off-device (check-shaders.py) for the declared-beats-synthesised rule to be
            // checkable at all. A slot with nothing bound is not sampled, exactly as before.
            float specTexel = (slots & SOWX_SLOT_SPEC) != 0 ? texture(uSpecTex, uv).r : 0.0;
            float roughTexel = (slots & SOWX_SLOT_ROUGH) != 0 ? texture(uRoughTex, uv).r : 0.0;
            vec3 pbr = SowxSynthPbr(d.pbr.xyz, slots, envLevel, specTexel, roughTexel);
            float spec = pbr.x, rough = pbr.y, metal = pbr.z;
            vec3 base = albedo * matDiffuse;
            vec3 specColor = SowxSpecColor(d.spc, base, metal, slots);
            vec3 F0 = specColor * spec;

            vec3 sun = uAreaDiffuse * (sunNdl * sunVis);
            // #143's wrap is a DIFFUSE fix -- it stands in for light bleeding across a low-poly terminator,
            // which is a subsurface/interreflection effect. A specular highlight has no such excuse: fed the
            // wrapped dot, a glossy face angled away from the sun picks up a lobe it cannot geometrically
            // see. The lobe takes the raw dot, the way the ambient hemisphere already does.
            vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);
            vec3 pointSpec;
            vec3 point = SowxClusteredLight(vWorld, N, V, vDepth, rough, spec, pointSpec);
            // The engine's split: every light accumulates its own lobe, then ONE view-side normalisation
            // (fSpecularBaseModifier) and the material's specular colour scale the sum
            // (inc_lighting.shd:456 and :615). fSpecularity is already inside the lobe via the Fresnel term.
            vec3 specLight = (sunSpec * SowxSpecLobe(N, V, uAreaDir, rough, spec) + pointSpec)
                             * (specColor * SowxSpecBase(max(dot(N, V), 0.0), rough));
            vec3 diffuse = sun + point;
            // the 0.15 ambient floor and the sun-facing wrap are inc_lighting.shd:679 verbatim; the height
            // map's occlusion scales the AMBIENT, which is the term it is actually occluding
            vec3 ambient = uAreaAmbient * matAmbient * mix(0.15, 1.0, sunRaw) * occl;
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
            // Specular and self-illumination land AFTER the albedo multiply: a highlight is light off the
            // surface, not light through it (inc_lighting.shd:615 `ColorIO.rgb += SpecularColor *
            // SpecularLight`, and the texUnit5 add just above it).
            vec3 lit = albedo * total + specLight;
            if ((slots & SOWX_SLOT_ILLUM) != 0) lit += SowxLinear(texture(uIllumTex, uv).rgb);
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
            float kh = SowxKeyhole(d.rot.z, vWorld, vDepth);
            if (kh < 0.0) discard;
            // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
            // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
            // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
            // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
            frag = vec4(SowxFog(lit * kh, vDepth), texel.a * d.rot.w);
        }
        """;
}
