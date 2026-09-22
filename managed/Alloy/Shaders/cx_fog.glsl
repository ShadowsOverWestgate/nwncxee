#include "cx_fogmix.glsl"
/// How fogged this fragment is, 0..1. The area author's ramp, times the height layer.
float CxFogFactor(vec3 world, float depth)
{
    if (uFogParams.x == 0.0) return 0.0;
    // radial from the player (uFogOrigin.w == 1), so zooming the camera out does not move the fog;
    // else the engine's own view depth. CullPass measures the same way, or the cut shows.
    float d = uFogOrigin.w > 0.0 ? distance(world, uFogOrigin.xyz) : depth;
    float f = (d - uFogParams.y) * uFogParams.w;
    if (f <= 0.0) return 0.0;
    // #341: fog as a LAYER, thinning above uFogSky.z. Off (uFogSky.y == 0) is the boot default and
    // the author's ramp alone -- this is the one term here that changes density rather than colour.
    if (uFogSky.y > 0.0) f *= exp(-max(world.z - uFogSky.z, 0.0) * uFogSky.y);
    return min(f, 1.0);
}
vec3 CxFog(vec3 lin, vec3 world, float depth) { return CxFogMix(lin, world, CxFogFactor(world, depth)); }
