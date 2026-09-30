#ifndef CX_WX_SOAK
#define CX_WX_SOAK
// The one place wetness darkens an albedo, shared by the lit splice, the grass cards and the procedural
// blades -- so no stage can grow its own unbounded copy again.
//
// Lagarde ("Water drop 3b -- Physically based wet surfaces") darkens a soaked porous surface by raising
// its albedo to a power, which deepens and SATURATES it where a linear scale only greys it. The trap is
// that a power is unbounded from below: a blade's linear albedo is around 0.05, and 0.05^2 is 0.0025 --
// black grass at "Wet surfaces" 1. The procedural blade had it worse still (albedo *= 1 - k * wet went
// NEGATIVE past k * wet = 1). Measured wet stone, soil and foliage lose roughly half their diffuse
// reflectance and never all of it, so the exponent is capped per fragment at the largest one whose
// darkening still leaves CX_WX_SOAK_FLOOR of the surface. Capping the EXPONENT rather than the result
// keeps the saturation: every channel still moves by its own power, just not as far.

/// The share of its own reflectance a fully soaked surface keeps. Wet asphalt, stone and soil measure
/// around half; 0.45 is the darkest any knob setting may reach.
const float CX_WX_SOAK_FLOOR = 0.45;
/// A leaf's cuticle is waxy, not porous: foliage wets glossy rather than dark, so a blade takes this
/// share of the exponent a stone fragment does.
const float CX_WX_LEAF_SOAK = 0.5;
const vec3 CX_WX_SOAK_LUMA = vec3(0.2126, 0.7152, 0.0722);

/// <c>albedo</c> soaked by <c>k</c> extra units of exponent, bounded: the return is never darker than
/// <c>CX_WX_SOAK_FLOOR</c> of the input, whatever <c>k</c> is asked for. k &lt;= 0 is the input itself,
/// bit for bit -- a dry fragment must be the texel it always was and not a rounding of it.
vec3 CxWetSoak(vec3 albedo, float k)
{
    vec3 a = max(albedo, vec3(0.0));
    if (k <= 0.0) return a;
    // the fragment's own luminance decides the cap: lum^kMax == CX_WX_SOAK_FLOOR, and a dark surface
    // therefore gets a smaller exponent than a pale one -- which is exactly the black the cap is for
    float lum = clamp(dot(a, CX_WX_SOAK_LUMA), 1e-3, 0.999);
    float kMax = log2(CX_WX_SOAK_FLOOR) / log2(lum);
    return pow(a, vec3(1.0 + min(k, kMax)));
}
#endif
