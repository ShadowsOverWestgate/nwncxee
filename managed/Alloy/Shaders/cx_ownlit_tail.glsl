    // #121/CX-51: the reflection on EVERY lit surface, as the probe sees it. The material's env map is not
    // consulted: it was the vanilla renderer's stand-in for exactly this; it still marks legacy content shiny
    // (CxSynthPbr). Radiance under the split-sum weight -- DFG with dielectric Fresnel on legacy content,
    // specular occlusion, capped at 1 so no bounce adds energy. /light probegain is the dial. The weight goes
    // out in the reflection record too, and Ssr swaps the probe for the screen wherever the screen shows R.
    // what a puddle's water reflects never reaches the stone under it: at grazing angles the mirror, not the
    // lit ground, is what the eye gets
    float wxTransmit = 1.0;
    vec3 reflW = vec3(0.0);
    // R4: the film's own split-sum weight, in the film's roughness -- what the screen march has to trace
    // where standing water is the mirror. cxCoat already carries the film's coverage, so this is taken at
    // its full strength and the BASE weight is what it displaces, never a second fade on top of it.
    vec3 reflFilm = vec3(0.0);
    if (uCapture != 1)
    {
        vec3 R = reflect(-V, N);
        vec2 reflAb = CxEnvBrdf(NoV, sqrt(rough)) * vec2(1.0, cxFresnel);
        vec3 reflF0 = physical ? cxF0 : mix(vec3(CX_DIELECTRIC_F0), cxF0, metal);
        reflW = min((reflF0 * reflAb.x + reflAb.y) * msc * CxSpecOcclusion(NoV, occl, rough) * coatT * uProbeGain, vec3(1.0));
        if (uProbeMips > 0.0)
            specLight += CxProbeFetch(uProbe, vWorld, R, sqrt(rough) * (uProbeMips - 1.0)) * reflW;
        if (wxFilm > 0.0 && cxCoat > 0.0)
        {
            vec2 fab = CxEnvBrdf(NoV, sqrt(cxCoatRough));
            reflFilm = vec3(min(cxCoat * (CX_DIELECTRIC_F0 * fab.x + fab.y)
                                * CxSpecOcclusion(NoV, occl, cxCoatRough) * uProbeGain, 1.0));
        }
        wxTransmit = 1.0 - max(max(reflW.r, max(reflW.g, reflW.b)), reflFilm.r) * wxFilm;
    }
    // 6 = the specular lobe alone: exactly what #120 adds. A part that reads flat in the composite is
    //     either black here (no lobe at all) or washed out by the diffuse, and nothing else tells the
    //     two apart.
    if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
    // energy split: what the specular lobe reflects, the diffuse does not -- inc_lighting.shd's own
    // `DiffuseLight * (1.0 - fSpecularity)` and `AmbientLight * (1.0 - fSpecularity)`
    // ponytail: the sheen takes no ambient term (Filament's needs a DFG table of its own); cloth reads by
    // its direct rim, and a sheen that wants presence in shade is the table's cue.
    vec3 total = (ambient + diffuse * matDiffuse) * (kd * coatT * wxTransmit) + d.emi.rgb;
    // 9 = the LIGHT arriving here, with no albedo on it: vanilla's TotalLight, the value
    //     inc_lighting.shd multiplies ColorIO by. Paired with 31 it splits "the light is short"
    //     from "the surface is dark", which no composite frame can do.
    if (uViz == 9) { frag = vec4(total, 1.0); return; }
    // Specular and self-illumination land AFTER the albedo multiply: a highlight is light off the
    // surface, not light through it (inc_lighting.shd:615 `ColorIO.rgb += SpecularColor *
    // SpecularLight`, and the texUnit5 add just above it).
    // inc_lighting.shd:672-677: "Specularity ignores material transparency" -- vanilla pre-divides the
    // specular by the alpha it is about to be blended by, so a ghosted creature keeps its highlights.
    float partA = coverage * d.rot.w;
    if (partA < 1.0 && partA > 0.001) specLight /= max(partA, 0.1);
    vec3 lit = albedo * total + specLight;
    // ADR 0026: CxEmissiveStrength (KHR_materials_emissive_strength) scales the map into HDR, where the
    // bloom can see it; undeclared (-1) and the engine model take it at 1.
    if ((slots & CX_SLOT_ILLUM) != 0)
        lit += CxLinear(texture(uIllumTex, uv, uLodBias).rgb) * (uMaterial.x > 0.5 && d.ext2.x >= 0.0 ? d.ext2.x : 1.0);
    // #132: everything from here down stays LINEAR -- the screen and the probe want the same values
    // now, and PostChain's resolve does the frame's one encode. The probe never wanted anything else
    // (#121: glGenerateMipmap filters what is STORED, and filtering display-space values darkens
    // them), so what changed is the screen path catching up to it.
    // The dissolve and the fog are both linear multiplies/lerps here. #124 matched the fslit tail's
    // own order (ApplyKeyhole, RevertColorSpace, ApplyFog) and #132 knowingly drops the encode out of
    // the middle of it, which moves fog into linear -- a deliberate look change on foggy frames.
    // Both terms are camera-dependent, so the probe capture takes neither: a dissolve baked into the
    // cube is a permanent hole in every reflection the area ever shows.
    // CX-51: the capture keeps creatures out (a figure baked into the cube stands in every reflection until the
    // next capture) and stores 1 / distance in alpha, the parallax correction's depth (CxProbeFetch)
    if (uCapture != 0)
    {
        // CX-51: the mirrored draw keeps everything, creatures included; alpha 1 = something is mirrored here
        if (uCapture == 2) { frag = vec4(lit, 1.0); return; }
        if (int(d.feat.x) == CX_FEAT_SKIN || d.feat.w < 0.0) discard;
        frag = vec4(lit, 1.0 / max(distance(vWorld, uEyePos), 1e-3));
        return;
    }
    // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
    // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
    // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
    // #294: times COVERAGE, not the raw texel alpha -- on an env-mapped material that channel is
    // reflectivity and the engine leaves the fragment alpha to the material alone.
    // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
    // #346/#347: the display-space overlays go on LAST, after the fog, exactly where
    // inc_standard.shd puts them -- noir, racial vision, then the targeting shape.
    // ponytail: the fog mixes in display space, so (1 - f) is the linear transmittance only approximately;
    // the display overlays never reach the swapped-in reflection. Exact = Ssr refogging, if a foggy puddle shows it.
    // R4: where the water film dominates, the record carries the FILM's roughness and the film's weight --
    // the screen march must trace the mirror the film is, not the matte bed the base roughness describes.
    CxReflOut(N, mix(rough, cxCoatRough, wxFilm), max(reflW, reflFilm) * (kh * (1.0 - CxFogFactor(vWorld, vDepth))));
    frag = CxOitOut(vec4(CxDisplayOverlays(CxFog(lit * kh, vWorld, vDepth)), coverage * d.rot.w));
}
