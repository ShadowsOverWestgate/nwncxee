namespace CX.Alloy;

/// <summary>The bake's two programs. The bake stage draws a charted mesh with its chart UV as the position
/// (UV2 -> rect offset/scale -> page NDC) and its world position and vertex normal interpolated, then runs
/// the colour pass's own per-light body -- <c>CxPointLight</c> / <c>CxPointDiffuse</c> / <c>CxLocalShadow</c>
/// from <see cref="AlloyLights.FragmentLib"/> and <see cref="AlloyLights.LocalShadowLib"/> -- against the
/// scratch cube in slot 0, so a baked texel is the lit stage's own arithmetic (#666). The finish stage folds a
/// page's scratch pair into the resident pair.</summary>
internal static partial class LightmapBake
{
    internal const string BakeVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 2) in vec3 aNrm;
        layout(location = 3) in vec2 aChart;

        """ + AlloyRenderer.DrawBufDecl + """
        uniform int uSlot;      // the instance's CxDraw record: its model matrix is the placement
        uniform vec4 uRect;     // x, y = the rect's origin in page texels, z = page texels per atlas texel
        const float CX_LM_PAGE = 4096.0;
        out vec3 vWorld;
        out vec3 vNrm;
        void main()
        {
            CxDraw d = uDraw[uSlot];
            vec4 w = d.model * vec4(aPos, 1.0);
            vWorld = w.xyz;
            vNrm = mat3(d.model) * aNrm;
            vec2 t = (uRect.xy + aChart * uRect.z) / CX_LM_PAGE;
            gl_Position = vec4(t * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    internal const string BakeFs = """
        #version 460 core
        in vec3 vWorld;
        in vec3 vNrm;
        uniform vec4 uBakeLight;   // xyz = position, w = the lit radius (GetLightAdjustedRadius)
        uniform vec3 uBakeColor;   // the walk's colour, linearised in CxPointLight exactly as the loop does
        uniform int uBakeMode;     // 0 = coverage only (A' alpha 1), 1 = one light's contribution, additive
        layout(location = 0) out vec4 oA;   // sum rgb, coverage alpha
        layout(location = 1) out vec4 oB;   // sum luminance * direction

        """ + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + """
        void main()
        {
            if (uBakeMode == 0) { oA = vec4(0.0, 0.0, 0.0, 1.0); oB = vec4(0.0); return; }
            vec3 N = normalize(vNrm);
            vec3 c, L; float att, f2, ndlRaw, ndl;
            if (!CxPointLight(uBakeLight, uBakeColor, vWorld, N, c, L, att, f2, ndlRaw, ndl) || ndl <= 0.0) discard;
            // slot 0 of the one-cube scratch array: the same receiver the frame's cube slots go through
            float sh = CxLocalShadow(0, vWorld, N, ndlRaw, 0.0, 0.0);
            vec3 lit = CxPointDiffuse(c, att, ndlRaw, ndl) * mix(uLocalFloor, 1.0, sh);
            oA = vec4(lit, 0.0);
            oB = vec4(dot(lit, vec3(0.2126, 0.7152, 0.0722)) * L, 0.0);
        }
        """;

    /// <summary>Per page texel: a covered texel (alpha > 0) converts as is; an uncovered one within two of a
    /// covered one takes its nearest covered neighbour (the dilation that keeps bilinear reads off the
    /// gutter); anything else discards, so cache-hit rects already resident on the page stay untouched.</summary>
    internal const string FinishFs = """
        #version 460 core
        in vec2 vUV;
        uniform sampler2D uScrA;
        uniform sampler2D uScrB;
        const int CX_LM_PAGE = 4096;
        layout(location = 0) out vec4 oA;
        layout(location = 1) out vec4 oB;
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec4 a = texelFetch(uScrA, p, 0);
            vec4 b = vec4(0.0);
            if (a.a > 0.0) b = texelFetch(uScrB, p, 0);
            else
            {
                float best = 1e9;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        ivec2 q = p + ivec2(dx, dy);
                        if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, ivec2(CX_LM_PAGE)))) continue;
                        vec4 s = texelFetch(uScrA, q, 0);
                        float d = float(dx * dx + dy * dy);
                        if (s.a > 0.0 && d < best) { best = d; a = s; b = texelFetch(uScrB, q, 0); }
                    }
                if (best > 1e8) discard;
            }
            float l = length(b.xyz);
            vec3 dir = l > 1e-6 ? b.xyz / l : vec3(0.0, 0.0, 1.0);
            oA = vec4(max(a.rgb, vec3(0.0)), 1.0);
            oB = vec4(dir * 0.5 + 0.5, 1.0);
        }
        """;
}
