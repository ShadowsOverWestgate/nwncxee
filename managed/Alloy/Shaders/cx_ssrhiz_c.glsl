#version 460 core
// CX-51: the reflection trace's hi-Z pyramid. Level 0 is the frame depth as INVERSE view depth (1/w, which
// is linear in screen space, so the march interpolates it exactly); each level above is the MAX over the
// footprint of the one below -- the CLOSEST surface in the cell, which a ray must reach before it can hit
// anything there. 3x3 rather than 2x2 so an odd-sized level's last row and column are never dropped.
layout(local_size_x = 8, local_size_y = 8) in;
layout(binding = 2) uniform sampler2D uSrc;          // the window depth (level 0) or this pyramid (above)
layout(r32f, binding = 0) writeonly uniform image2D uDst;
uniform int uLevel;
uniform vec2 uNf;                                     // the frame's near/far
void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(p, imageSize(uDst)))) return;
    float k = 0.0;
    if (uLevel == 0)
    {
        float d = texelFetch(uSrc, p, 0).r * 2.0 - 1.0;
        k = (uNf.y + uNf.x - d * (uNf.y - uNf.x)) / (2.0 * uNf.x * uNf.y);
    }
    else
    {
        ivec2 lim = textureSize(uSrc, uLevel - 1) - ivec2(1), s = p * 2;
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                k = max(k, texelFetch(uSrc, min(s + ivec2(x, y), lim), uLevel - 1).r);
    }
    imageStore(uDst, p, vec4(k));
}
