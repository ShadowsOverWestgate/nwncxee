#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
out vec2 vUV;
flat out float vAlpha;         // #126: the per-draw alpha multiplier, forwarded rather than re-read
out vec3 vWorld;               // the sky band's ray
#include "cx_feat_v.glsl"
#include "cx_bone_v.glsl"
void main()
{
    CxDraw d = uDraw[CxSlot(uDrawBase + gl_DrawID)];
    float anchorZ;
    vec4 pos = CxFeatPos(d, aPos, anchorZ);
    int bb = CxBoneBase(d);
    if (bb >= 0) pos = CxSkinMat(bb) * vec4(aPos, 1.0);
    // The skinned arm takes the DISPLACED expression, not a third one, and takes it first: a skinned
    // position cannot be the literal clip expression the prepass compiled, which is why #137 keeps
    // this class out of the prepass (ShaderFeatures.OutOfPrepass says so in one place for both).
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else if (!CxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
    else gl_Position = uVP * d.model * pos;
    vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
    vAlpha = d.rot.w;
    vWorld = (d.model * pos).xyz;
}
