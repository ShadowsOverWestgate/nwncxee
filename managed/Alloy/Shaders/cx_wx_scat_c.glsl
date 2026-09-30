// R2, pass A: latch the area wetness map's terrain from the cover map. One invocation per map texel in
// the rect the cover view covers, run once per cover BAKE -- so a map texel's height and relief are
// recorded as the player walks and never recomputed after that.
// The cover view is an orthographic depth view along the fall vector, so its depth at a texel IS the
// top-down first static hit: that surface's world Z is what the rain lands on, and everything below it
// is sheltered by it. The relief is how far that point stands above the lowest surface within the basin
// radius -- eight taps of the same view, which is a real height field and not a noise field.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba32f, binding = 0) uniform image2D uMap;
uniform sampler2D uCoverDepth;   // the cover layer, plain depth, compare mode off
uniform mat4 uCoverVp;           // world -> the cover view's clip space
uniform mat4 uCoverVpInv;        // ...and back
// x,y the map origin (world XY), z texels per metre, w the representative world Z the forward
// projection starts from (the fall vector is near-vertical, so one Newton step converges)
uniform vec4 uMapXf;
uniform ivec2 uMapSize;
uniform ivec2 uRectMin;          // the map texel this dispatch's (0,0) stands on
// x the basin radius in metres, y the relief ceiling in metres
uniform vec2 uBasin;

const int CX_WX_RING = 8;

vec3 CxCoverWorld(vec2 uv, float d)
{
    vec4 w = uCoverVpInv * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
    return w.xyz / w.w;
}

/// The cover uv this world point projects to; `ok` false = outside the view's fit.
vec2 CxCoverUv(vec3 p, out bool ok)
{
    vec4 c = uCoverVp * vec4(p, 1.0);
    vec3 n = c.xyz / c.w;
    ok = all(lessThanEqual(abs(n.xy), vec2(1.0))) && abs(n.z) <= 1.0;
    return n.xy * 0.5 + 0.5;
}

void main()
{
    ivec2 t = uRectMin + ivec2(gl_GlobalInvocationID.xy);
    if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, uMapSize))) return;
    vec2 xy = uMapXf.xy + (vec2(t) + 0.5) / uMapXf.z;
    bool ok;
    vec2 uv = CxCoverUv(vec3(xy, uMapXf.w), ok);
    if (!ok) return;
    float d = texture(uCoverDepth, uv).r;
    if (d >= 1.0) return;   // nothing stood in this ray: no surface to record
    // one Newton step: the first projection guessed the height, the reconstruction knows it, so
    // re-project at the real Z and the lateral error the fall's lean caused is gone
    vec3 p = CxCoverWorld(uv, d);
    uv = CxCoverUv(vec3(xy, p.z), ok);
    if (!ok) return;
    d = texture(uCoverDepth, uv).r;
    if (d >= 1.0) return;
    p = CxCoverWorld(uv, d);

    // the basin: the lowest surface within the radius, in the same view. The offset is in cover uv, and
    // the uv-per-metre comes off the view itself -- a top-down orthographic fit's world-xy block is a
    // rotation times one isotropic scale, so the first column's length IS twice that scale.
    vec2 du = vec2(0.5 * length(vec2(uCoverVp[0][0], uCoverVp[1][0])) * uBasin.x);
    float low = p.z;
    for (int i = 0; i < CX_WX_RING; i++)
    {
        float a = float(i) * 0.7853982;
        vec2 o = uv + vec2(cos(a), sin(a)) * du;
        float dn = texture(uCoverDepth, o).r;
        if (dn >= 1.0) continue;
        low = min(low, CxCoverWorld(o, dn).z);
    }
    vec4 prev = imageLoad(uMap, t);
    // the two integrated channels are carried through untouched: two invocations that land on one texel
    // write the same pair, and either one's height sample is a valid reading of the same surface
    imageStore(uMap, t, vec4(prev.x, prev.y, p.z, clamp(p.z - low, 0.0, uBasin.y)));
}
