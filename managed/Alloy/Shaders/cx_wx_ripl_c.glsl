// The ripple tile (cx_wx_impact's uWxRipple), once per frame while it rains: both half-cell-offset lattices'
// rings over one wrapped tile of CX_RIPPLE_WRAP cells, stored as the surface tilt (minus the height
// gradient). The footprint fade stays per pixel, in CxWeatherRipple.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
uniform int uTimeOfDay;
uniform int uSize;
uniform float uRate;
float CxClock() { return float(uTimeOfDay % 3600000); }
const float CX_RIPPLE_WRAP = 24.0;
#define CX_RIPPLE_KEY(c) mod(c, CX_RIPPLE_WRAP)
#include "cx_wx_ring.glsl"
layout(rg16f, binding = 0) writeonly uniform image2D uOut;

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, ivec2(uSize)))) return;
    vec2 p = (vec2(t) + 0.5) / float(uSize) * (CX_RIPPLE_CELL_M * CX_RIPPLE_WRAP);
    vec2 g = CxRippleLayer(p, uRate, 0.0) + CxRippleLayer(p + CX_RIPPLE_CELL_M * 0.5, uRate, 37.0);
    imageStore(uOut, t, vec4(-g, 0.0, 0.0));
}
