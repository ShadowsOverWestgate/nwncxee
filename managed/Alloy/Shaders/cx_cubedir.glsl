// CX-46: the world direction through a cube texel, GL's face table inverted (glspec 8.13): face 0..5 =
// +X,-X,+Y,-Y,+Z,-Z, uv in [-1, 1] across the face with v down the rows. Unnormalised.
const float CX_PI = 3.14159265;
vec3 CxFaceDir(int face, vec2 uv)
{
    if (face == 0) return vec3(1.0, -uv.y, -uv.x);
    if (face == 1) return vec3(-1.0, -uv.y, uv.x);
    if (face == 2) return vec3(uv.x, 1.0, uv.y);
    if (face == 3) return vec3(uv.x, -1.0, -uv.y);
    if (face == 4) return vec3(uv.x, -uv.y, 1.0);
    return vec3(-uv.x, -uv.y, -1.0);
}
