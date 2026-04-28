using System;
using System.Collections.Generic;
using UnityEngine;

namespace Grass.Core
{
    [Serializable]
    public class TextureGroup
    {
        public Material material;
        public float weight = 1f;
        public bool useTextureColor;
    }

    public enum GrassVariantKind
    {
        Grass = 0,
        Flower = 1,
        Custom = 2
    }

    [Serializable]
    public class GrassVariant
    {
        public string name = "Variant";
        public Material material;
        public GrassVariantKind kind = GrassVariantKind.Grass;

        [Range(0.001f, 100f)]
        public float weight = 1f;

        [Tooltip("Flowers normally use sprite colors directly. Grass normally uses root material tinting.")]
        public bool useTextureColor;

        [Header("Flower Clumps")]
        [Range(0.001f, 2f)]
        public float clumpScale = 0.12f;

        [Range(0f, 1f)]
        public float clumpThreshold = 0.7f;

        [Range(0f, 1f)]
        public float clumpDensity = 0.2f;

        public int seed;

        [Header("Generated Normal Variation")]
        [Range(0f, 1f)]
        public float normalNudgeProbability = 0.05f;

        [Range(0f, 0.5f)]
        public float normalNudgeStrength = 0.08f;

        public bool IsValid => material != null && weight > 0f;
    }

    [Serializable]
    public class GrassMaterialSystem : ISerializationCallbackReceiver
    {
        public List<GrassVariant> variants = new();

        [SerializeField, HideInInspector] private List<TextureGroup> grassGroups = new();
        [SerializeField, HideInInspector] private List<TextureGroup> flowerGroups = new();
        [SerializeField, HideInInspector] private float clusterScale = 0.1f;
        [SerializeField, HideInInspector] private float flowerClusterScale = 0.05f;
        [SerializeField, HideInInspector] private float grassNormalNudgeProbability = 0.3f;

        [Header("Grass Clustering")]
        [Range(0.001f, 2f)]
        public float grassClusterScale = 0.2f;

        public int TotalMaterialCount => CountValidVariants();

        public bool IsValid()
        {
            return CountValidVariants() > 0;
        }

        public Material[] GetAllMaterials()
        {
            var materials = new List<Material>();

            for (int i = 0; i < variants.Count; i++)
            {
                GrassVariant variant = variants[i];
                if (variant == null || !variant.IsValid)
                    continue;

                ConfigureMaterialKeywords(variant);
                materials.Add(variant.material);
            }

            return materials.ToArray();
        }

        public GrassVariant GetValidVariant(int materialIndex)
        {
            int validIndex = 0;
            for (int i = 0; i < variants.Count; i++)
            {
                GrassVariant variant = variants[i];
                if (variant == null || !variant.IsValid)
                    continue;

                if (validIndex == materialIndex)
                    return variant;

                validIndex++;
            }

            return null;
        }

        public int SelectMaterialIndex(Vector3 worldPosition)
        {
            return SelectVariantIndex(worldPosition);
        }

        public int SelectVariantIndex(Vector3 worldPosition)
        {
            if (!IsValid())
                return 0;

            int flowerIndex = SelectFlowerVariant(worldPosition);
            if (flowerIndex >= 0)
                return flowerIndex;

            return SelectWeightedVariant(worldPosition, GrassVariantKind.Grass, allowCustom: true);
        }

        public void InvalidateCache()
        {
        }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            if (variants.Count > 0)
                return;

            for (int i = 0; i < grassGroups.Count; i++)
            {
                TextureGroup group = grassGroups[i];
                if (group == null || group.material == null)
                    continue;

                variants.Add(new GrassVariant
                {
                    name = $"Migrated Grass {i + 1}",
                    material = group.material,
                    kind = GrassVariantKind.Grass,
                    weight = Mathf.Max(0.001f, group.weight),
                    useTextureColor = group.useTextureColor,
                    normalNudgeProbability = grassNormalNudgeProbability
                });
            }

            for (int i = 0; i < flowerGroups.Count; i++)
            {
                TextureGroup group = flowerGroups[i];
                if (group == null || group.material == null)
                    continue;

                variants.Add(new GrassVariant
                {
                    name = $"Migrated Flower {i + 1}",
                    material = group.material,
                    kind = GrassVariantKind.Flower,
                    weight = Mathf.Max(0.001f, group.weight),
                    useTextureColor = true,
                    clumpScale = Mathf.Max(0.001f, flowerClusterScale),
                    clumpThreshold = 0.75f,
                    clumpDensity = Mathf.Clamp01(group.weight * 20f),
                    seed = i + 1
                });
            }

