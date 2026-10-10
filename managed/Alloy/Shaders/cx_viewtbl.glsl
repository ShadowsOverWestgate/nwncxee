// d = detail thresholds (bound for level 0 / for level 1), two spare
struct CxView { mat4 vp; vec4 a; vec4 b; vec4 c; vec4 d; };   // a = focus/reach, b = dir/along, c = receiver/point/set/layer
layout(std430, binding = 20) readonly buffer CxBakeViews { CxView uViewTbl[]; };
