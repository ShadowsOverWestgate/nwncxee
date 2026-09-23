// The cluster build. One invocation per froxel: reconstruct the froxel's eye-space AABB from the
// projection diagonal (x = ndc.x * depth / P00), then a sphere-vs-AABB test per light. O(clusters *
// lights) with no CPU involvement -- 3456 * ~120 tests is nothing for the GPU and, unlike the engine's
// CPU prioritizer, its cost does not grow with draw count.
#version 460 core
layout(local_size_x = $GROUP$) in;
layout(std430, binding = 0) readonly buffer LightBuf { vec4 uLights[]; };
layout(std430, binding = 1) writeonly buffer ClusterCount { uint uClCount[]; };
layout(std430, binding = 2) buffer ClusterIdx { uint uClIdx[]; };
layout(std430, binding = 3) buffer Stats { uint uOverflow; };
uniform mat4 uView;
uniform vec2 uProjXY;
uniform vec2 uZ;
uniform ivec3 uGrid;
uniform int uLightCount;
uniform float uReach;
uniform int uBinCap;
uniform vec2 uAtten;
const uint CAP = $CAP$u;
const uint SORT = $SORT$u;

void main()
{
    uint id = gl_GlobalInvocationID.x;
    if (id >= uint(uGrid.x * uGrid.y * uGrid.z)) return;
    int gx = int(id) % uGrid.x;
    int gy = (int(id) / uGrid.x) % uGrid.y;
    int gz = int(id) / (uGrid.x * uGrid.y);

    float lz = log(uZ.y / uZ.x);
    float d0 = uZ.x * exp(lz * (float(gz) / float(uGrid.z)));
    float d1 = uZ.x * exp(lz * (float(gz + 1) / float(uGrid.z)));
    float x0 = -1.0 + 2.0 * float(gx) / float(uGrid.x), x1 = -1.0 + 2.0 * float(gx + 1) / float(uGrid.x);
    float y0 = -1.0 + 2.0 * float(gy) / float(uGrid.y), y1 = -1.0 + 2.0 * float(gy + 1) / float(uGrid.y);

    vec3 lo = vec3(1e30), hi = vec3(-1e30);
    for (int c = 0; c < 8; c++)
    {
        float d = (c & 4) == 0 ? d0 : d1;
        vec3 p = vec3(((c & 1) == 0 ? x0 : x1) * d / uProjXY.x,
                      ((c & 2) == 0 ? y0 : y1) * d / uProjXY.y,
                      -d);
        lo = min(lo, p); hi = max(hi, p);
    }

    // #336: walk order, unsorted. A per-froxel order for the receiver's tap cap put the cap's seam
    // on the cluster grid (screen-aligned squares wherever four cube lights overlapped); the cap
    // chooses per FRAGMENT now, so the list needs no order.
    uint n = 0u;
    uint base = id * CAP;
    uint dropped = 0u;
    // EXPERIMENT (/light bincap): keep only the K strongest, by the engine's curve at the froxel's
    // nearest point times luma -- the ordering the per-fragment body would find, made once per froxel.
    uint K = uBinCap <= 0 ? CAP : min(uint(uBinCap), SORT);
    float sc[SORT];
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    for (int i = 0; i < uLightCount; i++)
    {
        vec4 pr = uLights[i * 2];
        float r = pr.w * uReach;
        vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
        vec3 dv = e - clamp(e, lo, hi);
        float dd = dot(dv, dv);
        if (dd > r * r) continue;
        if (uBinCap <= 0)
        {
            if (n >= CAP) { dropped = 1u; break; }
            uClIdx[base + n] = uint(i);
            n++;
            continue;
        }
        float f2 = dd / (pr.w * pr.w);
        float s = (1.0 - f2) / (at.x + at.y * f2) * dot(uLights[i * 2 + 1].rgb, vec3(0.299, 0.587, 0.114));
        uint j;
        if (n < K) j = n++;
        else if (s > sc[K - 1u]) { j = K - 1u; dropped = 1u; }
        else { dropped = 1u; continue; }
        while (j > 0u && sc[j - 1u] < s)
        {
            sc[j] = sc[j - 1u]; uClIdx[base + j] = uClIdx[base + j - 1u];
            j--;
        }
        sc[j] = s; uClIdx[base + j] = uint(i);
    }
    // Cold statics to the back: a static light with no cube (w = 255, the static bit over slot -1) is in
    // the pages and can never go hot, so a baked matte receiver stops at the live count in the high half.
    uint live = 0u, tail = n;
    while (live < tail)
    {
        if (int(uLights[uClIdx[base + live] * 2u + 1u].w) != 255) { live++; continue; }
        tail--;
        uint t = uClIdx[base + live]; uClIdx[base + live] = uClIdx[base + tail]; uClIdx[base + tail] = t;
    }
    uClCount[id] = n | (live << 16u);
    if (dropped != 0u && uBinCap <= 0) atomicAdd(uOverflow, 1u);
}
