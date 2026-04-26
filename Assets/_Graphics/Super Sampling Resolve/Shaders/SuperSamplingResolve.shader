Shader "Ledsna/SuperSamplingResolve"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Super Sampling Resolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_PixelPerfectDetailTexture);
            TEXTURE2D(_CameraObjectIDTexture);
            int _SuperSamplingScale;

            bool SameID(float2 a, float2 b)
            {
                return distance(a, b) < 0.1;
            }

            static const float DEPTH_DETAIL_CUTOFF = 50.0;

            // Epsilon for treating two floating-point DETAIL values as the
            // same tier. DETAIL is in [0, 1]; any two values within this
            // distance belong to the same tier for the majority compare.
            static const float DETAIL_TIER_EPSILON = 1.0 / 255.0;

            bool SameDetail(float a, float b)
            {
                return abs(a - b) < DETAIL_TIER_EPSILON;
            }

            float3 ResolveBlock2x2(int2 base)
            {
                float3 color[4];
                float  linDepth[4];
                float2 objID[4];
                float  detail[4];

                int i = 0;
                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++, i++)
                {
                    int2 pos       = base + int2(x, y);
                    color[i]       = LOAD_TEXTURE2D(_BlitTexture,               pos).rgb;
                    linDepth[i]    = LinearEyeDepth(LOAD_TEXTURE2D(_CameraDepthTexture, pos).r, _ZBufferParams);
                    objID[i]       = LOAD_TEXTURE2D(_CameraObjectIDTexture,     pos).rg;
                    detail[i]      = LOAD_TEXTURE2D(_PixelPerfectDetailTexture, pos).r;
                }

                // Far away? Just average everything.
                float closestLin = linDepth[0];
                [unroll] for (int i = 1; i < 4; i++)
                    closestLin = min(closestLin, linDepth[i]);

                if (closestLin > DEPTH_DETAIL_CUTOFF)
                    return (color[0] + color[1] + color[2] + color[3]) * 0.25;

                // Count pixels with ANY detail (detail > 0).
                int detailCount = 0;
                [unroll] for (int i = 0; i < 4; i++)
                    if (detail[i] > 0.0) detailCount++;

                
                    // bred bliat

                                        // if (detailCount < 2)
                                        //     {
                                        //         // average just non-detail pixels
                                        //         float3 sum = 0;
                                        //         int count = 0;
                                        //         [unroll] for (int i = 0; i < 4; i++)
                                        //             if (detail[i] <= 0.0)
                                        //             {sum += color[i];
                                        //             count++;
                                        //             }
                                        //         return sum / count;
                                        //     }

                                        // else if (detailCount == 2) {
                                        //     // return closest pixel
                                        //     float3 closestColor = color[0];
                                        //     float closestDepth = linDepth[0];
                                        //     [unroll] for (int i = 1; i < 4; i++)
                                        //     {
                                        //         if (linDepth[i] <= closestDepth)
                                        //         {closestColor = color[i];
                                        //         closestDepth = linDepth[i];
                                        //         }
                                        //     }
                                        //     return closestColor;

                                        // }

                                        // else if (detailCount > 2)
                                        // {
                                        //     // average just detail pixels
                                        //         float3 sum = 0;
                                        //         int count = 0;
                                        //         [unroll] for (int i = 0; i < 4; i++)
                                        //             if (detail[i] > 0.0)
                                        //             {sum += color[i];
                                        //             count++;
                                        //             }
                                        //         return sum / count;
                                        // }




                if (detailCount <= 0)
                    return (color[0] + color[1] + color[2] + color[3]) * 0.25;

                // A single detail pixel: just pick the closest-depth pixel.
                // if (detailCount < 2)
                // {
                //     float3 closestColor = color[0];
                //     float  closestDepth = linDepth[0];
                //     [unroll] for (int i = 1; i < 4; i++)
                //     {
                //         if (detail[i] > 0.0)
                //         {
                //             return color[i];
                //         }
                //     }
                // }

                if (detailCount < 2)
                {
                    float3 closestColor = color[0];
                    float closestDepth = linDepth[0];
                    [unroll] for (int i = 1; i < 4; i++)
                    {
                        if (linDepth[i] <= closestLin)
                        {
                            closestColor = color[i];
                            closestDepth = linDepth[i];
                        }
                    }
                    return closestColor;
                }

                // ── Stage 1: pick the winning OBJECT ID by majority among
                //    detail-bearing pixels (tie-broken by closest depth).
                int votes[4] = { 0, 0, 0, 0 };
                [unroll] for (int a = 0; a < 4; a++)
                {
                    if (detail[a] <= 0.0) continue;
                    votes[a] = 1;
                    [unroll] for (int b = a + 1; b < 4; b++)
                    {
                        if (detail[b] <= 0.0) continue;
                        if (SameID(objID[a], objID[b]))
                            { votes[a]++; votes[b]++; }
                    }
                }

                int maxVotes = 0;
                [unroll] for (int i = 0; i < 4; i++)
                    maxVotes = max(maxVotes, votes[i]);

                int    topIDCount = 0;
                float2 topIDs[4];
                [unroll] for (int i = 0; i < 4; i++)
                {
                    if (votes[i] != maxVotes) continue;
                    bool already = false;
                    for (int j = 0; j < topIDCount; j++)
                        if (SameID(objID[i], topIDs[j])) already = true;
                    if (!already)
                        topIDs[topIDCount++] = objID[i];
                }

                float2 winnerID = topIDs[0];
                if (topIDCount > 1)
                {
                    float closestTie = 1e30;
                    [unroll] for (int j = 0; j < topIDCount; j++)
                    {
                        [unroll] for (int i = 0; i < 4; i++)
                        {
                            if (detail[i] > 0.0 && SameID(objID[i], topIDs[j]) && linDepth[i] < closestTie)
                            {
                                closestTie = linDepth[i];
                                winnerID  = topIDs[j];
                            }
                        }
                    }
                }

                // ── Stage 2: among the winning object's detail pixels, find
                //    the HIGHEST DETAIL TIER and average only those pixels.
                //
                //    "Tier" = a unique detail value (within an epsilon).
                //    We scan the winning-object detail values, track the
                //    max tier, and also count how many pixels sit at that
                //    tier. The final color is the mean of those pixels.
                float maxTier = -1.0;
                [unroll] for (int i = 0; i < 4; i++)
                {
                    if (detail[i] > 0.0 && SameID(objID[i], winnerID) && detail[i] > maxTier)
                        maxTier = detail[i];
                }

                float3 winnerSum   = 0;
                int    winnerCount = 0;
                [unroll] for (int i = 0; i < 4; i++)
                {
                    if (detail[i] > 0.0
                        && SameID(objID[i], winnerID)
                        && SameDetail(detail[i], maxTier))
                    {
                        winnerSum += color[i];
                        winnerCount++;
                    }
                }

                return winnerSum / max(winnerCount, 1);
            }

            half4 frag(Varyings input) : SV_Target
            {
                int2 outputPos = int2(input.positionCS.xy);
                int2 basePos   = outputPos * _SuperSamplingScale;

                float3 resolvedColor;

                if (_SuperSamplingScale <= 2)
                {
                    resolvedColor = ResolveBlock2x2(basePos);
                }
                else
                {
                    int h = _SuperSamplingScale / 2;
                    float3 q0 = ResolveBlock2x2(basePos);
                    float3 q1 = ResolveBlock2x2(basePos + int2(h, 0));
                    float3 q2 = ResolveBlock2x2(basePos + int2(0, h));
                    float3 q3 = ResolveBlock2x2(basePos + int2(h, h));
                    resolvedColor = (q0 + q1 + q2 + q3) * 0.25;
                }

                return half4(resolvedColor, 1.0);
            }
            ENDHLSL
        }
    }
}
