// CX-51: screen-space reflections, for the lit stage and the grass. A fragment traces LAST frame's history
// (Ssr's copy, taken after grass, the sky and the blended class) through its hi-Z pyramid: the walk starts at
// half resolution and climbs while the ray stays in front of each cell's closest surface, so an open ray
// crosses the screen in a handful of fetches; only the final hit is confirmed at full resolution. What it
// hits replaces the probe in the caller's reflection, so a miss, a reject or a ray off the screen is the
// probe's look, faded in rather than switched. One frame of lag, no G-buffer: the ray is the fragment's own
// normal-mapped reflection vector.
#ifndef CX_SSR
#define CX_SSR
#include "cx_pass.glsl"
layout(binding = 18) uniform sampler2D uSsrColor;   // last frame's linear HDR colour, mipmapped
layout(binding = 19) uniform sampler2D uSsrHiZ;     // ...its 1 / view depth, full res at level 0, each level up the MAX (closest)
/// The pyramid level the walk may climb to (a 64-pixel cell): coarser cells overlap too much of the scene to skip.
const int CX_SSR_TOP = 6;
/// The longest reflection, in view units.
const float CX_SSR_DIST = 60.0;
/// How far behind the history's depth a hit may sit and still count: a fixed skin plus a share of the distance.
/// Thicker streaks behind every edge; thinner lets a ray slip through a grass blade.
const float CX_SSR_SKIN = 0.2, CX_SSR_THICK = 0.03;
/// The screen-edge fade, in uv: a hit this close to the border fades toward the probe instead of cutting.
const float CX_SSR_EDGE = 0.08;
/// Brightest radiance a hit (or a probe texel beside it) may carry. The history holds last frame's
/// reflections, so an unbounded hit compounds frame over frame into inf -- and one inf texel poisons its mips.
const float CX_SSR_MAX = 16.0;
/// How far (as a fraction of its view depth) the history's depth under the fragment may disagree with the
/// fragment before the history is judged not to have seen it.
const float CX_SSR_REPROJ = 0.05;
/// The caller's reflection weight below which no ray is traced (the probe alone), faded in over this range:
/// a 3% reflection of the screen is not worth a march.
const vec2 CX_SSR_WEIGHT = vec2(0.03, 0.05);

/// The dielectric reflectance at normal incidence a glTF material assumes (IOR 1.5) -- and the stock
/// fSpecularityMin, so a physical and a legacy dielectric start from the same F0.
const float CX_DIELECTRIC_F0 = 0.04;

