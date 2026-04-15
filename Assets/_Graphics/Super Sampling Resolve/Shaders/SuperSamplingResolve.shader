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

            float3 ResolveBlock2x2(int2 base)
            {
                float3 color[4];
                float linDepth[4];
                float2 objID[4];
                bool hasDetail[4];

                int i = 0;
                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++, i++)
                {
                    int2 pos       = base + int2(x, y);
                    color[i]       = LOAD_TEXTURE2D(_BlitTexture, pos).rgb;
                    linDepth[i]    = LinearEyeDepth(LOAD_TEXTURE2D(_CameraDepthTexture, pos).r, _ZBufferParams);
                    objID[i]       = LOAD_TEXTURE2D(_CameraObjectIDTexture, pos).rg;
                    hasDetail[i]   = LOAD_TEXTURE2D(_PixelPerfectDetailTexture, pos).r > 0.5;
                }

                // Far away? Just average everything
                float closestLin = linDepth[0];
                [unroll] for (int i = 1; i < 4; i++)
                    closestLin = min(closestLin, linDepth[i]);

                if (closestLin > DEPTH_DETAIL_CUTOFF)
                    return (color[0] + color[1] + color[2] + color[3]) * 0.25;

                // Count detail pixels
                int detailCount = 0;
                [unroll] for (int i = 0; i < 4; i++)
                    if (hasDetail[i]) detailCount++;

                if (detailCount == 0)
                    return (color[0] + color[1] + color[2] + color[3]) * 0.25;

                // Less than 2 detail? Pick closest to camera
                if (detailCount < 2)
                {
                    float3 closestColor = color[0];
                    [unroll] for (int i = 1; i < 4; i++)
                    {
                        if (linDepth[i] <= closestLin)
                            closestColor = color[i];
                    }
                    return closestColor;
                }

                // 2+ detail: vote for majority Object ID among detail pixels
                int votes[4] = { 0, 0, 0, 0 };
                [unroll] for (int a = 0; a < 4; a++)
                {
                    if (!hasDetail[a]) continue;
                    votes[a] = 1;
                    [unroll] for (int b = a + 1; b < 4; b++)
                    {
                        if (!hasDetail[b]) continue;
                        if (SameID(objID[a], objID[b]))
                            { votes[a]++; votes[b]++; }
                    }
                }

                int maxVotes = 0;
                [unroll] for (int i = 0; i < 4; i++)
                    maxVotes = max(maxVotes, votes[i]);

                // Count how many distinct IDs hold maxVotes
                int topIDCount = 0;
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

                // Tie? Pick closest object among tied IDs
                if (topIDCount > 1)
                {
                    float closestTie = 1e30;
                    int winnerIdx = 0;
                    [unroll] for (int j = 0; j < topIDCount; j++)
                    {
                        [unroll] for (int i = 0; i < 4; i++)
                        {
                            if (hasDetail[i] && SameID(objID[i], topIDs[j]) && linDepth[i] < closestTie)
                            {
                                closestTie = linDepth[i];
                                winnerIdx = j;
                            }
                        }
                    }

                    float3 sum = 0; int count = 0;
                    [unroll] for (int i = 0; i < 4; i++)
                    {
                        if (hasDetail[i] && SameID(objID[i], topIDs[winnerIdx]))
                        {
                            sum += color[i];
                            count++;
                        }
                    }
                    return sum / max(count, 1);
                }

                // Clear winner: average its detail samples
                float3 winnerSum = 0;
                int winnerCount = 0;
                [unroll] for (int i = 0; i < 4; i++)
                {
                    if (hasDetail[i] && SameID(objID[i], topIDs[0]))
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
