#version 460 core
in vec2 vUV;
uniform sampler2D uScrA;
uniform sampler2D uScrB;
const int CX_LM_PAGE = 4096;
layout(location = 0) out vec4 oA;
layout(location = 1) out vec4 oB;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    vec4 a = texelFetch(uScrA, p, 0);
    vec4 b = vec4(0.0);
    if (a.a > 0.0) b = texelFetch(uScrB, p, 0);
    else
    {
        float best = 1e9;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                ivec2 q = p + ivec2(dx, dy);
                if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, ivec2(CX_LM_PAGE)))) continue;
                vec4 s = texelFetch(uScrA, q, 0);
                float d = float(dx * dx + dy * dy);
                if (s.a > 0.0 && d < best) { best = d; a = s; b = texelFetch(uScrB, q, 0); }
            }
        if (best > 1e8) discard;
    }
    float l = length(b.xyz);
    vec3 dir = l > 1e-6 ? b.xyz / l : vec3(0.0, 0.0, 1.0);
    oA = vec4(max(a.rgb, vec3(0.0)), 1.0);
    oB = vec4(dir * 0.5 + 0.5, 1.0);
}
