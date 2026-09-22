#version 460 core
layout(local_size_x = 256) in;
#include "cx_partrec.glsl"
layout(std430, binding = 11) buffer CxParticleIdx { uint partIdx[]; };
// (record base, particles, index offset, -), one per emitter the pass placed
layout(std430, binding = 17) readonly buffer CxSpanBuf { uvec4 spans[]; };
// (first span, spans, index offset, particles | 0x80000000 when this group may be reordered)
layout(std430, binding = 18) readonly buffer CxGroupBuf { uvec4 groups[]; };

uniform vec3 uEye;

const uint CX_SORT_CAP = 2048u;
shared float sKey[CX_SORT_CAP];
shared uint  sVal[CX_SORT_CAP];

void main()
{
    uvec4 g = groups[gl_WorkGroupID.x];
    uint tid = gl_LocalInvocationID.x;
    uint lanes = gl_WorkGroupSize.x;
    uint dst = g.z;
    uint count = g.w & 0x7fffffffu;

    for (uint s = 0u; s < g.y; ++s)
    {
        uvec4 sp = spans[g.x + s];
        for (uint j = tid; j < sp.y; j += lanes) partIdx[sp.z + j] = sp.x + j;
    }
    // The whole workgroup takes this branch together -- g is workgroup-uniform -- so leaving here
    // cannot strand anyone at a barrier below.
    if ((g.w & 0x80000000u) == 0u) return;
    memoryBarrierBuffer();
    barrier();

    uint n = 1u;
    while (n < count) n <<= 1u;
    for (uint i = tid; i < n; i += lanes)
    {
        if (i < count)
        {
            uint r = partIdx[dst + i];
            vec3 d = particles[r].centre.xyz - uEye;
            sKey[i] = -dot(d, d);
            sVal[i] = r;
        }
        else { sKey[i] = 3.0e38; sVal[i] = 0u; }
    }
    memoryBarrierShared();
    barrier();

    for (uint k = 2u; k <= n; k <<= 1u)
    {
        for (uint j = k >> 1u; j > 0u; j >>= 1u)
        {
            for (uint i = tid; i < n; i += lanes)
            {
                uint l = i ^ j;
                if (l > i && (sKey[i] > sKey[l]) == ((i & k) == 0u))
                {
                    float fk = sKey[i]; sKey[i] = sKey[l]; sKey[l] = fk;
                    uint fv = sVal[i]; sVal[i] = sVal[l]; sVal[l] = fv;
                }
            }
            memoryBarrierShared();
            barrier();
        }
    }

    for (uint i = tid; i < count; i += lanes) partIdx[dst + i] = sVal[i];
}
