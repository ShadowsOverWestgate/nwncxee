
// 1 = open sky above this point along the fall vector, 0 = covered. No map = open.
float CxCovered(vec3 p)
{
    if (uCoverVp[3][3] == 0.0) return 1.0;
    vec4 c = uCoverVp * vec4(p, 1.0);
    vec3 n = c.xyz / c.w * 0.5 + 0.5;
    if (any(lessThan(n.xy, vec2(0.0))) || any(greaterThan(n.xy, vec2(1.0)))) return 1.0;
    return texture(uCover, vec4(n.xy, uCoverLayer, n.z - 0.002));
}

// 1 = under a lip: covered here, open somewhere within the feather distance. Mid-roof and open sky are 0.
float CxLip(vec3 P, vec3 R, vec3 U)
{
    float e = max(uCoverSoft, 0.15);
    if (CxCovered(P) >= 0.5) return 0.0;
    return max(max(CxCovered(P + R * e), CxCovered(P - R * e)), max(CxCovered(P + U * e), CxCovered(P - U * e)));
}

void main()
{
    uint id = uint(gl_InstanceID);
    vec3 h = CxHash3(id * 2654435761u + 1u);
    vec3 F = uFall;
    vec3 R = normalize(cross(F, abs(F.z) < 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0)));
    vec3 U = cross(R, F);
    vec3 size = vec3(uBox.x, uBox.x, uBox.y);
    float speed = 0.75 + float(id & 3u) * 0.125;    // eighths: continuous at the phase wrap
    vec3 home = h * size;
    home.z = mod(home.z + uPhase * speed, size.z);
    vec3 c = vec3(dot(uCentre, R), dot(uCentre, U), dot(uCentre, F));
    vec3 q = home + size * floor((c - home) / size + 0.5);
    if (uKind == 2) q.xy += vec2(sin(uTime + h.y * 6.2832), cos(uTime * 1.5 + h.z * 6.2832)) * uDrift;
    vec3 P = R * q.x + U * q.y + F * q.z;
    vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
    vec3 toCam = uEye - P;
    vec3 axis, side;
    if (uKind != 2) { axis = F; side = normalize(cross(F, toCam)); }
    else { side = normalize(cross(vec3(0.0, 0.0, 1.0), toCam)); axis = normalize(cross(toCam, side)); }
    float len = uKind != 2 ? uSize.x * (0.5 + 0.8 * h.y) : uSize.x;
    vec3 pos = P + axis * (corner.y * len * 0.5) + side * (corner.x * uSize.y * 0.5);
    vUV = corner;
    // brightness: hashed 0.3..1.0
    vSeed = 0.3 + 0.7 * fract(h.x * 7.31);
    // no edge: the field thins RADIALLY from 40% of the half-width out, by drop COUNT (a hashed keep)
    // and not by a brightness ramp -- a ramp on a square box read as a band with a corner in it,
    // fewer drops reads as distance
    vec2 rel = (q.xy - c.xy) / (size.xy * 0.5);
    float edge = 1.0 - smoothstep(0.4, 1.0, length(rel));
    vec4 vp = uView * vec4(pos, 1.0);
    vViewZ = vp.z;
    // the cover test at a hashed offset in the fall-normal plane: wind and splash feather a roof's edge
    vec3 Pc = P + (R * (h.y - 0.5) + U * (h.z - 0.5)) * uCoverSoft * 2.0;
    float shown = fract(h.z * 91.7) > edge ? 0.0 : uKind == 3 ? CxLip(P, R, U) : CxCovered(Pc);
    gl_Position = shown < 0.5 ? vec4(2.0, 2.0, 2.0, 1.0) : uProj * vp;
    // a drop has no surface: the camera-facing direction stands in for its normal, so a lamp behind the
    // camera lights it and one behind the drop does not -- the cheapest read of "wet glints towards you"
    vLight = uAreaAmbient + uAreaDiffuse * (uKind == 2 ? 1.0 : 0.5);
    if (uLit != 0 && shown >= 0.5)
    {
        vec3 N = normalize(toCam);
        vec4 cv = uView * vec4(P, 1.0);
        vec4 cc = uProj * cv;
        cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
        vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(P, N, ndl, ndl));
        vec3 point = CxClusteredDiffuse(P, N, max(-cv.z, 1e-3));
        vLight = uAreaAmbient * mix(0.15, 1.0, ndl) + sun + point;
        if (uBack > 0.0)
        {
            float back = pow(max(dot(-N, uAreaDir), 0.0), 4.0);
            vLight += uBack * (uAreaDiffuse * back * CxSunShadow(P, -N, back, back) + CxClusteredDiffuse(P, -N, max(-cv.z, 1e-3)));
        }
    }
}
