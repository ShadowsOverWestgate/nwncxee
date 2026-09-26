
/// (Specularity, Roughness, Metallicness) -- declared wins, then the map, then the generated floor.
vec3 CxSynthPbr(vec3 declared, int slots, float envLevel, float specTexel, float roughTexel)
{
    float envShiny = min(envLevel * CX_ENV_SPEC_SCALE, 1.0);
    float envRough = min(envLevel * CX_ENV_ROUGH_SCALE, 1.0);
    bool sm = (slots & CX_SLOT_SPEC) != 0;
    float spec = declared.x;
    if (spec <= 0.0)
    {
        if (sm) spec = specTexel;
        // #121: env map + no specular map = legacy "shiny" content, and this is the engine's own
        // translation of it (inc_material.shd, "mainly for the sake of enabling specularity on
        // legacy content").
        else if (envShiny > 0.0) spec = mix(CX_SPEC_MIN, CX_ENV_SPEC_MAX, envShiny);
        else spec = CX_SPEC_MIN;
    }
    float rough = declared.y;
    if (rough <= 0.0)
    {
        // A ROUGHNESS MAP IS DECLARED CONTENT: it is taken whole, and the env curve below never
        // touches it -- stock applies that curve only where the roughness was generated
        // (inc_material.shd puts the min() inside the specular-map branch, not after it).
        if ((slots & CX_SLOT_ROUGH) != 0) rough = roughTexel;
        else
        {
            // a specular map with no roughness map is pre-PBR content: translate rather than trust it
            rough = sm ? mix(CX_ROUGH_MIN, CX_ROUGH_MAX, (1.0 - spec) * (1.0 - spec))
                       : CX_ROUGH_MAX;
            // ...and the env map drives it toward the same floor, on TOP of that translation. With
            // no maps at all the min() is stock's bare assignment: the mix never exceeds the max.
            if (envLevel > 0.0)
                rough = min(rough, mix(CX_ROUGH_MAX, CX_ROUGH_MIN, envRough));
        }
    }
    float metal = declared.z;
    // the old gen keys this curve on whether a SPECULAR map is present -- 3.0/0.2 with one, 10.0/0.04
    // without (fMetallicnessModifier * fGeneratedMetallicnessOffset). Collapsing them to one branch
    // would generate visibly different metal for content that declares Specularity but ships no map.
    if (metal <= 0.0)
        metal = clamp((sm ? CX_METAL_SCALE_MAP : CX_METAL_SCALE_BARE) * spec
                      - (sm ? CX_METAL_BIAS_MAP : CX_METAL_BIAS_BARE), 0.0, 1.0);
    return vec3(spec, rough, metal);
}

/// The material's specular colour: CustomSpecularColor where declared (w = the CPU-side "not black"
/// test, which is stock's own sentinel), else generated off the albedo.
vec3 CxSpecColor(vec4 declared, vec3 base, float metal, int slots)
{
    if (declared.w > 0.0) return CxLinear(declared.rgb);
    // stock takes plain white as the non-metal base once a height map is bound -- the peak-channel
    // rescue below is for flat content, and a height-mapped material already carries its crevices
    if ((slots & CX_SLOT_HEIGHT) != 0) return mix(vec3(1.0), base, metal);
    // non-metals should be white, but dark diffuse texels stand in for crevices, so the peak channel
    // scales it -- inc_material.shd's own reasoning, in its own words
    return mix(vec3(min(1.0, max(base.r, max(base.g, base.b)) / CX_MID_GREY)), base, metal);
}

// ---- ADR 0026: the modern material model ----------------------------------------------------------
// Every roughness below is in the ENGINE's units (alpha = rough), whatever the content declared it in:
// the translation happens once, at the material, so the loop, the lightmap, the glossy tests and every
// weather splice keep reading one convention.

