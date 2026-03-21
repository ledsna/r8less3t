#ifndef OUTLINES_INCLUDED
#define OUTLINES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

TEXTURE2D(_ObjectIDTexture);

SamplerState point_clamp_sampler;
float2 _PixelResolution;

static const half3 NORMAL_EDGE_BIAS = half3(0.57735, 0.57735, 0.57735); // normalize(1,1,1)

half GetDepth(half2 uv)
{
    half rawDepth = SampleSceneDepth(uv);
    if (!unity_OrthoParams.w)
    {
        return LinearEyeDepth(rawDepth, _ZBufferParams);
    }
    half orthoLinearDepth = _ProjectionParams.x > 0 ? rawDepth : 1 - rawDepth;
    half orthoEyeDepth = lerp(_ProjectionParams.y, _ProjectionParams.z, orthoLinearDepth);
    return orthoEyeDepth;
}

half3 GetNormal(half2 uv)
{
    return SampleSceneNormals(uv);
}

void GetNeighbourUVs(half2 uv, half distance, out half2 neighbours[4])
{
    // Use actual render target size so outlines are 1 texel wide even under SSAA
    // half2 pixelSize = 1.0 / _PixelResolution / 2;
    half2 pixelSize = 1.0 / _ScaledScreenParams.xy;
    half2 offset = pixelSize * distance;
    
    neighbours[0] = uv + half2(0,  offset.y);  // Top
    neighbours[1] = uv + half2(0, -offset.y);  // Bottom
    neighbours[2] = uv + half2( offset.x, 0);  // Right
    neighbours[3] = uv + half2(-offset.x, 0);  // Left
}

void GetDepthDiffSum(half depth, half2 neighbours[4], out half depth_diff_sum)
{
    depth_diff_sum = 0;
    [unroll]
    for (int i = 0; i < 4; ++i)
        depth_diff_sum += GetDepth(neighbours[i]) - depth;
}

void GetNormalDiffSum(half3 normal, half2 neighbours[4], out half normal_diff_sum)
{
    normal_diff_sum = 0;

    [unroll]
    for (int j = 0; j < 4; ++j)
    {
        half3 neighbour_normal = GetNormal(neighbours[j]);
        half3 normal_diff = normal - neighbour_normal;
        half normal_diff_weight = smoothstep(-.01, .01, dot(normal_diff, NORMAL_EDGE_BIAS));

        normal_diff_sum += dot(normal_diff, normal_diff) * normal_diff_weight;
    }
}

half GetObjectID(half2 uv)
{
    return SAMPLE_TEXTURE2D(_ObjectIDTexture, point_clamp_sampler, uv).r;
}

void GetObjectIDDiffSum(half id, half depth, half2 neighbours[4], out half id_diff_sum)
{
    id_diff_sum = 0;
    [unroll]
    for (int k = 0; k < 4; ++k)
    {
        half nID    = GetObjectID(neighbours[k]);
        half nDepth = GetDepth(neighbours[k]);

        // Only count if: different object AND we are the closer-to-camera pixel
        // (foreground gets the outline, background stays clean)
        half isDifferent = step(0.004, abs(nID - id));
        half isForeground = step(depth, nDepth); // 1 if we're closer or equal
        id_diff_sum += isDifferent * isForeground;
    }
}

half OutlineType(half2 uv)
{
    half2 neighbour_depths[4];
    half2 neighbour_normals[4];
    
    GetNeighbourUVs(uv, _ExternalScale, neighbour_depths);
    GetNeighbourUVs(uv, _InternalScale, neighbour_normals);

    half depth = GetDepth(uv);
    half depth_diff_sum, normal_diff_sum;
    GetDepthDiffSum(depth, neighbour_depths, depth_diff_sum);
    GetNormalDiffSum(GetNormal(uv), neighbour_normals, normal_diff_sum);

    half depth_edge = step(_DepthThreshold / 10000.0h, depth_diff_sum);
    half normal_edge = step(_NormalsThreshold, sqrt(normal_diff_sum));

    // Object ID discontinuity: reuse the depth/external sampling radius
    // Depth-biased: only foreground pixels get the ID edge
    half id_diff_sum;
    GetObjectIDDiffSum(GetObjectID(uv), depth, neighbour_depths, id_diff_sum);
    half id_edge = step(0.5, id_diff_sum); // any foreground neighbour with a different ID triggers

    // Priority: External edges first (depth OR object ID), then Internal edges (normals)
    half outlineType = 0.0h;
    
    if (( depth_edge > 0.0h) && _External)
        outlineType = 1.0h;
    else if (normal_edge > 0.0h && (depth_diff_sum >= 0.0h && _Convex || depth_diff_sum < 0.0h && _Concave))
        outlineType = 2.0h;
    
    return outlineType;
}

#endif