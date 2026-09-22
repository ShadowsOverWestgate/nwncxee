#version 460 core
in vec2 vUV;
out vec4 frag;
uniform usampler2D uId;
uint CxHash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x;
}
void main()
{
    uint id = texelFetch(uId, ivec2(gl_FragCoord.xy), 0).r;
    if (id == 0u) { frag = vec4(0.0, 0.0, 0.0, 1.0); return; }
    uint h = CxHash(id);
    frag = vec4(vec3(h & 0xFFu, (h >> 8) & 0xFFu, (h >> 16) & 0xFFu) / 255.0 * 0.8 + 0.1, 1.0);
}
