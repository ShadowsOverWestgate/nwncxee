#version 460 core
#extension GL_ARB_shader_viewport_layer_array : require
layout(location = 0) in vec3 aPos;
#include "AlloyRenderer.DrawBufDecl.glsl"
#include "AlloyRenderer.FeatDecl.glsl"
#include "AlloyRenderer.BoneVs.glsl"

#include "CasterGather.ViewTblDecl.glsl"
invariant gl_Position;
void main()
{
    int vi = gl_BaseInstance >> 18;
    CxDraw d = uDraw[gl_BaseInstance & 0x3ffff];
    gl_Layer = int(uViewTbl[vi].c.w);
    mat4 uVP = uViewTbl[vi].vp;
    int bb = CxBoneBase(d);
    if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
}
