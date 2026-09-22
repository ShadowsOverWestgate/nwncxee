// MUST mirror ShaderFeatures.Displaces: this predicate decides which parts take the displaced clip
// expression, and that one decides which parts the CPU keeps out of the prepass. Disagree and a part
// sits in the prepass computing a clip expression the prepass never compiled.
bool CxDisplaces(int feat) { return feat >= CX_FEAT_WINDGRASS && feat <= CX_FEAT_ROOFKNM; }

vec4 CxFeatPos(CxDraw d, vec3 aPos, out float anchorZ)
{
    vec4 pos = vec4(aPos, 1.0);
    anchorZ = (d.model * pos).z;
    int feat = int(d.feat.x);
    if (feat >= CX_FEAT_WINDGRASS && feat <= CX_FEAT_WINDPLANT)
    {
        float wind = sqrt(dot(uAreaWind.x, uAreaWind.y));
        if (wind == 0.0) wind = 0.356;
        bool plant = feat == CX_FEAT_WINDPLANT;
        float strength = d.feat.z / (plant ? 1000.0 : 100.0);
        float speed = d.feat.y * (feat == CX_FEAT_WINDGRASS ? 20000.0 : 7000.0);
        float phase = (d.model * pos).y * (plant ? 2.0 : 1.0) * float(uTimeOfDay) / speed;
        float force = sin(phase) * strength;
        // leaves have no roots to anchor, so a flat multiplier where the others take a gradient
        force *= feat == CX_FEAT_WINDLEAF ? 5.0 : clamp(aPos.z, 0.0, 1.0) * 10.0;
        pos.x += force * wind;
        pos.y += force * wind;
        if (plant) pos.z += force * wind / 4.0;
        else if (feat == CX_FEAT_WINDLEAF) pos.z += force * wind / 2.0;
        // the tileset-mesh lift: grass and plant meshes are authored under the walkmesh so the
        // toolset will not collide with them. The leaf shader declares no such uniform, and its
        // record's w carries nothing.
        if (!(feat == CX_FEAT_WINDLEAF) && d.feat.w != 0.0) pos.z += d.feat.w;
    }
    else if (feat == CX_FEAT_ROOFKN || feat == CX_FEAT_ROOFKNM)
    {
        // roofknm measures the clip height from the PLAYER, roofkn from the world
        float clip = d.feat.z + (feat == CX_FEAT_ROOFKNM ? uPlayerPos.z : 0.0);
        bool interior = uAreaFlags == 1 || uAreaFlags == 3 || uAreaFlags == 5 || uAreaFlags == 7;
        if ((uCamPos.z < clip || d.feat.w == 1.0) && interior) pos.z += d.feat.y;
        else pos.z -= 500.0;   // out of sight under the floor, for pits and open water
    }
    return pos;
}
