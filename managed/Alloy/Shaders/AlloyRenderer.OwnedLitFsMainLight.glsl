    vec3 base = albedo * matDiffuse;
    vec3 specColor = SpecularColor;
    vec3 F0 = specColor * spec;

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
        lm *= 1.0 - min(lmSub.x / max(dot(lm, vec3(0.2126, 0.7152, 0.0722)), 1e-5), 1.0);
        point += lm;
        // a glossy receiver took each static light's own lobe in the loop instead (light.hidden)
        if (!CxGlossy(rough)) pointSpec += lm * CxSpecLobe(N, V, lmDir, rough, spec);
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
    vec3 specLight = (sunSpec * CxSpecLobe(N, V, uAreaDir, rough, spec) + pointSpec)
                     * (specColor * CxSpecBase(max(dot(N, V), 0.0), rough)) * dirOcc;
    vec3 diffuse = (sun + sunSss + point) * dirOcc;
    // the 0.15 ambient floor and the sun-facing wrap are inc_lighting.shd:679 verbatim; the height
    // map's occlusion scales the AMBIENT, which is the term it is actually occluding
    vec3 ambient = uAreaAmbient * matAmbient * mix(0.15, 1.0, sunRaw) * occl;
    // 34 = the ONLY term occlusion can move inside `total` (the (1-spec) share of the ambient).
    //      Black at both /ao on 0 and /ao on 1 means AO has nothing to scale here, however bright
    //      viz 8 reads -- which no other view in the ladder can say.
    if (uViz == 34) { frag = vec4(ambient * (1.0 - spec), 1.0); return; }
    // #121: ambient specular -- the term vanilla feeds from the area env map on EVERY lit part
    // (inc_lighting.shd:456-512 gates it on SPECULAR_LIGHT, not on ENVIRONMENT_MAP) and the owned
    // pass dropped on all of them. Weighted the ENGINE's way: the env sample TINTS the light already
    // arriving at this fragment -- `EnvironmentColor = (AmbientLight + DiffuseLight) * envTexel`,
    // then `* fEnvironmentSpecularity` -- rather than adding radiance on top of it. Added radiance
    // (the split-sum form this first shipped as) makes a brick road reflect the sky like wet stone
    // and lights unlit corners out of the probe; this form cannot exceed the light that is there.
    // ponytail: no split-sum BRDF and no specular Fresnel (inc_lighting.shd:463, SPECULAR_FRESNEL).
    // /light probegain is the dial if the term wants more presence than the engine gave it.
    // ...and fed only where the material DECLARES an env map. RenderFlat @0x450eb0 also binds the
    // area's own env map (currentscene+0x1e8, verified) for plain parts, so being faithful to the
    // BINDING would put this term on everything in such an area -- and live that reflects the sky off
    // city cobblestone, which vanilla plainly does not do: its authored spheremap is a dim texel,
    // while the probe carries real scene radiance. /light probeareawide 1 opts back into the engine's
    // rule (uSceneEnv); the default is the case the reflection exists for.
    if (uCapture == 0 && uProbeMips > 0.0 && (uSceneEnv != 0 || (slots & CX_SLOT_ENV) != 0))
    {
        vec3 env = textureLod(uProbe, reflect(-V, N), rough * (uProbeMips - 1.0)).rgb;
        specLight += (ambient + diffuse) * env * F0 * uProbeGain;
    }
