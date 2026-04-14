#ifndef OUTLINES_INCLUDED
#define OUTLINES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

TEXTURE2D(_CameraObjectIDTexture);

SamplerState point_clamp_sampler;
float2 _PixelResolution;


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

    // The face more aligned with the camera gets the outline
    half3 camForward = UNITY_MATRIX_V[2].xyz;
    half facingness = dot(normal, camForward);

    [unroll]
    for (int j = 0; j < 4; ++j)
    {
        half3 neighbour_normal = GetNormal(neighbours[j]);
        half3 normal_diff = normal - neighbour_normal;

        half nFacingness = dot(neighbour_normal, camForward);
        half normal_diff_weight = step(nFacingness, facingness);

        normal_diff_sum += dot(normal_diff, normal_diff) * normal_diff_weight;
    }
}

float2 GetObjectID(float2 uv)
{
    // Use Unity's built-in Point Clamp sampler to guarantee no filtering across edges
    return SAMPLE_TEXTURE2D(_CameraObjectIDTexture, sampler_PointClamp, uv).rg;
}

void GetObjectIDDiffSum(float2 id, float depth, half2 neighbours[4], out float id_diff_sum)
{
    id_diff_sum = 0;
    [unroll]
    for (int k = 0; k < 4; ++k)
    {
        float2 nID    = GetObjectID(neighbours[k]);
        float nDepth = GetDepth(neighbours[k]);

        // distance() works for float2. If the ID or SubmeshID differs by more than 0.1, it's an edge.
        float isDifferent = step(0.1, distance(nID, id));
        float isForeground = step(depth, nDepth); 
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
    
    if ((id_edge > 0.0 || depth_edge > 0.0) && _External)
        outlineType = 1.0h;
    else if (normal_edge > 0.0h && (depth_diff_sum >= 0.0h && _Convex || depth_diff_sum < 0.0h && _Concave))
        outlineType = 2.0h;
    
    return outlineType;
}

#endif