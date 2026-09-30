// CX-93: the client's Sharpen video option, Alloy's own. Vanilla serves it from inc_postpr_shrp.shd
// inside the fsfbpostpr chain, which Alloy used to host by splicing the stock text in; this is a stage
// of the display-space stack instead, like gamma and the cel pass.
// The asymmetry IS the effect and is kept: the darker side of a luma edge is darkened and the whole
// frame lifted back up, so the image gains local contrast without haloing bright edges. What is NOT
// kept is the stock file's own "minimal performance impact AA" (Alloy has SMAA and TAA ahead of it) and
// its per-fragment depth and fog falloff -- the stock file's own MOBILE branch drops both for the two
// constants below, and its fogStart/fogEnd could never have resolved here anyway.

// inc_postpr_shrp.shd's own dials, name for name: fSharpenModifier, fSharpenTreshold,
// fSharpenBrightness, fSharpenRange, and the MOBILE branch's fSharpenDepthModifier.
const float CX_SH_AMOUNT = 0.065;
const float CX_SH_THRESH = 0.004;
const float CX_SH_LIFT   = 0.3;
const float CX_SH_RANGE  = 0.15;
const float CX_SH_DEPTH  = 0.75;

float CxSharpLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

vec4 CxStage(vec4 c)
{
    vec2 px = 1.0 / vec2(textureSize(uSrc, 0));
    float l = CxSharpLuma(c.rgb);
    // The strongest luma STEP to a neighbour over the four axes, diagonals at 1/sqrt(2). The opposite
    // side's magnitude is subtracted from the winner: a pair that is brighter on both sides is a thin
    // line rather than an edge, and sharpening it makes it crawl.
    float best = 0.0;
    for (int i = 0; i < 4; i++)
    {
        vec2 o = i == 0 ? vec2(px.x, 0.0) : i == 1 ? vec2(0.0, px.y)
               : i == 2 ? vec2(-px.x, px.y) : -px;
        float a = CxSharpLuma(texture(uSrc, vUV + o).rgb) - l;
        float b = CxSharpLuma(texture(uSrc, vUV - o).rgb) - l;
        best = max(best, (a > b ? a - abs(b) : b - abs(a)) * (i > 1 ? 0.707 : 1.0));
    }
    float m = CX_SH_LIFT;
    float e = best - CX_SH_THRESH;
    if (e > 0.0 && e < CX_SH_RANGE)
    {
        float t = max(1.0 - e * (2.0 / CX_SH_RANGE), 0.0);
        m -= 1.0 - t * t;
    }
    // Divided by the fragment's own luminance, so the step is an absolute one rather than a proportion:
    // hue and saturation survive it. The 0.2 floor is the stock file's, against precision in the blacks.
    m /= max(l, 0.2);
    c.rgb = clamp(c.rgb * (1.0 + CX_SH_DEPTH * CX_SH_AMOUNT * m), 0.0, 1.0);
    return c;
}
