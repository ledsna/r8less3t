#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LightCookie/LightCookie.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Clustering.hlsl"

#ifndef QUANTIZE_INCLUDED
#define QUANTIZE_INCLUDED

float3 RGBtoHSV(float3 In)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 P = lerp(float4(In.bg, K.wz), float4(In.gb, K.xy), step(In.b, In.g));
    float4 Q = lerp(float4(P.xyw, In.r), float4(In.r, P.yzx), step(P.x, In.r));
    float D = Q.x - min(Q.w, Q.y);
    float  E = 1e-10;
    return float3(abs(Q.z + (Q.w - Q.y)/(6.0 * D + E)), D / (Q.x + E), Q.x);
}

float3 HSVtoRGB(float3 In)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 P = abs(frac(In.xxx + K.xyz) * 6.0 - K.www);
    return In.z * lerp(K.xxx, saturate(P - K.xxx), In.y);
}

float SmoothRound(float x, float r)
{
    float p = 1 / (1 - r);
    float a = pow(2 * x, p);
    float b = 2 - pow(2 - 2 * x, p);
    float c = sign(x - 0.5) + 1;
    float d = (a * (2 - c) + b * c) / 4;
    return d;
}

real3 NonZero(real3 vec)
{
    return real3(
        max(vec.x, 1e-6),
        max(vec.y, 1e-6),
        max(vec.z, 1e-6));
}

float3 DeferredCel(float3 final_color, float3 base_color, float palette_size)
{
    float3 f = RGBtoHSV(final_color.rgb / NonZero(base_color.rgb));
    // hue
    f.r *= palette_size;
    f.r = floor(f.r) + SmoothRound(frac(f.r), 1);
    f.r /= palette_size;

    // saturation
    f.g *= 10;
    f.g = SmoothRound(frac(f.g), 1) + floor(f.g);
    f.g /= 10;
    // f.g  = Quantize(_SaturationSteps, f.g);

    // value
    float v = log2(f.b);
    v *= .5;
    v = floor(v) + SmoothRound(frac(v), 1);
    v /= .5;
    f.b = pow(2, v);
    return NonZero(base_color.rgb) * HSVtoRGB(f);
}

real3 QuantizeDirectionSpherical(real3 dir, real levelsTheta, real levelsPhi)
{
    real theta = acos(clamp(dir.y, -1.0, 1.0));      // elevation
    real phi   = atan2(dir.z, dir.x);                // azimuth

    theta = floor(theta * levelsTheta / PI) * (PI / levelsTheta);
    phi   = floor((phi + PI) * levelsPhi / (2.0 * PI)) * (2.0 * PI / levelsPhi) - PI;

    float3 result;
    result.x = sin(theta) * cos(phi);
    result.y = cos(theta);
    result.z = sin(theta) * sin(phi);
    return result;
}


real Quantize(real steps, real shade)
{
    if (steps == -1) return shade;
    if (steps == 0) return 0;
    if (steps == 1) return 1;

    shade = round(shade * (steps - .5)) / (steps - 1);
    return shade;
}

real3 Quantize(real steps, real3 shade)
{
    if (steps == -1) return shade;
    if (steps == 0) return 0;
    if (steps == 1) return 1;

    shade = round(shade * (steps - .5)) / (steps - 1);
    return shade;
}
#endif
