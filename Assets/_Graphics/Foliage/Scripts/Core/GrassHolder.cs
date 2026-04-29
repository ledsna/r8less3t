using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using __Project.Shared.Attributes;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Grass.Core
{
    [ExecuteAlways]
    public class GrassHolder : MonoBehaviour
    {
        private static readonly int SourcePositionGrass = Shader.PropertyToID("_SourcePositionGrass");
        private static readonly int RotationScaleMatrix = Shader.PropertyToID("m_RS");
        private static readonly int Scale = Shader.PropertyToID("_Scale");
        private static readonly int InstancedBaseId = Shader.PropertyToID("_InstancedBaseID");
        private static readonly int FlowerSizeMultiplier = Shader.PropertyToID("_FlowerSizeMultiplier");
        private static readonly int FlowerSizeVariation = Shader.PropertyToID("_FlowerSizeVariation");
        private static readonly int FlowerCameraNudge = Shader.PropertyToID("_FlowerCameraNudge");

        private static readonly int Chunks = Shader.PropertyToID("_Chunks");
        private static readonly int Ranges = Shader.PropertyToID("_Ranges");
        private static readonly int VisibleInstanceIndices = Shader.PropertyToID("_VisibleInstanceIndices");
        private static readonly int UseVisibleInstanceIndices = Shader.PropertyToID("_UseVisibleInstanceIndices");
        private static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int CameraPosition = Shader.PropertyToID("_CameraPosition");
        private static readonly int MaxDrawDistanceSquared = Shader.PropertyToID("_MaxDrawDistanceSquared");
        private static readonly int ChunkCount = Shader.PropertyToID("_ChunkCount");
        private static readonly int MaterialIndex = Shader.PropertyToID("_MaterialIndex");

        private const int GpuChunkStride = sizeof(float) * 8;
        private const int GpuRangeStride = sizeof(int) * 4;
        private const int IndirectInstanceCountOffset = sizeof(uint);
        private const int ComputeThreadGroupSize = 64;

        private static readonly string[] RootMaterialProperties =
        {
            // Root material drives terrain/lighting colour. Variant materials keep alpha, outline,
            // object-ID, render-state, and billboard-specific controls live/editable.
            "_DiffuseSpecularCelShader", "_DiffuseSteps", "_FresnelSteps", "_SpecularStep",
            "_DistanceSteps", "_ShadowSteps", "_ReflectionSteps",
            "_BaseColor", "_BaseMap", "_SpecColor", "_WorkflowMode", "_Smoothness", "_Metallic",
            "_MetallicGlossMap", "_SpecGlossMap", "_BumpScale", "_BumpMap", "_OcclusionStrength",
            "_OcclusionMap", "_EmissionColor", "_EmissionMap"
        };

        [NonSerialized] public List<GrassData> grassData = new();
        [HideInInspector] public Material _rootMeshMaterial;
        [HideInInspector] public int lightmapIndex = -1;

        [SerializeField] private Mesh mesh;

        [Header("Material Variants")]
        [SerializeField] public GrassMaterialSystem materialSystem = new();

        [Header("Generation Settings")]
        [SerializeField, Range(0f, 1f)] public float normalLimit = 0.1f;

        [Header("Runtime Layout")]
        [SerializeField, Range(1, 64)] private int chunkGridResolution = 16;
        [SerializeField, Min(0f), Tooltip("Extra culling margin for billboard size, wind, and flower scaling. Too high makes more cells visible.")]
        private float boundsPadding = 2f;

        [Header("Rendering")]
        [SerializeField] public uint renderingLayerMask = 1;
        [SerializeField] private bool useGpuCulling = true;
        [SerializeField] private ComputeShader frustumCullingCompute;
        [SerializeField, Min(0f)] private float maxDrawDistance = 0f;
        [SerializeField, Min(0f), Tooltip("Camera movement required before rebuilding visible grass buffers.")]
        private float cullingPositionThreshold = 0.2f;
        [SerializeField, Min(0f), Tooltip("Camera rotation in degrees required before rebuilding visible grass buffers.")]
        private float cullingRotationThreshold = 1f;
        [SerializeField] private bool drawBounds;
        [SerializeField] private bool highlightRenderedCells;

        [FileAsset(".grassdata"), SerializeField]
        public GrassDataAsset GrassDataSource;

        [SerializeField, HideInInspector] private string lastAttachedGrassDataSourcePath;
        [SerializeField, HideInInspector] private bool grassWasCleared;

        private sealed class MaterialRenderData
        {
            public int materialIndex;
            public float objectId;
            public Material material;
            public MaterialPropertyBlock propertyBlock;
            public RenderParams renderParams;
            public GraphicsBuffer commandBuffer;
            public GraphicsBuffer.IndirectDrawIndexedArgs[] command;
            public ComputeBuffer visibleInstanceBuffer;
            public ComputeBuffer visibleIndexBuffer;
            public GrassData[] visibleInstances;
            public int visibleInstanceCount;
            public int visibleCommandCount;
            public bool hasScale;
            public bool hasFlowerSizeMultiplier;
            public bool hasFlowerSizeVariation;
            public bool hasFlowerCameraNudge;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuChunkData
        {
            public Vector3 center;
            public int firstRange;
            public Vector3 extents;
            public int rangeCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuRangeData
        {
            public int materialIndex;
            public int startInstance;
            public int instanceCount;
            public int padding;
        }

        private GrassRuntimeData _runtimeData;
        private MaterialRenderData[] _renderDataByMaterial = Array.Empty<MaterialRenderData>();
        private readonly List<MaterialRenderData> _renderDataList = new();
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private readonly Vector4[] _frustumPlaneVectors = new Vector4[6];
        private ComputeBuffer _allInstancesBuffer;
        private ComputeBuffer _chunkBuffer;
        private ComputeBuffer _rangeBuffer;
        private Camera _mainCamera;
        private Vector3 _cachedCameraPosition;
        private Quaternion _cachedCameraRotation;
        private Matrix4x4 _currentRotationMatrix;
        private bool[] _visibleChunks = Array.Empty<bool>();
        private int _visibleChunkCount;
        private int _visibleRangeCount;
        private int _visibleDrawCommandCount;
        private int _gpuCullKernel = -1;
        private bool _useGpuCullingRuntime;
        private bool _commandsDirty = true;
        private bool _initialized;

#if UNITY_EDITOR
        private SceneView _sceneView;
#endif

        public int ChunkGridResolution => chunkGridResolution;
        public float BoundsPadding => boundsPadding;
        public int VisibleChunkCount => _visibleChunkCount;
        public int TotalChunkCount => _runtimeData?.chunks?.Length ?? 0;
        public int VisibleRangeCount => _visibleRangeCount;
        public int VisibleDrawCommandCount => _visibleDrawCommandCount;
        public int TotalRangeCount => _runtimeData?.ranges?.Length ?? 0;

        public void FastSetup()
        {
            EnsureCamera();

            if (_initialized)
                ReleaseRuntimeResources();

            if (grassData.Count == 0)
                return;

            ApplyLoadedData(CreateBakedDataFromMemory(), replaceSourceList: false);
            InitRenderer();
        }

        public GrassRuntimeData CreateBakedDataFromMemory()
        {
            int materialCount = materialSystem != null ? materialSystem.TotalMaterialCount : 0;
            return GrassRuntimeBuilder.Build(grassData, materialCount, chunkGridResolution, boundsPadding, lightmapIndex);
        }

        public void ApplyLoadedData(GrassRuntimeData runtimeData, bool replaceSourceList = true)
        {
            _runtimeData = runtimeData;
            lightmapIndex = runtimeData?.lightmapIndex ?? -1;

            if (replaceSourceList)
                grassData = runtimeData?.instances != null ? new List<GrassData>(runtimeData.instances) : new List<GrassData>();
        }

        public void ClearRuntimeData()
        {
            _runtimeData = null;
            _commandsDirty = true;
        }

        public void PrepareForRegeneration()
        {
            ReleaseRuntimeResources();
            grassData.Clear();
            ClearRuntimeData();
            SetGrassClearedFlag(false);
        }

        public void Reinitialize()
        {
            OnDisable();
            OnEnable();
        }

        public void SetGrassClearedFlag(bool cleared)
        {
            grassWasCleared = cleared;
        }

        private void InitRenderer()
        {
            if (_runtimeData == null || !_runtimeData.IsValid)
                return;

            if (mesh == null)
                mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");

            if (mesh == null)
            {
                Debug.LogError($"GrassHolder on {gameObject.name}: no quad mesh assigned.", this);
                return;
            }

            if (materialSystem == null || !materialSystem.IsValid())
            {
                Debug.LogError($"GrassHolder on {gameObject.name}: no grass material variants configured.", this);
                return;
            }

            Material[] materials = materialSystem.GetAllMaterials();
            if (materials.Length == 0)
                return;

            _useGpuCullingRuntime = useGpuCulling && frustumCullingCompute != null && SystemInfo.supportsComputeShaders;
            if (_useGpuCullingRuntime)
                InitGpuCullingBuffers();

            _renderDataByMaterial = new MaterialRenderData[materials.Length];
            _visibleChunks = new bool[_runtimeData.chunks.Length];
            int[] instanceCountsByMaterial = CountInstancesByMaterial(materials.Length);

            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                if (materials[materialIndex] == null || instanceCountsByMaterial[materialIndex] == 0)
                    continue;

                int instanceCapacity = instanceCountsByMaterial[materialIndex];

                var renderData = new MaterialRenderData
                {
                    materialIndex = materialIndex,
                    objectId = ResolveObjectId(materialIndex),
                    material = materials[materialIndex],
                    propertyBlock = new MaterialPropertyBlock(),
                    command = new GraphicsBuffer.IndirectDrawIndexedArgs[1],
                    commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                        GraphicsBuffer.IndirectDrawIndexedArgs.size),
                    visibleInstances = _useGpuCullingRuntime ? Array.Empty<GrassData>() : new GrassData[instanceCapacity],
                    visibleInstanceBuffer = _useGpuCullingRuntime
                        ? null
                        : new ComputeBuffer(instanceCapacity, GrassRuntimeBuilder.GrassDataStride,
                            ComputeBufferType.Structured, ComputeBufferMode.Dynamic),
                    visibleIndexBuffer = _useGpuCullingRuntime
                        ? new ComputeBuffer(instanceCapacity, sizeof(uint), ComputeBufferType.Append)
                        : null,
                    hasScale = materials[materialIndex].HasProperty(Scale),
                    hasFlowerSizeMultiplier = materials[materialIndex].HasProperty(FlowerSizeMultiplier),
                    hasFlowerSizeVariation = materials[materialIndex].HasProperty(FlowerSizeVariation),
                    hasFlowerCameraNudge = materials[materialIndex].HasProperty(FlowerCameraNudge)
                };

                renderData.command[0] = CreateCommandArgs(0);
                renderData.commandBuffer.SetData(renderData.command);

                SyncMaterialKeywords(renderData.material);
                BindStaticMaterialProperties(renderData);

                renderData.renderParams = new RenderParams(renderData.material)
                {
                    layer = gameObject.layer,
                    renderingLayerMask = renderingLayerMask,
                    worldBounds = _runtimeData.bounds,
                    matProps = renderData.propertyBlock,
                    reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes
                };

                _renderDataByMaterial[materialIndex] = renderData;
                _renderDataList.Add(renderData);
            }

            _commandsDirty = true;
            _initialized = _renderDataList.Count > 0;
        }

        private float ResolveObjectId(int materialIndex)
        {
            GrassVariant variant = materialSystem.GetValidVariant(materialIndex);

            // Grass should behave like background in the ObjectID texture. Non-grass variants get
            // procedural base IDs equivalent to unity_RendererUserValue for normal renderers.
            return variant == null || variant.kind == GrassVariantKind.Grass ? 0f : AllocateObjectId();
        }

        private static float AllocateObjectId()
        {
            return global::WriteRendererID.GetNextID();
        }

        private void InitGpuCullingBuffers()
        {
            _gpuCullKernel = frustumCullingCompute.FindKernel("CullAndCompact");

            _allInstancesBuffer = new ComputeBuffer(_runtimeData.instances.Length, GrassRuntimeBuilder.GrassDataStride,
                ComputeBufferType.Structured, ComputeBufferMode.Immutable);
            _allInstancesBuffer.SetData(_runtimeData.instances);

            var chunkData = new GpuChunkData[_runtimeData.chunks.Length];
            for (int i = 0; i < _runtimeData.chunks.Length; i++)
            {
                GrassChunk chunk = _runtimeData.chunks[i];
                chunkData[i] = new GpuChunkData
                {
                    center = chunk.bounds.center,
                    firstRange = chunk.firstRange,
                    extents = chunk.bounds.extents,
                    rangeCount = chunk.rangeCount
                };
            }

            _chunkBuffer = new ComputeBuffer(chunkData.Length, GpuChunkStride, ComputeBufferType.Structured,
                ComputeBufferMode.Immutable);
            _chunkBuffer.SetData(chunkData);

            var rangeData = new GpuRangeData[_runtimeData.ranges.Length];
            for (int i = 0; i < _runtimeData.ranges.Length; i++)
            {
                GrassDrawRange range = _runtimeData.ranges[i];
                rangeData[i] = new GpuRangeData
                {
                    materialIndex = range.materialIndex,
                    startInstance = range.startInstance,
                    instanceCount = range.instanceCount
                };
            }

            _rangeBuffer = new ComputeBuffer(rangeData.Length, GpuRangeStride, ComputeBufferType.Structured,
                ComputeBufferMode.Immutable);
            _rangeBuffer.SetData(rangeData);

            frustumCullingCompute.SetBuffer(_gpuCullKernel, Chunks, _chunkBuffer);
            frustumCullingCompute.SetBuffer(_gpuCullKernel, Ranges, _rangeBuffer);
            frustumCullingCompute.SetInt(ChunkCount, _runtimeData.chunks.Length);
        }

        private int[] CountInstancesByMaterial(int materialCount)
        {
            int[] counts = new int[materialCount];

            for (int i = 0; i < _runtimeData.ranges.Length; i++)
            {
                GrassDrawRange range = _runtimeData.ranges[i];
                int materialIndex = range.materialIndex;
                if ((uint)materialIndex < (uint)materialCount)
                    counts[materialIndex] += range.instanceCount;
            }

            return counts;
        }

        private GraphicsBuffer.IndirectDrawIndexedArgs CreateCommandArgs(uint instanceCount)
        {
            return new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = mesh.GetIndexCount(0),
                instanceCount = instanceCount,
                startIndex = mesh.GetIndexStart(0),
                baseVertexIndex = 0,
                startInstance = 0
            };
        }

        private void Update()
        {
            if (!_initialized || _runtimeData == null || !_runtimeData.IsValid)
                return;

            EnsureCamera();
            if (_mainCamera == null)
                return;

            if (_commandsDirty || CameraChanged())
            {
                if (_useGpuCullingRuntime)
                    DispatchGpuCulling();
                else
                    RebuildVisibleCommands();
            }

            _currentRotationMatrix = GetRotationMatrix();

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                MaterialRenderData renderData = _renderDataList[i];
                if (renderData.visibleCommandCount == 0)
                    continue;

                BindDynamicMaterialProperties(renderData);
                Graphics.RenderMeshIndirect(renderData.renderParams, mesh, renderData.commandBuffer, 1);
            }
        }

        private bool CameraChanged()
        {
            Transform cameraTransform = _mainCamera.transform;
            float positionThresholdSqr = cullingPositionThreshold * cullingPositionThreshold;

            if ((cameraTransform.position - _cachedCameraPosition).sqrMagnitude > positionThresholdSqr)
                return true;

            return Quaternion.Angle(cameraTransform.rotation, _cachedCameraRotation) > cullingRotationThreshold;
        }

        private void DispatchGpuCulling()
        {
            GeometryUtility.CalculateFrustumPlanes(_mainCamera, _frustumPlanes);
            for (int i = 0; i < _frustumPlanes.Length; i++)
            {
                Plane plane = _frustumPlanes[i];
                Vector3 normal = plane.normal;
                _frustumPlaneVectors[i] = new Vector4(normal.x, normal.y, normal.z, plane.distance);
            }

            Vector3 cameraPosition = _mainCamera.transform.position;
            float maxDistanceSqr = maxDrawDistance > 0f ? maxDrawDistance * maxDrawDistance : 0f;

            if (ShouldUpdateVisibleChunkDebug())
                UpdateVisibleChunkDebug(cameraPosition, maxDistanceSqr);
            else
                ClearVisibleChunkDebug();
            _visibleDrawCommandCount = 0;

            frustumCullingCompute.SetVectorArray(FrustumPlanes, _frustumPlaneVectors);
            frustumCullingCompute.SetVector(CameraPosition, cameraPosition);
            frustumCullingCompute.SetFloat(MaxDrawDistanceSquared, maxDistanceSqr);
            int threadGroups = Mathf.CeilToInt(_runtimeData.chunks.Length / (float)ComputeThreadGroupSize);

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                MaterialRenderData renderData = _renderDataList[i];
                renderData.visibleIndexBuffer.SetCounterValue(0);

                frustumCullingCompute.SetInt(MaterialIndex, renderData.materialIndex);
                frustumCullingCompute.SetBuffer(_gpuCullKernel, VisibleInstanceIndices, renderData.visibleIndexBuffer);
                frustumCullingCompute.Dispatch(_gpuCullKernel, threadGroups, 1, 1);
                GraphicsBuffer.CopyCount(renderData.visibleIndexBuffer, renderData.commandBuffer,
                    IndirectInstanceCountOffset);

                renderData.visibleCommandCount = 1;
                _visibleDrawCommandCount++;
            }

            _cachedCameraPosition = _mainCamera.transform.position;
            _cachedCameraRotation = _mainCamera.transform.rotation;
            _commandsDirty = false;
        }

        private void UpdateVisibleChunkDebug(Vector3 cameraPosition, float maxDistanceSqr)
        {
            if (_visibleChunks.Length != _runtimeData.chunks.Length)
                _visibleChunks = new bool[_runtimeData.chunks.Length];
            else
                Array.Clear(_visibleChunks, 0, _visibleChunks.Length);

            _visibleChunkCount = 0;
            _visibleRangeCount = 0;

            for (int chunkIndex = 0; chunkIndex < _runtimeData.chunks.Length; chunkIndex++)
            {
                GrassChunk chunk = _runtimeData.chunks[chunkIndex];

                if (maxDistanceSqr > 0f && chunk.bounds.SqrDistance(cameraPosition) > maxDistanceSqr)
                    continue;

                if (!TestPlanesAABB(_frustumPlanes, chunk.bounds))
                    continue;

                _visibleChunks[chunkIndex] = true;
                _visibleChunkCount++;

                int endRange = chunk.firstRange + chunk.rangeCount;
                for (int rangeIndex = chunk.firstRange; rangeIndex < endRange; rangeIndex++)
                {
                    GrassDrawRange range = _runtimeData.ranges[rangeIndex];
                    if ((uint)range.materialIndex < (uint)_renderDataByMaterial.Length &&
                        _renderDataByMaterial[range.materialIndex] != null)
                    {
                        _visibleRangeCount++;
                    }
                }
            }
        }

        private void ClearVisibleChunkDebug()
        {
            _visibleChunkCount = 0;
            _visibleRangeCount = 0;
        }

        private bool ShouldUpdateVisibleChunkDebug()
        {
#if UNITY_EDITOR
            return highlightRenderedCells;
#else
            return false;
#endif
        }

        private void RebuildVisibleCommands()
        {
            for (int i = 0; i < _renderDataList.Count; i++)
            {
                _renderDataList[i].visibleCommandCount = 0;
                _renderDataList[i].visibleInstanceCount = 0;
            }

            if (_visibleChunks.Length != _runtimeData.chunks.Length)
                _visibleChunks = new bool[_runtimeData.chunks.Length];
            else
                Array.Clear(_visibleChunks, 0, _visibleChunks.Length);

            _visibleChunkCount = 0;
            _visibleRangeCount = 0;
            _visibleDrawCommandCount = 0;

            GeometryUtility.CalculateFrustumPlanes(_mainCamera, _frustumPlanes);
            Vector3 cameraPosition = _mainCamera.transform.position;
            float maxDistanceSqr = maxDrawDistance > 0f ? maxDrawDistance * maxDrawDistance : 0f;

            for (int chunkIndex = 0; chunkIndex < _runtimeData.chunks.Length; chunkIndex++)
            {
                GrassChunk chunk = _runtimeData.chunks[chunkIndex];

                if (maxDistanceSqr > 0f && chunk.bounds.SqrDistance(cameraPosition) > maxDistanceSqr)
                    continue;

                if (!TestPlanesAABB(_frustumPlanes, chunk.bounds))
                    continue;

                _visibleChunks[chunkIndex] = true;
                _visibleChunkCount++;

                int endRange = chunk.firstRange + chunk.rangeCount;
                for (int rangeIndex = chunk.firstRange; rangeIndex < endRange; rangeIndex++)
                {
                    GrassDrawRange range = _runtimeData.ranges[rangeIndex];
                    if ((uint)range.materialIndex >= (uint)_renderDataByMaterial.Length)
                        continue;

                    MaterialRenderData renderData = _renderDataByMaterial[range.materialIndex];
                    if (renderData == null)
                        continue;

                    _visibleRangeCount++;
                    AddVisibleRange(renderData, range);
                }
            }

            uint indexCount = mesh.GetIndexCount(0);
            uint startIndex = mesh.GetIndexStart(0);

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                MaterialRenderData renderData = _renderDataList[i];
                if (renderData.visibleInstanceCount == 0)
                    continue;

                renderData.visibleInstanceBuffer.SetData(renderData.visibleInstances, 0, 0,
                    renderData.visibleInstanceCount);

                renderData.command[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = indexCount,
                    instanceCount = (uint)renderData.visibleInstanceCount,
                    startIndex = startIndex,
                    baseVertexIndex = 0,
                    startInstance = 0
                };

                renderData.commandBuffer.SetData(renderData.command);
                renderData.visibleCommandCount = 1;
                _visibleDrawCommandCount++;
            }

            _cachedCameraPosition = _mainCamera.transform.position;
            _cachedCameraRotation = _mainCamera.transform.rotation;
            _commandsDirty = false;
        }

        private void AddVisibleRange(MaterialRenderData renderData, GrassDrawRange range)
        {
            Array.Copy(_runtimeData.instances, range.startInstance, renderData.visibleInstances,
                renderData.visibleInstanceCount, range.instanceCount);
            renderData.visibleInstanceCount += range.instanceCount;
        }

        private static bool TestPlanesAABB(Plane[] planes, Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;

            for (int i = 0; i < planes.Length; i++)
            {
                Vector3 normal = planes[i].normal;
                float radius = extents.x * Mathf.Abs(normal.x) + extents.y * Mathf.Abs(normal.y) +
                               extents.z * Mathf.Abs(normal.z);
                float distance = Vector3.Dot(normal, center) + planes[i].distance;

                if (distance + radius < 0f)
                    return false;
            }

            return true;
        }

        private void BindStaticMaterialProperties(MaterialRenderData renderData)
        {
            renderData.propertyBlock.SetBuffer(SourcePositionGrass,
                _useGpuCullingRuntime ? _allInstancesBuffer : renderData.visibleInstanceBuffer);
            renderData.propertyBlock.SetFloat(UseVisibleInstanceIndices, _useGpuCullingRuntime ? 1f : 0f);

            if (_useGpuCullingRuntime)
                renderData.propertyBlock.SetBuffer(VisibleInstanceIndices, renderData.visibleIndexBuffer);

            renderData.propertyBlock.SetFloat(InstancedBaseId, renderData.objectId);
            BindRootMaterialProperties(renderData.propertyBlock, renderData.material);
            BindLightmapProperties(renderData.propertyBlock, renderData.material);
        }

        private void BindDynamicMaterialProperties(MaterialRenderData renderData)
        {
            float materialScale = renderData.hasScale ? renderData.material.GetFloat(Scale) : 1f;
            renderData.propertyBlock.SetMatrix(RotationScaleMatrix, _currentRotationMatrix);
            renderData.propertyBlock.SetFloat(Scale, materialScale);

            if (renderData.hasFlowerSizeMultiplier)
                renderData.propertyBlock.SetFloat(FlowerSizeMultiplier, renderData.material.GetFloat(FlowerSizeMultiplier));
            if (renderData.hasFlowerSizeVariation)
                renderData.propertyBlock.SetFloat(FlowerSizeVariation, renderData.material.GetFloat(FlowerSizeVariation));
            if (renderData.hasFlowerCameraNudge)
                renderData.propertyBlock.SetFloat(FlowerCameraNudge, renderData.material.GetFloat(FlowerCameraNudge));
        }

        private Matrix4x4 GetRotationMatrix()
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            Transform cameraTransform = _mainCamera.transform;
            matrix.SetColumn(0, cameraTransform.right);
            matrix.SetColumn(1, cameraTransform.up);
            matrix.SetColumn(2, cameraTransform.forward);
            matrix.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return matrix;
        }

        private void BindRootMaterialProperties(MaterialPropertyBlock block, Material destination)
        {
            if (_rootMeshMaterial == null || destination == null)
                return;

            for (int i = 0; i < RootMaterialProperties.Length; i++)
                CopyPropertyToBlock(block, destination, _rootMeshMaterial, RootMaterialProperties[i]);
        }

        private static void CopyPropertyToBlock(MaterialPropertyBlock block, Material destination, Material source,
            string propertyName)
        {
            if (!source.HasProperty(propertyName) || !destination.HasProperty(propertyName))
                return;

            int propertyIndex = source.shader.FindPropertyIndex(propertyName);
            if (propertyIndex < 0)
                return;

            switch (source.shader.GetPropertyType(propertyIndex))
            {
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                    block.SetColor(propertyName, source.GetColor(propertyName));
                    break;
                case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    block.SetVector(propertyName, source.GetVector(propertyName));
                    break;
                case UnityEngine.Rendering.ShaderPropertyType.Float:
                case UnityEngine.Rendering.ShaderPropertyType.Range:
                    block.SetFloat(propertyName, source.GetFloat(propertyName));
                    break;
                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    Texture texture = source.GetTexture(propertyName);
                    if (texture == null)
                        break;

                    block.SetTexture(propertyName, texture);
                    string textureScaleOffsetName = propertyName + "_ST";
                    if (destination.HasProperty(textureScaleOffsetName))
                    {
                        Vector2 scale = source.GetTextureScale(propertyName);
                        Vector2 offset = source.GetTextureOffset(propertyName);
                        block.SetVector(textureScaleOffsetName, new Vector4(scale.x, scale.y, offset.x, offset.y));
                    }
                    break;
            }
        }

        private void BindLightmapProperties(MaterialPropertyBlock block, Material material)
        {
            if (lightmapIndex < 0 || lightmapIndex >= LightmapSettings.lightmaps.Length)
            {
                material.DisableKeyword("LIGHTMAP_ON");
                material.DisableKeyword("DIRLIGHTMAP_COMBINED");
                return;
            }

            LightmapData lightmapData = LightmapSettings.lightmaps[lightmapIndex];
            if (lightmapData.lightmapColor == null)
                return;

            material.EnableKeyword("LIGHTMAP_ON");
            if (LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional)
                material.EnableKeyword("DIRLIGHTMAP_COMBINED");
            else
                material.DisableKeyword("DIRLIGHTMAP_COMBINED");

            block.SetTexture("unity_Lightmap", lightmapData.lightmapColor);
            block.SetVector("unity_LightmapST", new Vector4(1f, 1f, 0f, 0f));

            if (lightmapData.lightmapDir != null)
                block.SetTexture("unity_LightmapInd", lightmapData.lightmapDir);

            if (lightmapData.shadowMask != null)
            {
                block.SetTexture("unity_ShadowMask", lightmapData.shadowMask);
                material.EnableKeyword("SHADOWS_SHADOWMASK");

                if (QualitySettings.shadowmaskMode == ShadowmaskMode.DistanceShadowmask)
                    material.EnableKeyword("LIGHTMAP_SHADOW_MIXING");
                else
                    material.DisableKeyword("LIGHTMAP_SHADOW_MIXING");
            }
        }

        private void SyncMaterialKeywords(Material material)
        {
            material.enableInstancing = true;

            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 1f);

            material.EnableKeyword("_ALPHATEST_ON");

            if (_rootMeshMaterial == null)
                return;

            SyncKeyword(material, "_REFLECTION_PROBE_BLENDING");
            SyncKeyword(material, "_REFLECTION_PROBE_BOX_PROJECTION");
        }

        private void SyncKeyword(Material material, string keyword)
        {
            if (_rootMeshMaterial.IsKeywordEnabled(keyword))
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        private void EnsureCamera()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (_sceneView != null && _sceneView.camera != null)
                    SetCullingCamera(_sceneView.camera);
                else if (SceneView.lastActiveSceneView != null)
                    SetCullingCamera(SceneView.lastActiveSceneView.camera);
                return;
            }
