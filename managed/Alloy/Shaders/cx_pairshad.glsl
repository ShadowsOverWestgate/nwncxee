#ifndef CX_PAIR_LIB
#define CX_PAIR_LIB
// CX-188 step 4 (PairShadows): one depth tile per (static light, mover) PAIR -- a perspective from the light
// framed on the mover's sphere, fitted to the light's own reach -- in place of that light's whole dynamic cube
// page. A paired light publishes uLocalDynPage[slot] NEGATIVE: -(first pair + 1). Every existing reader tests
// `< 0.5` / `> 0.5` and therefore reads a paired light as having no page, which is the truth -- the page is
// gone. The two readers that want those movers back find them here:
//   * a BAKED receiver of the deferred frame, through the debt dispatch (cx_pairdebt), which is the whole point;
//   * an UNBAKED receiver (a creature standing in another creature's shadow under a lamp), through
//     CxLocalShadowQ, which min()s CxPairVis in where it used to tap the page.
// Nothing else changes: the forward light loop's hot gate already keys on the page, so a paired static light
// reads as COLD there, and that one pass-block lane is the only state that says so.
#include "cx_pass.glsl"
#include "cx_pairbuf.glsl"
layout(binding = 28) uniform sampler2DArrayShadow uPairMap;

// The acne terms, in the pair tile's own units. Deliberately NOT cx_localshadow's constants: this library is
// included from that one rather than the other way round, and PairShadows pins these by name.
const float CX_PAIR_NORMAL_OFFSET = 1.0;   // tile texels along the normal
const float CX_PAIR_SLOPE_OFFSET = 0.75;   // ...plus this x tan(theta)
const float CX_PAIR_SLOPE_MAX = 2.0;
const float CX_PAIR_BIAS = 0.02;           // world units toward the light, before the projection

/// The first pair of cube slot <paramref name="slot"/>, or -1 when that light has none this frame (it is cold,
/// dynamic, roaming, past the per-mover budget, or the atlas was full and it kept its page).
int CxPairBase(int slot)
{
    if (slot < 0 || slot >= 64) return -1;
    float pg = uLocalDynPage[slot];
    int at = pg < -0.5 ? int(-pg) - 1 : -1;
    return at < uPairN ? at : -1;
}

/// Visibility of <paramref name="worldPos"/> through pair <paramref name="pi"/>'s tile, 0 (the mover blocks
/// this light) .. 1, thinned by the pair's budget weight (fx.x). A point outside the tile is LIT: a pair's
/// frustum is the only place its mover can shadow.
/// <paramref name="soft"/> adds a four-tap disc one tile texel wide -- a baked receiver has no static map to
/// min() this against, so the lone hardware tap would show its own texel.
float CxPairTapAt(int pi, vec3 worldPos, vec3 N, float ndlRaw, bool soft)
{
    CxPair p = uPair[pi];
    // the light the tile was rendered from, which is the record's own: never uLocalLight, whose published
    // position the cube's texel slack lets differ from it
    vec3 d = worldPos - p.pr.xyz;
    float dist = length(d);
    if (dist < 1e-4) return 1.0;
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_PAIR_SLOPE_MAX);
    // the world size of one tile texel grows with the distance from the light, as a cube face's does
    float texel = p.col.w * dist;
    vec3 w = worldPos + N * (texel * (CX_PAIR_NORMAL_OFFSET + CX_PAIR_SLOPE_OFFSET * slope))
                      - (d / dist) * (CX_PAIR_BIAS * (1.0 + slope));
    vec4 c = p.vp * vec4(w, 1.0);
    if (c.w <= 0.0) return 1.0;
    vec3 n = c.xyz / c.w;
    if (any(greaterThanEqual(abs(n), vec3(1.0)))) return 1.0;
    vec2 uv = n.xy * 0.5 + 0.5;
    float ref = n.z * 0.5 + 0.5, layer = p.ix.x;
    if (!soft) return mix(1.0, texture(uPairMap, vec4(uv, layer, ref)), p.fx.x);
    float t = p.ix.y;
    return mix(1.0, 0.25 * (texture(uPairMap, vec4(uv + vec2(t, t), layer, ref))
                          + texture(uPairMap, vec4(uv + vec2(-t, t), layer, ref))
                          + texture(uPairMap, vec4(uv + vec2(t, -t), layer, ref))
                          + texture(uPairMap, vec4(uv + vec2(-t, -t), layer, ref))), p.fx.x);
}

/// Does the sphere (<paramref name="fc"/>, <paramref name="fr"/>) meet pair <paramref name="pi"/>'s shadow
/// VOLUME -- the cone from its light tangent to its mover sphere, truncated at the light's reach?
/// cx_lightbuild's <c>HotReach</c> for one sphere, plus the truncation the volume has and a froxel's test
/// never needed, and the pair's own liveness. Conservative: a sphere this rejects holds no point the tile tap
/// could darken, so the debt pass may cull a pair against a tile with it and the walk's answer is unchanged.
bool CxPairReach(int pi, vec3 fc, float fr)
{
    CxPair p = uPair[pi];
    // ...and is this pair LIVE at all? A light the cube scan found hidden or roaming after the plan committed
    // keeps its dynamic page, and a light whose static bake broke the scan never published a lane -- either way
    // its tiles are in the table unbaked and its movers are recorded somewhere else. The lane is the one
    // witness, so every consumer asks it here rather than each remembering to.
    int slot = int(p.ix.z);
    if (slot < 0 || slot >= 64 || uLocalDynPage[slot] > -0.5) return false;
    vec3 l = p.pr.xyz;
    vec3 ax = p.sph.xyz - l;
    float rr = p.sph.w, d = length(ax);
    vec3 v = fc - l;
    float vv = dot(v, v);
    float far = p.pr.w;
    if (vv > (far + fr) * (far + fr)) return false;   // past the light's own reach: no light there to owe
    // a sphere centre within sqrt(2) rr of the light shadows every direction (the tangent cone degenerates)
    if (d * d <= 2.0 * rr * rr) return true;
    ax /= d;
    float sn = rr / d, cs = sqrt(1.0 - sn * sn);
    float along = dot(v, ax);
    if (along < -fr) return false;   // wholly behind the light
    float perp = sqrt(max(vv - along * along, 0.0));
    return cs * perp - along * sn <= fr;
}

/// The MOVERS' visibility alone from the paired light in <paramref name="slot"/> -- CxMoverVisQ's answer off
/// the pair tiles instead of the dyn page. Walks that slot's own run of pairs and taps only the ones whose
/// shadow volume holds this point, so a fragment no mover shadows pays the cone test and nothing else.
float CxPairVis(int slot, vec3 worldPos, vec3 N, float ndlRaw, bool soft)
{
    int base = CxPairBase(slot);
    if (base < 0) return 1.0;
    float vis = 1.0;
    for (int i = base; i < uPairN; i++)
    {
        if (int(uPair[i].ix.z) != slot) break;   // the slot's run ends: the table is grouped by slot
        if (CxPairReach(i, worldPos, 0.0)) vis = min(vis, CxPairTapAt(i, worldPos, N, ndlRaw, soft));
    }
    return vis;
}
#endif
