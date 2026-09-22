float CxNoise(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
vec3 CxDither(vec3 c, vec2 px, float lsb)
{
    if (lsb <= 0.0) return c;
    float r1 = CxNoise(px);
    float r2 = CxNoise(px + vec2(17.31, 47.13));
    return c + (r1 + r2 - 1.0) * lsb;
}
