#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uEdges;
uniform sampler2D uArea;
uniform sampler2D uSearch;
#include "cx_smaadefs.glsl"
void main()
{
    vec4 off[3];
    vec2 pix;
    SMAABlendingWeightCalculationVS(vUV, pix, off);
    frag = SMAABlendingWeightCalculationPS(vUV, pix, off, uEdges, uArea, uSearch, vec4(0.0));
}
