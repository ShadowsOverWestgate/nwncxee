            // #618/#619: the weather on exposed surfaces -- exposure is the rain occlusion map, the amounts are
            // the CPU integrators'. Every surface takes it, a hosted weather body (td_ground_fs) included.
            float wxWet = 0.0, wxSnow = 0.0, wxPuddle = 0.0, wxPile = 0.0;
            vec2 wxDriftDir = vec2(0.0), wxP = vWorld.xy;
            if (uWeather.w > 2.5) { frag = d.emi.w < -0.5 ? vec4(1.0, 0.0, 0.0, 1.0) : d.emi.w > 0.5 ? vec4(0.0, 0.0, 1.0, 1.0) : vec4(0.0, 1.0, 0.0, 1.0); return; }
            if (uWeather.x > 0.0 || uWeather.y > 0.0 || uWeather.w > 1.5)
            {
                float wxExp = CxWeatherExposure(vWorld, N);
                if (uWeather.w > 1.5) { frag = vec4(vec3(wxExp), 1.0); return; }
                vec3 Ng = normalize(vNrm);
                float up = clamp(Ng.z, 0.0, 1.0);
                float n1 = CxWeatherNoise(vWorld.xy * 0.7), n2 = CxWeatherNoise(vWorld.xy * 3.1);
                // rain wets a face as much as the fall strikes it, relative to open ground: a wall parallel to the
                // fall stays dry, a windward one takes the lean. The fall is the cover view's axis (it is baked along it).
                vec3 wxFall = uCoverVp[3][3] == 0.0 ? vec3(0.0, 0.0, -1.0) : normalize(vec3(uCoverVp[0][2], uCoverVp[1][2], uCoverVp[2][2]));
                if (wxFall.z > 0.0) wxFall = -wxFall;
                wxWet = uWeather.x * wxExp * clamp(dot(Ng, -wxFall) / max(-wxFall.z, 0.2), 0.0, 1.0);
                if (uWeather.y > 0.0 && d.emi.w >= -0.5 && !(uSnowPom.z > 0.5 && d.emi.w > 0.5))   // no snow on a creature (emi.w -1), nor on a listed tileset's tiles
                {
                    float wxSlope = smoothstep(uSnowLook.w - 0.15, uSnowLook.w + 0.15, up + (n1 - 0.5) * 0.4);
                    // drift: banked against whatever stands higher, out to the drift width
                    vec3 wxDrift = uSnowLook.z > 0.0 ? CxSnowDrift(vWorld, uSnowLight.z) : vec3(0.0);
                    wxPile = smoothstep(0.0, 0.6, wxDrift.x) * uSnowLook.z * up;
                    wxDriftDir = wxDrift.yz;
                    // ground variation: broad patches of thinner cover across open ground
                    float wxVary = 1.0 - uSnowLight.w * smoothstep(0.35, 0.7, CxWeatherNoise(vWorld.xy * 0.23)) * 0.6;
                    wxSnow = clamp(uWeather.y * wxExp * wxSlope * (0.75 + 0.25 * n2) * wxVary * (1.0 + wxPile), 0.0, 1.0);
                    // parallax relief: the snow surface stands uSnowPom.x x height above the mesh. March the view
                    // ray from where it enters the relief's top down to the mesh, and shade the point where it
                    // meets the surface; the snow is one colour, so only the relief's own terms move, never the uv.
                    // The drift is taken at the entry and at the mesh point and interpolated along the ray.
                    if (uSnowPom.x > 0.0 && wxSnow > 0.4 && up > 0.7)
                    {
                        float amp = uSnowPom.x * wxSnow;
                        float vn = max(dot(V, Ng), 0.15);
                        vec2 top = vWorld.xy + V.xy * (amp / vn);
                        float pileTop = uSnowLook.z > 0.0 ? smoothstep(0.0, 0.6, CxSnowDrift(vec3(top, vWorld.z), uSnowLight.z).x) * uSnowLook.z * up : 0.0;
                        float n = uSnowPom.y, prevH = 1.0, prevS = 1.0, s = 1.0;
                        vec2 p = top;
                        for (int i = 1; i <= int(n); i++)
                        {
                            s = 1.0 - float(i) / n;
                            p = mix(vWorld.xy, top, s);
                            float hs = CxSnowHeight(p, uSnowLight.w, mix(wxPile, pileTop, s));
                            if (s <= hs)
                            {
                                float t = (prevS - prevH) / max((prevS - prevH) - (s - hs), 1e-4);   // where the ray crossed
                                s = mix(prevS, s, t);
                                break;
                            }
                            prevH = hs; prevS = s;
                        }
                        wxP = mix(vWorld.xy, top, s);
                        wxPile = mix(wxPile, pileTop, s);
                    }
                }
                if (uWeather.z > 0.0 && up > 0.5)
                {
                    // concave = the AO says low ground, or the rain view says the neighbours stand higher
                    float conc = clamp((1.0 - occl) * 1.5 + CxWeatherHollow(vWorld), 0.0, 1.0);
                    float wxFlat = smoothstep(0.9, 0.98, up);
                    float m = conc * 0.5 + n1 * 0.6 + n2 * 0.15 + uWeatherLook.w - 0.7;
                    wxPuddle = uWeather.z * wxFlat * wxExp * smoothstep(0.5 - uWeatherLook.z, 0.5 + uWeatherLook.z, m);
                }
                if (wxSnow > 0.0)
                {
                    albedo = mix(albedo, vec3(uWeatherLook.y), wxSnow);
                    rough = mix(rough, 0.75, wxSnow); spec = mix(spec, 0.08, wxSnow);
                    // mounds: the normal tilts down the relief's gradient at the point the ray met it (or under
                    // the pixel with the relief off), more as the cover deepens
                    vec2 wxG = vec2(CxSnowHeight(wxP + vec2(0.05, 0.0), uSnowLight.w, wxPile) - CxSnowHeight(wxP - vec2(0.05, 0.0), uSnowLight.w, wxPile),
                                    CxSnowHeight(wxP + vec2(0.0, 0.05), uSnowLight.w, wxPile) - CxSnowHeight(wxP - vec2(0.0, 0.05), uSnowLight.w, wxPile));
                    // a drift is a slope rising toward the wall: the normal leans away from it, most at its foot.
                    // The cover view's uv axes are its own right/up, not world xy; the tilt is applied in world
                    // xy through the view's inverse rotation -- the map is a top-down orthographic fit, so its
                    // uv is a rotation of world xy and the first two columns carry it.
                    vec2 wxLean = vec2(dot(uCoverVp[0].xy, wxDriftDir), dot(uCoverVp[1].xy, wxDriftDir));
                    wxLean = length(wxLean) > 1e-5 ? normalize(wxLean) : vec2(0.0);
                    N = normalize(mix(N, Ng, wxSnow * 0.7) - vec3(wxG * uSnowLook.y * 3.0 * wxSnow, 0.0) - vec3(wxLean * wxPile * 0.8, 0.0));
                    // deeper cover brightens and rounds off: the mound reads thicker than the blanket
                    albedo = mix(albedo, vec3(uWeatherLook.y) * 1.05, clamp(wxPile, 0.0, 1.0) * 0.5);
                }
                if (wxWet > 0.0)
                {
                    albedo *= 1.0 - uWeatherLook.x * wxWet;
                    rough = mix(rough, 0.35, wxWet); spec = max(spec, 0.25 * wxWet);
                }
                if (wxPuddle > 0.0)
                {
                    albedo *= 1.0 - 0.5 * wxPuddle;
                    rough = mix(rough, 0.03, wxPuddle); spec = max(spec, 0.4 * wxPuddle);
                    SpecularColor = mix(SpecularColor, vec3(1.0), wxPuddle);   // the water reflects, not the stone under it
                    N = normalize(mix(N, vec3(0.0, 0.0, 1.0), wxPuddle));
                }
                if (uCoverLayer.w > 0.0 && wxPuddle > 0.0 && up > 0.9)
                {
                    float wxRing, wxAmp = wxExp * wxPuddle;
                    N = normalize(N + vec3(CxWeatherImpact(vWorld.xy, uCoverLayer.w, CxWeatherImpactPx(vWorld, V), wxRing) * wxAmp, 0.0));
                    rough = CxWeatherImpactRough(rough, wxRing, wxAmp, uCoverLayer.w);
                }
            }