/// The dielectric reflectance at normal incidence a glTF material assumes (IOR 1.5) -- and the stock
/// fSpecularityMin, so a physical and a legacy dielectric start from the same F0.
const float CX_DIELECTRIC_F0 = 0.04;
/// The narrowest lobe a declared roughness may ask for. GGX peaks at 1/alpha^2 times the irradiance, and
/// the colour target is RGBA16F: 0.02 keeps a sunlit mirror at 2500, far from the 65504 ceiling and from
/// the bloom fireflies of an infinite peak. In perceptual units that is ~0.14: anything declared below it
/// draws as it.
const float CX_ALPHA_MIN = 0.02;
/// glTF's clearcoat and sheen roughness defaults are 0 -- a perfect mirror coat, a knife-edge sheen -- which
/// is right for a renderer with a real prefilter and wrong for a 128^2 probe; these are the defaults an
/// .mtr that names the layer but not its roughness gets (perceptual units).
const float CX_COAT_ROUGH_DEFAULT = 0.15, CX_SHEEN_ROUGH_DEFAULT = 0.5;

/// Does the material declare the glTF vocabulary (CxMetallic, CxRoughness, or a CxOrm map that is bound)?
/// `m0` is the record's ext0 (-1 = not declared), `ormOn` 1 when texture11 carries a packed ORM map.
bool CxPhysical(vec4 m0, float ormOn) { return m0.x >= 0.0 || m0.y >= 0.0 || ormOn > 0.5; }

/// ADR 0026: the glTF metal/rough view of a physical material, returned as (Specularity, Roughness,
/// Metallicness) in the ENGINE's units and the specular colour beside it. glTF's own rules: the factor
/// multiplies the map, a factor with no map stands alone, a map with no factor is taken whole. Two
/// departures, both toward the legacy view: no metallic factor and no map is a dielectric (glTF's default
/// factor of 1 would turn "declares only CxRoughness" into chrome), and no roughness at all keeps the
/// roughness the stock parameters already gave it. Perceptual roughness squares into the engine's alpha.
vec3 CxPhysicalPbr(vec4 m0, float ormOn, vec3 orm, vec3 legacy, vec3 base, out vec3 specColor)
{
    bool orms = ormOn > 0.5;
    float metal = clamp((m0.x >= 0.0 ? m0.x : orms ? 1.0 : 0.0) * (orms ? orm.b : 1.0), 0.0, 1.0);
    float rough = legacy.y;
    if (m0.y >= 0.0 || orms)
    {
        float p = clamp((m0.y >= 0.0 ? m0.y : 1.0) * (orms ? orm.g : 1.0), 0.0, 1.0);
        rough = max(p * p, CX_ALPHA_MIN);
    }
    // the loop's Fresnel is scalar (mix(spec, 1, Fc)) with the colour outside it: F0's peak channel is the
    // scalar, its hue the colour
    vec3 f0 = mix(vec3(CX_DIELECTRIC_F0), base, metal);
    float spec = max(max(f0.r, f0.g), max(f0.b, 1e-4));
    specColor = f0 / spec;
    return vec3(spec, rough, metal);
}

/// Karis' analytic fit of the split-sum DFG integral: (scale, bias) on F0 for the directional albedo of a
/// GGX lobe. `perceptual` is sqrt(alpha).
vec2 CxEnvBrdf(float NoV, float perceptual)
{
    const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = perceptual * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
    return vec2(-1.04, 1.04) * a004 + r.zw;
}

/// Multiple-scattering compensation (Fdez-Aguera 2019): a single-scatter GGX lobe loses the light that
/// bounces between microfacets, so rough metal reads dark. 1 + F0 (1/Ess - 1), Ess the lobe's directional
/// albedo at F0 = 1. Per channel: a coloured metal loses the most in the channel it reflects most.
vec3 CxMultiScatter(vec3 f0, float NoV, float perceptual)
{
    vec2 ab = CxEnvBrdf(NoV, perceptual);
    return 1.0 + f0 * (1.0 / max(ab.x + ab.y, 0.05) - 1.0);
}

/// Specular anti-aliasing (Kaplanyan & Hoffman 2016, Tokuyoshi's bounded form): the alpha^2 a
/// highlight must widen by, from the screen-space variance of the surface normal, so a lobe narrower
/// than a pixel does not crawl. The geometric normal, and taken ahead of every discard: a cutout kills
/// quad lanes, and a derivative across a killed lane is undefined.
float CxSpecAAKernel(vec3 n)
{
    vec3 dx = dFdx(n), dy = dFdy(n);
    return min(0.5 * (dot(dx, dx) + dot(dy, dy)), 0.18);
}

