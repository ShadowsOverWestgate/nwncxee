// One instanced quad per halo, corners from gl_VertexID, view-aligned. Reach follows vanilla:
// gone at flareradius. Occlusion is the fragment stage's, per pixel.
#version 460 core
layout(std430, binding = 5) readonly buffer CxHaloBuf { vec4 uHalos[]; };   // (pos, radius), (rgb, flareradius)
uniform mat4 uView;
uniform mat4 uProj;
uniform float uGain;
out vec2 vUV;
out vec3 vColor;
out float vViewZ;     // the light's view-space z (negative forward)
out float vRadius;    // world radius: the soft occlusion range

void main()
{
    vec4 a = uHalos[gl_InstanceID * 2], b = uHalos[gl_InstanceID * 2 + 1];
    vec3 vp = (uView * vec4(a.xyz, 1.0)).xyz;
    float d = length(vp);
    float reach = clamp(1.0 - d / max(b.w, 1e-3), 0.0, 1.0);
    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vUV = corner;
    vViewZ = vp.z;
    vRadius = a.w;
    float w = reach * uGain;
    vColor = b.rgb * w;
    vec3 pos = vp + vec3(corner * a.w, 0.0);
    gl_Position = w > 0.0 ? uProj * vec4(pos, 1.0) : vec4(2.0, 2.0, 2.0, 1.0);
}
