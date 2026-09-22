
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
