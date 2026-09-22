// The texel both fragment stages read: the atlas cell, repeated vTuft times across the
// blade. Derivatives are the cell's own, scaled, so the mip level does not jump at a copy seam.
in vec2 vCellUv;
flat in vec2 vCellBase;
flat in float vTuft;
vec4 CxGrassTexel()
{
    vec2 gx = dFdx(vCellUv) * vec2(vTuft, 1.0) * 0.5, gy = dFdy(vCellUv) * vec2(vTuft, 1.0) * 0.5;
    vec2 uv = vCellBase + vec2(fract(vCellUv.x * vTuft), vCellUv.y) * 0.5;
    // The cell edge, inset by one texel OF THE MIP THIS FOOTPRINT SELECTS: a coarse level's filter
    // reaches across the atlas seam otherwise, and the card's top row takes the next cell's colour --
    // the black dashes riding the far field's tips.
    vec2 inset = exp2(textureQueryLod(uTex, uv).x) / vec2(textureSize(uTex, 0));
    uv = clamp(uv, vCellBase + inset, vCellBase + 0.5 - inset);
    // #371/#499: the soften darkening is fixed where the mips are BUILT (TexScan.BuildMips averages
    // colour alpha-weighted); an in-shader division by the level's alpha over-brightened straight-alpha
    // atlases 1.25x field-wide and 2x on the fringe, which read as fullbright, jagged grass.
    return textureGrad(uTex, uv, gx, gy);
}
