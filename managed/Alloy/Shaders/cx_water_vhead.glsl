#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec3 aNrm;

// #119: the same clip expression, under the same qualifier, as every other Alloy vertex stage. Water
// is not in the depth prepass, so nothing re-tests these depths -- but the stage no longer
// displaces, so there is also no reason for it to differ.
invariant gl_Position;
