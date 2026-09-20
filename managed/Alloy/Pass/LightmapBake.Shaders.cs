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
        out vec3 gWorld;
        out vec3 gNrm;
        void main()
        {
            CxDraw d = uDraw[uSlot];
            vec4 w = d.model * vec4(aPos, 1.0);
            gWorld = w.xyz;
            gNrm = mat3(d.model) * aNrm;
            vec2 t = (uRect.xy + aChart * uRect.z) / CX_LM_PAGE;
            gl_Position = vec4(t * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>Conservative coverage: each triangle's edges are pushed out by <c>CX_LM_PUSH</c> page texels
    /// (mitre-clamped at sharp corners), so a face thinner than a texel still owns the texels it crosses --
    /// at density 4 a window recess a hand deep had no texel centre inside it and baked black (#674 live).
    /// The pushed vertices carry their barycentrics against the ORIGINAL triangle; the fragment stage clamps
    /// those back inside it, so a pushed texel shades the nearest point ON the face, never a point in the air
    /// or inside the wall behind it.</summary>
    internal const string BakeGs = """
        #version 460 core
        layout(triangles) in;
        layout(triangle_strip, max_vertices = 3) out;
        in vec3 gWorld[];
        in vec3 gNrm[];
        out vec3 vBary;
        out vec3 vNrm;
        flat out vec3 vW0, vW1, vW2;
        uniform float uBakeDepth;   // NDC z: the light's step under GREATER (see BakeDepthSteps); -1 = coverage
        const float CX_LM_PAGE = 4096.0;
        const float CX_LM_PUSH = 0.6;    // texels: past the half that puts a centre inside any strip
        const float CX_LM_MITRE = 3.0;   // texels: the most a sharp corner may travel
        void main()
        {
            vec2 p0 = (gl_in[0].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
            vec2 p1 = (gl_in[1].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
            vec2 p2 = (gl_in[2].gl_Position.xy * 0.5 + 0.5) * CX_LM_PAGE;
            vec2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
            float area = e0.x * (p2.y - p0.y) - e0.y * (p2.x - p0.x);
            float s = area < 0.0 ? -1.0 : 1.0;
            bool flat_ = abs(area) < 1e-4;
            // outward unit normals of the three edges (right-hand side of a CCW edge)
            vec2 n0 = s * normalize(vec2(e0.y, -e0.x));
            vec2 n1 = s * normalize(vec2(e1.y, -e1.x));
            vec2 n2 = s * normalize(vec2(e2.y, -e2.x));
            vec2 q[3]; vec2 p[3] = vec2[3](p0, p1, p2);
            vec2 nin[3] = vec2[3](n2, n0, n1), nout[3] = vec2[3](n0, n1, n2);
            for (int i = 0; i < 3; i++)
            {
                vec2 m = nin[i] + nout[i];
                float d = 1.0 + dot(nin[i], nout[i]);
                vec2 push = d > 1e-3 ? m * (CX_LM_PUSH / d) : nout[i] * CX_LM_PUSH;
                float l = length(push);
                if (l > CX_LM_MITRE) push *= CX_LM_MITRE / l;
                q[i] = flat_ ? p[i] : p[i] + push;
            }
            for (int i = 0; i < 3; i++)
            {
                vec3 b;
                if (flat_) b = vec3(i == 0 ? 1.0 : 0.0, i == 1 ? 1.0 : 0.0, i == 2 ? 1.0 : 0.0);
                else
                {
                    vec2 x = q[i];
                    float a0 = (p1.x - x.x) * (p2.y - x.y) - (p1.y - x.y) * (p2.x - x.x);
                    float a1 = (p2.x - x.x) * (p0.y - x.y) - (p2.y - x.y) * (p0.x - x.x);
                    b = vec3(a0, a1, area - a0 - a1) / area;
                }
                vBary = b;
                vNrm = gNrm[i];
                vW0 = gWorld[0]; vW1 = gWorld[1]; vW2 = gWorld[2];
                gl_Position = vec4(q[i] / CX_LM_PAGE * 2.0 - 1.0, uBakeDepth, 1.0);
                EmitVertex();
            }
            EndPrimitive();
        }
        """;

    internal const string BakeFs = """
        #version 460 core
        in vec3 vBary;
        in vec3 vNrm;
        flat in vec3 vW0, vW1, vW2;
        uniform vec4 uBakeLight;   // xyz = position, w = the lit radius (GetLightAdjustedRadius)
        uniform vec3 uBakeColor;   // the walk's colour, linearised in CxPointLight exactly as the loop does
        uniform int uBakeMode;     // 0 = coverage only (A' alpha 1), 1 = one light's contribution, additive
        layout(location = 0) out vec4 oA;   // sum rgb, coverage alpha
        layout(location = 1) out vec4 oB;   // sum luminance * direction

        """ + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + """
        void main()
        {
            if (uBakeMode == 0) { oA = vec4(0.0, 0.0, 0.0, 1.0); oB = vec4(0.0); return; }
            // a pushed texel shades the face's nearest interior point: barycentrics clamped back into the triangle
            vec3 b = max(vBary, vec3(0.0));
            b /= max(b.x + b.y + b.z, 1e-6);
            vec3 vWorld = b.x * vW0 + b.y * vW1 + b.z * vW2;
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
