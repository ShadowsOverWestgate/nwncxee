// CX-191: mip-level alpha compensation for the cutout test (Ben Golus, "Anti-aliased Alpha Test"). A mip's
// alpha is the AVERAGE of the texels under it, so a leaf's coverage shrinks level by level and distant foliage
// thins out and shimmers as the level changes. The test reads the alpha scaled up by the level the fragment
// samples; the authored mips and the alpha that is drawn are untouched. du1/du2 are dFdx/dFdy of the uv,
// taken by the caller ahead of its first discard; bias is the lod bias the caller samples with. The level
// stops at the last mip: past it the sampler stops averaging, and scaling on would fill the whole quad.
uniform float uAlphaMip;        // alloy.cutmip: 0 = the plain test
float CxAlphaMip(float a, vec2 du1, vec2 du2, vec2 texSize, float bias)
{
    if (uAlphaMip <= 0.0) return a;
    vec2 dx = du1 * texSize, dy = du2 * texSize;
    float lod = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8)) + bias;
    return a * (1.0 + clamp(lod, 0.0, log2(max(texSize.x, texSize.y))) * uAlphaMip);
}
