#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 2) in vec3 aNrm;   // the mesh arena's static format carries the normal here

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
uniform vec4 uProjSrc;   // xyz = extrusion direction in world space (w < 0: directional)
uniform float uZClip;    // projectionWorldZClip

// Constant value inherited from old NWN (fMinOffset, vs_beamvol.shd).
const float CX_BEAM_MIN_OFFSET = 0.1;

void main()
{
    vec3 wp = (uModel * vec4(aPos, 1.0)).xyz;
    vec3 vp = (uView * vec4(wp, 1.0)).xyz;
    vec3 dir = uProjSrc.xyz;
    // CX-120: the engine bakes fProjectionFrontal per silhouette vertex; here it is derived from the normal.
    // A surface the light travels INTO faces it, so its outward normal opposes the travel direction.
    vec3 wn = mat3(uModel) * aNrm;
    float frontal = -dot(normalize(wn), dir);
    float off = CX_BEAM_MIN_OFFSET;
    if (frontal <= 0.0)
        off = max(CX_BEAM_MIN_OFFSET, (uZClip - wp.z) / dir.z);
    vp += off * (mat3(uView) * dir);
    gl_Position = uProj * vec4(vp, 1.0);
}
