// Struct Data From CPU (must match C# GrassData struct layout exactly!)
struct GrassData
{
    float3 position;
    float3 normal;
    float2 lightmapUV;
    int materialIndex;
};

StructuredBuffer<GrassData> _SourcePositionGrass;
StructuredBuffer<uint> _VisibleInstanceIndices;

float _Scale;
float _WildGrassChance;
float _UseVisibleInstanceIndices;
// Inputs
float4x4 m_RS;
// Globals
float3 normalWS;
float3 positionWS;
float2 lightmapUV;
int materialIndex;
int textureIndex;
int isWildGrass;

// Flower properties (per-material)
float _FlowerSizeMultiplier;
float _FlowerSizeVariation;

// Simple hash for random values
float HashSimple(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Is called for each instance before vertex stage
void Setup()
{
    #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    InitIndirectDrawArgs(0);
    uint indirectInstanceID = GetIndirectInstanceID_Base(unity_InstanceID);
    uint instanceID = _UseVisibleInstanceIndices > 0.5 ? _VisibleInstanceIndices[indirectInstanceID] : indirectInstanceID;
    GrassData instanceData = _SourcePositionGrass[instanceID];

    normalWS = instanceData.normal;
    positionWS = instanceData.position;
    lightmapUV = instanceData.lightmapUV;
    materialIndex = instanceData.materialIndex;

    float instanceScale = _Scale;

    #if defined(_USE_TEXTURE_COLOR)
        float flowerScaleHash = HashSimple(positionWS + float3(13.7, 29.1, 5.3));
        float flowerScaleVariation = lerp(1.0 - _FlowerSizeVariation, 1.0 + _FlowerSizeVariation, flowerScaleHash);
        instanceScale *= _FlowerSizeMultiplier * flowerScaleVariation;
    #endif

    // Set position first
    unity_ObjectToWorld._m03_m13_m23_m33 = float4(positionWS + instanceData.normal * instanceScale / 2 , 1.0);

    // Apply the scale to the transformation matrix
    unity_ObjectToWorld._m00_m11_m22 = float3(instanceScale, instanceScale, instanceScale);
    unity_ObjectToWorld = mul(unity_ObjectToWorld, m_RS);

    // Slight random Y rotation to break camera-facing uniformity
    float rotHash = HashSimple(positionWS + float3(42.0, 0.0, 17.0));
    float yRot = (rotHash - 0.5) * 0.15; // +-0.075 radians (~4.3 degrees)
    float cy = cos(yRot); float sy = sin(yRot);
    float3x3 yRotMat = float3x3(
        cy, 0, sy,
         0, 1,  0,
       -sy, 0, cy
    );
    unity_ObjectToWorld._m00_m10_m20 = mul(yRotMat, unity_ObjectToWorld._m00_m10_m20);
    unity_ObjectToWorld._m02_m12_m22 = mul(yRotMat, unity_ObjectToWorld._m02_m12_m22);

    uint hashX = asuint(positionWS.x) * 1664525u;
    uint hashZ = asuint(positionWS.z) * 1013904223u;
    uint texHash = hashX ^ hashZ;
    textureIndex = int(texHash % 8u);

    float wildHash = frac(sin(dot(positionWS.xz, float2(12.9898, 78.233))) * 43758.5453);
    isWildGrass = wildHash < _WildGrassChance;

    positionWS += normalWS * 0.2;
    #endif
}
