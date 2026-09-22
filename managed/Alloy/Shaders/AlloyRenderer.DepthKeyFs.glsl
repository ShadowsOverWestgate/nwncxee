#version 460 core
in vec3 vWorld;
in float vDepth;
flat in int vDraw;
#include "AlloyRenderer.DrawBufDecl.glsl"
#include "AlloyRenderer.FeatDecl.glsl"
#include "AlloyRenderer.KeyFogDecl.glsl"
#include "IdTarget.OutDecl.glsl"

#include "AlloyRenderer.KeyholeFs.glsl"
void main()
{
    if (CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
    oVisId = CxVisId(vDraw, gl_PrimitiveID);
}
