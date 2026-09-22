#version 460 core
uniform vec3 uColor;
out vec4 frag;
void main() { frag = vec4(uColor, 1.0); }
