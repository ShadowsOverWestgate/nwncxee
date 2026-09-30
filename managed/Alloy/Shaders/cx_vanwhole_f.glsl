#ifndef CX_INC_STANDARD
#define CX_INC_STANDARD
#include "inc_scriptable"
#include "inc_noir"
// inc_standard.shd's own two entry points, for a stage Alloy runs whole: setup takes the display-space texel into
// WORKING space and multiplies it in (`MainTextureColor = ApplyColorSpace(...)`, `FragmentColor *=
// MainTextureColor`), and the apply call's whole body under LIGHTING 0 / FOG 0 / KEYHOLING 0 is the revert.
void SetupStandardShaderInputs() { FragmentColor *= ApplyColorSpace(CxWholeTexel); }
void ApplyStandardShader() { FragmentColor.rgb = RevertColorSpace(FragmentColor.rgb); }
#endif
