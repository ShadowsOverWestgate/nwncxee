#version 460 core
in vec2 vUV;
in vec3 vWorld;
in float vDepth;
flat in int vDraw;
#include "AlloyRenderer.DrawBufDecl.glsl"
#include "AlloyRenderer.FeatDecl.glsl"
#include "AlloyRenderer.KeyFogDecl.glsl"
#include "IdTarget.OutDecl.glsl"
uniform sampler2D uTex;
uniform float uAlphaCut;
#include "AlloyRenderer.KeyholeFs.glsl"
void main()
{
    // a dissolving cutout/wind batch punches the same aperture DepthKeyFs does, so it stays in the
    // prepass (and keeps its screen-space AO) when the camera closes in
    if (CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
    oVisId = CxVisId(vDraw, gl_PrimitiveID);
    if (uAlphaCut < 0.0) return;
    if ((int(uDraw[vDraw].pbr.w) & 64) != 0) return;
    if (texture(uTex, vUV, uLodBias).a <= uAlphaCut) discard;
}
