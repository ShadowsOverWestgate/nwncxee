#version 460 core
uniform sampler2D uTex;
uniform float uAlphaCut;
#include "CasterPass.DitherLib.glsl"
in vec2 vUV;
flat in float vAlpha;
