#version 460 core
in vec2 vUV;
uniform sampler2D uMask;
uniform float uFill;    // fill opacity
uniform float uRadius;  // outline thickness, px
uniform vec2 uUnjitter; // #389: the mask's camera jitter in uv, read back out (0 with TAA off)
out vec4 frag;
void main()
{
    vec2 ts = 1.0 / vec2(textureSize(uMask, 0));
    // The mask was drawn with the frame's JITTERED camera so its depth test holds; the overlay lands in
    // the accumulated (unjittered) frame, so the mask is sampled half a jitter back -- bilinear, so a
    // sub-pixel shift is a soft edge rather than a snap.
    vec2 uv = vUV + uUnjitter;
    vec4 m = texture(uMask, uv);
    vec3 accC = vec3(0.0);
    float accA = 0.0;
    for (int i = 0; i < 12; i++)
    {
        float a = 6.2831853 * float(i) / 12.0;
        vec2 d = vec2(cos(a), sin(a)) * ts * uRadius;
        vec4 s0 = texture(uMask, uv + d);
        vec4 s1 = texture(uMask, uv + d * 0.5);
        accC += s0.rgb + s1.rgb;   // rgb scales with coverage at bilinear edges: premultiplied for free
        accA += s0.a + s1.a;
    }
    float outline = smoothstep(0.06, 0.4, accA / 24.0) * (1.0 - m.a);
    float fillA = m.a * uFill;
    float alpha = max(fillA, outline);
    if (alpha <= 0.001) discard;
    vec3 fillC = m.rgb / max(m.a, 1e-4);
    vec3 outC = accC / max(accA, 1e-4);
    frag = vec4(outline > fillA ? outC : fillC, alpha);
}
