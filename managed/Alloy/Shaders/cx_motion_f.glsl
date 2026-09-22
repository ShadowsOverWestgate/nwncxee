#version 460 core
in vec4 vCur;
in vec4 vPrev;
out vec2 frag;
uniform vec2 uJitter;    // this frame's sub-pixel jitter, NDC
uniform vec2 uSize;      // the render size, pixels
void main()
{
    if (abs(vPrev.w) < 1e-6 || abs(vCur.w) < 1e-6) { frag = vec2(0.0); return; }
    vec2 v = (vPrev.xy / vPrev.w - (vCur.xy / vCur.w - uJitter)) * 0.5 * uSize;
    if (any(isnan(v)) || any(isinf(v))) v = vec2(0.0);
    frag = clamp(v, -uSize, uSize);
}
