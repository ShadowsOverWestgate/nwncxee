#version 460 core
layout(location = 0) in vec4 aPos;
layout(location = 1) in float aFrontal;   // fProjectionFrontal: > 0 = the cap facing the light

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec4 uProjSrc;   // xyz = extrusion direction in world space (w < 0: directional)
uniform float uZClip;    // projectionWorldZClip

// Constant value inherited from old NWN (fMinOffset, vs_beamvol.shd).
const float CX_BEAM_MIN_OFFSET = 0.1;

void main()
{
    vec3 wp = (uModel * aPos).xyz;
    vec3 vp = (uView * vec4(wp, 1.0)).xyz;
    vec3 dir = uProjSrc.xyz;
    float off = CX_BEAM_MIN_OFFSET;
    if (aFrontal <= 0.0)
        off = max(CX_BEAM_MIN_OFFSET, (uZClip - wp.z) / dir.z);
    vp += off * (mat3(uView) * dir);
    gl_Position = uProj * vec4(vp, 1.0);
}
