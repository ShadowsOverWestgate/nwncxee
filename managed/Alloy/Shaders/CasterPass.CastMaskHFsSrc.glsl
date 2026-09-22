#include "CasterPass.CastMaskHead.glsl"
flat in int vDraw;
void main()
{
#ifdef CX_BINDLESS
    // a handle zeroed under a dying texture: opaque until the record is re-resolved, never sampled
    if (uTexH[vDraw << 3] == uvec2(0u)) return;
#endif
#include "CasterPass.CastMaskCut.glsl"
}
