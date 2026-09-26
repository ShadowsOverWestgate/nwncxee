    vec3 base = albedo * matDiffuse;
    vec3 specColor = SpecularColor;
    vec3 F0 = specColor * spec;
    // ADR 0026: the loop's per-fragment material state. Under light.brdf 0 this is the engine's split
    // exactly -- the view-side normalisation and the specular colour, applied per light instead of once
    // over the sum, which is the same product. Under 1 it adds multiple scattering and the two layers.
    float NoV = max(dot(N, V), 0.0);
    // after the weather splice, so a puddle's 0.12 is widened too
    if (uMaterial.z > 0.5) rough = sqrt(min(rough * rough + aaKernel, 1.0));
    cxCoat = 0.0; cxCoatRough = 0.0; cxSheen = vec3(0.0); cxSheenRough = 0.0;
    float coatT = 1.0;
    vec3 msc = vec3(1.0);
    if (uMaterial.x > 0.5)
    {
        msc = CxMultiScatter(F0, NoV, sqrt(rough));
        cxCoat = clamp(d.ext0.z, 0.0, 1.0);
        if (cxCoat > 0.0)
        {
            float cp = d.ext0.w >= 0.0 ? d.ext0.w : CX_COAT_ROUGH_DEFAULT;
            cxCoatRough = max(cp * cp, CX_ALPHA_MIN);
            // what the coat reflects never reaches the layer under it
            coatT = 1.0 - cxCoat * mix(CX_DIELECTRIC_F0, 1.0, CxFresnel(NoV));
        }
        if (max(d.ext1.r, max(d.ext1.g, d.ext1.b)) > 0.0)
        {
            cxSheen = d.ext1.rgb;
            float sp = d.ext1.w >= 0.0 ? d.ext1.w : CX_SHEEN_ROUGH_DEFAULT;
            cxSheenRough = max(sp * sp, CX_ALPHA_MIN);
        }
    }
    cxF0 = F0; cxSpec = spec;
    cxSpecScale = (uMaterial.x > 0.5 ? vec3(1.0) : specColor) * CxSpecBase(NoV, rough) * msc * coatT;
    // the diffuse share: the engine's (1 - fSpecularity); a physical metal has none at all. The max()
    // keeps a dielectric at 0.96 and still lets the wet splice's raised specularity take its share.
    float kd = physical ? (1.0 - metal) * (1.0 - max(spec * (1.0 - metal), CX_DIELECTRIC_F0)) : 1.0 - spec;

    vec3 sun = uAreaDiffuse * (sunNdl * sunVis);
    // #143's wrap is a DIFFUSE fix -- it stands in for light bleeding across a low-poly terminator,
    // which is a subsurface/interreflection effect. A specular highlight has no such excuse: fed the
    // wrapped dot, a glossy face angled away from the sun picks up a lobe it cannot geometrically
    // see. The lobe takes the raw dot, the way the ambient hemisphere already does.
    vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);
    vec3 sunSss = uAreaDiffuse * (sunVis * max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y);
    vec3 pointSpec;
    // #308 follow-up: 1 on a creature fragment (skinmesh, or the dangly marker in feat.w) -- the
    // local receiver's carrier-band gate, so a creature takes no dyn-pool self-shadow up close.
    float onMover = (int(d.feat.x) == CX_FEAT_SKIN || d.feat.w < 0.0) ? 1.0 : 0.0;
    // #674: the baked term. A surface whose bake landed reads its static light from the pages --
    // irradiance baked against the VERTEX normal, rebalanced here for the shading normal (Unity's
    // directional decode) -- and the loop skips every static light for it; a hot light's mover shadow
    // comes back out through lmSub. One specular lobe along the dominant direction with the
    // irradiance as its radiance stands in for the per-light lobes the loop no longer runs.
    vec3 lmIrr, lmDir, lmSub;
    bool baked = CxLightmap(uChart[vDraw], vChart, lmIrr, lmDir);
    vec3 point = CxClusteredLightLm(vWorld, N, V, vDepth, onMover, rough, spec, baked ? 1.0 : 0.0, pointSpec, lmSub);
    vec3 lm = vec3(0.0);
    if (baked)
    {
        lm = CxLightmapLit(lmIrr, lmDir, N, normalize(vNrm));
        lm *= (1.0 - min(lmSub.x / max(dot(lm, vec3(0.2126, 0.7152, 0.0722)), 1e-5), 1.0)) * uDayDim;
        point += lm;
        // a glossy receiver took each static light's own lobe in the loop instead (light.hidden)
        if (!CxGlossy(rough)) pointSpec += lm * CxLoopSpec(CxSpecLobe(N, V, lmDir, rough, spec), N, V, lmDir);
    }
    // 43 = the lightmap term alone: the baked light on a baked surface, MAGENTA on an unbaked one.
    // 44 = the pages' texel grid over baked surfaces (chart seams and density), magenta unbaked.
    if (uViz == 43) { frag = vec4(baked ? lm : vec3(1.0, 0.0, 1.0), 1.0); return; }
    if (uViz == 44)
    {
        vec4 rec = uChart[vDraw];
        vec2 t = floor(rec.yz + vChart * rec.w);
        frag = vec4(baked ? vec3(mod(t.x + t.y, 2.0) < 1.0 ? 0.3 : 0.7) : vec3(1.0, 0.0, 1.0), 1.0);
        return;
    }
    // The engine's split: every light accumulates its own lobe, then ONE view-side normalisation
    // (fSpecularBaseModifier) and the material's specular colour scale the sum
    // (inc_lighting.shd:456 and :615). fSpecularity is already inside the lobe via the Fresnel term.
    // ADR 0021 gives occlusion to the ambient term alone, which is right and is invisible wherever
    // the area ambient is dark -- every interior, where the light is lamps and the corners are
    // exactly what wants occluding. /ao direct is the dial back toward vanilla's post-multiply:
    // 0 is ADR 0021 untouched, 1 occludes the direct terms outright. The probe term below picks it
    // up through `diffuse` at no extra cost.
    float dirOcc = mix(1.0, occl, uAoDirect);
    vec3 specLight = (sunSpec * CxLoopSpec(CxSpecLobe(N, V, uAreaDir, rough, spec), N, V, uAreaDir) + pointSpec)
                     * dirOcc;
    vec3 diffuse = (sun + sunSss + point) * dirOcc;
    // the 0.15 ambient floor and the sun-facing wrap are inc_lighting.shd:679 verbatim; the height
    // map's occlusion scales the AMBIENT, which is the term it is actually occluding
    // ADR 0026 (light.ambientdir): the probe gives that ambient a direction -- sky above, ground below --
    // at the authored level; the stock wrap's 0.15 stays the floor. Never during the capture itself.
    float ambDir = mix(0.15, 1.0, sunRaw);
    vec3 ambShape = vec3(ambDir);
    if (uMaterial.y > 0.0 && uCapture == 0 && uProbeMips > 0.0)
        ambShape = mix(ambShape, max(CxAmbientShape(N), vec3(0.15)), uMaterial.y);
    vec3 ambient = uAreaAmbient * matAmbient * ambShape * occl;
    // 34 = the ONLY term occlusion can move inside `total` (the (1-spec) share of the ambient).
    //      Black at both /ao on 0 and /ao on 1 means AO has nothing to scale here, however bright
    //      viz 8 reads -- which no other view in the ladder can say.
    if (uViz == 34) { frag = vec4(ambient * (1.0 - spec), 1.0); return; }
    // ...and the clear coat reflects on its own lobe, on every material that declares one
    if (cxCoat > 0.0 && uCapture == 0 && uProbeMips > 0.0)
    {
        vec2 cab = CxEnvBrdf(NoV, sqrt(cxCoatRough));
        vec3 cenv = textureLod(uProbe, reflect(-V, N), sqrt(cxCoatRough) * (uProbeMips - 1.0)).rgb;
        specLight += (ambient + diffuse) * cenv * (cxCoat * (CX_DIELECTRIC_F0 * cab.x + cab.y)
                     * CxSpecOcclusion(NoV, occl, cxCoatRough)) * uProbeGain;
    }
