#version 460 core
out vec4 frag;
uniform int uStride;
layout(std430, binding = 22) writeonly buffer CxOitHeads { uint uOitCount; uint uOitHead[]; };
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    uOitHead[p.y * uStride + p.x] = 0u;
    if (p.x == 0 && p.y == 0) uOitCount = 0u;
    frag = vec4(0.0);
}
