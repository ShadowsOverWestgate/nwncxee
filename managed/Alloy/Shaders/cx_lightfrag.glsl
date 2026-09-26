#include "cx_pass.glsl"
layout(std430, binding = 0) readonly buffer CxLightBuf { vec4 uLights[]; };
// low 16 bits = lights binned, high 16 = how many of them lead the list before the cold statics
// (cx_lightbuild). Read through CxClusterN / CxClusterLive, never raw.
layout(std430, binding = 1) readonly buffer CxClusterCount { uint uClCount[]; };
layout(std430, binding = 2) readonly buffer CxClusterIdx { uint uClIdx[]; };
// CasterPass.CubeDynSphere: CX_DYN_SPHERES spheres (xyz, r; r < 0 unused) per cube slot round the movers
// its dyn page holds -- a hot light owes nothing outside their shadow cones
layout(std430, binding = 25) readonly buffer CxDynMovers { vec4 uDynMovers[]; };
const int CX_DYN_SPHERES = $DYNSPH$;
// CX-43: the cluster terms (uGrid, uTile, uZ) and the curve (uFalloff, uAtten, uHot, uReach, uWrap,
// uWrapN, uSss) ride the pass block; cx_pass.glsl says what each lane is.
// Mirrored lights (light.hidden): x = on, y = the roughness ceiling a surface counts as glossy under.
uniform vec2 uLightOcc;
bool CxGlossy(float rough) { return uLightOcc.x > 0.0 && rough < uLightOcc.y; }
// linear luminance under which a glossy lobe is not worth the fragment's one mirror tap
const float CX_MIRROR_FLOOR = 0.002;
const uint CX_CLUSTER_CAP = $CAP$u;
const int CX_CAP_MAX = 2;   // #336: the shadow cap's ceiling -- two NAMED candidates, no arrays
// The engine's own sRGB->linear conversion (Ghidra RenderInterface::GetColorSpaceFromSRGB: powf(c,
// 2.2) per channel, identity at 0 and 1). Textures and material colours both arrive non-linear --
// the stock shaders apply the same curve via ApplyColorSpace before lighting them.
// ponytail: unconditional. The engine gates it on GetGlobalShaderParami(0), the gamma pipeline the
// ship config runs and the shipped .shd assume (GAMMA_CORRECTION 1); plumb that param through if a
// legacy-pipeline build ever needs the Alloy path.
vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3($SRGB$)); }

// #143: energy-conserving wrapped diffuse. NWN's meshes are low-poly with smoothed vertex normals,
// so a punctual terminator lands mid-facet as a straight hard crease -- the artifact the community's
// GL 3.3 "light wrapping" shaders exist to hide. Widening the band by `w` and dividing the result by
// (1 + w) is the standard half-Lambert generalisation: the PEAK is unchanged, so a fully lit surface
// is bit-identical to the punctual one and only the band around the terminator moves.
// ponytail: one global wrap for every material. The modern refinement is pre-integrated skin
// (Penner 2011) -- a curvature-driven terminator LUT -- but it is per-material by nature and NWN
// carries no "this part is skin" tag; on armour and stone it reads wrong. Tag materials first.
float CxWrap(float ndl, float w)
{
    return w <= 0.0 ? clamp(ndl, 0.0, 1.0) : clamp((ndl + w) / (1.0 + w), 0.0, 1.0);
}

// #132: there is no encode here any more, and no highlight shoulder either. Every Alloy stage
// writes LINEAR into PostChain's own target and the resolve does the one encode for the whole frame
// -- one conversion per screen pixel instead of one per fragment, gated on the engine's own gamma
// parameter, and the only order in which blended compositing is correct. A stage that re-encoded
// here would be encoded twice. The pass stands down to vanilla outright on the frames PostChain
// cannot arm (see PostChain.Begin): a linear Alloy frame with no resolve is wrong by a gamma, and
// there is no display-space variant of these stages left to fall back to.

