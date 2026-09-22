#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;
uniform int uLod;
uniform vec2 uTexel;    // 1 / size of the SOURCE level

vec3 T(vec2 o) { return textureLod(uSrc, vUV + o * uTexel, float(uLod)).rgb; }

void main()
{
    vec3 c = T(vec2(-1.0,  1.0)) + T(vec2(0.0,  1.0)) * 2.0 + T(vec2(1.0,  1.0))
           + T(vec2(-1.0,  0.0)) * 2.0 + T(vec2(0.0, 0.0)) * 4.0 + T(vec2(1.0, 0.0)) * 2.0
           + T(vec2(-1.0, -1.0)) + T(vec2(0.0, -1.0)) * 2.0 + T(vec2(1.0, -1.0));
    frag = vec4(c * (1.0 / 16.0), 1.0);
}
