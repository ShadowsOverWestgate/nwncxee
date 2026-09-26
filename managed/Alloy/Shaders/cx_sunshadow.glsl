#include "cx_pass.glsl"
uniform sampler2DArrayShadow uSunShadow;

// #306: the sun's DYNAMIC half -- movers only, re-baked every frame with the SAME per-cascade fit
// matrix as the static layer, composed by min(): the cube pool's own rule (#305), so the receiver
// has one composition rule, not two. uSunDyn[i] is 1 when layer i holds a fresh dynamic bake this
// frame and 0 otherwise.
uniform sampler2DArrayShadow uSunDynShadow;

// Cascade edge kept clear of the PCF kernel: a fragment whose taps would straddle the border falls
// through to the next cascade instead of sampling past the map and reading "unshadowed".
const float CX_SUN_BORDER = 0.985;
// #308 follow-up (the "scanline"): the cascade handoff is a 4x jump in texel size, offset and bias,
// so the shadow visibly re-draws along the selection boundary -- and under the latched fits that
// boundary leaps several units at every re-latch. The fix is a cross-fade to the next cascade whose
// weight is a pure function of the fragment's distance from the LIVE fit focus -- never anything
// latched: a weight keyed on the latched fit jumps at every re-latch, which reads as the whole band
// flickering while walking. Fade runs from CX_SUN_FADE_LO to CX_SUN_FADE_HI of the cascade's
// radius; full handover at 90% keeps the weight saturated before the latched fit (drift <= 5% of
// the radius, CasterPass.SunFitSlack) can run out under the fragment. A zero radius disables the
// blend -- the pre-fade hard handoff.
const float CX_SUN_FADE_LO = 0.70;
const float CX_SUN_FADE_HI = 0.90;
// Shadow-accuracy pass: offsets halved from #308 (see LocalShadowLib); csmbias default halved with them.
// Sample point pushed along the normal by this many texels. Normal offset does what a depth bias
// cannot: it scales with the texel a slope actually spans, so acne goes without the detached-shadow
// peter-panning a bias big enough for grazing light would cost.
const float CX_SUN_NORMAL_OFFSET = 1.0;
// #308 follow-up (acne): the constant offset above is sized for a face the light hits square-on. A
// face lit at grazing incidence spans tan(theta) texels of DEPTH per texel of map, so the terminator
// marches across it in texel-sized stair steps unless the offset grows with the slope. Extra texels
// of push per unit of tan(theta), and the clamp that keeps a fully edge-on face (tan -> inf) from
// being lifted into the next caster over.
const float CX_SUN_SLOPE_OFFSET = 0.75;
const float CX_SUN_SLOPE_MAX = 2.0;
// #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
// 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
// actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
// along it walks the sample point sideways across the shadow map's texel grid and turns a clean
// lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
// what it needs.
// Unset reads 0, which is the surface behaviour every existing program and every content override
// already relies on -- the safe default is the one nobody has to declare.
uniform float uSunMedia;

// The fragment in cascade i's NDC, sampled from a point pushed along the normal by that cascade's
// texel-scaled acne offset -- so each cascade tests the surface it can actually resolve.
vec3 CxSunNdc(int i, vec3 worldPos, vec3 N, float slope)
{
    vec4 clip = uSunVp[i] * vec4(worldPos + N * (uSunTexel[i]
                    * (CX_SUN_NORMAL_OFFSET * (1.0 - uSunMedia)
                       + CX_SUN_SLOPE_OFFSET * slope)), 1.0);
    return clip.xyz / clip.w;
}

// One cascade's answer: the four-tap half-texel cross on the static layer, min-composed with the
// same kernel on the dynamic layer when that layer is fresh (#306) -- matching kernels, because the
// dynamic half is the player's own shadow and a harder edge on exactly that shadow is the
// regression the split must not have. The depth bias grows on the slope: the stored depth inside
// one grazing texel varies by tan(theta) texels' worth of world depth, so the flat bias under-covers it.
float CxSunTaps(int i, vec3 ndc, float slope, vec2 texel)
{
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float layer = float(i);
    float ref = ndc.z * 0.5 + 0.5 - uSunBias[i] * (1.0 + slope);
    vec2 o = texel * 0.5;
    float vs = 0.25 * (texture(uSunShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                     + texture(uSunShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
    float vd = 1.0;
    if (uSunDyn[i] > 0.5)
        vd = 0.25 * (texture(uSunDynShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                   + texture(uSunDynShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
    return min(vs, vd);
}

// Sun visibility at a world point, 0 (fully shadowed) .. 1 (lit). Four hardware-PCF fetches on a
// half-texel cross: each fetch is itself a bilinear 2x2 comparison, so the kernel is a 4x4 filter for
// the price of four samples and the edge is soft without any per-pixel loop.
// `ndl` gates: the lit stage passes its WRAPPED value so a wrap-lit terminator fragment can still be
// shadowed. `ndlRaw` is the geometric dot for the slope terms -- the wrap inflates it exactly where
// grazing incidence needs the extra bias, so the two must stay separate arguments.
float CxSunShadow(vec3 worldPos, vec3 N, float ndl, float ndlRaw)
{
    if (uSunCascades <= 0 || ndl <= 0.0) return 1.0;
    vec2 texel = 1.0 / vec2(textureSize(uSunShadow, 0).xy);
    // tan of the light-to-normal angle, the media gate folded in: a particle's camera-facing normal
    // makes the slope meaningless, so a volume keeps the constant bias alone as before. Zero where
    // the raw dot is not positive: those fragments are lit only by the WRAP, have no sun-facing
    // surface to grow acne on, and a slope pinned at max there punches the bias through doors and
    // thin casters exactly inside the band the wrapped gate exists to keep shadowable.
    float slope = ndlRaw <= 0.0 ? 0.0
                  : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_SUN_SLOPE_MAX)
                    * (1.0 - uSunMedia);
    for (int i = 0; i < uSunCascades; i++)
    {
        vec3 ndc = CxSunNdc(i, worldPos, N, slope);
        if (any(greaterThan(abs(ndc.xy), vec2(CX_SUN_BORDER))) || abs(ndc.z) > 1.0) continue;
        float v = CxSunTaps(i, ndc, slope, texel);
        // The handoff gradient: fade toward the next cascade's answer over a world-space band tied
        // to the live focus, so the 4x resolution/bias jump never draws a line and never jumps.
        float R = uSunRadius[i];
        float f = R > 0.0
                  ? clamp((distance(worldPos, uSunFocus) - CX_SUN_FADE_LO * R)
                          / ((CX_SUN_FADE_HI - CX_SUN_FADE_LO) * R), 0.0, 1.0)
                  : 0.0;
        if (f > 0.0 && i + 1 < uSunCascades)
        {
            vec3 n2 = CxSunNdc(i + 1, worldPos, N, slope);
            if (all(lessThan(abs(n2.xy), vec2(CX_SUN_BORDER))) && abs(n2.z) <= 1.0)
                v = mix(v, CxSunTaps(i + 1, n2, slope, texel), f);
        }
        return v;
    }
    return 1.0;   // past the outermost cascade: no shadow information, and lit is the safe answer
}
