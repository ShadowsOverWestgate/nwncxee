// CX-93: the client's Vibrance video option, Alloy's own. Vanilla serves it from inc_postpr_vibr.shd
// inside the fsfbpostpr chain, which Alloy used to host by splicing the stock text in; this is a stage of
// the display-space stack instead. The maths is the stock file's, because there is nothing to modernise
// about it -- it is the definition of the effect.
uniform float uVibrance;     // the client's Vibrance amount, the engine's global float param 7

float CxVibLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

vec4 CxStage(vec4 c)
{
    float l = CxVibLuma(c.rgb);
    if (l <= 0.0 || uVibrance == 0.0) return c;
    float hi = max(c.r, max(c.g, c.b));
    float lo = min(c.r, min(c.g, c.b));
    // Vibrance, not saturation: the weaker channels are pushed away from the strongest HARDEST where the
    // pixel is already grey, so an already-saturated pixel is left where it is.
    float sat = hi > 0.0 ? (hi - lo) / hi : 0.0;
    vec3 o = clamp(vec3(hi) + (c.rgb - hi) * (1.0 + (1.0 - sat) * (1.0 - sat) * uVibrance), 0.0, 1.0);
    // ...and the luminance is put back, so the slider changes colour and not exposure.
    c.rgb = o * (l / max(CxVibLuma(o), 1e-4));
    return c;
}
