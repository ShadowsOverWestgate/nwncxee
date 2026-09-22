uniform sampler2D uBlend;    // pass 2's weights
#include "cx_smaadefs.glsl"
vec4 CxStage(vec4 c)
{
    vec4 off;
    SMAANeighborhoodBlendingVS(vUV, off);
    return SMAANeighborhoodBlendingPS(vUV, off, uSrc, uBlend);
}
