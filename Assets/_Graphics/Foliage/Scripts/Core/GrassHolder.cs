using System;
using System.Collections.Generic;
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

        private static readonly string[] RootMaterialProperties =
        {
            "_DiffuseSpecularCelShader", "_DiffuseSteps", "_FresnelSteps", "_SpecularStep",
            "_DistanceSteps", "_ShadowSteps", "_ReflectionSteps",
            "_Surface", "_Blend", "_Cull", "_Cutoff", "_SrcBlend", "_DstBlend",
            "_SrcBlendAlpha", "_DstBlendAlpha", "_ZWrite", "_BlendModePreserveSpecular", "_AlphaToMask",
            "_BaseColor", "_BaseMap", "_SpecColor", "_WorkflowMode", "_Smoothness", "_Metallic",
            "_MetallicGlossMap", "_SpecGlossMap", "_BumpScale", "_BumpMap", "_OcclusionStrength",
            "_OcclusionMap", "_EmissionColor", "_EmissionMap", "_OutlineColour", "_OutlineStrength",
            "_DebugOn", "_External", "_Convex", "_Concave", "_DepthThreshold", "_NormalsThreshold",
            "_ExternalScale", "_InternalScale"
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
        [SerializeField, Min(0f)] private float boundsPadding = 2f;

        [Header("Rendering")]
        [SerializeField] public uint renderingLayerMask = 1;
        [SerializeField, Min(0f)] private float maxDrawDistance = 0f;
        [SerializeField] private bool drawBounds;
        [SerializeField] private bool highlightRenderedCells = true;

        [FileAsset(".grassdata"), SerializeField]
        public GrassDataAsset GrassDataSource;

        [SerializeField, HideInInspector] private string lastAttachedGrassDataSourcePath;
        [SerializeField, HideInInspector] private bool grassWasCleared;

        private sealed class MaterialRenderData
        {
            public int materialIndex;
            public Material material;
            public MaterialPropertyBlock propertyBlock;
            public RenderParams renderParams;
            public GraphicsBuffer commandBuffer;
            public GraphicsBuffer.IndirectDrawIndexedArgs[] commands;
            public int visibleCommandCount;
        }

        private GrassRuntimeData _runtimeData;
        private ComputeBuffer _instanceBuffer;
        private MaterialRenderData[] _renderDataByMaterial = Array.Empty<MaterialRenderData>();
        private readonly List<MaterialRenderData> _renderDataList = new();
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private Camera _mainCamera;
        private Vector3 _cachedCameraPosition;
        private Quaternion _cachedCameraRotation;
        private bool[] _visibleChunks = Array.Empty<bool>();
        private int _visibleChunkCount;
        private int _visibleRangeCount;
        private int _visibleDrawCommandCount;
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

            _instanceBuffer = new ComputeBuffer(_runtimeData.instances.Length, GrassRuntimeBuilder.GrassDataStride,
                ComputeBufferType.Structured, ComputeBufferMode.Immutable);
            _instanceBuffer.SetData(_runtimeData.instances);

            _renderDataByMaterial = new MaterialRenderData[materials.Length];
            _visibleChunks = new bool[_runtimeData.chunks.Length];
            int[] rangeCountsByMaterial = CountRangesByMaterial(materials.Length);

            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                if (materials[materialIndex] == null || rangeCountsByMaterial[materialIndex] == 0)
                    continue;

                var renderData = new MaterialRenderData
                {
                    materialIndex = materialIndex,
                    material = materials[materialIndex],
                    propertyBlock = new MaterialPropertyBlock(),
                    commands = new GraphicsBuffer.IndirectDrawIndexedArgs[rangeCountsByMaterial[materialIndex]],
                    commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,
                        rangeCountsByMaterial[materialIndex], GraphicsBuffer.IndirectDrawIndexedArgs.size)
                };

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

        private int[] CountRangesByMaterial(int materialCount)
        {
            int[] counts = new int[materialCount];

            for (int i = 0; i < _runtimeData.ranges.Length; i++)
            {
                int materialIndex = _runtimeData.ranges[i].materialIndex;
                if ((uint)materialIndex < (uint)materialCount)
                    counts[materialIndex]++;
            }

            return counts;
        }

        private void Update()
        {
            if (!_initialized || _runtimeData == null || !_runtimeData.IsValid)
                return;

            EnsureCamera();
            if (_mainCamera == null)
                return;

            if (_commandsDirty || CameraChanged())
                RebuildVisibleCommands();

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                MaterialRenderData renderData = _renderDataList[i];
                if (renderData.visibleCommandCount == 0)
                    continue;

                BindDynamicMaterialProperties(renderData);

                // Procedural instancing Setup() has no SV_DrawID, so each visible range is submitted
                // as one command to keep UnityIndirect's startInstance aligned with the drawn range.
                for (int commandIndex = 0; commandIndex < renderData.visibleCommandCount; commandIndex++)
                    Graphics.RenderMeshIndirect(renderData.renderParams, mesh, renderData.commandBuffer, 1,
                        commandIndex);
            }
        }

        private bool CameraChanged()
        {
            return _mainCamera.transform.position != _cachedCameraPosition ||
                   _mainCamera.transform.rotation != _cachedCameraRotation;
        }

        private void RebuildVisibleCommands()
        {
            for (int i = 0; i < _renderDataList.Count; i++)
                _renderDataList[i].visibleCommandCount = 0;

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

            uint indexCount = mesh.GetIndexCount(0);
            uint startIndex = mesh.GetIndexStart(0);

            for (int chunkIndex = 0; chunkIndex < _runtimeData.chunks.Length; chunkIndex++)
            {
                GrassChunk chunk = _runtimeData.chunks[chunkIndex];

                if (maxDistanceSqr > 0f && chunk.bounds.SqrDistance(cameraPosition) > maxDistanceSqr)
                    continue;

                if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, chunk.bounds))
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
                    AddVisibleCommand(renderData, range, indexCount, startIndex);
                }
            }

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                MaterialRenderData renderData = _renderDataList[i];
                _visibleDrawCommandCount += renderData.visibleCommandCount;

                if (renderData.visibleCommandCount > 0)
                    renderData.commandBuffer.SetData(renderData.commands, 0, 0, renderData.visibleCommandCount);
            }

            _cachedCameraPosition = _mainCamera.transform.position;
            _cachedCameraRotation = _mainCamera.transform.rotation;
            _commandsDirty = false;
        }

        private static void AddVisibleCommand(MaterialRenderData renderData, GrassDrawRange range, uint indexCount,
            uint startIndex)
        {
            uint rangeStart = (uint)range.startInstance;
            uint rangeCount = (uint)range.instanceCount;
            int previousIndex = renderData.visibleCommandCount - 1;

            if (previousIndex >= 0)
            {
                GraphicsBuffer.IndirectDrawIndexedArgs previous = renderData.commands[previousIndex];
                uint previousEnd = previous.startInstance + previous.instanceCount;

                if (previous.indexCountPerInstance == indexCount && previous.startIndex == startIndex &&
                    previous.baseVertexIndex == 0 && previousEnd == rangeStart)
                {
                    previous.instanceCount += rangeCount;
                    renderData.commands[previousIndex] = previous;
                    return;
                }
            }

            int commandIndex = renderData.visibleCommandCount++;
            renderData.commands[commandIndex] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = indexCount,
                instanceCount = rangeCount,
                startIndex = startIndex,
                baseVertexIndex = 0,
                startInstance = rangeStart
            };
        }

        private void BindStaticMaterialProperties(MaterialRenderData renderData)
        {
            renderData.propertyBlock.SetBuffer(SourcePositionGrass, _instanceBuffer);
            renderData.propertyBlock.SetFloat(InstancedBaseId, 0f);
            BindRootMaterialProperties(renderData.propertyBlock, renderData.material);
            BindLightmapProperties(renderData.propertyBlock, renderData.material);
        }

        private void BindDynamicMaterialProperties(MaterialRenderData renderData)
        {
            float materialScale = renderData.material.HasProperty(Scale) ? renderData.material.GetFloat(Scale) : 1f;
            renderData.propertyBlock.SetMatrix(RotationScaleMatrix, GetRotationMatrix());
            renderData.propertyBlock.SetFloat(Scale, materialScale);

            if (renderData.material.HasProperty(FlowerSizeMultiplier))
                renderData.propertyBlock.SetFloat(FlowerSizeMultiplier, renderData.material.GetFloat(FlowerSizeMultiplier));
            if (renderData.material.HasProperty(FlowerSizeVariation))
                renderData.propertyBlock.SetFloat(FlowerSizeVariation, renderData.material.GetFloat(FlowerSizeVariation));
            if (renderData.material.HasProperty(FlowerCameraNudge))
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

            _instanceBuffer?.Release();
            _instanceBuffer = null;

            for (int i = 0; i < _renderDataList.Count; i++)
            {
                _renderDataList[i].commandBuffer?.Release();
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
            _commandsDirty = true;

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
        }
    }
}
