// The depth-copy fade every soft billboard stage runs -- particles, halos, rain, snow: the
// scene's view-space DISTANCE (positive) under a fragment, plus the stage's viz switch and output.
// The two coefficients come out of the PROJECTION MATRIX rather than from near/far uniforms -- it is
// the matrix this draw is actually using, so the linearisation cannot drift from the pass's own
// frustum the way a pair of separately-uploaded planes can.
uniform mat4 uProj;
uniform sampler2D uSceneDepth;
uniform int uViz;
out vec4 frag;

float CxSceneViewZ(vec2 uv)
{
    float ndc = texture(uSceneDepth, uv).r * 2.0 - 1.0;
    return uProj[3].z / (ndc + uProj[2].z);
}
