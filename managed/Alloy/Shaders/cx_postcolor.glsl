vec3 CxEncode(vec3 c) { return pow(max(c, vec3(0.0)), vec3(1.0 / $SRGB$)); }
