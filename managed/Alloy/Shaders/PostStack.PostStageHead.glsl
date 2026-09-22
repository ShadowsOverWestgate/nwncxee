#version 460 core
in vec2 vUV;
out vec4 frag;
uniform sampler2D uSrc;      // the previous stage's output (or the resolve's), DISPLAY space
uniform sampler2D uDepth;    // the frame's depth, for a stage that wants it
uniform float uDither;       // one LSB of the 8-bit target when this stage writes it, else 0
#include "PostChain.PostDitherLib.glsl"
