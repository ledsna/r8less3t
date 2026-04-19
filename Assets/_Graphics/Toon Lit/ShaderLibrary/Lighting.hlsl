#ifndef UNIVERSAL_LIGHTING_INCLUDED
#define UNIVERSAL_LIGHTING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Debug/Debugging3D.hlsl"
// #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
#include "GlobalIllumination.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"
// #include "AmbientOcclusion.hlsl"
#include "Assets/_Graphics/Pixel Art/ShaderLibrary/Outlines.hlsl"

#if defined(LIGHTMAP_ON)
    #define DECLARE_LIGHTMAP_OR_SH(lmName, shName, index) float2 lmName : TEXCOORD##index
    #define OUTPUT_LIGHTMAP_UV(lightmapUV, lightmapScaleOffset, OUT) OUT.xy = lightmapUV.xy * lightmapScaleOffset.xy + lightmapScaleOffset.zw;
    #define OUTPUT_SH4(absolutePositionWS, normalWS, viewDir, OUT, OUT_OCCLUSION)
    #define OUTPUT_SH(normalWS, OUT)
#else
    #define DECLARE_LIGHTMAP_OR_SH(lmName, shName, index) half3 shName : TEXCOORD##index
    #define OUTPUT_LIGHTMAP_UV(lightmapUV, lightmapScaleOffset, OUT)
    #ifdef USE_APV_PROBE_OCCLUSION
        #define OUTPUT_SH4(absolutePositionWS, normalWS, viewDir, OUT, OUT_OCCLUSION) OUT.xyz = SampleProbeSHVertex(absolutePositionWS, normalWS, viewDir, OUT_OCCLUSION)
    #else
        #define OUTPUT_SH4(absolutePositionWS, normalWS, viewDir, OUT, OUT_OCCLUSION) OUT.xyz = SampleProbeSHVertex(absolutePositionWS, normalWS, viewDir)
    #endif
    // Note: This is the legacy function, which does not support APV.
    // Kept to avoid breaking shaders still calling it (UUM-37723)
    #define OUTPUT_SH(normalWS, OUT) OUT.xyz = SampleSHVertex(normalWS)
#endif

///////////////////////////////////////////////////////////////////////////////
//                      Lighting Functions                                   //
///////////////////////////////////////////////////////////////////////////////

float2 ComputeDitherUV(half3 positionWS)
{
    float4 hclipPosition = TransformWorldToHClip(positionWS);
    return hclipPosition.xy / hclipPosition.w * 0.5 + 0.5;
}

float Unity_Dither(float In, float4 ScreenPosition)
{
    float2 uv = ScreenPosition.xy * _ScreenParams.xy;
    float DITHER_THRESHOLDS[16] = 
    {
        1.0 / 17.0, 9.0 / 17.0, 3.0 / 17.0, 11.0 / 17.0,
        13.0 / 17.0, 5.0 / 17.0, 15.0 / 17.0, 7.0 / 17.0,
        4.0 / 17.0, 12.0 / 17.0, 2.0 / 17.0, 10.0 / 17.0,
        16.0 / 17.0, 8.0 / 17.0, 14.0 / 17.0, 6.0 / 17.0
    };
    uint index = (uint(uv.x) % 4) * 4 + uint(uv.y) % 4;
    return In - DITHER_THRESHOLDS[index];
}

bool IsIsophote(half NdotL, half3 viewDirectionWS, half3 normalWS)
{
    half result = 0;

    float bandPhase = NdotL * (_DiffuseSteps - 0.5);
    float2 grad = float2(ddx(bandPhase), ddy(bandPhase));
    
    float pixelDist = 1 + (bandPhase - 0.5 - ceil(bandPhase - 0.5)) / max(abs(grad.x), abs(grad.y));

    uint ringIndex = uint(floor(abs(pixelDist)));

    // Mark last pixel of the lit band (pixelDist > 0) instead of first pixel of shadow band
    result = (ringIndex % 2 == 1 && ringIndex <= _DistanceSteps && pixelDist < 0) ? 1.0 : 0.0;

    // Face-the-camera envelope.
    // Fade the hatch out at grazing (silhouette) angles where it reads as
    // noise rather than shading. N·V is 1 when the surface faces the camera,
    // 0 at the silhouette.
    //
    // NOTE: normalWS and viewDirectionWS should already be unit length by
    // the time they reach here; double-normalizing is a no-op but
    // defensive. Keep the fade range WIDE — on a curved surface most
    // visible pixels sit in the 0.2–0.8 range of N·V, so if you smoothstep
    // above 0.75 you kill the hatch on most of the object.
    half NdotV = saturate(dot(normalWS, viewDirectionWS));
    half alignment = smoothstep(0.5, 1, NdotL);
    result *= smoothstep(0.85, 1, NdotV) * alignment;
    return result > 0;
}

