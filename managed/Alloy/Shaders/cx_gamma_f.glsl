uniform float uGamma;        // the client's gamma option, the engine's global float param 0x0b
vec4 CxStage(vec4 c)
{
    c.rgb = pow(max(c.rgb, vec3(0.0)), vec3(uGamma / 2.2));
    return c;
}
