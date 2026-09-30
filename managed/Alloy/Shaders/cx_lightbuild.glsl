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
// CasterPass.CubeDynSphere, the lit stage's own mover spheres (xyz, r; r < 0 unused), $DYNSPH$ per cube slot
layout(std430, binding = 25) readonly buffer DynMovers { vec4 uDynMovers[]; };
uniform mat4 uView;
uniform vec2 uProjXY;
uniform vec2 uZ;
uniform ivec3 uGrid;
uniform int uLightCount;
uniform float uReach;
uniform int uBinCap;
uniform vec2 uAtten;
// per cube slot: its dyn page plus one (0 = no mover near the light), as the lit stage's uLocalDynPage
uniform float uDynPage[$CUBES$];
// x = lightmap.movers (1 = a static light can go hot), y = shadow.cubedist (0 = no cut)
uniform vec2 uHot;
// R4 (weather.filmlights): how many cold statics to rank as reflection candidates for a water film.
// 0 = none, which is every frame with no film, and then this pass writes exactly what it always did.
uniform int uReflK;
const int CX_REFL_MAX = 4;
const uint CAP = $CAP$u;
const uint SORT = $SORT$u;
const int DYNSPH = $DYNSPH$;
const int CUBES = $CUBES$;

// Could a baked static light in cube slot `slot` go HOT anywhere in this froxel -- could the lit stage's
// per-fragment ray test (cx_lightfrag: the light-to-fragment segment within r + 0.3 of a mover sphere) pass
// for a point in it? Every such point lies in the cone from the light tangent to the inflated sphere, so a
// froxel whose bounding sphere (fc, fr) misses every slot's cone cannot hold one. Conservative by design.
bool HotReach(int slot, vec3 e, vec3 fc, float fr)
{
    if (slot < 0 || slot >= CUBES || uDynPage[slot] < 0.5) return false;
    for (int q = 0; q < DYNSPH; q++)
    {
        vec4 ms = uDynMovers[slot * DYNSPH + q];
        if (ms.w < 0.0) continue;
        vec3 ax = (uView * vec4(ms.xyz, 1.0)).xyz - e;
        float rr = ms.w + 0.3, d = length(ax);
        // the lit test also passes a centre up to rr BEHIND the light along the ray, reachable only while the
        // light is within sqrt(2) rr of it: every direction, then
        if (d * d <= 2.0 * rr * rr) return true;
        ax /= d;
        float sn = rr / d, cs = sqrt(1.0 - sn * sn);
        vec3 v = fc - e;
        float along = dot(v, ax);
        if (along < -fr) continue;   // the froxel is wholly behind the light
        float perp = sqrt(max(dot(v, v) - along * along, 0.0));
        if (cs * perp - along * sn <= fr) return true;
    }
    return false;
}

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
    // Cold statics to the back: a static light is in the pages, and a baked matte receiver owes it nothing
    // unless it goes HOT -- which needs a cube with a dyn page, lightmap.movers, a froxel nearer than the
    // cube distance and a mover whose shadow cone reaches the froxel (HotReach). Everything else trails the
    // list, and such a receiver stops at the live count in the high half. Glossy and unbaked receivers walk
    // the whole list, so the order changes nothing for them.
    uint live = 0u, tail = n;
    vec3 fc = 0.5 * (lo + hi);
    float fr = 0.5 * length(hi - lo);
    bool hotOn = uHot.x > 0.5 && !(uHot.y > 0.0 && d0 >= uHot.y);
    while (live < tail)
    {
        uint li = uClIdx[base + live];
        int w = int(uLights[li * 2u + 1u].w);
        int set = (w - 128) / 512 - 1;
        if (w < 128 || hotOn && HotReach(w - 256 - 512 * (set + 1), (uView * vec4(uLights[li * 2u].xyz, 1.0)).xyz, fc, fr))
        { live++; continue; }
        tail--;
        uint t = uClIdx[base + live]; uClIdx[base + live] = uClIdx[base + tail]; uClIdx[base + tail] = t;
    }
    // R4: the reflection candidates -- the K cold statics with the strongest curve x luma at this froxel,
    // permuted to the front of the cold tail so the lit stage can walk [live, live + refl) and nothing
    // else. A PERMUTATION, never an overwrite: a glossy or unbaked receiver still walks the whole list.
    uint refl = 0u;
    if (uReflK > 0 && tail > live)
    {
        int K = min(uReflK, CX_REFL_MAX);
        float rs[CX_REFL_MAX]; uint rp[CX_REFL_MAX];
        for (uint k = live; k < tail; k++)
        {
            uint li = uClIdx[base + k];
            vec4 pr = uLights[li * 2u];
            vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
            vec3 dv = e - clamp(e, lo, hi);
            float f2 = dot(dv, dv) / max(pr.w * pr.w, 1e-6);
            float s = (1.0 - f2) / (at.x + at.y * f2) * dot(uLights[li * 2u + 1u].rgb, vec3(0.299, 0.587, 0.114));
            uint j;
            if (refl < uint(K)) { j = refl; refl++; }
            else if (s > rs[K - 1]) j = uint(K) - 1u;
            else continue;
            while (j > 0u && rs[j - 1u] < s) { rs[j] = rs[j - 1u]; rp[j] = rp[j - 1u]; j--; }
            rs[j] = s; rp[j] = k;
        }
        for (uint j = 0u; j < refl; j++)
        {
            uint dst = live + j, src = rp[j];
            if (src == dst) continue;
            uint sw = uClIdx[base + dst]; uClIdx[base + dst] = uClIdx[base + src]; uClIdx[base + src] = sw;
            // a later candidate standing on the slot just claimed moved to where this one came from
            for (uint q = j + 1u; q < refl; q++) if (rp[q] == dst) rp[q] = src;
        }
    }
    uClCount[id * 2u] = n | (live << 16u);
    uClCount[id * 2u + 1u] = refl;
    if (dropped != 0u && uBinCap <= 0) atomicAdd(uOverflow, 1u);
}
