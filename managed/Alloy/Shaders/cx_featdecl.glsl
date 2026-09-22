#define CX_FEAT_WINDGRASS 1
#define CX_FEAT_WINDLEAF  2
#define CX_FEAT_WINDPLANT 3
#define CX_FEAT_ROOFKN    4
#define CX_FEAT_ROOFKNM   5
#define CX_FEAT_FLOWMAP   7
#define CX_FEAT_SKIN      8
#define CX_FEAT_NOIRWET   9
#define CX_FEAT_NOIRGLOSS 10
#define CX_FEAT_GROUNDWEATHER 11
uniform int uTimeOfDay;        // worldtimerTimeOfDay, the engine's own int
uniform float uLodBias;        // #391: log2(render scale), <= 0; every material sample takes it
uniform vec3 uAreaWind;        // areaGlobalWind
uniform int uAreaFlags;        // areaFlags
uniform vec3 uCamPos;          // cameraPosition
uniform vec3 uPlayerPos;       // playerPosition
uniform vec3 uCamFocus;        // cameraFocus -- the camera PIVOT (runtime +0x90), not uCamPos
