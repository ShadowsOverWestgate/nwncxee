    // 6 = the specular lobe alone: exactly what #120 adds. A part that reads flat in the composite is
    //     either black here (no lobe at all) or washed out by the diffuse, and nothing else tells the
    //     two apart.
    if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
    // energy split: what the specular lobe reflects, the diffuse does not -- inc_lighting.shd's own
    // `DiffuseLight * (1.0 - fSpecularity)` and `AmbientLight * (1.0 - fSpecularity)`
    vec3 total = (ambient + diffuse * matDiffuse) * (1.0 - spec) + d.emi.rgb;
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
    if ((slots & CX_SLOT_ILLUM) != 0) lit += CxLinear(texture(uIllumTex, uv, uLodBias).rgb);
    // #132: everything from here down stays LINEAR -- the screen and the probe want the same values
    // now, and PostChain's resolve does the frame's one encode. The probe never wanted anything else
    // (#121: glGenerateMipmap filters what is STORED, and filtering display-space values darkens
    // them), so what changed is the screen path catching up to it.
    // The dissolve and the fog are both linear multiplies/lerps here. #124 matched the fslit tail's
    // own order (ApplyKeyhole, RevertColorSpace, ApplyFog) and #132 knowingly drops the encode out of
    // the middle of it, which moves fog into linear -- a deliberate look change on foggy frames.
    // Both terms are camera-dependent, so the probe capture takes neither: a dissolve baked into the
    // cube is a permanent hole in every reflection the area ever shows.
    if (uCapture != 0) { frag = vec4(lit, 1.0); return; }
    // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
    // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
    // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
    // #294: times COVERAGE, not the raw texel alpha -- on an env-mapped material that channel is
    // reflectivity and the engine leaves the fragment alpha to the material alone.
    // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
    // #346/#347: the display-space overlays go on LAST, after the fog, exactly where
    // inc_standard.shd puts them -- noir, racial vision, then the targeting shape.
    frag = CxOitOut(vec4(CxDisplayOverlays(CxFog(lit * kh, vWorld, vDepth)), coverage * d.rot.w));
}
