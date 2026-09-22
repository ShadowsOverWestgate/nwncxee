uniform mat4 uVP;
uniform int uDrawBase;
out vec2 vUV;
out vec3 vWorld;
out vec3 vNrm;
out float vDepth;
flat out int vDraw;

void main()
{
    int id = CxSlot(uDrawBase + gl_DrawID);
    CxDraw d = uDraw[id];
    vWorld = (d.model * vec4(aPos, 1.0)).xyz;
    vNrm = mat3(d.model) * aNrm;
    vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
    vDraw = id;
    gl_Position = uVP * d.model * vec4(aPos, 1.0);
    vDepth = gl_Position.w;
}