// Cluster of this fragment: screen tile from gl_FragCoord, depth slice from the interpolated view
// depth (gl_Position.w, which for a perspective projection IS the linear view depth -- no dependence
// on the projection's handedness convention). Must mirror the compute pass's slice mapping exactly.
// gl_FragCoord is WINDOW-relative, so the viewport origin comes out first -- the engine's reflection
// subframes do not all render at (0,0). A VERTEX stage composing this library (#336: particles light
// per vertex) has no gl_FragCoord and defines CX_CLUSTER_XY itself, in tile units, before the lib.
#ifndef CX_CLUSTER_XY
#define CX_CLUSTER_XY ((gl_FragCoord.xy - uTile.xy) / uTile.zw)
#endif
uint CxClusterN(uint cl) { return min(uClCount[cl] & 0xffffu, CX_CLUSTER_CAP); }
uint CxClusterLive(uint cl) { return min(uClCount[cl] >> 16u, CX_CLUSTER_CAP); }
uint CxCluster(float viewDepth)
{
    vec2 tile = CX_CLUSTER_XY;
    int gx = clamp(int(tile.x), 0, uGrid.x - 1);
    int gy = clamp(int(tile.y), 0, uGrid.y - 1);
    float k = log(max(viewDepth, uZ.x) / uZ.x) / log(uZ.y / uZ.x);
    int gz = clamp(int(k * float(uGrid.z)), 0, uGrid.z - 1);
    return uint(gx + uGrid.x * (gy + uGrid.y * gz));
}

// fAngleMin (inc_lighting.shd:88): the floor the engine clamps N.V to before the view-side
// normalisation divides by it.
const float CX_ANGLE_MIN = 0.01;

// Schlick's Fresnel, the pow-5 form the ship quality mode takes (inc_lighting.shd GetFresnel,
// SHADER_QUALITY_MODE > 1; the lower modes use pow 4).
float CxFresnel(float c)
{
    float f = 1.0 - c;
    float sq = f * f;
    return clamp(f * sq * sq, 0.0, 1.0);
}

// light.brdf (ADR 0026): x = 1 runs the modern material model, 0 the engine's own lobe exactly. A program
// that never uploads it (water, grass, particles) reads 0 and keeps the engine's lobe.
// y = directional ambient strength, z = specular anti-aliasing on.
uniform vec3 uMaterial;

// The Schlick-Smith k. The engine takes k = alpha, which over-shadows a rough lobe at grazing angles;
// the modern model takes Karis' k = alpha / 2 (the Schlick fit to Smith-GGX). Both are separable into
// a light half and a view half, which is what keeps the per-light / once-per-fragment split below.
float CxSmithK(float rough) { return uMaterial.x > 0.5 ? rough * 0.5 : rough; }

#ifdef CX_LIT_MATERIAL
// ADR 0026: one light's full specular radiance per unit irradiance, from the engine lobe `lobe` -- the
// view-side normalisation, the specular colour and the clear-coat and sheen layers folded in. Defined
// by cx_material.glsl from the fragment's own material; only the lit stage defines CX_LIT_MATERIAL.
vec3 CxLoopSpec(float lobe, vec3 N, vec3 V, vec3 L);
#define CX_LOOP_SPEC(lobe, N, V, L) CxLoopSpec(lobe, N, V, L)
#else
#define CX_LOOP_SPEC(lobe, N, V, L) vec3(lobe)
#endif

