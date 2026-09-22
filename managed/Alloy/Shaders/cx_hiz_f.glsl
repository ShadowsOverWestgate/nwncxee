#version 460 core
uniform sampler2D uSrc;
uniform int uLevel;
in vec2 vUV;
out vec4 oCol;
void main()
{
    ivec2 lim = textureSize(uSrc, uLevel) - ivec2(1);
    ivec2 s = ivec2(gl_FragCoord.xy) * 2;
    float m = 0.0;
    for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            m = max(m, texelFetch(uSrc, min(s + ivec2(x, y), lim), uLevel).r);
    oCol = vec4(m);
}
