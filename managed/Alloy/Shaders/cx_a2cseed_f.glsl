#version 460 core
// CX-189: the coverage layer's seed -- the lit frame and its depth into every sample, so the samples the
// alpha-to-coverage draw leaves uncovered still hold the background and a box resolve IS the composite.
uniform sampler2D uHdr;
uniform sampler2D uDepth;
out vec4 frag;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    frag = texelFetch(uHdr, p, 0);
    gl_FragDepth = texelFetch(uDepth, p, 0).r;
}
