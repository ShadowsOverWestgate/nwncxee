uniform samplerCubeArrayShadow uLocalShadow;
uniform vec4 uLocalLight[64];   // xyz = the position the cube was baked from, w = its far plane
uniform vec2 uLocalPlane;       // x = the near plane every cube shares, y = 1 / face size in texels
uniform int uLocalCubes;        // 0 = no cubes to sample this frame (vanilla, no lights, not yet drawn)
// #139: x = PCF disc radius in cube texels, y = the world distance inside which a fragment is lit
// outright. Both default to 0 on a program that never sets them, which IS the pre-#139 behaviour --
// that is deliberate, so a stale content override degrades to a hard single tap rather than to
// something wrong.
uniform float uLocalSoftWorld;   // #674 bake only: the disc is at least this wide in world units (one lightmap texel), else its cube-texel edge aliases against the page
uniform vec4 uLocalSoft;   // x = disc texels, y = near skip, z = disc stand-down: world radius per unit view depth (0 = none), w = cube taps per fragment (0 = every light)

// #305: the DYNAMIC pool -- movers only, re-baked every frame at reduced resolution, composed with
// the static cube by min(): depth visibility is a min-of-blockers, so the two arrays compose exactly
// and NO COPY exists anywhere on the path (#187's composite cache lost to its own per-view copy;
// this is not that design). uLocalDynPage[slot] carries the pool PAGE PLUS ONE (0 = no movers near
// that light) -- a program that never sets it reads 0 everywhere, so a stale content override
// degrades to static-only rather than to sampling page zero.
uniform samplerCubeArrayShadow uLocalDynShadow;
// #642: the same two arrays through a sampler object with NO compare, so the blocker search reads a
// depth rather than a verdict. Bound by layout past the 0..15 table (TextureSlots.LocalShadowRaw);
// the driver's unit count is asked once and where it is short uLocalPcss.x stays 0, which stands the
// search down (a fixed disc at the ceiling) -- see CasterPass.RawSearch.
layout(binding = 16) uniform samplerCubeArray uLocalShadowRaw;
layout(binding = 17) uniform samplerCubeArray uLocalDynShadowRaw;
uniform float uLocalDynPage[64];
uniform vec4 uLocalDynSphere[64];   // the movers a page holds (xyz, r): a hot light owes nothing outside their shadow cone
uniform float uLocalFade[64];   // per-slot shadow strength: a slot fading in after its bake or out after its light left the budget

// Shadow-accuracy pass: every offset/bias below is HALF its #308 value (the stacked push reached 8
// texels = 0.5-1u of peter-panning on the outer cascades). Creature self-shadowing stays off
// (CX_LOCAL_DYN_NEAR), so the low-poly acne these were sized against no longer needs covering.
// Sample point pushed along the normal by this many cube texels -- the same acne fight the sun
// cascades run, scaled by the texel a face actually spans at this distance rather than a constant.
const float CX_LOCAL_NORMAL_OFFSET = 1.0;
// Depth bias in WORLD units along the face's major axis, applied before the projection so it stays a
// constant distance whatever the light's reach is.
const float CX_LOCAL_BIAS = 0.02;
// #308 follow-up (acne): the sun receiver's slope terms, same reasoning -- a face lit at grazing
// incidence needs offset and bias that grow with tan(theta), or the terminator stair-steps.
const float CX_LOCAL_SLOPE_OFFSET = 0.75;
const float CX_LOCAL_SLOPE_MAX = 2.0;
// #308 follow-up: fragments ON A CREATURE (skinmesh or dangly -- exactly the classes the caster
// walk puts in the dynamic set every frame) skip the dyn-pool tap when they stand within this many
// times the near-field skip (uLocalSoft.y) of the light. In that band the pool's blocker is the
// carrier's own body, whose pose-vs-bake mismatch is a flicker that cannot be made accurate at pool
// resolution -- so creature self-shadowing from a carried light is OFF, while the creature's shadow
// on the GROUND (a static fragment, never gated) and every static-map shadow stay untouched.
// Scales with /shadow near, so the cutoff is tunable live.
const float CX_LOCAL_DYN_NEAR = 3.0;
// #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
// 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
// actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
// along it walks the sample point sideways across the shadow map's texel grid and turns a clean
// lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
// what it needs.
// Unset reads 0, which is the surface behaviour every existing program and every content override
// already relies on -- the safe default is the one nobody has to declare.
uniform float uLocalMedia;
// #641: the shadow floor -- the fraction of a point light's DIFFUSE that still arrives inside its own
// shadow, standing in for the indirect light Alloy has none of. Pre-scaled by the area ambient on the
// CPU; the light's own colour rides in the diffuse term it multiplies, so a torch shadow stays warm.
// Unset reads 0, which is the full-black cutout this shipped with.
uniform float uLocalFloor;

