#version 460 core
#include "cx_partrec.glsl"
layout(std430, binding = 11) readonly buffer CxParticleIdx { uint partIdx[]; };

uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uEye;

out vec4 vColor;
out vec2 vUv;
out float vViewZ;
out vec3 vWorld;
out vec3 vNrm;
out float vSoft;
out vec3 vLight;            // #336: ambient + sun + point at this vertex; 1.0 when the draw is unlit
flat out int vType;

uniform int uLit;           // 0 = this draw is self-illuminated (additive, punch-through) or unlit
uniform vec3 uAreaAmbient;
uniform vec3 uAreaDiffuse;
uniform vec3 uAreaDir;

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
