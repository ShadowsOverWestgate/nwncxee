#version 460 core
// CX-51: Ssr's trace onto the frame, added under ONE/ONE (a viz replaces). Same size as the frame: texel for pixel.
layout(location = 0) out vec4 frag;
uniform sampler2D uSrc;
void main() { frag = texelFetch(uSrc, ivec2(gl_FragCoord.xy), 0); }
