#version 460 core
// CX-46: the probe's ambient shape as 9 SH irradiance coefficients (Ramamoorthi & Hanrahan 2001). One
// workgroup walks the capture at its <= 16-texel face level; the cosine lobe's band weights and the basis
// constants are folded in here, so cx_material's CxAmbientShape evaluates E(N) as a bare polynomial.
layout(local_size_x = 64) in;
layout(binding = 0) uniform samplerCube uSrc;          // the capture, box-mipped
layout(std430, binding = 15) writeonly buffer CxProbeShOut { vec4 uShOut[9]; vec4 uShAt; };
uniform vec3 uOrigin;                                  // where the capture stood: CxProbeSh's uProbeAt

#include "cx_cubedir.glsl"
shared vec3 cxSh[9][64];

void main()
{
    uint t = gl_LocalInvocationIndex;
    int levels = textureQueryLevels(uSrc), level = max(levels - 5, 0);
    int size = textureSize(uSrc, level).x;
    vec3 c[9];
    for (int k = 0; k < 9; k++) c[k] = vec3(0.0);
    for (int i = int(t); i < 6 * size * size; i += 64)
    {
        int face = i / (size * size), xy = i % (size * size);
        vec2 uv = (vec2(xy % size, xy / size) + 0.5) * (2.0 / float(size)) - 1.0;
        vec3 d = CxFaceDir(face, uv);
        float r2 = dot(d, d);
        d *= inversesqrt(r2);
        // the texel's solid angle: (2/size)^2 projected onto the unit sphere
        vec3 L = textureLod(uSrc, d, float(level)).rgb * (4.0 / (float(size * size) * r2 * sqrt(r2)));
        c[0] += L;
        c[1] += L * d.y; c[2] += L * d.z; c[3] += L * d.x;
        c[4] += L * d.x * d.y; c[5] += L * d.y * d.z; c[6] += L * (3.0 * d.z * d.z - 1.0);
        c[7] += L * d.x * d.z; c[8] += L * (d.x * d.x - d.y * d.y);
    }
    for (int k = 0; k < 9; k++) cxSh[k][t] = c[k];
    barrier();
    if (t == 9u) uShAt = vec4(uOrigin, 1.0);
    if (t >= 9u) return;
    vec3 s = vec3(0.0);
    for (int j = 0; j < 64; j++) s += cxSh[t][j];
    // A_l * K^2 per coefficient: the band's cosine-lobe weight times its basis constant, squared
    const float w[9] = float[9](CX_PI * 0.079577, 2.0 * CX_PI / 3.0 * 0.238732, 2.0 * CX_PI / 3.0 * 0.238732,
                                2.0 * CX_PI / 3.0 * 0.238732, CX_PI / 4.0 * 1.193662, CX_PI / 4.0 * 1.193662,
                                CX_PI / 4.0 * 0.099472, CX_PI / 4.0 * 1.193662, CX_PI / 4.0 * 0.298416);
    uShOut[t] = vec4(s * w[t], 0.0);
}
