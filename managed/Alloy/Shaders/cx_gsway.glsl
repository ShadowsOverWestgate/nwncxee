// CX-48: the wind sway, and the previous frame's clock to run it on again. One text for both grass vertex
// paths and for both frames: last frame's blade is this expression at last frame's clock, which is the whole
// of what gives a swaying blade a motion vector of its own.
uniform int uPrevTimeOfDay;    // last frame's uTimeOfDay; equal to it on a reset frame, so the step is zero

// the animation clock of the previous frame, wrapped exactly as CxClock wraps this one
float CxPrevClock() { return float(uPrevTimeOfDay % 3600000); }

// Two frequencies so the field ripples instead of pulsing; the phase comes from the blade's world position,
// so neighbouring blades lean together and distant ones do not.
float CxGrassSway(float t, float phase)
{
    return (sin(t * 1.7 + phase) + 0.4 * sin(t * 3.9 + phase * 2.3)) * 0.5;
}

#ifdef CX_MOTION
uniform mat4 uPrevVP;          // last frame's view-projection, unjittered
out vec4 vCur;                 // this vertex's clip position, and last frame's
out vec4 vPrev;
#endif