/// Lagarde's specular occlusion: how much of the ambient reflection an occlusion value `ao` leaves, which
/// is less than `ao` itself for a glossy lobe looking into a crevice and more for a rough one.
float CxSpecOcclusion(float NoV, float ao, float rough)
{
    return clamp(pow(NoV + ao, exp2(-16.0 * rough - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

/// The probe's directional shape over the area ambient: how much brighter the captured scene is toward N
/// than away from it, 2a / (a + b) in [0, 2]. Normalised against its own opposite, so the authored ambient
/// keeps its level and only gains a direction -- sky above, ground below, the lit wall beside.
float CxAmbientShape(vec3 N)
{
    float lod = max(uProbeMips - 2.0, 0.0);
    const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
    float a = dot(textureLod(uProbe, N, lod).rgb, lum), b = dot(textureLod(uProbe, -N, lod).rgb, lum);
    return 2.0 * a / max(a + b, 1e-4);
}

/// Per-fragment material state CxLoopSpec reads, set by the lit main before the first light.
vec3 cxSpecScale;               // specColor (engine model only) x view-side normalisation x multiscatter x coat transmission
vec3 cxF0; float cxSpec;        // modern model: per-channel F0 and the scalar CxSpecLobe's Fresnel was built on
float cxCoat, cxCoatRough;      // clear-coat weight and alpha; 0 weight = no coat
vec3 cxSheen; float cxSheenRough;   // sheen colour (linear) and alpha; 0 alpha = no sheen

/// The clear coat's lobe per unit irradiance: GGX, Kelemen's visibility, a fixed 0.04 dielectric
/// (KHR_materials_clearcoat). Pi-less, like every lobe in the lit stage.
float CxCoatLobe(vec3 N, vec3 V, vec3 L, float a)
{
    vec3 H = normalize(V + L);
    float NoH = clamp(dot(N, H), 0.0, 1.0), LoH = clamp(dot(L, H), 0.0, 1.0);
    float a2 = a * a, den = 1.0 - NoH * NoH * (1.0 - a2);
    return a2 / max(den * den, 1e-8) * (0.25 / max(LoH * LoH, 1e-4)) * mix(CX_DIELECTRIC_F0, 1.0, CxFresnel(LoH));
}

/// The sheen's lobe per unit irradiance: Estevez & Kulla's "Charlie" distribution with Neubelt's
/// visibility (KHR_materials_sheen) -- the retro-reflective rim of cloth and velvet. Pi-less.
float CxSheenLobe(vec3 N, vec3 V, vec3 L, float a)
{
    vec3 H = normalize(V + L);
    float NoH = clamp(dot(N, H), 0.0, 1.0), NoL = clamp(dot(N, L), 0.0, 1.0), NoV = clamp(dot(N, V), 1e-4, 1.0);
    float inv = 1.0 / a;
    float d = (2.0 + inv) * pow(max(1.0 - NoH * NoH, 1e-4), 0.5 * inv) * 0.5;
    return d / (4.0 * max(NoL + NoV - NoL * NoV, 1e-4));
}

vec3 CxLoopSpec(float lobe, vec3 N, vec3 V, vec3 L)
{
    vec3 s = lobe * cxSpecScale;
    // Schlick per channel, F0 + (1 - F0) Fc, so coloured specular whitens at grazing. The lobe carries the
    // engine's scalar mix(spec, 1, Fc) with the hue outside it, which never leaves the specular colour.
    if (uMaterial.x > 0.5)
    {
        float fc = CxFresnel(clamp(dot(normalize(V + L), V), 0.0, 1.0));
        s *= mix(cxF0, vec3(1.0), fc) / max(mix(cxSpec, 1.0, fc), 1e-4);
    }
    if (cxCoat > 0.0) s += vec3(cxCoat * CxCoatLobe(N, V, L, cxCoatRough));
    if (cxSheenRough > 0.0) s += cxSheen * CxSheenLobe(N, V, L, cxSheenRough);
    return s;
}
