            if (wxSnow > 0.0)
            {
                // scatter: the terminator wraps past the sun-facing edge, the way light travelling inside snow does
                diffuse += uAreaDiffuse * sunVis * max(dot(N, uAreaDir) + uSnowLight.x, 0.0) * uSnowLight.x * wxSnow;
                // the sky fills the shade: what makes a snow shadow blue -- never brighter than the sun it stands
                // in for, or a pale fog sky under a low sun draws the shadow lighter than the lit snow beside it
                vec3 wxFill = uFogSkyColor * (uSnowLight.y * occl);
                const vec3 wxLuma = vec3(0.2126, 0.7152, 0.0722);
                wxFill *= min(1.0, dot(uAreaDiffuse * matDiffuse, wxLuma) * sunNdl * dirOcc / max(dot(wxFill, wxLuma), 1e-4));
                ambient += wxFill * (1.0 - sunVis) * wxSnow;
                // sparkle: a 6 mm cell hash re-rolled as the view turns, the brightest 1.5% of cells glint
                if (uSnowLook.x > 0.0)
                {
                    float wxGl = CxWeatherHash(floor(vWorld.xy * 160.0) + floor(vWorld.z * 160.0) * 0.37 + floor(V.xy * 12.0));
                    specLight += (uAreaDiffuse * sunVis + uAreaAmbient) * smoothstep(0.985, 1.0, wxGl) * uSnowLook.x * wxSnow * 4.0;
                }
            }
