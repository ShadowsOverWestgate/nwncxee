#version 460 core
layout(location = 0) in vec3 aPos;
#include "cx_drawbuf.glsl"
uniform mat4 uVP;
uniform mat4 uPrevVP;    // last frame's view-projection, unjittered
uniform int uDrawBase;
invariant gl_Position;
out vec4 vCur;
out vec4 vPrev;
void main()
{
    CxDraw d = uDraw[CxSlot(uDrawBase + gl_DrawID)];
    gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vCur = gl_Position;
    vPrev = uPrevVP * d.prev * vec4(aPos, 1.0);
}
