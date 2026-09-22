#ifndef CX_INC_STANDARD
#define CX_INC_STANDARD
#include "inc_scriptable"
void SetupStandardShaderInputs() { FragmentColor *= CxHostTexel; }
void ApplyStandardShader() { }
#endif
