#version 460 core
// CX-102: one part of a rain crown. Index 0 is a ring on the ground, the rest are droplet streaks; both are
// scatter, premultiplied like the streak and the flake, so an unlit impact is nothing rather than a grey
// mark on the stone. No contact fade here: a splash SITS on the surface, and the fade that saves a falling
// streak from a hard intersection line would erase the whole thing. The hardware depth test is what puts a
// splash behind a wall in front of it.
in vec2 vUV;
in float vSeed;
in float vAge;
in float vPart;
in vec3 vLight;
uniform vec3 uTint;
uniform float uAlpha;
out vec4 frag;

/// Rec.709 luminance: how much of its own scatter a splash also occludes.
const vec3 CX_WX_LUMA = vec3(0.2126, 0.7152, 0.0722);
/// Half-width of the ring's crest, as a share of the quad's half-extent. One world width at every radius.
/// The quad's half-extent is 1.15 reaches, so the crest is 2 x 0.13 x 1.15 = 0.3 of a reach -- which is the
/// vertex stage's CX_SPLASH_CREST, the share its footprint fade measures. Move one and move the other.
const float CX_SPLASH_RINGW = 0.13;
/// Where the crest has got to by the end of the life, in the same share. Short of 1 so the ring dies inside
/// its own quad instead of being clipped by its edge.
const float CX_SPLASH_RINGR = 0.87;
/// The ring is the faint half of an impact: the crown is what the eye reads, the ring only says "on water".
const float CX_SPLASH_RINGA = 0.5;

void main()
{
    float a;
    if (vPart < 0.5)
    {
        float r = length(vUV);
        if (r > 1.0) discard;
        // sqrt in time, damped: the crest leaves fast, slows as it spreads and is gone by the life's end
        float u = (r - CX_SPLASH_RINGR * sqrt(vAge)) / CX_SPLASH_RINGW;
        a = exp(-u * u) * (1.0 - vAge) * CX_SPLASH_RINGA;
    }
    else
    {
        float s = (1.0 - vUV.x * vUV.x) * (1.0 - vUV.y * vUV.y);
        a = s * s * (1.0 - vAge * vAge);
    }
    if (a <= 0.01) discard;
    // R1: premultiplied scatter, blended ONE / ONE_MINUS_SRC_ALPHA -- the precipitation stages' contract.
    // rgb is the light this part throws at the eye weighted by its coverage; alpha is the extinction behind
    // it, riding the SAME scatter, so a splash with no light on it neither adds nor hides anything.
    float cov = a * vSeed;
    vec3 scatter = uTint * vLight;
    frag = vec4(scatter * cov, cov * uAlpha * min(dot(scatter, CX_WX_LUMA), 1.0));
}
