#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "cx_drawbuf.glsl"
uniform mat4 uVP;
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;
void main()
{
    CxDraw d = uDraw[gl_BaseInstance];
    gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vUV = aUV;
    vAlpha = d.rot.w;
}
