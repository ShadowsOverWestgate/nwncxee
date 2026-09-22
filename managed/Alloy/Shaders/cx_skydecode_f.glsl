#version 460 core
in vec2 vUV;
layout(location = 0) out vec4 frag;
#include "cx_oitout.glsl"
#include "cx_fogdecl.glsl"
#include "cx_fogmix.glsl"
uniform sampler2D uHdr;
// The owned dome's horizon band (OwnedFs uSkyFog), for the hosted dome: culled ground reveals its lower
// half too, and unfogged it reads as a dark cut-out past the fog (CullPass.FogCull). 0 = no band.
uniform float uSkyFog;
uniform mat4 uInvVP;   // the frame's inverse view-projection: this pixel's world ray
void main()
{
    vec4 c = texelFetch(uHdr, ivec2(gl_FragCoord.xy), 0);
    vec3 rgb = pow(max(c.rgb, vec3(0.0)), vec3($SRGB$));
    if (uSkyFog > 0.0 && uFogParams.x != 0.0)
    {
        vec4 p = uInvVP * vec4(vUV * 2.0 - 1.0, 0.0, 1.0);
        vec3 world = p.xyz / p.w;
        rgb = CxFogMix(rgb, world, 1.0 - smoothstep(0.0, uSkyFog, normalize(world - uEyePos).z));
    }
    frag = vec4(rgb, c.a);
}
