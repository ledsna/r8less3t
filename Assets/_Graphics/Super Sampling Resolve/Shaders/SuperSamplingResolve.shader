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
            #pragma multi_compile_fragment _ _RESOLVE_NORMALS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_PixelPerfectTexture);

            #ifdef _RESOLVE_NORMALS
            TEXTURE2D(_CameraNormalsTexture);
            #endif

            int _SuperSamplingScale;

            struct FragOutput
            {
                float4 color  : SV_Target0;
                float4 depth  : SV_Target1;
                #ifdef _RESOLVE_NORMALS
                float4 normal : SV_Target2;
                #endif
            };

            // Result of resolving a 2x2 block via 3-tier pixel-perfect logic
            struct BlockResult
            {
                float3 color;
                float  rawDepth;
                float  closestLinearDepth; // min linear depth in block (for foreground comparison)
                bool   important;          // true if block contained any pixel-perfect pixels
                #ifdef _RESOLVE_NORMALS
                float4 normal;
                #endif
            };

            // ---------------------------------------------------------------------------
            // Resolve a 2x2 texel block using 3-tier pixel-perfect logic:
            //   >= 2 important → average only the important samples
            //   == 1 important → closest-to-camera sample (foreground wins)
            //   == 0 important → average all 4 samples
            // ---------------------------------------------------------------------------
            BlockResult ResolveBlock2x2(int2 base)
            {
                float3 impColorSum = 0; float impDepthSum = 0; int impCount = 0;
                float3 nrmColorSum = 0; float nrmDepthSum = 0;
                #ifdef _RESOLVE_NORMALS
                float4 impNormalSum = 0;
                float4 nrmNormalSum = 0;
                #endif

                float  closestLin = 1e30;
                float  closestRaw = 0;
                float3 closestCol = 0;
                #ifdef _RESOLVE_NORMALS
                float4 closestNrm = 0;
                #endif

                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++)
                {
                    int2   pos  = base + int2(x, y);
                    float3 c    = LOAD_TEXTURE2D(_BlitTexture, pos).rgb;
                    float  m    = LOAD_TEXTURE2D(_PixelPerfectTexture, pos).r;
                    float  rawD = LOAD_TEXTURE2D(_CameraDepthTexture, pos).r;
                    float  linD = LinearEyeDepth(rawD, _ZBufferParams);
                    #ifdef _RESOLVE_NORMALS
                    float4 n    = LOAD_TEXTURE2D(_CameraNormalsTexture, pos);
                    #endif

                    if (linD < closestLin)
                    {
                        closestLin = linD;
                        closestRaw = rawD;
                        closestCol = c;
                        #ifdef _RESOLVE_NORMALS
                        closestNrm = n;
                        #endif
                    }

                    if (m > 0.5)
                    {
                        impColorSum += c; impDepthSum += rawD; impCount++;
                        #ifdef _RESOLVE_NORMALS
                        impNormalSum += n;
                        #endif
                    }
                    else
                    {
                        nrmColorSum += c; nrmDepthSum += rawD;
                        #ifdef _RESOLVE_NORMALS
                        nrmNormalSum += n;
                        #endif
                    }
                }

                BlockResult r;
                r.closestLinearDepth = closestLin;
                r.important = (impCount > 0);

                if (impCount >= 2)
                {
                    float inv   = 1.0 / impCount;
                    r.color     = impColorSum * inv;
                    r.rawDepth  = impDepthSum * inv;
                    #ifdef _RESOLVE_NORMALS
                    r.normal    = impNormalSum * inv;
                    r.normal.xyz = normalize(r.normal.xyz);
                    #endif
                }
                else if (impCount == 1)
                {
                    r.color    = closestCol;
                    r.rawDepth = closestRaw;
                    #ifdef _RESOLVE_NORMALS
                    r.normal   = closestNrm;
                    #endif
                }
                else
                {
                    r.color    = nrmColorSum * 0.25;
                    r.rawDepth = nrmDepthSum * 0.25;
                    #ifdef _RESOLVE_NORMALS
                    r.normal    = nrmNormalSum * 0.25;
                    r.normal.xyz = normalize(r.normal.xyz);
                    #endif
                }

                return r;
            }

            FragOutput frag(Varyings input)
            {
                int2 outputPos = int2(input.positionCS.xy);
                int2 basePos   = outputPos * _SuperSamplingScale;

                float3 resolvedColor;
                float  resolvedDepth;
                #ifdef _RESOLVE_NORMALS
                float4 resolvedNormal;
                #endif

                if (_SuperSamplingScale <= 2)
                {
                    // ---- Scale 2x: single 2x2 resolve ----
                    BlockResult r = ResolveBlock2x2(basePos);
                    resolvedColor = r.color;
                    resolvedDepth = r.rawDepth;
                    #ifdef _RESOLVE_NORMALS
                    resolvedNormal = r.normal;
                    #endif
                }
                else
                {
                    // ---- Scale 4x: hierarchical two-level resolve ----
                    //  Level 1: resolve four independent 2x2 quadrants
                    //  Level 2: 3-tier resolve over the four block results
                    int h = _SuperSamplingScale / 2;

                    BlockResult blocks[4];
                    blocks[0] = ResolveBlock2x2(basePos);
                    blocks[1] = ResolveBlock2x2(basePos + int2(h, 0));
                    blocks[2] = ResolveBlock2x2(basePos + int2(0, h));
                    blocks[3] = ResolveBlock2x2(basePos + int2(h, h));

                    // Classify blocks as important/normal, track closest
                    float3 impColorSum = 0; float impDepthSum = 0; int impCount = 0;
                    float3 nrmColorSum = 0; float nrmDepthSum = 0; int nrmCount = 0;
                    #ifdef _RESOLVE_NORMALS
                    float4 impNormalSum = 0;
                    float4 nrmNormalSum = 0;
                    #endif

                    float  closestLin = 1e30;
                    float3 closestCol = 0;
                    float  closestRaw = 0;
                    #ifdef _RESOLVE_NORMALS
                    float4 closestNrm = 0;
                    #endif

                    [unroll] for (int i = 0; i < 4; i++)
                    {
                        if (blocks[i].closestLinearDepth < closestLin)
                        {
                            closestLin = blocks[i].closestLinearDepth;
                            closestRaw = blocks[i].rawDepth;
                            closestCol = blocks[i].color;
                            #ifdef _RESOLVE_NORMALS
                            closestNrm = blocks[i].normal;
                            #endif
                        }

                        if (blocks[i].important)
                        {
                            impColorSum += blocks[i].color;
                            impDepthSum += blocks[i].rawDepth;
                            impCount++;
                            #ifdef _RESOLVE_NORMALS
                            impNormalSum += blocks[i].normal;
                            #endif
                        }
                        else
                        {
                            nrmColorSum += blocks[i].color;
                            nrmDepthSum += blocks[i].rawDepth;
                            nrmCount++;
                            #ifdef _RESOLVE_NORMALS
                            nrmNormalSum += blocks[i].normal;
                            #endif
                        }
                    }

                    // Same 3-tier logic at the block level
                    if (impCount >= 2)
                    {
                        float inv      = 1.0 / impCount;
                        resolvedColor  = impColorSum * inv;
                        resolvedDepth  = impDepthSum * inv;
                        #ifdef _RESOLVE_NORMALS
                        resolvedNormal = impNormalSum * inv;
                        resolvedNormal.xyz = normalize(resolvedNormal.xyz);
                        #endif
                    }
                    else if (impCount == 1)
                    {
                        resolvedColor  = closestCol;
                        resolvedDepth  = closestRaw;
                        #ifdef _RESOLVE_NORMALS
                        resolvedNormal = closestNrm;
                        #endif
                    }
                    else
                    {
                        float inv      = 1.0 / max(nrmCount, 1);
                        resolvedColor  = nrmColorSum * inv;
                        resolvedDepth  = nrmDepthSum * inv;
                        #ifdef _RESOLVE_NORMALS
                        resolvedNormal = nrmNormalSum * inv;
                        resolvedNormal.xyz = normalize(resolvedNormal.xyz);
                        #endif
                    }
                }

                FragOutput output;
                output.color = float4(resolvedColor, 1.0);
                output.depth = float4(resolvedDepth, 0, 0, 0);
                #ifdef _RESOLVE_NORMALS
                output.normal = resolvedNormal;
                #endif
                return output;
            }
            ENDHLSL
        }
    }
}
