#version 460 core
layout(local_size_x = 16, local_size_y = 16) in;
uniform sampler2D uHdr;
uniform vec2 uRange;   // (log2 floor, 1 / log2 span)
#include "PostChain.AutoBufLib.glsl"
shared uint sBin[64];
void main()
{
    uint li = gl_LocalInvocationIndex;
    if (li < 64u) sBin[li] = 0u;
    barrier();
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (all(lessThan(p, textureSize(uHdr, 0))))
    {
        float l = dot(texelFetch(uHdr, p, 0).rgb, vec3(0.2126, 0.7152, 0.0722));
        uint b = 0u;
        if (l > 1e-5) b = min(uint(clamp((log2(l) - uRange.x) * uRange.y, 0.0, 1.0) * 62.0), 61u) + 1u;
        atomicAdd(sBin[b], 1u);
    }
    barrier();
    if (li < 64u && sBin[li] != 0u) atomicAdd(uHistBin[li], sBin[li]);
}
