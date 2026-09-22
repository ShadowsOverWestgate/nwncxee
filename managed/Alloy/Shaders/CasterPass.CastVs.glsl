#version 460 core
layout(location = 0) in vec3 aPos;
#include "AlloyRenderer.DrawBufDecl.glsl"
#include "AlloyRenderer.FeatDecl.glsl"
#include "AlloyRenderer.BoneVs.glsl"
uniform mat4 uVP;
invariant gl_Position;
void main()
{
    CxDraw d = uDraw[gl_BaseInstance];
    int bb = CxBoneBase(d);
    if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));
    else gl_Position = uVP * d.model * vec4(aPos, 1.0);
}