#endif
            SetCullingCamera(Camera.main);
        }

        private void SetCullingCamera(Camera camera)
        {
            if (camera == null || camera == _mainCamera)
                return;

            _mainCamera = camera;
            _commandsDirty = true;
        }

        public void OnEnable()
        {
#if UNITY_EDITOR
            SceneView.duringSceneGui -= OnScene;
            SceneView.duringSceneGui += OnScene;
#endif
            EnsureCamera();

            if (Application.isPlaying)
                grassWasCleared = false;

            ReleaseRuntimeResources();

            if (GrassDataSource != null && grassData.Count == 0 && !grassWasCleared)
            {
                if (!GrassDataManager.TryLoadGrassData(this))
                    return;
            }
            else if (grassData.Count > 0)
            {
                ApplyLoadedData(CreateBakedDataFromMemory(), replaceSourceList: false);
            }

            InitRenderer();
        }

        public void OnDisable()
        {
#if UNITY_EDITOR
            SceneView.duringSceneGui -= OnScene;
#endif
            ReleaseRuntimeResources();
        }

        private void ReleaseRuntimeResources()
        {
            _initialized = false;
            _commandsDirty = true;

            _allInstancesBuffer?.Release();
            _allInstancesBuffer = null;
            _chunkBuffer?.Release();
            _chunkBuffer = null;
            _rangeBuffer?.Release();
            _rangeBuffer = null;
            _gpuCullKernel = -1;
            _useGpuCullingRuntime = false;

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                _renderDataList[i].commandBuffer?.Release();
                _renderDataList[i].visibleInstanceBuffer?.Release();
                _renderDataList[i].visibleIndexBuffer?.Release();
                _renderDataList[i].propertyBlock?.Clear();
            }

            _renderDataList.Clear();
            _renderDataByMaterial = Array.Empty<MaterialRenderData>();
            _visibleChunks = Array.Empty<bool>();
            _visibleChunkCount = 0;
            _visibleRangeCount = 0;
            _visibleDrawCommandCount = 0;
        }

