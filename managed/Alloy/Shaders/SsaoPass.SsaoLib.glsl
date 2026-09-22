// Shared by both stages: view-space reconstruction off the frame depth and the pass's own
// projection -- the same two coefficients the particle fade linearises with, so nothing can drift.
uniform sampler2D uDepth;    // the frame depth after the prepass
uniform mat4 uProj;          // this pass's projection, raw engine bytes (see CxViewDist)

// Positive view distance: z_clip = z_v*M22 + M32, w_clip = -z_v, so z_v = -M32 / (ndc + M22).
float CxViewDist(float depth)
{
    return uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z);
}
// View-space position (camera at the origin looking down -z) of the pixel at uv.
vec3 CxViewPos(vec2 uv)
{
    float dist = CxViewDist(textureLod(uDepth, uv, 0.0).r);
    vec2 n = uv * 2.0 - 1.0;
    return vec3(n.x * dist / uProj[0].x, n.y * dist / uProj[1].y, -dist);
}