// #120, corrected by the live A/B on #121: the specular lobe every lit term goes through, as the
// ENGINE computes it (inc_lighting.shd:150-265 at the ship quality mode -- inc_config.shd's
// SPECULAR_DISTRIBUTION_MODEL 1 = GGX, SPECULAR_GEOMETRIC_SHADOWING 2 = Schlick, SPECULAR_FRESNEL 1
// = full). This replaced a textbook GGX, which differed in the one place that decides how a surface
// reads: it remapped alpha = rough^2, while the engine feeds fRoughness_sq = rough^2 as alpha^2 --
// alpha = rough. Every authored and synthesised roughness in the content is tuned for the engine's
// convention, so the remap shaded a 0.24-roughness stone as a 0.06 one: a far tighter, far brighter
// highlight on every part carrying a specular map, which is ~100% of SoW's.
// <para>Two halves, split the way the engine splits them: this per-LIGHT one, and the view-side
// normalisation in CxSpecBase, which is common to every light and applied once (the engine's
// fSpecularBaseModifier). fSpecularity rides in through the Fresnel term and must NOT be multiplied
// in again -- inc_lighting.shd:600-602 scales by it only when SPECULAR_FRESNEL != 1.</para>
// Returns the lobe per unit IRRADIANCE -- the caller has already folded N.L and attenuation into the
// light term it multiplies this by.
// ponytail: the low-quality branches (Blinn-Phong, no shadowing, the simplified Fresnel) are not
// reproduced. Add them if a device that cannot run the ship mode ever needs to match its own vanilla.
float CxSpecLobe(vec3 N, vec3 V, vec3 L, float rough, float spec)
{
    vec3 H = normalize(V + L);
    float NdotH = clamp(dot(N, H), 0.0, 1.0);
    float VdotH = clamp(dot(H, V), 0.0, 1.0);
    float NdotL = clamp(dot(N, L), 0.0, 1.0);
    float a2 = rough * rough;
    // GGX distribution minus its alpha^2 numerator, which lives in CxSpecBase
    float den = 1.0 - NdotH * NdotH * (1.0 - a2);
    float d = 1.0 / max(den * den, 1e-8);
    d *= 1.0 / mix(NdotL, 1.0, CxSmithK(rough));   // geometric shadowing, light side
    return d * mix(spec, 1.0, CxFresnel(VdotH));
}

// The half that does not vary per light: alpha^2/4 and the view-side shadowing term.
float CxSpecBase(float NdotV, float rough)
{
    return rough * rough * 0.25 / mix(max(NdotV, CX_ANGLE_MIN), 1.0, CxSmithK(rough));
}

// #674: one light's colour, attenuation and geometry -- the per-light body, shared by the cluster loop
// below and the bake stage (LightmapBake.BakeFs), so a baked texel and a live one are ONE code path.
// False = out of reach (nothing to add). `c` is the linearised colour, `att` the engine's curve (#140,
// #142) already floored at 0, `f2` the range-normalised distance squared (the cap's ranking term),
// `ndlRaw` the geometric dot and `ndl` the wrapped one (#139 sphere wrap, #143 floor).
bool CxPointLight(vec4 pr, vec3 c0, vec3 worldPos, vec3 N,
                  out vec3 c, out vec3 L, out float att, out float f2, out float ndlRaw, out float ndl)
{
    c = vec3(0.0); L = vec3(0.0); att = 0.0; f2 = 0.0; ndlRaw = 0.0; ndl = 0.0;
    // pr.w is GetLightAdjustedRadius: the engine's own R, overbright surplus already spent
    // on it. Squared here because the engine's own cull is the same test on the same value
    // (SetLightGL @0x4c1007 uploads exactly R*R as lightQuadraticAtten).
    float rr = pr.w * pr.w;
    vec3 d = pr.xyz - worldPos;
    float dd = dot(d, d);
    if (dd > rr) return false;
    // The walk already divided an overbright colour down by its max channel (spending the
    // surplus on radius, as GetLightAdjustedColor does), so every channel is <= 1 here and the
    // plain sRGB curve is the right linearization.
    c = CxLinear(c0);
    // #140: the ENGINE's curve, range-normalised -- inc_lighting.shd's ComputePointLightSource
    // at GAMMA_CORRECTION == 1, verbatim. The authored range IS the shape here, which is the
    // whole reason a dim cave glow and a torch can share one formula: the glow's R is small and
    // its colour dim, the torch's R is large (its overbright surplus went there), and neither
    // needs a global gain to rescue it. The rim fade reaches exactly 0 at f2 = 1, so no separate
    // cutoff window is needed and a light still cannot pop against its own cull sphere.
    f2 = dd / rr;
    float fade = 1.0 - f2;
    if (uReach > 0.0)
    {
        float r2 = uReach * uReach;
        if (f2 > r2) return false;
        fade *= min((r2 - f2) / (r2 * 0.3), 1.0);   // to zero over the last 30% of the reach (in f2)
    }
    // Uniform-valued branch, so it is coherent across the whole draw and free -- and at the
    // shipped 1.0 the pow is not merely cheap but exactly skipped, which is what makes "vanilla"
    // bit-exact rather than approximately right.
    if (uFalloff.z != 1.0) fade = pow(max(fade, 0.0), uFalloff.z);
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    att = uFalloff.x * fade / (at.x + at.y * f2);
    // #142: the near-field core -- a compact-support bump reaching uHot.y of the range and
    // exactly nothing past it, so the body above owns the whole tail and the gain that was
    // calibrated against vanilla cannot drift. Peak is uHot.x / at.x, so gain + hot = 1 puts
    // the very centre at the engine's own peak.
    if (uHot.x > 0.0)
    {
        float t = max(1.0 - f2 / (uHot.y * uHot.y), 0.0);
        att += uHot.x * fade * t * t / at.x;
    }
    att = max(att, 0.0);
    float invD = inversesqrt(max(dd, 1e-8));
    L = d * invD;
    ndlRaw = dot(N, L);
    // #139: the light is a SPHERE of radius uFalloff.y -- the same radius the attenuation
    // plateau above already models it with -- not a point. `s` is the sine of the half-angle it
    // subtends here, and wrapping N.L by it softens the terminator by exactly the amount the
    // source's own size would. The (1 + s) divide is what keeps this a redistribution rather
    // than free energy. Far away s -> 0 and the whole expression IS max(dot(N, L), 0.0) again,
    // so placed lights read exactly as they did; only a light held at arm's length differs --
    // which is the case where a punctual terminator cuts a low-poly facet in half.
    float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y * invD, 0.0, 1.0);
    // max, not sum: at arm's length the sphere term is already at full strength and adding the
    // #143 floor on top of it would wrap past the point of looking lit at all.
    ndl = CxWrap(ndlRaw, max(s, uWrapN));
    return true;
}
// The unshadowed diffuse of one light: the wrapped Lambert term plus the stock subsurface leak.
vec3 CxPointDiffuse(vec3 c, float att, float ndlRaw, float ndl)
{
    return c * (att * ndl) + c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y);
}

