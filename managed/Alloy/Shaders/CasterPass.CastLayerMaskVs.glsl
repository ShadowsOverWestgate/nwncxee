#version 460 core
#extension GL_ARB_shader_viewport_layer_array : require
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "AlloyRenderer.DrawBufDecl.glsl"

#include "CasterGather.ViewTblDecl.glsl"
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;
void main()
{
    int vi = gl_BaseInstance >> 18;
    CxDraw d = uDraw[gl_BaseInstance & 0x3ffff];
    gl_Layer = int(uViewTbl[vi].c.w);
    mat4 uVP = uViewTbl[vi].vp;
    gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vUV = aUV;
    vAlpha = d.rot.w;
}
