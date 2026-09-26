#version 460 core
// CX-46: the probe's roughness chain. Level i of the published cube is the capture convolved with the GGX
// lobe of perceptual roughness i / (levels - 1) -- the inverse of every sample site's sqrt(rough) *
// (uProbeMips - 1.0) -- with N = V = R (the split-sum's isotropic assumption). Filtered importance sampling
// (Colbert & Krivanek, GPU Gems 3 ch. 20): each tap reads the capture's box chain at the mip whose texel
// matches the sample's solid angle, so 64 taps do not alias.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 0) uniform samplerCube uSrc;          // the capture, box-mipped
layout(rgba16f, binding = 0) writeonly uniform imageCube uDst;
uniform int uLevel;

#include "cx_cubedir.glsl"
const int CX_TAPS = 64;

void main()
{
    ivec3 p = ivec3(gl_GlobalInvocationID);
    int size = imageSize(uDst).x;
    if (p.x >= size || p.y >= size) return;
    vec3 N = normalize(CxFaceDir(p.z, (vec2(p.xy) + 0.5) * (2.0 / float(size)) - 1.0));
    int levels = textureQueryLevels(uSrc);
    float perceptual = float(uLevel) / float(max(levels - 1, 1));
    if (uLevel == 0) { imageStore(uDst, p, vec4(textureLod(uSrc, N, 0.0).rgb, 1.0)); return; }

    float a = perceptual * perceptual, a2 = a * a;
    float src = float(textureSize(uSrc, 0).x);
    float texelAngle = 4.0 * CX_PI / (6.0 * src * src);
    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tx = normalize(cross(up, N)), ty = cross(N, tx);
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < CX_TAPS; i++)
    {
        vec2 xi = vec2(float(i) / float(CX_TAPS), float(bitfieldReverse(uint(i))) * 2.3283064365386963e-10);
        float phi = 2.0 * CX_PI * xi.x;
        float cosT = sqrt((1.0 - xi.y) / (1.0 + (a2 - 1.0) * xi.y)), sinT = sqrt(1.0 - cosT * cosT);
        vec3 H = tx * (sinT * cos(phi)) + ty * (sinT * sin(phi)) + N * cosT;
        vec3 L = 2.0 * cosT * H - N;
        float NoL = dot(N, L);
        if (NoL <= 0.0) continue;
        float den = cosT * cosT * (a2 - 1.0) + 1.0;
        float pdf = a2 / (CX_PI * den * den) * 0.25;
        // +1: Karis's bias over the footprint lod, trading a touch of extra blur for no undersampling noise
        float lod = clamp(0.5 * log2(1.0 / (float(CX_TAPS) * pdf * texelAngle)) + 1.0, 0.0, float(levels - 1));
        sum += textureLod(uSrc, L, lod).rgb * NoL;
        wsum += NoL;
    }
    imageStore(uDst, p, vec4(sum / max(wsum, 1e-4), 1.0));
}
