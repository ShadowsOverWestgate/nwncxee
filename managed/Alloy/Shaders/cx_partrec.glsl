// #288: the index build and the depth sort, in one dispatch of one workgroup per draw group.
// What moved. #261 already expanded the six vertices a particle from gl_VertexID
// against the persistent record buffer, so no per-quad geometry has been staged on the CPU since. What
// remained on the CPU was the DRAW-ORDER INDEX LIST -- one write and four bytes a particle, every
// frame, uploaded whole. That is now three numbers an emitter uploaded as a span descriptor, and this
// stage writes the list.
// The sort. Alpha-over is the only class whose result depends on the order two of its own
// particles arrive in, so it is the only class ordered: a shared-memory bitonic network over squared
// distance to the eye, back to front. The other two are left alone deliberately -- punch-through writes
// depth and discards, and addition commutes with itself. #131's rule is that additive does NOT commute
// with alpha-over, and this dispatch cannot break it, because a workgroup only ever touches the index
// range of its own single-class group.
// The network is the textbook one: for each k, for each j, the element at i
// pairs with i ^ j and only the LOWER of the two swaps, so within one step every element belongs
// to exactly one pair and the stride loop cannot race itself. The tail past the particle count is
// padded with +inf, which sorts to the end and is never written back.
// The record and its buffer, as the vertex stage, the velocity arm and the sort read it -- one text, so
// none of them can disagree about the layout RecFloats stages.
// #261: one particle. Seven vec4, std430 -- see Particles.RecFloats for the C# side of this layout.
struct CxParticle
{
    vec4 posCol;      // xyz = near centre (world), w = RGBA8 colour as raw bits
    vec4 farColFar;   // xyz = far centre (world),  w = the far end's colour
    vec4 rightSoft;   // xyz = scaled right axis (world), w = particleSoftness
    vec4 upType;      // xyz = scaled up axis (world),    w = render type
    vec4 centre;      // xyz = the particle's OWN centre, which the facing direction is taken from
    vec4 uv;          // u0, u1, v0, v1
    vec4 prev;        // CX-48: xyz = the centre this particle had LAST frame (its own centre where there is
                      // no previous frame to take one from), w unused
};
layout(std430, binding = 10) readonly buffer CxParticleBuf { CxParticle particles[]; };
