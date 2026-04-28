using System;
using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Grass.Core
{
    public static class GrassDataManager
    {
        private const int Magic = 0x32535247; // GRS2
        private const int Version = 1;

        public static bool TryLoadGrassData(GrassHolder grassHolder)
        {
            try
            {
                if (grassHolder.GrassDataSource == null)
                {
                    Debug.LogError("Error: Grass data source is null.", grassHolder);
                    return false;
                }

                byte[] binaryData = grassHolder.GrassDataSource.Data;
                if (binaryData == null || binaryData.Length == 0)
                {
                    Debug.LogError("Error: Grass data source has no baked data.", grassHolder);
                    return false;
                }

                using var dataStream = new MemoryStream(binaryData);
                using var reader = new BinaryReader(dataStream);

                GrassRuntimeData data = ReadBakedData(reader);
                grassHolder.ApplyLoadedData(data);
                grassHolder.SetGrassClearedFlag(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load grass data: {ex.Message}\n{ex.StackTrace}", grassHolder);
                return false;
            }
        }

#if UNITY_EDITOR
        public static bool TrySaveGrassData(GrassHolder grassHolder)
        {
            try
            {
                string path = AssetDatabase.GetAssetPath(grassHolder.GrassDataSource);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogError("Error: Grass data source path is invalid or missing.", grassHolder);
                    return false;
                }

                GrassRuntimeData bakedData = grassHolder.CreateBakedDataFromMemory();
                if (!bakedData.IsValid)
                {
                    Debug.LogError("Error: No grass data to save.", grassHolder);
                    return false;
                }

                using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(fileStream))
                {
                    WriteBakedData(writer, bakedData);
                }

                grassHolder.ApplyLoadedData(bakedData);
                grassHolder.SetGrassClearedFlag(false);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to save grass data: {ex.Message}\n{ex.StackTrace}", grassHolder);
                return false;
            }
        }

        public static bool TryClearGrassData(GrassHolder grassHolder)
        {
            try
            {
                grassHolder.OnDisable();
                grassHolder.grassData.Clear();
                grassHolder.ClearRuntimeData();
                grassHolder.SetGrassClearedFlag(true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to clear grass data: {ex.Message}\n{ex.StackTrace}", grassHolder);
                return false;
            }
        }

        public static void CreateGrassDataAsset(string folderPath, GrassHolder grassHolder = null)
        {
            string baseName = "New Grass Data";

            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            int index = 0;
            string filePath;
            string fileName;
            do
            {
                fileName = index == 0 ? $"{baseName}.grassdata" : $"{baseName}({index}).grassdata";
                filePath = Path.Combine(folderPath, fileName);
                index++;
            } while (File.Exists(filePath));

            using (File.Create(filePath))
            {
            }

            AssetDatabase.Refresh();

            if (grassHolder != null)
            {
                grassHolder.GrassDataSource = AssetDatabase.LoadAssetAtPath<GrassDataAsset>(filePath);
                EditorUtility.SetDirty(grassHolder);
            }
        }
#endif

        private static GrassRuntimeData ReadBakedData(BinaryReader reader)
        {
            int magic = reader.ReadInt32();
            if (magic != Magic)
                throw new InvalidDataException("Unsupported grass data file. Regenerate grass data with the new grass system.");

            int version = reader.ReadInt32();
            if (version != Version)
                throw new InvalidDataException($"Unsupported grass data version {version}.");

            var data = new GrassRuntimeData
            {
                bounds = new Bounds(ReadVector3(reader), ReadVector3(reader)),
                lightmapIndex = reader.ReadInt32()
            };

            int instanceCount = reader.ReadInt32();
            int chunkCount = reader.ReadInt32();
            int rangeCount = reader.ReadInt32();

            data.instances = new GrassData[instanceCount];
            data.chunks = new GrassChunk[chunkCount];
            data.ranges = new GrassDrawRange[rangeCount];

            for (int i = 0; i < instanceCount; i++)
            {
                data.instances[i] = new GrassData(
                    ReadVector3(reader),
                    ReadVector3(reader),
                    ReadVector2(reader),
                    reader.ReadInt32());
            }

            for (int i = 0; i < chunkCount; i++)
            {
                data.chunks[i] = new GrassChunk
                {
                    bounds = new Bounds(ReadVector3(reader), ReadVector3(reader)),
                    firstRange = reader.ReadInt32(),
                    rangeCount = reader.ReadInt32()
                };
            }

            for (int i = 0; i < rangeCount; i++)
            {
                data.ranges[i] = new GrassDrawRange
                {
                    materialIndex = reader.ReadInt32(),
                    startInstance = reader.ReadInt32(),
                    instanceCount = reader.ReadInt32()
                };
            }

            return data;
        }

        private static void WriteBakedData(BinaryWriter writer, GrassRuntimeData data)
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteVector3(data.bounds.center, writer);
            WriteVector3(data.bounds.size, writer);
            writer.Write(data.lightmapIndex);
            writer.Write(data.instances.Length);
            writer.Write(data.chunks.Length);
            writer.Write(data.ranges.Length);

            for (int i = 0; i < data.instances.Length; i++)
            {
                GrassData instance = data.instances[i];
                WriteVector3(instance.position, writer);
                WriteVector3(instance.normal, writer);
                WriteVector2(instance.lightmapUV, writer);
                writer.Write(instance.materialIndex);
            }

            for (int i = 0; i < data.chunks.Length; i++)
            {
                GrassChunk chunk = data.chunks[i];
                WriteVector3(chunk.bounds.center, writer);
                WriteVector3(chunk.bounds.size, writer);
                writer.Write(chunk.firstRange);
                writer.Write(chunk.rangeCount);
            }

            for (int i = 0; i < data.ranges.Length; i++)
            {
                GrassDrawRange range = data.ranges[i];
                writer.Write(range.materialIndex);
                writer.Write(range.startInstance);
                writer.Write(range.instanceCount);
            }
        }

        private static void WriteVector3(Vector3 vector, BinaryWriter writer)
        {
            writer.Write(vector.x);
            writer.Write(vector.y);
            writer.Write(vector.z);
        }

        private static void WriteVector2(Vector2 vector, BinaryWriter writer)
        {
            writer.Write(vector.x);
            writer.Write(vector.y);
        }

        private static Vector2 ReadVector2(BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
