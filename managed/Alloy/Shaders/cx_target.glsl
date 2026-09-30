
/// CX-93: the SPELL-TARGETING GROUND SHAPE -- the circle (or cone, or rectangle) a caster sees on the
/// ground while picking a target. Vanilla draws it as arithmetic in a fragment stage rather than as a
/// draw call, and Alloy used to splice the engine's own `inc_target.shd` here; this is Alloy's, from
/// analytic SDFs in the shape's own frame, with an outline at least a pixel and a half wide instead of
/// the stock 5cm band that vanishes at any camera distance.
/// The uniform NAMES are still the engine's: `AlloyRenderer.UploadTargetShape` writes the engine's own
/// global shader constants into them, so the values are the ones vanilla's own copy would have read.
/// This file is a cx_* library, so a content repo restyles the shape by shipping `cx_target.shd`.

#define CX_TGT_NONE    0
#define CX_TGT_SPHERE  1
#define CX_TGT_RECT    2
#define CX_TGT_CONE    3
#define CX_TGT_HSPHERE 4

/// Object type 0: the cursor is over nothing at all, which is the engine's own "draw no shape".
#define CX_TGT_OBJ_NONE 0

/// The outline's world-space half-thickness floor, the engine's own 5cm. Widened to 1.5 fragments
/// where that is thinner, which is what keeps the rim a line rather than a dotted crawl up close.
#define CX_TGT_EDGE 0.05
/// The interior's weight, the engine's: the shape reads as a wash with a bright rim.
#define CX_TGT_FILL 0.1
/// The dotted out-of-range line's half-width, and the period of its dashes in metres.
#define CX_TGT_DASH_W 0.025
#define CX_TGT_DASH_P 1.0

uniform int targetShape;               // CX_TGT_*
uniform int targetShapeOriginOnSelf;   // 1 = the shape grows from the caster, not from the cursor
uniform int targetObjectType;          // CX_TGT_OBJ_NONE = the cursor is over nothing
uniform float targetShapeSizeX;        // radius / length / slant height, by shape
uniform float targetShapeSizeY;        // width (rect) or outer radius (hsphere)
uniform float targetShapeSpellRange;   // how far the caster can reach; 0 = no range ring, no dotted line
uniform vec4 targetShapeColour;        // DISPLAY space, .a the overall strength
uniform vec3 targetWorldCoords;        // the ground point under the mouse

/// The engine's throb: one slow sine on the world timer, 0.3 at the trough and 1.1 at the peak.
float CxTargetThrob()
{
    return 0.3 + (1.0 + sin(float(uTimeOfDay) / 250.0)) * 0.4;
}

