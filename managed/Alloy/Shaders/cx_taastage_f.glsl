uniform sampler2D uHist;     // this frame's resolved history (Prepare wrote it)
uniform float uSharp;        // post.taasharp: the unsharp mask's amount, 0 = the raw resolve
vec4 CxStage(vec4 c)
{
    ivec2 sz = textureSize(uHist, 0);
    ivec2 p = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
    vec3 h = texelFetch(uHist, p, 0).rgb;
    if (uSharp > 0.0)
    {
        vec3 n = texelFetch(uHist, clamp(p + ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p - ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p + ivec2(0, 1), ivec2(0), sz - 1), 0).rgb
               + texelFetch(uHist, clamp(p - ivec2(0, 1), ivec2(0), sz - 1), 0).rgb;
        h = max(h + (h * 4.0 - n) * (uSharp * 0.25), vec3(0.0));
    }
    return vec4(h, c.a);
}
