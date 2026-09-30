// CX-95: the stock GUI colour pipeline (fst/fstc/fs over inc_standard): texture, vertex colour and globalColor are
// taken to linear, multiplied and re-encoded -- so display = globalColor * vcolor * texture, alpha multiplied straight.
// The records hold the engine's sRGB caches; its uniforms went through GetColorSpaceFromSRGB @0x55db30 (rgb ^ 2.2),
// hence the GuiLin on globalColor and the material. Lit draws (the model view, the compass) take a fixed key light.
#version 460 core
in vec2 vUv;
in vec4 vColor;
in vec3 vNrm;
flat in int vRec;
layout(std430, binding = 31) readonly buffer CxGuiBuf { vec4 uGui[]; };
layout(binding = 0) uniform sampler2D uTex;
uniform int uGamma;   // GAMMA_CORRECTION: the engine's ApplyColorSpace/RevertColorSpace pair is live
out vec4 frag;

vec3 GuiLin(vec3 c) { return uGamma != 0 ? sign(c) * pow(abs(c), vec3(2.2)) : c; }
vec3 GuiEnc(vec3 c) { return uGamma != 0 ? sign(c) * pow(abs(c), vec3(0.45454545)) : c; }

void main()
{
    int r = vRec * 16;
    int flags = int(uGui[r + 13].x);
    vec4 t = (flags & 1) != 0 ? texture(uTex, vUv) : vec4(1.0);
    vec4 c;
    if ((flags & 4) != 0)
    {
        vec3 n = vNrm;
        float k = dot(n, n) > 1e-8 ? max(dot(normalize(n), normalize(vec3(0.35, 0.5, 0.8))), 0.0) : 1.0;
        vec3 light = GuiLin(uGui[r + 12].rgb) + GuiLin(uGui[r + 10].rgb) * 0.3 + GuiLin(uGui[r + 11].rgb) * (0.25 + 0.75 * k);
        c = vec4(GuiEnc(GuiLin(t.rgb) * light), t.a * uGui[r + 11].a);
    }
    else
    {
        vec4 g = uGui[r + 9];
        vec4 vc = (flags & 2) != 0 ? vec4(GuiLin(vColor.rgb), vColor.a) : vec4(1.0);
        c = vec4(GuiEnc(vc.rgb * GuiLin(g.rgb) * GuiLin(t.rgb)), vc.a * g.a * t.a);
    }
    if ((flags & 8) != 0 && c.a <= 0.2) discard;   // SetUniforms: fAlphaDiscardValue = cap 1 ? 0.2 : -1
    frag = c;
}