// #642: x = the light's source radius in world units (the penumbra scale; 0 = no blocker search, the
// disc sits at its ceiling), y = the temporal rotation phase 0..1 (0 when the taa stage is not live,
// so the dither is a fixed pattern rather than a shimmer), z = disc taps. All zero on a program that
// never sets it, which is a fixed disc.
uniform vec4 uLocalPcss;
// Below this radius (in texels) the disc stands down to the hardware tap: a fragment in contact.
const float CX_LOCAL_DISC_MIN = 0.25;
const float CX_LOCAL_DISC_WORLD_MAX = 64.0;   // #674 bake: the world-minimum disc's ceiling in cube texels
const int CX_LOCAL_SEARCH = 4;
// #642: the per-fragment dither the disc spins by. Interleaved gradient noise on the pixel where a
// fragment head declares CX_FS; a vertex stage (grass, weather, particles compile this lib there
// too) has no gl_FragCoord, and a head that forgets the define gets the coarser hash, not a
// compile failure.
#ifdef CX_FS
float CxShadowNoise(vec3 worldPos) { return fract(52.9829189 * fract(0.06711056 * gl_FragCoord.x + 0.00583715 * gl_FragCoord.y)); }
#else
float CxShadowNoise(vec3 worldPos) { return fract(sin(dot(floor(worldPos * 64.0), vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
#endif
// A Vogel disc of up to CX_VOGEL_MAX taps before its 1/sqrt(n) scale: tap i is
// sqrt(i + 0.5) * (cos, sin)(i * golden angle). The per-light trig lives in CxSpin, once.
const int CX_VOGEL_MAX = 16;
const vec2 CX_VOGEL[CX_VOGEL_MAX] = vec2[](
    vec2(0.707107, 0.000000), vec2(-0.903089, 0.827303), vec2(0.138231, -1.575085), vec2(1.138286, 1.484690),
    vec2(-2.088893, -0.369494), vec2(1.978780, -1.258741), vec2(-0.661860, 2.462101), vec2(-1.262250, -2.430376),
    vec2(2.738570, 1.000116), vec2(-2.849022, 1.176042), vec2(1.373411, -2.934918), vec2(1.014929, 3.235725),
    vec2(-3.058988, -1.772735), vec2(3.588534, -0.788940), vec2(-2.190018, 3.115096), vec2(-0.505961, -3.904357));
// The disc of n taps at `radius`, spun by `rot` turns: tap i is CxSpin(...) * CX_VOGEL[i].
mat2 CxSpin(int n, float rot, float radius)
{
    float a = rot * 6.283185, s = radius * inversesqrt(float(n));
    return mat2(cos(a), sin(a), -sin(a), cos(a)) * s;
}
// The stored window depth back to a major-axis distance: the inverse of the `ref` line below.
float CxCubeDist(float z, float near, float far) { return near / max(1.0 - z * (far - near) / far, 1e-4); }
// #642: the penumbra radius in texels, 0 .. uLocalSoft.x -- PCSS: the mean depth of the blockers found
// by CX_LOCAL_SEARCH raw reads on a fixed ring at the ceiling radius (plus `mc`, the centre read the
// caller already has), then (receiver - blocker) / blocker scaled by the source radius. The ring is
// NOT dithered: neighbouring fragments must agree on the penumbra, or the edge speckles where one
// fragment blurs and the next takes the hard tap (seen live). A search that finds no blocker is a
// hard tap; uLocalPcss.x = 0 (no raw view on this driver) is the ceiling everywhere. `full` = every
// read found a blocker: the disc would only re-confirm the shadow the hardware tap already holds.
float CxPenumbra(samplerCubeArray raw, float layer, vec3 d, vec3 u1, vec3 v1,
                 float m, float mc, float near, float far, float texel, out bool full)
{
    full = false;
    float src = uLocalPcss.x;
    if (src <= 0.0) return uLocalSoft.x;
    float sum = 0.0, n = 0.0;
    if (mc < m) { sum += mc; n += 1.0; }
    mat2 ring = CxSpin(CX_LOCAL_SEARCH, 0.0, texel * uLocalSoft.x);
    for (int k = 0; k < CX_LOCAL_SEARCH; k++)
    {
        vec2 o = ring * CX_VOGEL[k];
        float mk = CxCubeDist(texture(raw, vec4(d + u1 * o.x + v1 * o.y, layer)).r, near, far);
        if (mk < m) { sum += mk; n += 1.0; }
    }
    if (n <= 0.0) return 0.0;
    full = n > float(CX_LOCAL_SEARCH);
    float mb = sum / n;
    return clamp((m - mb) / mb * src / texel, 0.0, uLocalSoft.x);
}

// Visibility of world point `worldPos` from the light in cube slot `slot`, 0 (shadowed) .. 1 (lit).
// The reference depth is the exact inverse of ShadowMath.LinearizeFaceDepth: for a 90deg face fitted
// to [near, far], the window depth stored at major-axis distance m is far/(far-near) * (1 - near/m).
// ShadowMath's SelfCheck welds that round-trip headless, so a convention slip fails there, not here.
// `ndlRaw` is the caller's geometric dot(N, L) -- already in hand at every lit call site -- for the
// slope terms; recomputing it here cost a normalize per fragment per shadowing light.
float CxLocalShadow(int slot, vec3 worldPos, vec3 N, float ndlRaw, float onMover, float viewDepth)
{
    if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    // outside the recorded shell there is no depth to compare against, and lit is the safe answer
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    // #139: inside the source sphere there is nothing to occlude. A carried torch sits INSIDE the
    // hand and forearm holding it, so a punctual test there shadows the carrier from its own light
    // -- and at that range one cube texel spans most of the arm, which is the blocky self-shadow.
    // CARRIER ONLY: on the ground this box (Chebyshev, world-aligned) drew as a lit diamond around
    // every fire placeable -- the sun's shadow apparently cut out in a square.
    if (onMover > 0.5 && m < uLocalSoft.y) return 1.0;
    float texel = 2.0 * m * uLocalPlane.y;
    // The acne offset/bias must clear the COARSEST map that will answer this fragment: a light with
    // an occupied dynamic page samples that page too, at (typically) half the static resolution.
    // Queried off the array itself so a stale content override merely lacks the fix. The PCF disc
    // below keeps the STATIC texel -- it only filters the static map, and widening it whenever a
    // mover claims the page would pop the penumbra on an unrelated event.
    float pg = uLocalDynPage[slot];
    float biasTexel = 2.0 * m * max(uLocalPlane.y,
                                    pg > 0.5 ? 1.0 / float(textureSize(uLocalDynShadow, 0).x) : 0.0);
    // tan of the light-to-normal angle, media-gated exactly as the sun receiver's slope is and for
    // the same billboard reason -- and zero on a light-backfacing fragment, where there is no acne
    // to fight and a pinned-at-max slope would only punch the bias through thin casters.
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1),
                        CX_LOCAL_SLOPE_MAX) * (1.0 - uLocalMedia);
    d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET * (1.0 - uLocalMedia)
                           + CX_LOCAL_SLOPE_OFFSET * slope));
    // the bias moves the compare point TOWARD the light, never away: the test is `ref <= stored` =
    // lit, and a fragment lying on its own recorded surface stores exactly its own depth. Pushing
    // the reference the other way makes every lit surface shadow itself.
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    float vs = texture(uLocalShadow, vec4(d, float(slot)), ref);
    // #308 follow-up: the dyn tap stands down for a CREATURE fragment inside the carrier band --
    // see CX_LOCAL_DYN_NEAR. Ground and static receivers pass onMover 0 and are never gated.
    bool dyn = pg > 0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y);
    float vd = dyn ? texture(uLocalDynShadow, vec4(d, pg - 1.0), ref) : 1.0;
    float v = min(vs, vd);
    // #326: one tap when the disc is under its pixel budget on screen (z = px / lod scale) -- four of
    // every five cube fetches
    if (uLocalSoft.x <= 0.0 || (uLocalSoft.z > 0.0 && texel * uLocalSoft.x < viewDepth * uLocalSoft.z)) return mix(1.0, v, uLocalFade[slot]);
    // #642: the centre's raw depth, the blocker search's first sample
    float mc = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalShadowRaw, vec4(d, float(slot))).r, near, far) : m;
    // #642: contact-hardening. The disc radius is not uLocalSoft.x but a per-fragment penumbra
    // estimate with uLocalSoft.x as its ceiling -- see CxPenumbra.
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d));
    vec3 v1 = normalize(cross(d, u1));
    float rot = fract(CxShadowNoise(worldPos) + uLocalPcss.y);
    // Full occlusion on either map is min()'s answer already: the hardware tap stands. Not in the bake,
    // whose search still reads the receiver's own plane as a blocker next to a floor-level light.
    bool full;
    float rs = max(CxPenumbra(uLocalShadowRaw, float(slot), d, u1, v1, m, mc, near, far, texel, full),
                   min(uLocalSoftWorld / texel, CX_LOCAL_DISC_WORLD_MAX));
    if (full && uLocalSoftWorld <= 0.0) return mix(1.0, v, uLocalFade[slot]);
    float rd = 0.0, dtex = 0.0;
    if (dyn)
    {
        dtex = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
        float md = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalDynShadowRaw, vec4(d, pg - 1.0)).r, near, far) : m;
        rd = CxPenumbra(uLocalDynShadowRaw, pg - 1.0, d, u1, v1, m, md, near, far, dtex, full);
        if (full && uLocalSoftWorld <= 0.0) return mix(1.0, v, uLocalFade[slot]);
    }
    // #642: a rotated Vogel disc at the estimated radius. The cube fetch is already hardware PCF, so
    // this WIDENS that filter rather than replacing it. The reference depth is deliberately the
    // centre tap's for every tap -- re-deriving it per tap would compare each neighbour against its
    // own surface and dissolve the penumbra it is there to create.
    // ponytail: one disc for both maps at its own radius each; no per-tap weighting, no bilateral term.
    int taps = min(int(uLocalPcss.z), CX_VOGEL_MAX);
    if (rs >= CX_LOCAL_DISC_MIN && taps > 0)
    {
        mat2 disc = CxSpin(taps, rot, texel * rs);
        for (int i = 0; i < taps; i++)
        {
            vec2 o = disc * CX_VOGEL[i];
            vec3 dt = d + u1 * o.x + v1 * o.y;
            float rt = ref;
            if (uLocalSoftWorld > 0.0)
            {   // bake: next to a floor-level light this disc spans many cube texels, and against the
                // centre's depth the receiver's own plane reads as a blocker (a dark disc round every
                // brazier) -- each tap compares against the plane's depth along its own ray instead
                float dn = dot(dt, N);
                float s = abs(dn) > 1e-4 ? clamp(dot(d, N) / dn, 0.5, 2.0) : 1.0;
                float mt = clamp(max(max(abs(dt.x), abs(dt.y)), abs(dt.z)) * s - CX_LOCAL_BIAS * (1.0 + slope), near, far);
                rt = far / (far - near) * (1.0 - near / mt);
            }
            vs += texture(uLocalShadow, vec4(dt, float(slot)), rt);
        }
        vs /= float(taps + 1);
    }
    if (dyn && rd >= CX_LOCAL_DISC_MIN && taps > 0)
    {
        mat2 disc = CxSpin(taps, rot, dtex * rd);
        for (int i = 0; i < taps; i++)
        {
            vec2 o = disc * CX_VOGEL[i];
            vd += texture(uLocalDynShadow, vec4(d + u1 * o.x + v1 * o.y, pg - 1.0), ref);
        }
        vd /= float(taps + 1);
    }
    return mix(1.0, min(vs, vd), uLocalFade[slot]);
}

