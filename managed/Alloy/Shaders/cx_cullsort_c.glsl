#version 460 core
layout(local_size_x = 64) in;   // GroupSize
layout(std430, binding = 8) readonly buffer CxCullSrc     { uint uSrc[]; };
layout(std430, binding = 10) writeonly buffer CxCullCmd    { uint uCmd[]; };
layout(std430, binding = 11) writeonly buffer CxCullOutIdx { int  uOut[]; };
layout(std430, binding = 13) readonly buffer CxCullKey     { float uKey[]; };
uniform int uDrawN;
uniform int uCompactBelow;

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uDrawN)) return;
    uint b = i * 6u;
    uint batch = uSrc[b + 4u];
    if (batch < uint(uCompactBelow)) return;
    float k = uKey[i];
    if (k < 0.0) return;              // the cull dropped it
    uint base = uSrc[b + 5u];
    uint rank = 0u;
    for (uint j = base; j < uint(uDrawN); j++)
    {
        if (uSrc[j * 6u + 4u] != batch) break;   // batches are contiguous in the source list
        if (j == i) continue;
        float kj = uKey[j];
        if (kj < 0.0) continue;
        // Farther first. An exact tie falls to the lower staged index, which is the CPU sort's own
        // stable order -- so the two agree draw for draw and /alloy cull 0 is a like-for-like A/B.
        if (kj > k || (kj == k && j < i)) rank++;
    }
    uint o = base + rank;
    uint c = o * 5u;
    uCmd[c] = uSrc[b];
    uCmd[c + 1u] = 1u;
    uCmd[c + 2u] = uSrc[b + 1u];
    uCmd[c + 3u] = uSrc[b + 2u];
    uCmd[c + 4u] = 0u;
    uOut[o] = int(uSrc[b + 3u]);
}
