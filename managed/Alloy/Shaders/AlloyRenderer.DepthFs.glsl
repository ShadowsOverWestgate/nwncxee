#version 460 core
flat in int vDraw;
#include "IdTarget.OutDecl.glsl"
void main() { oVisId = CxVisId(vDraw, gl_PrimitiveID); }
