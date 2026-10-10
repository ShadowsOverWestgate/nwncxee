// CX-48: the velocity convention for the stages that correct the field while they draw -- grass and
// particles -- one text, so a late writer cannot disagree with cx_motioncam_f/cx_motion_f about it:
// prev minus current, in RENDER pixels, the current position UNJITTERED, bounded and never NaN.
uniform vec2 uJitter;      // this frame's sub-pixel jitter, NDC (0 with TAA off)
uniform vec2 uSize;        // the render size, pixels

vec2 CxMotionVec(vec4 cur, vec4 prev)
{
    if (abs(prev.w) < 1e-6 || abs(cur.w) < 1e-6) return vec2(0.0);
    vec2 v = (prev.xy / prev.w - (cur.xy / cur.w - uJitter)) * 0.5 * uSize;
    if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
    return clamp(v, -uSize, uSize);
}