/// Signed distance to a box, negative inside (iquilezles.org/articles/distfunctions).
float CxSdBox(vec3 p, vec3 b)
{
    vec3 q = abs(p) - b;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

/// Signed distance to a cone of slant height <c>h</c> and half-angle (sin, cos) <c>sc</c>, apex at the
/// origin and opening toward -y, in the (radius, axis) plane (iquilezles.org/articles/distfunctions).
float CxSdCone(vec2 w, vec2 sc, float h)
{
    vec2 q = h * vec2(sc.x / sc.y, -1.0);
    vec2 a = w - q * clamp(dot(w, q) / dot(q, q), 0.0, 1.0);
    vec2 b = w - q * vec2(clamp(w.x / q.x, 0.0, 1.0), 1.0);
    float k = sign(q.y);
    return sqrt(min(dot(a, a), dot(b, b))) * sign(max(k * (w.x * q.y - w.y * q.x), k * (w.y - q.y)));
}

/// The 60-degree cone every NWN cone spell projects: half-angle 30 degrees as (sin, cos).
#define CX_TGT_CONE_SC vec2(0.5, 0.86602540)

/// Coverage of the region an SDF is negative in, one fragment wide at the boundary.
float CxTargetCover(float sd, float aa)
{
    return 1.0 - smoothstep(-aa, aa, sd);
}

/// The shape's weight at this fragment: 0 outside, CX_TGT_FILL across the interior, the throb along
/// the rim, and the dotted line back to the caster when the cursor is out of reach.
float CxTargetStrength()
{
    if (targetObjectType == CX_TGT_OBJ_NONE) return 0.0;
    if (targetShape == CX_TGT_NONE && targetShapeSpellRange <= 0.0) return 0.0;

    // Toward the CASTER, which is the axis every shape is laid out along: +y in shape space, so a cone
    // or a rectangle grows toward the cursor at -y.
    vec3 toCaster = uPlayerPos - targetWorldCoords;
    float reach = length(toCaster);
    vec3 axis = reach > 1e-4 ? toCaster / reach : vec3(0.0, 1.0, 0.0);
    vec2 axis2 = length(axis.xy) > 1e-4 ? normalize(axis.xy) : vec2(0.0, 1.0);
    float over = max(reach - targetShapeSpellRange, 0.0);

    vec3 origin = targetShapeOriginOnSelf != 0 ? uPlayerPos : targetWorldCoords;
    if (targetShapeOriginOnSelf != 0)
    {
        // Pushed toward the cursor: a rectangle by half its length, so its near end sits on the caster
        // rather than its centre, and an out-of-reach cursor by the overshoot, so the shape is drawn
        // where the caster would have to stand for that cursor to be exactly at maximum range.
        origin -= axis * ((targetShape == CX_TGT_RECT ? targetShapeSizeX * 0.5 : 0.0) + over);
    }

    vec3 d = vWorld - origin;
    vec3 lp = vec3(axis2.y * d.x - axis2.x * d.y, dot(d.xy, axis2), d.z);

    float sd = 1e9;
    if (targetShape == CX_TGT_SPHERE)
        sd = length(lp) - targetShapeSizeX;
    else if (targetShape == CX_TGT_RECT)
        sd = CxSdBox(lp, vec3(targetShapeSizeY * 0.5, targetShapeSizeX * 0.5, max(1.0, abs(toCaster.z))));
    else if (targetShape == CX_TGT_CONE)
        sd = CxSdCone(vec2(length(lp.xz), lp.y), CX_TGT_CONE_SC, targetShapeSizeX);
    else if (targetShape == CX_TGT_HSPHERE)
    {
        float r = length(lp);
        sd = max(r - targetShapeSizeY, targetShapeSizeX - r);
    }

    float throb = CxTargetThrob();
    float s = 0.0;
    if (sd < 1e8)
    {
        float aa = max(fwidth(sd), 1e-4);
        float edge = max(CX_TGT_EDGE, aa * 1.5);
        // The rim reaches full weight `edge` inside the boundary and falls to the fill further in.
        s = CxTargetCover(sd, aa) * mix(CX_TGT_FILL, throb, smoothstep(-edge - aa, -edge + aa, sd));
    }

    if (targetShapeSpellRange > 0.0 && over > 0.0)
    {
        // The dashed run from the caster to the furthest point they could cast at, dashed on the world
        // distance so the dashes hold their length whatever the camera does.
        vec3 dl = vWorld - uPlayerPos + axis * (over * 0.5);
        vec3 lq = vec3(axis2.y * dl.x - axis2.x * dl.y, dot(dl.xy, axis2), dl.z);
        float sq = CxSdBox(lq, vec3(CX_TGT_DASH_W, over * 0.5, max(1.0, abs(toCaster.z))));
        float aa = max(fwidth(sq), 1e-4);
        float dash = step(mod(length(vWorld - uPlayerPos), CX_TGT_DASH_P), CX_TGT_DASH_P * 0.5);
        s = max(s, CxTargetCover(sq, aa) * dash * throb);
    }
    return s;
}

/// Lay the shape over a DISPLAY-space colour, at the strength CxTargetStrength answered with. The mix
/// weight is per channel, which is how the engine's own colour vector tints the white it mixes toward.
void CxTargetApply(inout vec4 FragmentColor, float fStrength)
{
    if (fStrength <= 0.0) return;
    FragmentColor.rgb = mix(FragmentColor.rgb, vec3(1.0), targetShapeColour.rgb * fStrength * targetShapeColour.a);
}

/// #346/#347: the three display-space overlays, in ONE encode bracket and in inc_standard.shd's own
/// order -- noir desat, then racial vision, then the targeting shape. The engine applies all three
/// after RevertColorSpace; the same arithmetic on linear values is a different, stronger effect
/// (#222's trap), and bracketing them together costs one encode instead of three.
/// <para>The shape goes last on purpose, and the content says why: it is UI drawn on the ground, and
/// a caster has to read its colour over a monochrome scene.</para>
vec3 CxDisplayOverlays(vec3 lin)
{
    float strength = CxTargetStrength();
    vec4 c = vec4(pow(max(lin, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0);
    // The content's display-space passes first (SoW puts its desaturation and racial vision here),
    // then the targeting shape last -- inc_standard.shd's own order, and its own reason: the shape is
    // UI drawn on the ground, and a caster has to read its colour over a monochrome scene.
    c.rgb = CxDisplayHook(c.rgb);
    CxTargetApply(c, strength);
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
