using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Grass.Core
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassData
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 lightmapUV;
        public int materialIndex;

        public GrassData(Vector3 position, Vector3 normal, Vector2 lightmapUV, int materialIndex = 0)
        {
            this.position = position;
            this.normal = normal;
            this.lightmapUV = lightmapUV;
            this.materialIndex = materialIndex;
        }
    }

    [Serializable]
    public struct GrassChunk
    {
        public Bounds bounds;
        public int firstRange;
        public int rangeCount;
    }

    [Serializable]
    public struct GrassDrawRange
    {
        public int materialIndex;
        public int startInstance;
        public int instanceCount;
    }

    public sealed class GrassRuntimeData
    {
        public GrassData[] instances = Array.Empty<GrassData>();
        public GrassChunk[] chunks = Array.Empty<GrassChunk>();
        public GrassDrawRange[] ranges = Array.Empty<GrassDrawRange>();
        public Bounds bounds;
        public int lightmapIndex = -1;

        public bool IsValid => instances.Length > 0 && chunks.Length > 0 && ranges.Length > 0;
    }

    public static class GrassRuntimeBuilder
    {
        public const int GrassDataStride = sizeof(float) * 8 + sizeof(int);

        public static GrassRuntimeData Build(IList<GrassData> source, int materialCount, int chunkGridResolution,
            float boundsPadding, int lightmapIndex)
        {
            var runtimeData = new GrassRuntimeData { lightmapIndex = lightmapIndex };

            if (source == null || source.Count == 0)
                return runtimeData;

            materialCount = Mathf.Max(1, materialCount);
            chunkGridResolution = Mathf.Max(1, chunkGridResolution);
            boundsPadding = Mathf.Max(0f, boundsPadding);

            Bounds globalBounds = CalculateBounds(source, boundsPadding);
            runtimeData.bounds = globalBounds;

            int cellCount = chunkGridResolution * chunkGridResolution;
            var cells = new List<GrassData>[cellCount];
            Vector3 min = globalBounds.min;
            Vector3 size = globalBounds.size;
            float cellSizeX = size.x / chunkGridResolution;
            float cellSizeZ = size.z / chunkGridResolution;

            for (int i = 0; i < source.Count; i++)
            {
                GrassData data = source[i];
                data.materialIndex = Mathf.Clamp(data.materialIndex, 0, materialCount - 1);

                int x = cellSizeX > 0f ? Mathf.Clamp(Mathf.FloorToInt((data.position.x - min.x) / cellSizeX), 0, chunkGridResolution - 1) : 0;
                int z = cellSizeZ > 0f ? Mathf.Clamp(Mathf.FloorToInt((data.position.z - min.z) / cellSizeZ), 0, chunkGridResolution - 1) : 0;
                int cellIndex = z * chunkGridResolution + x;

                cells[cellIndex] ??= new List<GrassData>();
                cells[cellIndex].Add(data);
            }

            var instances = new List<GrassData>(source.Count);
            var chunks = new List<GrassChunk>();
            var ranges = new List<GrassDrawRange>();

            for (int i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                if (cell == null || cell.Count == 0)
                    continue;

                cell.Sort(static (a, b) => a.materialIndex.CompareTo(b.materialIndex));

                int firstRange = ranges.Count;
                int rangeStart = instances.Count;
                int currentMaterial = cell[0].materialIndex;
                Bounds chunkBounds = new Bounds(cell[0].position, Vector3.zero);

                for (int j = 0; j < cell.Count; j++)
                {
                    GrassData data = cell[j];

                    if (data.materialIndex != currentMaterial)
                    {
                        ranges.Add(new GrassDrawRange
                        {
                            materialIndex = currentMaterial,
                            startInstance = rangeStart,
                            instanceCount = instances.Count - rangeStart
                        });

                        currentMaterial = data.materialIndex;
                        rangeStart = instances.Count;
                    }

                    instances.Add(data);
                    chunkBounds.Encapsulate(data.position);
                }

                ranges.Add(new GrassDrawRange
                {
                    materialIndex = currentMaterial,
                    startInstance = rangeStart,
                    instanceCount = instances.Count - rangeStart
                });

                chunkBounds.Expand(boundsPadding);
                chunks.Add(new GrassChunk
                {
                    bounds = chunkBounds,
                    firstRange = firstRange,
                    rangeCount = ranges.Count - firstRange
                });
            }

            runtimeData.instances = instances.ToArray();
            runtimeData.chunks = chunks.ToArray();
            runtimeData.ranges = ranges.ToArray();
            runtimeData.bounds = globalBounds;
            return runtimeData;
        }

        private static Bounds CalculateBounds(IList<GrassData> source, float padding)
        {
            Bounds bounds = new Bounds(source[0].position, Vector3.zero);

            for (int i = 1; i < source.Count; i++)
                bounds.Encapsulate(source[i].position);

            bounds.Expand(padding);
            return bounds;
        }
    }
}
