struct CxView { mat4 vp; vec4 a; vec4 b; vec4 c; };   // a = focus/reach, b = dir/along, c = receiver/point/set/layer
layout(std430, binding = 20) readonly buffer CxBakeViews { CxView uViewTbl[]; };