// #674: the visibility a baked receiver still owes a hot static light: the mover's occlusion off the
// DYNAMIC page, scaled by the STATIC cube's visibility -- where the bake already holds this light's
// shadow (a fence's) the mover adds nothing, else the creature's shadow stacked on the fence's from
// the same light. Same offset and bias as the full receiver; 1.0 where the light holds no page.
float CxLocalDynShadow(int slot, vec3 worldPos, vec3 N, float ndlRaw)
{
    if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
    float pg = uLocalDynPage[slot];
    if (pg < 0.5) return 1.0;
    vec4 L = uLocalLight[slot];
    float far = L.w, near = uLocalPlane.x;
    vec3 d = worldPos - L.xyz;
    float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
    if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
    float biasTexel = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_LOCAL_SLOPE_MAX);
    d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET + CX_LOCAL_SLOPE_OFFSET * slope));
    m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
    float ref = far / (far - near) * (1.0 - near / m);
    // a baked receiver has no static cube to min() this against, so the one hardware tap the live
    // path gets away with shows its texel here: a 4-tap disc one dyn texel wide
    float t = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
    vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 u1 = normalize(cross(axis, d)) * t, v1 = normalize(cross(d, u1)) * t;
    float v = texture(uLocalDynShadow, vec4(d + u1 + v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 + v1, pg - 1.0), ref)
            + texture(uLocalDynShadow, vec4(d + u1 - v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 - v1, pg - 1.0), ref);
    // the static half through the same disc: one hard tap against the bake's soft edge cut the
    // creature's silhouette out of the fence's penumbra
    float vs = texture(uLocalShadow, vec4(d + u1 + v1, float(slot)), ref) + texture(uLocalShadow, vec4(d - u1 + v1, float(slot)), ref)
             + texture(uLocalShadow, vec4(d + u1 - v1, float(slot)), ref) + texture(uLocalShadow, vec4(d - u1 - v1, float(slot)), ref);
    return mix(1.0, 1.0 - vs * 0.25 * (1.0 - v * 0.25), uLocalFade[slot]);
}
