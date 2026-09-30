// R2, pass B: integrate the area wetness map. One invocation per map texel, no texture reads -- the
// soak/dry/fill/drain rates arrive as per-second scalars and the texel's own state is the only input.
// Every latched texel IS a top-down first hit, so every one of them is open to the sky by construction:
// there is no exposure term here. What stands UNDER a latched surface is sheltered, and the consumers
// decide that per fragment against the latched height (CxWetShelter).
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba32f, binding = 0) uniform image2D uMap;
uniform ivec2 uMapSize;
// x dt seconds, y soak per second (0 = not raining), z dry per second, w mode: <0 clear, >0.5 settle
// the map outright (an area entered with the weather already running), else integrate
uniform vec4 uStep;
// x the wetness a basin starts filling at, y fill per second at full wetness, z drain per second
uniform vec4 uFill;

const float CX_WX_UNVISITED = -1000.0;
/// The sentinel a cleared texel carries: far below CX_WX_UNVISITED, so a bilinear mix with a real
/// height still reads unvisited.
const float CX_WX_CLEAR_Z = -10000.0;

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, uMapSize))) return;
    if (uStep.w < 0.0) { imageStore(uMap, t, vec4(0.0, 0.0, CX_WX_CLEAR_Z, 0.0)); return; }
    vec4 m = imageLoad(uMap, t);
    if (m.z <= CX_WX_UNVISITED) return;
    float wet = clamp(m.x + (uStep.y > 0.0 ? uStep.y : -uStep.z) * uStep.x, 0.0, 1.0);
    float th = uFill.x;
    float rise = th < 1.0 && wet > th ? uFill.y * (wet - th) / (1.0 - th) : -uFill.z;
    float fill = clamp(m.y + rise * uStep.x, 0.0, 1.0);
    if (uStep.w > 0.5 && uStep.y > 0.0) { wet = 1.0; fill = th < 1.0 ? 1.0 : 0.0; }
    imageStore(uMap, t, vec4(wet, fill, m.z, m.w));
}