// #674: a static light's record carries its cube slot PLUS this when it is in the lightmap pages
// (AlloyLights.LightStatic). Decode: w >= 128 is static, slot = w - 256 (-1 survives as 255).
const float CX_LIGHT_STATIC = 256.0;
// #674: lightmap.movers -- 1 = a HOT static light (a mover under it this frame) takes its mover's
// shadow back out of the baked term; 0 = baked receivers skip static lights outright. Unset reads
// 0, the off position, which is what a stale content override degrades to.
uniform int uLmMovers;

// Diffuse AND specular contribution of every light binned into this fragment's cluster, in ONE walk:
// both terms want the same per-light colour, attenuation, N.L and cube-shadow fetch, and running the
// cluster twice would pay for all four again. World space throughout: the Alloy pass has world
// positions and normals in hand, so there is no eye-space round trip.
// #674: `baked` > 0.5 = this fragment reads its static light from the lightmap pages, so every static
// light is skipped here -- except a HOT one under lightmap.movers, whose mover shadow (the dyn page
// alone, never the static half: the bake already holds that) accumulates into `lmSub` (x occluded, y total) as the
// contribution to take back out of the baked term (#666: exact wherever the bake had the light
// unshadowed; over-darkens only down to the floor where static geometry already shadowed the texel).
vec3 CxClusteredLightLm(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                        float rough, float specularity, float baked, out vec3 spec, out vec3 lmSub)
{
    spec = vec3(0.0); lmSub = vec3(0.0);
    uint cl = CxCluster(viewDepth);
    bool glossy = CxGlossy(rough);
    // a baked receiver with no mirror to take skips every cold static outright -- they trail the list
    uint n = baked > 0.5 && !glossy ? CxClusterLive(cl) : CxClusterN(cl);
    uint base = cl * CX_CLUSTER_CAP;
    vec3 acc = vec3(0.0);
    // #336: /shadow cap -- the cube taps a fragment may pay, chosen PER FRAGMENT by reach
    // (f2 = dd/rr, the attenuation's own term): the capN lights reaching this fragment most
    // strongly get a cube fetch, the rest draw unshadowed. Per fragment, never per froxel -- a
    // per-froxel choice put the seam on the cluster grid, squares glued to the screen wherever four
    // cube lights overlapped. The candidates are kept sorted; an evicted one is added unshadowed.
    // EXPERIMENT: the candidates used to be five local arrays indexed by a runtime variable in a
    // while loop -- what a shader compiler spills to scratch memory. Two named slots, A the strongest.
    int capN = int(min(uLocalSoft.w, float(CX_CAP_MAX)));
    // shadow.cubedist: a far fragment pays no cube fetch at all (the old pipeline's distance cut), the
    // last quarter fades so the cut has no edge; uLocalPcss.w = 0 is every fragment
    float dfade = uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0;
    bool farSh = dfade <= 0.0;
    float aF2 = 1e30, bF2 = 1e30; int aSlot = -1, bSlot = -1; float aNdl = 0.0, bNdl = 0.0;
    // the MIRROR candidate (light.hidden): the strongest lobe on a glossy fragment with a cube to tap.
    // ONE extra tap per fragment, after the loop, and only when the cap did not shadow it already --
    // a tap per lobe over the floor was the frame (rain = every ground fragment x every light).
    float mLum = CX_MIRROR_FLOOR; int mSlot = -1; float mNdl = 0.0; vec3 mSpc = vec3(0.0);
    vec3 R = reflect(-V, N);
    vec3 aLit = vec3(0.0), aSpc = vec3(0.0), bLit = vec3(0.0), bSpc = vec3(0.0);
    // the same cap over HOT lights, ranked by the luminance they would take back: the capN strongest
    // get the soft dyn shadow, the rest one tap per map (uncapped, 15 hot lights x 8 taps was the frame)
    float hA = 0.0, hB = 0.0; int hASlot = -1, hBSlot = -1; float hANdl = 0.0, hBNdl = 0.0;
    for (uint k = 0u; k < n; k++)
    {
        uint li = uClIdx[base + k];
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        // the record's spare w carries this light's cube slot (-1 = it casts no shadow this frame),
        // and the #674 static bit above it
        int w = int(cs.w);
        bool isStatic = w >= 128;
        int slot = isStatic ? w - 256 : w;
        // a static light on a baked receiver is in the pages already: a cold one costs nothing here,
        // a HOT one (a mover under it) owes its mover's shadow and nothing else...
        bool inPages = isStatic && baked > 0.5;
        // ...except on a GLOSSY receiver (wet ground, metal), which owes each light's own mirror image:
        // the pages hold the diffuse, and the one lobe along their blended direction is a highlight of
        // a lamp that is nowhere. Spec only, occluded by the source's visibility, never a cube tap.
        bool mirror = inPages && glossy;
        // and only while a mover stands under it (a dyn page this frame): a static light with no
        // mover is in the pages and costs this fragment nothing -- with the check on the mover knob
        // alone, 74 baked-away lights of 92 still ran the point-light body per fragment (run 8)
        bool hot = inPages && !(farSh || uLmMovers == 0 || slot < 0 || uLocalDynPage[slot] < 0.5);
        if (inPages && !hot && !mirror) continue;
        // ...and only a light the reflection vector points near: the lobe of a light outside that cone
        // is under the floor, and the point body is what 74 baked-away lights cost per fragment (run 8).
        // The cone widens with roughness: 40 deg at a puddle's 0.12, the hemisphere at the 0.5 ceiling.
        if (mirror && !hot && dot(R, normalize(pr.xyz - worldPos)) < 1.0 - 2.0 * rough) continue;
        if (hot)
        {   // the light-to-fragment ray misses every mover sphere: nothing on the page can shadow here,
            // and the eight taps below are the opaque pass on a plaza (run 20: 15 hot lights, 7-8 ms)
            vec3 lp = uLocalLight[slot].xyz;
            vec3 dp = worldPos - lp; float dl = max(length(dp), 1e-4);
            bool near = false;
            for (int q = 0; q < CX_DYN_SPHERES && !near; q++)
            {
                vec4 ms = uDynMovers[slot * CX_DYN_SPHERES + q];
                if (ms.w < 0.0) continue;
                vec3 dc = ms.xyz - lp; float t = dot(dc, dp) / dl;
                float rr = ms.w + 0.3;
                near = t > -rr && t < dl + rr && dot(dc, dc) - t * t <= rr * rr;
            }
            if (!near) { if (!mirror) continue; hot = false; }
        }
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
        // slot -2 = ambient class: vanilla adds c*att to the ambient term, no N.L, no shadow
        if (slot == -2) { if (!inPages) acc += c * att; continue; }
        if (ndl <= 0.0) continue;   // facing away: no light, and no reason to pay for a cube fetch
        if (hot)
        {
            // lmSub.x = the luminance a mover occludes, over the static lights with a mover under
            // them; the caller takes it off the baked term's own luminance, so a mover's shadow from
            // one light darkens only that light's share. ponytail: where the static bake already
            // shadowed that light at this texel the subtraction overshoots (clamped at the caller);
            // the exact total needs every static light evaluated live, which was the opaque pass.
            float lum = dot(CxPointDiffuse(c, att, ndlRaw, ndl), vec3(0.2126, 0.7152, 0.0722));
            int eSlot = slot; float eNdl = ndlRaw, eLum = lum;   // the light this one displaces: itself by default; hard tap, as above
            if (capN > 0 && lum > hA)
            {
                if (capN >= 2) { eSlot = hBSlot; eNdl = hBNdl; eLum = hB; hB = hA; hBSlot = hASlot; hBNdl = hANdl; }
                else { eSlot = hASlot; eNdl = hANdl; eLum = hA; }
                hA = lum; hASlot = slot; hANdl = ndlRaw;
            }
            else if (capN >= 2 && lum > hB)
            {
                eSlot = hBSlot; eNdl = hBNdl; eLum = hB;
                hB = lum; hBSlot = slot; hBNdl = ndlRaw;
            }
            if (eSlot >= 0 && eLum > 0.0) lmSub.x += eLum * dfade * (1.0 - CxLocalDynShadowQ(eSlot, worldPos, N, eNdl, false));
            if (!mirror) continue;
        }
        vec3 dif = c * (att * ndl);
        float lobe = CxSpecLobe(N, V, L, rough, specularity);
        vec3 spc = dif * CX_LOOP_SPEC(lobe, N, V, L);
        // a glossy surface's lobe is the mirror image of the source, and the source must be in view of
        // the FRAGMENT: a lamp on a terrace mirrors in the street from where the ground first sees over
        // the parapet, not from the wall's foot. That is the light's own shadow, so the strongest lobe
        // takes its cube tap whether or not the cap below would have given it one. A rough lobe is
        // untouched: too broad to be an image of anything. (light.hidden)
        if (glossy && slot >= 0 && !farSh)
        {
            float l = dot(dif, vec3(0.2126, 0.7152, 0.0722)) * lobe;   // engine units: the floor is tuned in them
            if (l > mLum) { mLum = l; mSlot = slot; mNdl = ndlRaw; mSpc = spc; }
        }
        if (mirror) { spec += spc; continue; }
        // ponytail: a light behind the surface (ndl <= 0, continued above) skips its subsurface leak
        // rather than being fetched unshadowed; stock has no shadow to lose there, Alloy does
        vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);
        if (slot < 0 || farSh) { acc += unsh; spec += spc; continue; }
        // cap 0 = every light, at the hardware tap: a soft shadow INSIDE the loop is inlined per light
        // and its registers are the loop's (lightbench: -23% on the lit stage with only hard taps here)
        if (capN <= 0)
        {
            float sh = mix(1.0, CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, false), dfade);
            acc += unsh * mix(uLocalFloor, 1.0, sh); spec += spc * sh;   // floor on diffuse only: a floored highlight is a wrong highlight
            if (mSlot == slot) mSlot = -1;   // shadowed here: no mirror tap owed
            continue;
        }
        if (f2 < aF2)
        {   // new strongest: A moves to B (capN 2) or is evicted unshadowed (capN 1); old B is evicted
            if (capN >= 2) { if (bSlot >= 0) { acc += bLit; spec += bSpc; }
                             bF2 = aF2; bSlot = aSlot; bNdl = aNdl; bLit = aLit; bSpc = aSpc; }
            else if (aSlot >= 0) { acc += aLit; spec += aSpc; }
            aF2 = f2; aSlot = slot; aNdl = ndlRaw; aLit = unsh; aSpc = spc;
        }
        else if (capN >= 2 && f2 < bF2)
        {
            if (bSlot >= 0) { acc += bLit; spec += bSpc; }
            bF2 = f2; bSlot = slot; bNdl = ndlRaw; bLit = unsh; bSpc = spc;
        }
        else { acc += unsh; spec += spc; }
    }
    if (aSlot >= 0)
    {
        float sh = mix(1.0, CxLocalShadow(aSlot, worldPos, N, aNdl, onMover, viewDepth), dfade);
        acc += aLit * mix(uLocalFloor, 1.0, sh); spec += aSpc * sh;
    }
    if (bSlot >= 0)
    {
        float sh = mix(1.0, CxLocalShadow(bSlot, worldPos, N, bNdl, onMover, viewDepth), dfade);
        acc += bLit * mix(uLocalFloor, 1.0, sh); spec += bSpc * sh;
    }
    if (hASlot >= 0) lmSub.x += hA * dfade * (1.0 - CxLocalDynShadow(hASlot, worldPos, N, hANdl));
    if (hBSlot >= 0) lmSub.x += hB * dfade * (1.0 - CxLocalDynShadow(hBSlot, worldPos, N, hBNdl));
    // the mirror tap: the strongest lobe's shadow, taken back out of the unshadowed sum it went into. The
    // hardware tap alone -- a highlight has no penumbra worth a blocker search
    if (mSlot >= 0 && mSlot != aSlot && mSlot != bSlot)
        spec -= mSpc * (1.0 - mix(1.0, CxLocalShadowQ(mSlot, worldPos, N, mNdl, onMover, viewDepth, false), dfade));
    // the daylight ratio; lmSub stays in the undimmed units the caller's baked term is compared in
    spec *= uDayDim;
    return acc * uDayDim;
}
// The pre-#674 signature: an unbaked receiver, every light live.
vec3 CxClusteredLight(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                        float rough, float specularity, out vec3 spec)
{
    vec3 sub;
    return CxClusteredLightLm(worldPos, N, V, viewDepth, onMover, rough, specularity, 0.0, spec, sub);
}

