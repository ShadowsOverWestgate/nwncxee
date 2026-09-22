#version 460 core
layout(location = 0) out vec4 frag;
#include "AlloyRenderer.OitOutFs.glsl"
uniform sampler2D uHdr;
void main()
{
    vec4 c = texelFetch(uHdr, ivec2(gl_FragCoord.xy), 0);
    frag = vec4(pow(max(c.rgb, vec3(0.0)), vec3($SRGB$)), c.a);
}
