
void main()
{
    vec4 c = CxStage(texture(uSrc, vUV));
    frag = vec4(CxDither(c.rgb, gl_FragCoord.xy, uDither), c.a);
}