            grassClusterScale = Mathf.Max(0.001f, clusterScale);
        }

        private int CountValidVariants()
        {
            int count = 0;
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i] != null && variants[i].IsValid)
                    count++;
            }

            return count;
        }

        private int SelectFlowerVariant(Vector3 worldPosition)
        {
            int selectedIndex = -1;
            float bestScore = 0f;
            int validIndex = 0;

            for (int i = 0; i < variants.Count; i++)
            {
                GrassVariant variant = variants[i];
                if (variant == null || !variant.IsValid)
                    continue;

                if (variant.kind == GrassVariantKind.Flower)
                {
                    float scale = Mathf.Max(0.001f, variant.clumpScale);
                    float noise = ValueNoise2D(worldPosition.x * scale + variant.seed * 19.19f,
                        worldPosition.z * scale + variant.seed * 37.37f);
                    float clump = Mathf.InverseLerp(variant.clumpThreshold, 1f, noise);
                    float scatter = RandomHash(worldPosition, variant.seed);
                    float score = scatter <= variant.clumpDensity ? clump * variant.weight : 0f;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        selectedIndex = validIndex;
                    }
                }

                validIndex++;
            }

            return selectedIndex;
        }

        private int SelectWeightedVariant(Vector3 worldPosition, GrassVariantKind requiredKind, bool allowCustom)
        {
            float totalWeight = 0f;
            int validIndex = 0;

            for (int i = 0; i < variants.Count; i++)
            {
                GrassVariant variant = variants[i];
                if (variant == null || !variant.IsValid)
                    continue;

                if (variant.kind == requiredKind || (allowCustom && variant.kind == GrassVariantKind.Custom))
                    totalWeight += variant.weight;

                validIndex++;
            }

            if (totalWeight <= 0f)
                return 0;

            float selectionNoise = ValueNoise2D(worldPosition.x * grassClusterScale, worldPosition.z * grassClusterScale);
            float selection = Mathf.Clamp01(selectionNoise) * totalWeight;
            float cumulative = 0f;
            validIndex = 0;

            for (int i = 0; i < variants.Count; i++)
            {
                GrassVariant variant = variants[i];
                if (variant == null || !variant.IsValid)
                    continue;

                if (variant.kind == requiredKind || (allowCustom && variant.kind == GrassVariantKind.Custom))
                {
                    cumulative += variant.weight;
                    if (selection <= cumulative)
                        return validIndex;
                }

                validIndex++;
            }

            return Mathf.Max(0, validIndex - 1);
        }

        private static void ConfigureMaterialKeywords(GrassVariant variant)
        {
            if (variant.useTextureColor || variant.kind == GrassVariantKind.Flower)
                variant.material.EnableKeyword("_USE_TEXTURE_COLOR");
            else
                variant.material.DisableKeyword("_USE_TEXTURE_COLOR");

            variant.material.EnableKeyword("_ALPHATEST_ON");
        }

        private static float RandomHash(Vector3 position, int seed)
        {
            unchecked
            {
                uint x = (uint)Mathf.Abs(position.x * 73856093f);
                uint y = (uint)Mathf.Abs(position.y * 19349663f);
                uint z = (uint)Mathf.Abs(position.z * 83492791f);
                uint s = (uint)(seed * 374761393);
                uint hash = x ^ y ^ z ^ s;
                hash ^= hash >> 13;
                hash *= 1274126177u;
                return (hash & 0x00ffffff) / 16777215f;
            }
        }

        private static float ValueNoise2D(float x, float y)
        {
            int ix = Mathf.FloorToInt(x);
            int iy = Mathf.FloorToInt(y);
            float fx = x - ix;
            float fy = y - iy;

            float u = fx * fx * (3f - 2f * fx);
            float v = fy * fy * (3f - 2f * fy);

            float a = Hash2D(ix, iy);
            float b = Hash2D(ix + 1, iy);
            float c = Hash2D(ix, iy + 1);
            float d = Hash2D(ix + 1, iy + 1);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Hash2D(int x, int y)
        {
            unchecked
            {
                int n = x * 374761393 + y * 668265263;
                n = (n ^ (n >> 13)) * 1274126177;
                return ((n ^ (n >> 16)) & 0x7fffffff) / 2147483647f;
            }
        }
    }
}
