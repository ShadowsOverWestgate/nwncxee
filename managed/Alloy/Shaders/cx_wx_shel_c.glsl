// The shelter cache (Weather.Shelter): once per cover BAKE, the shelter test and the snow drift for the
// surface the rain lands on at every cover texel -- the work the lit stage, the grass cards and the blades
// used to redo per pixel per frame for a roof that had not moved. A fragment that IS that surface reads
// one texel (CxWxTop); one under a roof, on a wall or on a slope still runs the live disk.
// The disk here has twice the live ceiling of taps and no per-cell rotation: it runs once, so it can afford
// the taps that leave no plateaus, and the dither that hid them is not needed.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
#include "cx_wx_shape.glsl"
layout(rgba16f, binding = 0) writeonly uniform image2D uOut;
uniform sampler2D uCoverDepth;   // the cover layer, plain depth, compare mode off
uniform mat4 uCoverVp;           // world -> the cover view's clip space
uniform mat4 uCoverVpInv;        // ...and back
uniform ivec2 uSize;
// x uv per metre, y the disk's world radius (m), z the breakup, w the drift width in uv (0 = no drift)
uniform vec4 uEdge;
uniform float uHollow;           // the drift's height step, in depth units

const int CX_WX_SHEL_TAPS = 24;

float CxDepth(vec2 uv)
{
    ivec2 s = textureSize(uCoverDepth, 0);
    return texelFetch(uCoverDepth, clamp(ivec2(uv * vec2(s)), ivec2(0), s - 1), 0).r;
}

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, uSize))) return;
    vec2 uv = (vec2(t) + 0.5) / vec2(uSize);
    float d = CxDepth(uv);
    if (d >= 1.0) { imageStore(uOut, t, vec4(1.0, 0.0, 0.0, 0.0)); return; }
    vec4 w = uCoverVpInv * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
    vec3 p = w.xyz / w.w;
    // the live test's lift for a level surface: the bias along up, no slope share
    vec4 c = uCoverVp * vec4(p + vec3(0.0, 0.0, CX_WX_EDGE_BIAS_M), 1.0);
    vec3 n = c.xyz / c.w;
    vec2 q = n.xy * 0.5 + 0.5;
    float ref = n.z * 0.5 + 0.5 - CX_WX_EDGE_EPS;
    float r = uEdge.y * uEdge.x, open = 0.0;
    for (int i = 0; i < CX_WX_SHEL_TAPS; i++)
    {
        float s = (float(i) + 0.5) / float(CX_WX_SHEL_TAPS);
        float a = float(i) * CX_WX_EDGE_GOLD;
        open += ref <= CxDepth(q + sqrt(s) * r * vec2(cos(a), sin(a))) ? 1.0 : 0.0;
    }
    open = CxWetOpenShape(open / float(CX_WX_SHEL_TAPS), p.xy, uEdge.z);
    // the drift: three rings of eight asking whether the ground there stands a step higher (cx_wx_lib's
    // old per-pixel CxSnowDrift, here once per bake)
    vec3 drift = vec3(0.0);
    if (uEdge.w > 0.0)
    {
        float href = d - uHollow;
        for (int k = 0; k < 3; k++)
        {
            float rad = uEdge.w * (0.25 + 0.375 * float(k));
            float wk = 1.0 - 0.3 * float(k);
            for (int i = 0; i < 8; i++)
            {
                float a = float(i) * 0.7853982 + float(k) * 0.3;
                vec2 o = vec2(cos(a), sin(a));
                float higher = href <= CxDepth(uv + o * rad) ? 0.0 : 1.0;
                drift.x += higher * wk;
                drift.yz += o * higher * wk;
            }
        }
        drift.x /= 8.0 * 2.1;
    }
    imageStore(uOut, t, vec4(open, drift));
}
