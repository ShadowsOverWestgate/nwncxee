// Pass 1: luma edges of the resolved frame, RG = (left, top) edge flags.
#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;      // the resolved frame, DISPLAY space -- SMAA wants perceptual luma
#include "cx_smaadefs.glsl"
void main()
{
    vec4 off[3];
    SMAAEdgeDetectionVS(vUV, off);
    frag = vec4(SMAALumaEdgeDetectionPS(vUV, off, uSrc), 0.0, 1.0);
}
