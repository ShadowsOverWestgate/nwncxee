#version 460 core
in vec2 vUV;
uniform sampler2D uSrc;
uniform vec2 uStep;
uniform int uFirst;
out vec2 frag;
void main()
{
    vec2 s = vec2(0.0); float wsum = 0.0;
    for (int i = -6; i <= 6; i++)
    {
        float w = exp(-float(i * i) / 18.0);
        vec2 m = texture(uSrc, vUV + uStep * float(i)).rg;
        if (uFirst != 0) m.y = m.x * m.x;
        s += w * m;
        wsum += w;
    }
    frag = s / wsum;
}