/// Karis' analytic fit of the split-sum DFG integral: (scale, bias) on F0 for the directional albedo of a
/// GGX lobe. `perceptual` is sqrt(alpha).
vec2 CxEnvBrdf(float NoV, float perceptual)
{
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = perceptual * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

/// View position of the history at a full-res texel, off level 0.
vec3 CxSsrPos(ivec2 p, vec2 size)
{
    vec2 uv = (vec2(p) + 0.5) / size;
    return vec3((2.0 * uv.x - 1.0) / uSsrProj[0][0], (2.0 * uv.y - 1.0) / uSsrProj[1][1], -1.0)
         / texelFetch(uSsrHiZ, clamp(p, ivec2(0), ivec2(size) - 1), 0).r;
}

/// What the screen shows along world reflection R, for a surface of roughness `rough` whose reflection
/// weighs `weight`: rgb = the hit's radiance, a = how far to trust it (0 = a miss, a reject, off screen, too
/// rough, too faint to trace, or off). uSsr.x is the roughness past which nothing is traced (0 = off); the
/// hit fades out over its upper quarter. uSsr.y caps the cell visits per ray, uSsr.w is the finest pyramid
/// level a hit is confirmed at (0 = full resolution): the `light.ssrdetail` knob.
vec4 CxSsr(vec3 R, float rough, float weight)
{
    if (uSsr.x <= 0.0 || rough >= uSsr.x || weight < CX_SSR_WEIGHT.x) return vec4(0.0);
    ivec2 isize = textureSize(uSsrHiZ, 0);
    vec2 size = vec2(isize);
    vec3 o = (uSsrView * vec4(vWorld, 1.0)).xyz;
    vec3 r = normalize(mat3(uSsrView) * R);
    vec4 h0 = uSsrProj * vec4(o, 1.0);
    if (h0.w <= 0.0) return vec4(0.0);
    vec2 ouv = h0.xy / h0.w * 0.5 + 0.5;
    // the history must have SEEN this fragment: off its screen or hidden in it, the walk starts inside whatever hid it
    if (any(lessThan(ouv, vec2(0.0))) || any(greaterThan(ouv, vec2(1.0)))
        || abs(1.0 / texelFetch(uSsrHiZ, ivec2(ouv * size), 0).r + o.z) > CX_SSR_REPROJ * -o.z + 0.1) return vec4(0.0);
    // clip the ray at the near plane, then its screen image at the viewport
    float len = r.z > 0.0 ? min(CX_SSR_DIST, (-uSsr.z * 1.01 - o.z) / r.z) : CX_SSR_DIST;
    if (len <= 0.0) return vec4(0.0);
    vec4 h1 = uSsrProj * vec4(o + r * len, 1.0);
    float k0 = 1.0 / h0.w, k1 = 1.0 / h1.w;
    vec2 p0 = ouv * size, p1 = (h1.xy * k1 * 0.5 + 0.5) * size, d = p1 - p0;
    bvec2 moves = greaterThan(abs(d), vec2(1e-4));
    vec2 invD = 1.0 / mix(vec2(1.0), d, moves), dirS = step(0.0, d);
    vec2 tAx = mix(vec2(1.0), (mix(vec2(-1e-3), size + 1e-3, dirS) - p0) * invD, moves);
    float tEnd = clamp(min(tAx.x, tAx.y), 0.0, 1.0);
    float px = max(abs(d.x), abs(d.y));
    if (px * tEnd < 1.0) return vec4(0.0);
    // in t, a hundredth of a pixel: what nudges a step across the cell wall it stopped on
    float tEps = 0.01 / px;
    // (1/w is linear in screen space: the ray's inverse depth at t is mix(k0, k1, t))
    vec2 tx = mix(vec2(1e30), (floor(p0) + dirS - p0) * invD, moves);
    float t = min(tx.x, tx.y) + tEps;   // out of the fragment's own texel first
    int top = min(textureQueryLevels(uSsrHiZ) - 1, CX_SSR_TOP);
    int fine = min(int(uSsr.w), top), iter = int(uSsr.y);
    int lv = max(fine, 1);
    bool found = false;
    for (int i = 0; i < iter && t < tEnd; i++)
    {
        float cs = float(1 << lv);
        vec2 cell = floor((p0 + d * t) / cs);
        tx = mix(vec2(1e30), ((cell + dirS) * cs - p0) * invD, moves);
        float tExit = min(min(tx.x, tx.y), tEnd);
        float kS = texelFetch(uSsrHiZ, ivec2(cell), lv).r;
        float kA = mix(k0, k1, t), kB = mix(k0, k1, tExit);
        // in front of the closest surface across the whole cell: skip it, and try a coarser cell next
        if (min(kA, kB) > kS) { t = tExit + tEps; lv = min(lv + 1, top); continue; }
        // the ray reaches that surface inside the cell: move to where, if it is still ahead
        float tc = abs(k1 - k0) > 1e-9 ? (kS - k0) / (k1 - k0) : t;
        if (kA > kS) t = clamp(tc, t, tExit);
        if (lv > fine) { lv--; continue; }
        // the finest level: a hit if the ray sits within the skin behind this texel's surface
        float ray = 1.0 / mix(k0, k1, t);
        if (ray - 1.0 / kS < CX_SSR_SKIN + CX_SSR_THICK * ray) { found = true; break; }
        t = tExit + tEps;   // passed behind it: walk on at this level
    }
    if (!found) return vec4(0.0);
    vec2 ph = p0 + d * t, uv = ph / size, e = min(uv, 1.0 - uv);
    // a hit on a surface facing along the ray is its back, not what the mirror sees
    ivec2 hp = ivec2(ph);
    vec3 c = CxSsrPos(hp, size);
    vec3 hn = normalize(cross(CxSsrPos(hp + ivec2(1, 0), size) - c, CxSsrPos(hp + ivec2(0, 1), size) - c));
    float w = clamp(0.1 - 4.0 * dot(hn, r), 0.0, 1.0) * smoothstep(0.0, CX_SSR_EDGE, min(e.x, e.y))
            * (1.0 - smoothstep(0.8, 1.0, t)) * (1.0 - smoothstep(0.75 * uSsr.x, uSsr.x, rough))
            * smoothstep(CX_SSR_WEIGHT.x, CX_SSR_WEIGHT.y, weight);
    // the lobe's cone (alpha ~ its half-angle tangent) widens with the distance travelled: its footprint picks the mip
    vec3 hit = textureLod(uSsrColor, uv, max(log2(max(distance(ph, p0) * rough, 1.0)), float(fine))).rgb;
    if (any(isnan(hit)) || any(isinf(hit))) return vec4(0.0);
    return vec4(min(hit, vec3(CX_SSR_MAX)), clamp(w, 0.0, 1.0));
}
#endif