half3 LightingPhysicallyBased(BRDFData brdfData,
    half3 lightColor, half3 lightDirectionWS, float distanceAttenuation, float shadowAttenuation,
    half3 normalWS, half3 viewDirectionWS, half2 normalizedScreenSpaceUV,
    bool specularHighlightsOff, float3 positionWS, inout half isPixelPerfectDetail)
{
    half NdotL = dot(normalWS, lightDirectionWS);
    half s = saturate(_SpecularStep + HALF_MIN);
    half steps = _DiffuseSpecularCelShader ? _DiffuseSteps : -1;

    float camDist = length(positionWS - _WorldSpaceCameraPos);

    // return dot(normalWS, viewDirectionWS);
    // half distFade = saturate((camDist - 50.0) / 150.0); // linear 0→1 from 50 to 100 units

    // if (steps != -1)
    // {
    //     steps = lerp(steps, 30, distFade);
    // }
    // s = lerp(s, HALF_MIN, distFade * 5);
    
    float isophoteRings = 0;

    float dither = 0;

    #if defined(IS_BILLBOARD)
        dither = Unity_Dither(0.5, float4(normalizedScreenSpaceUV, 0, 0));
    #endif

    half qNdotL = Quantize(steps, NdotL + dither / 2 / steps);

    bool isIsophote = IsIsophote(NdotL, viewDirectionWS, normalWS);
    qNdotL += isIsophote / (steps - 1);

    if (isIsophote > 0)
        isPixelPerfectDetail = max(isPixelPerfectDetail, 0.5);

    // Mark band transition pixels (last pixel of the lit band)
    if (steps > 0)
    {
        half baseBand = Quantize(steps, NdotL);
        half bandLow = Quantize(steps, NdotL + fwidth(NdotL));
        if (bandLow != baseBand)
            isPixelPerfectDetail = max(isPixelPerfectDetail, 0.5);
    }

    half inShadow = (shadowAttenuation >= 0.5 ? 1 : 0);

    half3 diffuse = lightColor * saturate(qNdotL) * inShadow * distanceAttenuation;

    // isPixelPerfectDetail = fwidth(inShadow) > 0 ? 1 : isPixelPerfectDetail;

    half3 brdf = brdfData.diffuse;


#ifndef _SPECULARHIGHLIGHTS_OFF
    [branch] if (!specularHighlightsOff)
    {
        half specComponent = max(DirectBRDFSpecular(brdfData, normalWS, lightDirectionWS, viewDirectionWS), HALF_MIN);
        half qSpec =  exp(round(log(specComponent) / s + dither / 2) * s);
        // half s3 =  exp(floor( (log(specComponent) + brdfData.roughness * dither * _SpecularStep) / s) * s);

        brdf += brdfData.specular * qSpec;// * saturate((2 - max(length(fwidth(normalWS)), 1)));
    }
#endif // _SPECULARHIGHLIGHTS_OFF
    return brdf * diffuse;
}

half3 VertexLighting(float3 positionWS, half3 normalWS)
{
    half3 vertexLightColor = half3(0.0, 0.0, 0.0);

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
    uint lightsCount = GetAdditionalLightsCount();
    uint meshRenderingLayers = GetMeshRenderingLayer();

    LIGHT_LOOP_BEGIN(lightsCount)
        Light light = GetAdditionalLight(lightIndex, positionWS);

    #ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
        #endif
    {
        half3 lightColor = light.color * light.distanceAttenuation;
        vertexLightColor += LightingLambert(lightColor, light.direction, normalWS);
    }

    LIGHT_LOOP_END
#endif

    return vertexLightColor;
}

struct LightingData
{
    half3 giColor;
    half3 mainLightColor;
    half3 additionalLightsColor;
    half3 emissionColor;
};

half3 CalculateLightingColor(LightingData lightingData, half outlineType)
{
    half3 lightingColor = 0;

    if (IsOnlyAOLightingFeatureEnabled())
        return lightingData.giColor; // Contains white + AO

    if (IsLightingFeatureEnabled(DEBUGLIGHTINGFEATUREFLAGS_GLOBAL_ILLUMINATION))
        lightingColor += lightingData.giColor;

    // return lightingColor;

    if (IsLightingFeatureEnabled(DEBUGLIGHTINGFEATUREFLAGS_MAIN_LIGHT))
        lightingColor += lightingData.mainLightColor;

    if (IsLightingFeatureEnabled(DEBUGLIGHTINGFEATUREFLAGS_ADDITIONAL_LIGHTS))
        lightingColor += lightingData.additionalLightsColor;

    // blue for outer, red for inner, black for no outline
    if (_DebugOn)
    {
        return lerp(half3(0, 0, 0), half3(floor(outlineType / 2), 0, outlineType % 2), outlineType);
    }

    if (outlineType == 1)
        return lightingColor / (1 + _OutlineStrength);
    if (outlineType == 2)
        return lightingColor * (1 + _OutlineStrength);
    
    // Why is this even here? Could potentially break something, but leaving it for now
    // lightingColor *= albedo;

    if (IsLightingFeatureEnabled(DEBUGLIGHTINGFEATUREFLAGS_EMISSION))
        lightingColor += lightingData.emissionColor;

    return lightingColor;
}
//.
half4 CalculateFinalColor(LightingData lightingData, half outlineType, half alpha)
{
    half3 finalColor = CalculateLightingColor(lightingData, outlineType);

    return half4(finalColor, alpha);
}

