#version 460 core
in vec3 vWorld;
in float vDepth;
flat in int vDraw;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
#include "cx_idout.glsl"

#include "cx_keyhole.glsl"
void main()
{
    if (CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
    oVisId = CxVisId(vDraw, gl_PrimitiveID);
}
