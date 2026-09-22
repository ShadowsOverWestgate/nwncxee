#version 460 core
flat in int vDraw;
#include "cx_idout.glsl"
void main() { oVisId = CxVisId(vDraw, gl_PrimitiveID); }