//.
LightingData CreateLightingData(InputData inputData, SurfaceData surfaceData)
{
    LightingData lightingData;

    lightingData.giColor = inputData.bakedGI;
    lightingData.emissionColor = surfaceData.emission;
    lightingData.mainLightColor = 0;
    lightingData.additionalLightsColor = 0;

    return lightingData;
}

///////////////////////////////////////////////////////////////////////////////
//                      Fragment Functions                                   //
//       Used by ShaderGraph and others builtin renderers                    //
///////////////////////////////////////////////////////////////////////////////

////////////////////////////////////////////////////////////////////////////////
/// PBR lighting...
////////////////////////////////////////////////////////////////////////////////
half4 UniversalFragmentPBR(InputData inputData, SurfaceData surfaceData, out half isPixelPerfectDetail)
{
    #if defined(_SPECULARHIGHLIGHTS_OFF)
        bool specularHighlightsOff = true;
    #else
        bool specularHighlightsOff = false;
    #endif
    BRDFData brdfData;

    // NOTE: can modify "surfaceData"...
    InitializeBRDFData(surfaceData, brdfData);

    #if defined(DEBUG_DISPLAY)
    half4 debugColor;

    if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
    {
        return debugColor;
    }
    #endif

    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    AmbientOcclusionFactor aoFactorNoAO;
    aoFactorNoAO.indirectAmbientOcclusion = 1;
    aoFactorNoAO.directAmbientOcclusion = 1;
    if (_ReflectionSteps == 120)
        aoFactor = aoFactorNoAO;
    uint meshRenderingLayers = GetMeshRenderingLayer();

    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);

    // NOTE: We don't apply AO to the GI here because it's done in the lighting calculation below...
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    LightingData lightingData = CreateLightingData(inputData, surfaceData);

    half3 directSpecular = inputData.bakedGI * brdfData.diffuse;
    lightingData.giColor = directSpecular * aoFactor.indirectAmbientOcclusion;

    half outlineType = OutlineType(inputData.normalizedScreenSpaceUV);
    isPixelPerfectDetail = outlineType > 0 ? 1.0 : 0.0;

    half3 lightingNormal = inputData.normalWS;

    if (outlineType == 1)
    {
        half3 viewDir = inputData.viewDirectionWS;
        lightingNormal = normalize(lightingNormal - dot(lightingNormal, viewDir) * viewDir);
    }


#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.mainLightColor = LightingPhysicallyBased(brdfData, mainLight.color,
            mainLight.direction, mainLight.distanceAttenuation, mainLight.shadowAttenuation,
            lightingNormal, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV, specularHighlightsOff, inputData.positionWS, isPixelPerfectDetail);
    }

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();

    #if USE_FORWARD_PLUS
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        FORWARD_PLUS_SUBTRACTIVE_LIGHT_CHECK

        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, light.color, light.direction,
                light.distanceAttenuation, light.shadowAttenuation, lightingNormal, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV,
                specularHighlightsOff, inputData.positionWS, isPixelPerfectDetail);
        }
    }
    #endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, light.color, light.direction,
                light.distanceAttenuation, light.shadowAttenuation, lightingNormal, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV,
                specularHighlightsOff, inputData.positionWS, isPixelPerfectDetail);
        }
    LIGHT_LOOP_END
    #endif


    // if (SAMPLE_TEXTURE2D(_CameraNormalsTexture, point_clamp_sampler, inputData.normalizedScreenSpaceUV).a >= 2)
    {
        half3 reflectVector = reflect(-inputData.viewDirectionWS, inputData.normalWS);
        half NoV = dot(inputData.normalWS, inputData.viewDirectionWS);

        reflectVector = QuantizeDirectionSpherical(reflectVector, _ReflectionSteps, _ReflectionSteps);

        half billboardOffset = 0;
        #if defined(IS_BILLBOARD)
            billboardOffset = Unity_Dither(0.5, float4(inputData.normalizedScreenSpaceUV, 0, 0)) / 4 / _FresnelSteps;
        #endif
        half fresnelTerm = Pow4(1.0 - saturate(Quantize(_FresnelSteps, NoV + billboardOffset)));

        half3 indirectSpecular = GlossyEnvironmentReflection(reflectVector, inputData.positionWS, brdfData.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV);

        // lightingData.giColor += indirectSpecular * EnvironmentBRDFSpecular(brdfData, fresnelTerm) * aoFactor.indirectAmbientOcclusion;
        lightingData.giColor += (indirectSpecular * EnvironmentBRDFSpecular(brdfData, fresnelTerm) * aoFactor.indirectAmbientOcclusion) / (outlineType == 0 ? 1 : 4);

    }
    

#if REAL_IS_HALF
    // Clamp any half.inf+ to HALF_MAX
    return min(CalculateFinalColor(lightingData, outlineType, surfaceData.alpha), HALF_MAX);
#else
    return CalculateFinalColor(lightingData, outlineType, surfaceData.alpha);
#endif

}
#endif