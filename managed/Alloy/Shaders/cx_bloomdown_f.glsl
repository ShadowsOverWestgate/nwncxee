#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;
uniform int uLod;
uniform vec2 uTexel;    // 1 / size of the SOURCE level
uniform int uFirst;     // 1 = this pass reads the HDR colour: apply the threshold
uniform vec2 uThresh;   // x = brightness threshold, y = soft-knee width
uniform float uExposure;    // the resolve's gain: the threshold reads the exposed value
uniform int uAuto;          // 1 = the resolve applies uAutoGain on top
#include "cx_autobuf_read.glsl"

vec3 T(vec2 o) { return textureLod(uSrc, vUV + o * uTexel, float(uLod)).rgb; }

void main()
{
    vec3 a = T(vec2(-2.0,  2.0)), b = T(vec2( 0.0,  2.0)), c = T(vec2( 2.0,  2.0));
    vec3 d = T(vec2(-2.0,  0.0)), e = T(vec2( 0.0,  0.0)), f = T(vec2( 2.0,  0.0));
    vec3 g = T(vec2(-2.0, -2.0)), h = T(vec2( 0.0, -2.0)), i = T(vec2( 2.0, -2.0));
    vec3 j = T(vec2(-1.0,  1.0)), k = T(vec2( 1.0,  1.0));
    vec3 l = T(vec2(-1.0, -1.0)), m = T(vec2( 1.0, -1.0));
    // Karis average on the first downsample only: each 2x2 group is weighted by 1/(1+luma), so one
    // runaway texel cannot dominate its group. Past level 0 the fireflies are gone, and reweighting
    // filtered mips would only darken the bright cores.
    vec3 grp[5] = vec3[5]((j + k + l + m) * 0.25, (a + b + d + e) * 0.25, (b + c + e + f) * 0.25,
                          (d + e + g + h) * 0.25, (e + f + h + i) * 0.25);
    const float w[5] = float[5](0.5, 0.125, 0.125, 0.125, 0.125);
    vec3 sum = vec3(0.0);
    float wsum = 0.0;
    for (int n = 0; n < 5; n++)
    {
        float kw = uFirst != 0 ? w[n] / (1.0 + dot(grp[n], vec3(0.2126, 0.7152, 0.0722))) : w[n];
        sum += grp[n] * kw;
        wsum += kw;
    }
    vec3 col = sum / max(wsum, 1e-6);
    if (uFirst != 0)
    {
        // Unreal's soft-knee curve: nothing below the threshold blooms at all, which is what keeps
        // the effect off engine-drawn geometry's range and off every ordinary lit surface.
        float br = max(col.r, max(col.g, col.b)) * uExposure * (uAuto != 0 ? uAutoGain : 1.0);
        float soft = clamp(br - uThresh.x + uThresh.y, 0.0, 2.0 * uThresh.y);
        soft = soft * soft / (4.0 * uThresh.y);
        col *= max(soft, br - uThresh.x) / max(br, 1e-6);
    }
    frag = vec4(max(col, vec3(0.0)), 1.0);
}
