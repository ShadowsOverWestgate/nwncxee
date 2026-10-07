#ifndef CX_WX_EDGE
#define CX_WX_EDGE
#include "cx_wx_shape.glsl"
// The SHELTER test: how open to the falling rain a point is. Composed after the sun-shadow library, whose
// array carries the cover layer (CasterPass.CoverLayer), into every stage that wets a surface -- the lit
// splice, the grass cards and the procedural blades -- so all three draw ONE boundary.
//
// It is deliberately not the area wetness map's single fetch (CxWetShelter). That map is fitted to the
// area and capped at 512 texels a side, so a large area gets around 1.6 texels per metre and its shelter
// test drew the dry strip under an eave as half-metre stair steps. The cover map is the sun shadow map's
// own resolution over 2 x coverrange -- about six centimetres a texel, five to six times finer -- and an
// edge belongs at the resolution of the thing that casts it. The area map keeps what only it can answer:
// how much water has soaked in, how much has dried, and how deep a basin stands.
//
// It is not the filtered (VSM) read cx_wx_lib still makes for snow either. Chebyshev's bound sets the
// width of its ramp from the blur kernel's DEPTH variance, so one knob setting drew a two-centimetre
// edge under a five-metre roof and a ten-centimetre one under a two-metre roof; and the bleed cut that
// every variance map needs -- smoothstep(0.2, 1.0, ...) -- clips what is left back into a step. A
// world-space kernel of hardware compares has a width in METRES that is the same under every roof, and
// no bleed to cut.
uniform mat4 uCoverVp;        // world -> the cover view's clip space; zero = no map (fully exposed)
uniform vec4 uCoverLayer;     // x = the sun-array layer holding the map, y = the hollow step in depth units, z = the edge feather in uv, w = rain impact rate (0 = none)
// x = uv per metre in the cover view, y = the kernel's world radius (m), z = the breakup amount,
// w = how many taps of the disk to run. x <= 0 stands the test down to its caller's own fallback.
uniform vec4 uWxEdge;

/// The ceiling the tap loop is written to; <c>uWxEdge.w</c> picks how many of them actually run, so the
/// knob costs taps and not a recompile.
const int CX_WX_EDGE_TAPS = 12;
/// The shelter cache (Weather.Shelter): per cover texel, the shelter test already run for the surface the
/// rain lands on there (x), and the snow drift banked against higher ground beside it (y, its uv direction
/// in zw). Baked once per cover bake; uWxCache = 1 while it belongs to the bound cover view.
layout(binding = 23) uniform sampler2D uWxShelter;
uniform float uWxCache;
/// Up-facing enough to read the cache: the bake treats every top surface as level, so a slope keeps the
/// live disk and its rise-over-the-kernel lift.
const float CX_WX_TOP_UP = 0.9;

/// How open to the rain this point is: 1 out in the open, 0 well under a roof, a ragged world-space ramp
/// between. A Vogel disk of hardware depth compares over a radius in METRES, so the fraction of the
/// kernel that is open IS the soft strip wind-driven rain and splash-back leave under an eave -- the same
/// taps buy the edge and the falloff, and the ramp's width does not depend on how high the roof is.
/// The disk is rotated by a WORLD-space hash: stable under the camera, where a per-pixel rotation is
/// grain only TAA hides. <paramref name="fallback"/> is what a point outside the cover view's window
/// reads -- the coarse answer its caller has out there.
float CxWetOpen(vec3 worldPos, vec3 N, float fallback)
{
    if (uCoverVp[3][3] == 0.0 || uWxEdge.x <= 0.0) return 1.0;
    // the kernel spans a real distance ACROSS the surface, so a tap on a bank stands above the centre and
    // the point shelters itself unless it is lifted by the slope's own rise over the kernel
    float slope = sqrt(max(1.0 - N.z * N.z, 0.0));
    vec4 clip = uCoverVp * vec4(worldPos + N * (CX_WX_EDGE_BIAS_M + uWxEdge.y * slope), 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return fallback;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5 - CX_WX_EDGE_EPS;
    float r = uWxEdge.y * uWxEdge.x;
    int taps = clamp(int(uWxEdge.w), 1, CX_WX_EDGE_TAPS);
    float a0 = CxWxEdgeHash(floor(worldPos.xy * CX_WX_EDGE_CELL)) * 6.2831853;
    float open = 0.0;
    for (int i = 0; i < taps; i++)
    {
        float t = (float(i) + 0.5) / float(taps);
        float a = a0 + float(i) * CX_WX_EDGE_GOLD;
        open += texture(uSunShadow, vec4(uv + sqrt(t) * r * vec2(cos(a), sin(a)), uCoverLayer.x, ref));
    }
    open /= float(taps);
    return CxWetOpenShape(open, worldPos.xy, uWxEdge.z);
}

/// Is this point the surface the rain lands on -- nothing in the cover view above it, by the cover map's
/// own hardware compare? Then the cache's answer for that surface is its answer, at the uv returned. A
/// point under a roof, beside one (the compare's bilinear footprint straddles the edge) or outside the
/// window is not, and runs the live test.
bool CxWxTop(vec3 worldPos, out vec2 uv)
{
    uv = vec2(0.0);
    if (uWxCache < 0.5 || uCoverVp[3][3] == 0.0) return false;
    vec4 clip = uCoverVp * vec4(worldPos + vec3(0.0, 0.0, CX_WX_EDGE_BIAS_M), 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return false;
    uv = ndc.xy * 0.5 + 0.5;
    return texture(uSunShadow, vec4(uv, uCoverLayer.x, ndc.z * 0.5 + 0.5 - CX_WX_EDGE_EPS)) >= 0.999;
}

/// <c>CxWetOpen</c> through the cache: one compare and one fetch for a level surface the rain
/// lands on, the live disk for everything else.
float CxWetOpenFast(vec3 worldPos, vec3 N, float fallback)
{
    vec2 uv;
    if (N.z >= CX_WX_TOP_UP && CxWxTop(worldPos, uv)) return textureLod(uWxShelter, uv, 0.0).x;
    return CxWetOpen(worldPos, N, fallback);
}

/// The snow drift the cache baked under this point: x the height (1 at the wall), yz the cover-uv direction
/// toward the higher ground. Zero with no cache: no drift is the safe side.
vec3 CxWxDriftAt(vec3 worldPos)
{
    if (uWxCache < 0.5 || uCoverVp[3][3] == 0.0) return vec3(0.0);
    vec4 clip = uCoverVp * vec4(worldPos, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0)))) return vec3(0.0);
    return textureLod(uWxShelter, ndc.xy * 0.5 + 0.5, 0.0).yzw;
}
#endif
