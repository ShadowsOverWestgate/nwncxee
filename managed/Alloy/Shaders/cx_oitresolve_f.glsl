#version 460 core
out vec4 frag;
uniform sampler2D uAccum;
uniform sampler2D uReveal;
uniform int uViz;
uniform int uMode;
uniform int uNet;        // 1 = the weighted pair was written this frame (mode 2 writes it only after an overflow)
uniform ivec2 uOitDim;   // (head-table stride, node capacity)
layout(std430, binding = 4) readonly buffer CxOitHeads { uint uOitCount; uint uOitHead[]; };
layout(std430, binding = 5) readonly buffer CxOitNodes { uvec4 uOitNode[]; };
// ponytail: 16 sorted layers a pixel; past that the far end composites unsorted. Raise if glass stacks deeper.
const int K = 16;
const int VizWalk = 64;   // uViz 4: nodes walked per pixel before the grey saturates
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    float reveal = texelFetch(uReveal, p, 0).r;
    if (uViz == 1) { frag = vec4(vec3(reveal), 1.0); return; }
    if (uViz == 2) { vec4 a = texelFetch(uAccum, p, 0); frag = vec4(a.rgb / max(a.a, 1e-5), 1.0); return; }
    if (uViz == 3) { frag = vec4(vec3(texelFetch(uAccum, p, 0).a * 0.1), 1.0); return; }
    bool linked = uMode == 2 && (uOitCount <= uint(uOitDim.y) || uNet == 0);
    if (uViz == 4)
    {
        // magenta = the list path is off this frame, yellow = the node pool overflowed; black = empty list
        if (uMode != 2) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
        if (uOitCount > uint(uOitDim.y)) { frag = vec4(1.0, 1.0, 0.0, 1.0); return; }
        int n = 0;
        for (uint i = linked ? uOitHead[p.y * uOitDim.x + p.x] : 0u; i != 0u && n < VizWalk; i = uOitNode[i].x) n++;
        frag = vec4(vec3(float(n) / float(K)), 1.0); return;
    }
    if (uViz == 5)
    {
        // the NEAREST node's object id, hashed to a colour; white = id 0 (ungrouped), black = no list
        uint best = 0xFFFFFFFFu;
        for (uint i = linked ? uOitHead[p.y * uOitDim.x + p.x] : 0u; i != 0u; i = uOitNode[i].x) best = min(best, uOitNode[i].y);
        if (best == 0xFFFFFFFFu) { frag = vec4(0.0, 0.0, 0.0, 1.0); return; }
        uint g = best & 0xFFu;
        frag = g == 0u ? vec4(1.0) : vec4(float(g * 97u % 255u) / 255.0, float(g * 57u % 255u) / 255.0, float(g * 31u % 255u) / 255.0, 1.0);
        return;
    }
    if (linked)
    {
        uint head = uOitHead[p.y * uOitDim.x + p.x];
        if (head == 0u) discard;
        uint idx[K]; uint z[K];   // z = the node's key: depth in the high 24 bits, object id in the low 8
        int n = 0;
        vec3 tailC = vec3(0.0); float tailT = 1.0;
        for (uint i = head; i != 0u; i = uOitNode[i].x)
        {
            uint d = uOitNode[i].y;
            uint spill = 0u;
            if (n == K)
            {
                if (d >= z[K - 1]) spill = i;
                else { spill = idx[K - 1]; n = K - 1; }
            }
            if (spill != 0u)
            {
                // an object already kept nearer composites once: its farther parts are its own back faces
                uint sg = uOitNode[spill].y & 0xFFu;
                bool dup = false;
                for (int k = 0; k < n && sg != 0u; k++) dup = dup || (z[k] & 0xFFu) == sg;
                if (!dup)
                {
                    vec4 c = vec4(unpackHalf2x16(uOitNode[spill].z), unpackHalf2x16(uOitNode[spill].w));
                    tailC += tailT * c.rgb * c.a; tailT *= 1.0 - c.a;
                }
                if (spill == i) continue;
            }
            int j = n++;
            while (j > 0 && z[j - 1] > d) { z[j] = z[j - 1]; idx[j] = idx[j - 1]; j--; }
            z[j] = d; idx[j] = i;
        }
        vec3 col = vec3(0.0); float T = 1.0;
        for (int k = 0; k < n; k++)
        {
            uint g = z[k] & 0xFFu;
            bool dup = false;
            for (int m = 0; m < k && g != 0u; m++) dup = dup || (z[m] & 0xFFu) == g;
            if (dup) continue;
            vec4 c = vec4(unpackHalf2x16(uOitNode[idx[k]].z), unpackHalf2x16(uOitNode[idx[k]].w));
            col += T * c.rgb * c.a; T *= 1.0 - c.a;
        }
        col += T * tailC; T *= tailT;
        frag = vec4(col, T);
        return;
    }
    if (!(reveal < 1.0)) discard;   // also NaN: nothing well-formed wrote here
    reveal = max(reveal, 0.0);
    vec4 accum = texelFetch(uAccum, p, 0);
    vec3 c = accum.rgb / max(accum.a, 1e-5);
    if (any(isinf(c)) || any(isnan(c))) c = vec3(0.0);
    // mode 2's blend pair is ONE / SRC_ALPHA: hand it the premultiplied form of the same composite
    frag = uMode == 2 ? vec4(c * (1.0 - reveal), reveal) : vec4(c, reveal);
}
