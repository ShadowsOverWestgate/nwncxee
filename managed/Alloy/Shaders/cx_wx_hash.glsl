// The instance hash both attribute-less weather stages ride: the precipitation lattice (cx_wx_vmain) and
// the splash disc (cx_wx_spmain) are each a pure function of gl_InstanceID, so this is the only state
// either of them has. Three decorrelated 0..1 values from one integer -- xorshift then an fmix finisher.

vec3 CxHash3(uint n)
{
    n ^= n << 13u; n ^= n >> 17u; n ^= n << 5u;
    uvec3 k = uvec3(n, n * 16807u, n * 48271u);
    k ^= k >> 16u; k *= 0x7feb352du; k ^= k >> 15u; k *= 0x846ca68bu; k ^= k >> 16u;
    return vec3(k) / 4294967296.0;
}