// The diffuse half alone, for <c>/alloy viz 1</c> and anything that has no material in hand.
vec3 CxClusteredDiffuse(vec3 worldPos, vec3 N, float viewDepth)
{
    vec3 spec;
    return CxClusteredLight(worldPos, N, N, viewDepth, 0.0, 1.0, 0.0, spec);
}

/// Darkest local-cube term over this fragment's cluster -- white lit, black fully occluded, and
/// exactly 1.0 where no light in reach carries a cube. <c>/alloy viz 5</c>. The composite cannot answer
/// "is the cube occluding anything?" because a lit neighbour washes the term out; this can.
float CxLocalShadowViz(vec3 worldPos, vec3 N, float viewDepth)
{
    uint cl = CxCluster(viewDepth);
    uint n = CxClusterN(cl);
    uint base = cl * CX_CLUSTER_CAP;
    float vis = 1.0;
    for (uint k = 0u; k < n; k++)
    {
        uint li = uClIdx[base + k];
        vec4 pr = uLights[li * 2u];
        int slot = int(uLights[li * 2u + 1u].w);
        if (slot >= 128) slot -= 256;   // #674: the static bit
        if (slot < 0) continue;
        vec3 d = pr.xyz - worldPos;
        if (dot(d, d) > pr.w * pr.w) continue;
        vis = min(vis, CxLocalShadow(slot, worldPos, N, dot(N, normalize(d)), 0.0, viewDepth));
    }
    return vis;
}