#if UNITY_EDITOR
        private void OnScene(SceneView scene)
        {
            _sceneView = scene;
            if (!Application.isPlaying && scene.camera != null)
                SetCullingCamera(scene.camera);
        }

        private void OnValidate()
        {
            chunkGridResolution = Mathf.Max(1, chunkGridResolution);
            boundsPadding = Mathf.Max(0f, boundsPadding);
            maxDrawDistance = Mathf.Max(0f, maxDrawDistance);
            cullingPositionThreshold = Mathf.Max(0f, cullingPositionThreshold);
            cullingRotationThreshold = Mathf.Max(0f, cullingRotationThreshold);
            _commandsDirty = true;

            if (frustumCullingCompute == null)
                frustumCullingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/_Graphics/Foliage/Shaders/GrassFrustumCulling.compute");

            string currentPath = GrassDataSource != null ? AssetDatabase.GetAssetPath(GrassDataSource) : string.Empty;
            if (lastAttachedGrassDataSourcePath != currentPath)
            {
                lastAttachedGrassDataSourcePath = currentPath;
                if (isActiveAndEnabled)
                    Reinitialize();
            }
        }
#endif

        private void OnDrawGizmos()
        {
            if ((!drawBounds && !highlightRenderedCells) || _runtimeData == null || _runtimeData.chunks == null)
                return;

            if (drawBounds)
            {
                Gizmos.color = new Color(0.4f, 0.8f, 0.9f, 0.35f);
                Gizmos.DrawWireCube(_runtimeData.bounds.center, _runtimeData.bounds.size);

                Gizmos.color = new Color(0.4f, 0.8f, 0.9f, 0.1f);
                for (int i = 0; i < _runtimeData.chunks.Length; i++)
                    Gizmos.DrawWireCube(_runtimeData.chunks[i].bounds.center, _runtimeData.chunks[i].bounds.size);
            }

            if (!highlightRenderedCells || _visibleChunks == null || _visibleChunks.Length == 0)
                return;

            int chunkCount = Mathf.Min(_runtimeData.chunks.Length, _visibleChunks.Length);
            for (int i = 0; i < chunkCount; i++)
            {
                if (!_visibleChunks[i])
                    continue;

                Bounds bounds = _runtimeData.chunks[i].bounds;
                Gizmos.color = new Color(1f, 0.78f, 0.12f, 0.12f);
                Gizmos.DrawCube(bounds.center, bounds.size);
                Gizmos.color = new Color(1f, 0.78f, 0.12f, 0.9f);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }
        }

        private void Reset()
        {
#if UNITY_EDITOR
            if (GrassDataSource == null)
            {
                string savePath = "Assets";
                var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(currentScene.path))
                    savePath = System.IO.Path.GetDirectoryName(currentScene.path);
                else
                    savePath = "Assets/Scenes";

                GrassDataManager.CreateGrassDataAsset(savePath, this);
                lastAttachedGrassDataSourcePath = AssetDatabase.GetAssetPath(GrassDataSource);
            }
#endif
            mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
#if UNITY_EDITOR
            if (frustumCullingCompute == null)
                frustumCullingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/_Graphics/Foliage/Shaders/GrassFrustumCulling.compute");
#endif
        }
    }
}
