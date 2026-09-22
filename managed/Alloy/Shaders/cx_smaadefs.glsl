// What the two SMAA passes and the blend stage share: the GLSL 4 port, the preset the option
// picked, and the reference library. Goes after a head that opened the file and before the pass body.
uniform vec4 uRt;            // (1/w, 1/h, w, h) of the frame -- SMAA_RT_METRICS
#define SMAA_RT_METRICS uRt
#define SMAA_GLSL_4 1
#define CX_AA_LEVEL $AA$
#if CX_AA_LEVEL >= 3
#define SMAA_PRESET_ULTRA 1
#elif CX_AA_LEVEL == 2
#define SMAA_PRESET_HIGH 1
#else
#define SMAA_PRESET_MEDIUM 1
#endif
#include "cx_smaa.glsl"
