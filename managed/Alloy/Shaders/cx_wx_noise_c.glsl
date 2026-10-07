// The weather noise tile (cx_wx_lib's uWxNoise), baked once per context: periodic value noise, each octave's
// lattice a whole number of cells across the 32 m tile so the tile repeats without a seam.
#version 460 core
layout(local_size_x = 8, local_size_y = 8) in;
#include "cx_wx_shape.glsl"
layout(rgba16f, binding = 0) writeonly uniform image2D uOut;
uniform int uSize;

const float CX_TILE_M = 32.0;

/// Value noise at <c>p</c> metres on a lattice of <c>cells</c> per tile, wrapped; <c>salt</c> decorrelates octaves.
float CxPNoise(vec2 p, float cells, float salt)
{
    vec2 q = p * (cells / CX_TILE_M);
    vec2 i = floor(q), f = fract(q);
    f = f * f * (3.0 - 2.0 * f);
    vec2 i0 = mod(i, cells), i1 = mod(i + 1.0, cells);
    return mix(mix(CxWxEdgeHash(vec2(i0.x, i0.y) + salt), CxWxEdgeHash(vec2(i1.x, i0.y) + salt), f.x),
               mix(CxWxEdgeHash(vec2(i0.x, i1.y) + salt), CxWxEdgeHash(vec2(i1.x, i1.y) + salt), f.x), f.y);
}

void main()
{
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(t, ivec2(uSize)))) return;
    vec2 p = (vec2(t) + 0.5) / float(uSize) * CX_TILE_M;
    imageStore(uOut, t, vec4(CxPNoise(p, 24.0, 0.0), CxPNoise(p, 100.0, 17.0), CxPNoise(p, 8.0, 41.0),
                             0.3 + 0.45 * CxPNoise(p, 54.0, 73.0) + 0.25 * CxPNoise(p, 192.0, 97.0)));
}
