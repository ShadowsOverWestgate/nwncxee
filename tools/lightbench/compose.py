#!/usr/bin/env python3
"""Compose a bench fragment from a shader directory, with the build's tokens.
  compose.py <shader dir> <out.glsl>          the light loop alone: cx_localshadow + cx_lightfrag + a plane main
  compose.py <shader dir> <out.glsl> --full   the real lit stage (cx_ownlit_fs, every #include expanded)
The lit stage's register pressure is what sets the loop's cost in game, so --full is the one to trust."""
import re, sys
d, out = sys.argv[1], sys.argv[2]
full = "--full" in sys.argv
tok = {"$CAP$": "128", "$SORT$": "32", "$DYNSPH$": "4", "$SRGB$": "2.2", "$GROUP$": "8", "$AA$": "3",
       "$POSTMASK$": "0", "$SQM$": "2", "$BAND$": "45.0, 0.0667"}

def expand(f, seen):
    if f in seen: return ""
    seen.add(f)
    return "\n".join(expand(m.group(1), seen) if (m := re.match(r'\s*#include "(.+)"', l)) else l
                     for l in open(f"{d}/{f}").read().split("\n"))

if full:
    body = expand("cx_ownlit_fs.glsl", set())
    lines = body.split("\n")
    ver = next((l for l in lines if l.startswith("#version")), "#version 460 core")
    src = ver + "\n" + "\n".join(l for l in lines if not l.startswith("#version"))
else:
    src = "#version 460 core\n#define CX_FS\n" + expand("cx_localshadow.glsl", set()) + "\n" + expand("cx_lightfrag.glsl", set()) + """
uniform vec2 uRes;
uniform float uBaked, uRough;
out vec4 frag;
void main()
{
    vec2 uv = gl_FragCoord.xy / uRes;
    vec3 wp = vec3(uv.x * 80.0 - 40.0, 0.0, uv.y * 60.0 - 10.0);
    vec3 cam = vec3(0.0, 25.0, -30.0);
    vec3 V = normalize(cam - wp);
    vec3 spec, sub;
    vec3 d = CxClusteredLightLm(wp, vec3(0.0, 1.0, 0.0), V, length(cam - wp), 0.0, uRough, 0.5, uBaked, spec, sub);
    frag = vec4(d + spec + sub.xxx, 1.0);
}
"""
for k, v in tok.items(): src = src.replace(k, v)
left = sorted(set(re.findall(r"\$[A-Z]+\$", src)))
if left: sys.exit(f"unexpanded tokens: {left}")
open(out, "w").write(src)
