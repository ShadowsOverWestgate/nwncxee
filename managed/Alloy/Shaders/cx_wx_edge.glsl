#ifndef CX_WX_EDGE
#define CX_WX_EDGE
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
/// Metres of surface the compare forgives outright, before the slope term. The cover view's depth range
/// is 3 x coverrange, so this is fractions of a depth unit -- it is written in metres because that is
/// what it means.
const float CX_WX_EDGE_BIAS_M = 0.12;
/// ...plus the cover view's own depth epsilon, matching the hard compare in cx_wx_lib.
const float CX_WX_EDGE_EPS = 0.0015;
/// Blobs per metre in the world-space breakup. A real eave's wet line is ragged at about the scale of
/// the tiles that shed onto it, not at the scale of the noise a shader usually reaches for.
const float CX_WX_EDGE_FREQ = 2.2;
/// Cells per metre the disk's rotation is hashed on. A rotation that is constant over the whole kernel
/// leaves the tap count's own plateaus in the ramp -- the nine visible bands under an awning that sent
/// the cover edge to a pre-filtered map in the first place. Hashing it per world CELL breaks them into a
/// dither that is fixed in the world, so it reads as grain on the ground and never crawls with the
/// camera; at roughly one cover texel a cell the cell itself is under the resolution being filtered.
const float CX_WX_EDGE_CELL = 24.0;
/// The golden angle: a Vogel disk stays evenly spread at any tap count, so dropping to four taps
/// thins the disk instead of clustering it.
const float CX_WX_EDGE_GOLD = 2.399963;

float CxWxEdgeHash(vec2 p) { return fract(sin(dot(p, vec2(21.98, 78.233))) * 43758.5453); }

float CxWxEdgeNoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(CxWxEdgeHash(i), CxWxEdgeHash(i + vec2(1.0, 0.0)), f.x),
               mix(CxWxEdgeHash(i + vec2(0.0, 1.0)), CxWxEdgeHash(i + vec2(1.0, 1.0)), f.x), f.y);
}

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
    // ...and the boundary is not a contour of the roof. Splash-back off the ground, a gust and a gutter's
    // drip make it ragged, and a straight line where a wall meets the floor is the one thing that reads as
    // a shader. The noise moves the THRESHOLD, so the ramp keeps its width and only its line wanders --
    // and it is windowed by 4 open (1 - open), which is 1 across the transition and exactly 0 at both
    // ends, so open ground stays fully wet and deep shelter fully dry however far the breakup is turned up.
    float n = CxWxEdgeNoise(worldPos.xy * CX_WX_EDGE_FREQ) - 0.5;
    return clamp(smoothstep(0.15, 0.85, open + n * uWxEdge.z * 4.0 * open * (1.0 - open)), 0.0, 1.0);
}
#endif
