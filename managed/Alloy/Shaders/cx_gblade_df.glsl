#version 460 core
// CX-24: blade depth (sun cascades). Opaque geometry: nothing to decide per fragment.
// CX-48: the same stage under CX_MOTION writes the blade's own velocity instead -- still nothing to decide,
// since a blade has no cutout.
#ifdef CX_MOTION
in vec4 vCur;
in vec4 vPrev;
out vec4 frag;
#include "cx_motionout.glsl"
#endif
void main()
{
#ifdef CX_MOTION
    frag = vec4(CxMotionVec(vCur, vPrev), 0.0, 0.0);
#endif
}
