#version 460 core
layout(local_size_x = 1) in;
uniform vec2 uRange;    // (log2 floor, log2 span)
uniform vec4 uParams;   // dt, min gain, max gain, key
uniform vec2 uRates;    // 1/s: gain rising, gain falling
#include "PostChain.AutoBufLib.glsl"
void main()
{
    float sum = 0.0, n = 0.0;
    for (int i = 1; i < 64; i++)
    {
        float c = float(uHistBin[i]);
        sum += c * (float(i - 1) + 0.5) / 62.0;
        n += c;
    }
    if (n <= 0.0) { if (uAutoGain <= 0.0) uAutoGain = clamp(1.0, uParams.y, uParams.z); return; }
    float avg = exp2(uRange.x + sum / n * uRange.y);
    float target = clamp(uParams.w / avg, uParams.y, uParams.z);
    float g = uAutoGain;
    if (g <= 0.0) g = target;
    else
    {
        float rate = target > g ? uRates.x : uRates.y;
        g += (target - g) * (1.0 - exp(-rate * uParams.x));
    }
    uAutoGain = g;
}
