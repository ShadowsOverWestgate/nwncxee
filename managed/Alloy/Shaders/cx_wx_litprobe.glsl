            if (wxSnow > 0.0)
            {
                // scatter: the terminator wraps past the sun-facing edge, the way light travelling inside snow does
                diffuse += uAreaDiffuse * sunVis * max(dot(N, uAreaDir) + uSnowLight.x, 0.0) * uSnowLight.x * wxSnow;
                // the sky fills the shade: what makes a snow shadow blue
                ambient += uFogSkyColor * uSnowLight.y * (1.0 - sunVis) * occl * wxSnow;
                // sparkle: a 6 mm cell hash re-rolled as the view turns, the brightest 1.5% of cells glint
                if (uSnowLook.x > 0.0)
                {
                    float wxGl = CxWeatherHash(floor(vWorld.xy * 160.0) + floor(vWorld.z * 160.0) * 0.37 + floor(V.xy * 12.0));
                    specLight += (uAreaDiffuse * sunVis + uAreaAmbient) * smoothstep(0.985, 1.0, wxGl) * uSnowLook.x * wxSnow * 4.0;
                }
            }
