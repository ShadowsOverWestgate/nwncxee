
// $VANILLA inc_target
float GetTargetGroundHighlightStrength() { return 0.0; }
void ApplyTargetGroundHighlight(inout vec4 FragmentColor, float fStrength) { }
// $VANILLA-END

/// #346/#347: the three display-space overlays, in ONE encode bracket and in inc_standard.shd's own
/// order -- noir desat, then racial vision, then the targeting shape. The engine applies all three
/// after RevertColorSpace; the same arithmetic on linear values is a different, stronger effect
/// (#222's trap), and bracketing them together costs one encode instead of three.
/// <para>The shape goes last on purpose, and the content says why: it is UI drawn on the ground, and
/// a caster has to read its colour over a monochrome scene.</para>
vec3 CxDisplayOverlays(vec3 lin)
{
    float strength = GetTargetGroundHighlightStrength();
    vec4 c = vec4(pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0);
    // The content's display-space passes first (SoW puts its desaturation and racial vision here),
    // then the ENGINE's targeting shape last -- inc_standard.shd's own order, and its own reason:
    // the shape is UI drawn on the ground, and a caster has to read its colour over a monochrome
    // scene.
    c.rgb = CxDisplayHook(c.rgb);
    ApplyTargetGroundHighlight(c, strength);
    return pow(max(c.rgb, vec3(0.0)), vec3($SRGB$));
}

/// #347: the CONTENT half alone, for a stage whose stock counterpart runs the hooks but not the
/// targeting shape. `fswater.shd` is the case and the reason: its tail calls ApplyNoirDesat and
/// ApplyRacialVision and stops there, so adding the shape to Alloy's water would paint a reticle
/// vanilla never painted. Same encode bracket, one term short.
vec3 CxDisplayContent(vec3 lin)
{
    vec3 c = pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$));
    c = CxDisplayHook(c);
    return pow(max(c, vec3(0.0)), vec3($SRGB$));
}
