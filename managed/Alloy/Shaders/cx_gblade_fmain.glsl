void main()
{
    // the cell's own colours, root to tip, off the art (GrassAtlas)
    vec3 ramp = vRoot < 0.5 ? mix(vColRoot, vColMid, vRoot * 2.0) : mix(vColMid, vColTip, vRoot * 2.0 - 1.0);
    vec3 albedo = CxLinear(ramp);
    if (uSnow.y > 0.0)
    {
        float lum = dot(albedo, vec3(0.3, 0.59, 0.11));
        albedo = mix(albedo, lum * vec3(1.15, 0.95, 0.6), uSnow.y * 0.8);
        albedo = mix(albedo, vec3(0.85), uSnow.y * smoothstep(0.45, 0.9, vRoot) * 0.85);
    }
    vec3 N = normalize(vNrm);
    vec3 V = normalize(uEyePos - vWorld);
    float ndlRaw = dot(N, uAreaDir);
    float ndl = clamp(ndlRaw, 0.0, 1.0);
    bool near = vDist <= uShade;
    float sh = near ? CxSunShadow(vWorld, N, ndl, ndl) : 1.0;
    // a little wrap: a blade is thin, light reaches past its terminator
    vec3 sun = uAreaDiffuse * clamp((ndlRaw + 0.3) / 1.3, 0.0, 1.0) * sh;
    vec3 point = uVLit != 0 ? vLight : ((uLit != 0 && near) ? CxClusteredDiffuse(vWorld, N, vDepth) : vec3(0.0));
    // backlit: the sun behind the field shines THROUGH it, tinted by the blade, strongest toward the tip
    float back = pow(clamp(dot(-V, uAreaDir), 0.0, 1.0), 4.0) * uLook.x * (0.35 + 0.65 * vRoot);
    float sheen = pow(clamp(dot(N, normalize(uAreaDir + V)), 0.0, 1.0), 40.0) * uLook.y * vRoot;
    // self-occlusion inside the clump: the root sits in its neighbours' shade
    float ao = mix(1.0 - uRootAo, 1.0, sqrt(vRoot));
    vec3 lit = albedo * ((uAreaAmbient * vMatAmb + point * vMatDif) * ao + (sun * ao + uAreaDiffuse * sh * back) * vMatDif)
             + uAreaDiffuse * sh * sheen;
    vec3 col = CxFog(lit, vWorld, vDepth);
    if (uGroundOn != 0)
    {
        vec3 g = texelFetch(uGround, ivec2(gl_FragCoord.xy), 0).rgb;
        if (uGroundTint > 0.0) col = mix(g, col, max(smoothstep(0.0, uGroundTint, vRoot), 1.0 - vLookDown));
        // the ladder's thinning edge and the far cut go out INTO the ground, not out of the frame: a blade on
        // its way out takes the colour of what is behind it, so no edge line and no pop
        col = mix(g, col, vFade);
    }
    frag = vec4(CxDisplayOverlays(col), 1.0);
}
