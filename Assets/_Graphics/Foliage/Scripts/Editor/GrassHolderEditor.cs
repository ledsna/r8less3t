using Grass.Core;
using UnityEditor;
using UnityEngine;

namespace Grass.Editor
{
    [CustomEditor(typeof(GrassHolder))]
    public class GrassHolderEditor : UnityEditor.Editor
    {
        // Serialized Properties
        private SerializedProperty mesh;
        private SerializedProperty materialSystem;
        private SerializedProperty normalLimit;
        private SerializedProperty chunkGridResolution;
        private SerializedProperty boundsPadding;
        private SerializedProperty useGpuCulling;
        private SerializedProperty frustumCullingCompute;
        private SerializedProperty maxDrawDistance;
        private SerializedProperty cullingPositionThreshold;
        private SerializedProperty cullingRotationThreshold;
        private SerializedProperty drawBounds;
        private SerializedProperty highlightRenderedCells;
        private SerializedProperty GrassDataSource;
        private SerializedProperty renderingLayerMask;

        private void OnEnable()
        {
            // Initialize serialized properties
            mesh = serializedObject.FindProperty("mesh");
            materialSystem = serializedObject.FindProperty("materialSystem");
            normalLimit = serializedObject.FindProperty("normalLimit");
            chunkGridResolution = serializedObject.FindProperty("chunkGridResolution");
            boundsPadding = serializedObject.FindProperty("boundsPadding");
            useGpuCulling = serializedObject.FindProperty("useGpuCulling");
            frustumCullingCompute = serializedObject.FindProperty("frustumCullingCompute");
            maxDrawDistance = serializedObject.FindProperty("maxDrawDistance");
            cullingPositionThreshold = serializedObject.FindProperty("cullingPositionThreshold");
            cullingRotationThreshold = serializedObject.FindProperty("cullingRotationThreshold");
            drawBounds = serializedObject.FindProperty("drawBounds");
            highlightRenderedCells = serializedObject.FindProperty("highlightRenderedCells");
            GrassDataSource = serializedObject.FindProperty("GrassDataSource");
            renderingLayerMask = serializedObject.FindProperty("renderingLayerMask");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update(); // Start updating serialized properties

            // Get reference to the target script
            var script = (GrassHolder)target;

            // Check if the TextAsset is null
            if (script.GrassDataSource == null)
            {
                // Grass Data Source
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Grass Data", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(GrassDataSource, new GUIContent("Grass Data Source"));

                EditorGUILayout.HelpBox("Grass Data Source missing! Create new or select existing .grassdata file",
                    MessageType.Error);

                serializedObject.ApplyModifiedProperties();
                return;
            }

            // Grass Data Source
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grass Data", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(GrassDataSource, new GUIContent("Grass Data Source"));

            // Material System and Mesh
            EditorGUILayout.Space();
            DrawMaterialSystem();
            EditorGUILayout.PropertyField(mesh, new GUIContent("Mesh"));

            // Generation Settings
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generation Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(normalLimit, new GUIContent("Slope Limit"));

            // Rendering Options
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rendering Settings", EditorStyles.boldLabel);
            DrawRenderingLayerMaskField();
            EditorGUILayout.PropertyField(useGpuCulling, new GUIContent("Use GPU Culling"));
            if (useGpuCulling.boolValue)
                EditorGUILayout.PropertyField(frustumCullingCompute, new GUIContent("Culling Compute"));
            EditorGUILayout.PropertyField(maxDrawDistance, new GUIContent("Max Draw Distance"));
            EditorGUILayout.PropertyField(cullingPositionThreshold, new GUIContent("Culling Position Threshold"));
            EditorGUILayout.PropertyField(cullingRotationThreshold, new GUIContent("Culling Rotation Threshold"));

            // Culling Options
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Baked Chunk Settings", EditorStyles.boldLabel);
            EditorGUILayout.IntSlider(chunkGridResolution, 1, 64, new GUIContent("Chunk Grid Resolution"));
            EditorGUILayout.PropertyField(boundsPadding, new GUIContent("Bounds Padding"));
            EditorGUILayout.PropertyField(drawBounds, new GUIContent("Draw Bounds"));
            EditorGUILayout.PropertyField(highlightRenderedCells, new GUIContent("Highlight Rendered Cells"));
            if (highlightRenderedCells.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "Rendered cell highlighting runs an extra CPU visibility pass for debug gizmos. Disable it for performance profiling.",
                    MessageType.Warning);
            }

            if (script.TotalChunkCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Visible chunks: {script.VisibleChunkCount}/{script.TotalChunkCount}\n" +
                    $"Visible ranges: {script.VisibleRangeCount}/{script.TotalRangeCount}\n" +
                    $"Draw commands after merge: {script.VisibleDrawCommandCount}",
                    MessageType.Info);
            }

            // Apply changes
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawMaterialSystem()
        {
            EditorGUILayout.LabelField("Material Variants", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Variants are baked into generated grass. Use Grass for root-tinted blades and Flower for sprite-coloured patches.",
                MessageType.Info);

            SerializedProperty grassClusterScale = materialSystem.FindPropertyRelative("grassClusterScale");
            SerializedProperty variants = materialSystem.FindPropertyRelative("variants");

            EditorGUILayout.PropertyField(grassClusterScale,
                new GUIContent("Grass Patch Variation",
                    "Controls how quickly grass material choices vary across world space. Higher values create smaller patches."));

            EditorGUILayout.Space(4);

            for (int i = 0; i < variants.arraySize; i++)
            {
                SerializedProperty variant = variants.GetArrayElementAtIndex(i);
                DrawVariant(variants, variant, i);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Grass"))
                AddVariant(variants, GrassVariantKind.Grass);
            if (GUILayout.Button("Add Flower"))
                AddVariant(variants, GrassVariantKind.Flower);
            if (GUILayout.Button("Add Custom"))
                AddVariant(variants, GrassVariantKind.Custom);
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawVariant(SerializedProperty variants, SerializedProperty variant, int index)
        {
            SerializedProperty name = variant.FindPropertyRelative("name");
            SerializedProperty material = variant.FindPropertyRelative("material");
            SerializedProperty kind = variant.FindPropertyRelative("kind");
            SerializedProperty weight = variant.FindPropertyRelative("weight");
            SerializedProperty useTextureColor = variant.FindPropertyRelative("useTextureColor");
            SerializedProperty clumpScale = variant.FindPropertyRelative("clumpScale");
            SerializedProperty clumpThreshold = variant.FindPropertyRelative("clumpThreshold");
            SerializedProperty clumpDensity = variant.FindPropertyRelative("clumpDensity");
            SerializedProperty seed = variant.FindPropertyRelative("seed");
            SerializedProperty normalNudgeProbability = variant.FindPropertyRelative("normalNudgeProbability");
            SerializedProperty normalNudgeStrength = variant.FindPropertyRelative("normalNudgeStrength");

            string title = string.IsNullOrWhiteSpace(name.stringValue) ? $"Variant {index + 1}" : name.stringValue;
            if (material.objectReferenceValue != null)
                title += $" ({material.objectReferenceValue.name})";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            variant.isExpanded = EditorGUILayout.Foldout(variant.isExpanded, title, true);
            if (GUILayout.Button("Remove", GUILayout.Width(70f)))
            {
                variants.DeleteArrayElementAtIndex(index);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            if (variant.isExpanded)
            {
                EditorGUILayout.PropertyField(name, new GUIContent("Name"));
                EditorGUILayout.PropertyField(material, new GUIContent("Material"));
                EditorGUILayout.PropertyField(kind, new GUIContent("Type"));
                EditorGUILayout.PropertyField(weight,
                    new GUIContent("Abundance", "Relative chance compared with variants of the same type."));
                EditorGUILayout.PropertyField(useTextureColor,
                    new GUIContent("Use Sprite Colors", "Use texture/sprite colours instead of root material tinting."));

                GrassVariantKind variantKind = (GrassVariantKind)kind.enumValueIndex;
                if (variantKind == GrassVariantKind.Flower)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Flower Patch Controls", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(clumpScale,
                        new GUIContent("Patch Size", "Higher values create smaller/more frequent flower patches."));
                    EditorGUILayout.PropertyField(clumpThreshold,
                        new GUIContent("Patch Rarity", "Higher values make patches rarer and tighter."));
                    EditorGUILayout.PropertyField(clumpDensity,
                        new GUIContent("Flowers Inside Patch", "Density of this flower inside accepted patches."));
                    EditorGUILayout.PropertyField(seed, new GUIContent("Patch Seed"));
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Generated Normal Variation", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(normalNudgeProbability,
                    new GUIContent("Chance", "Chance to nudge this variant's stored normal during generation."));
                EditorGUILayout.PropertyField(normalNudgeStrength,
                    new GUIContent("Strength", "Maximum normal nudge amount during generation."));
            }

            EditorGUILayout.EndVertical();
        }

        private static void AddVariant(SerializedProperty variants, GrassVariantKind kind)
        {
            int index = variants.arraySize;
            variants.InsertArrayElementAtIndex(index);

            SerializedProperty variant = variants.GetArrayElementAtIndex(index);
            variant.isExpanded = true;
            variant.FindPropertyRelative("name").stringValue = kind == GrassVariantKind.Flower
                ? $"Flower {index + 1}"
                : $"Grass {index + 1}";
            variant.FindPropertyRelative("material").objectReferenceValue = null;
            variant.FindPropertyRelative("kind").enumValueIndex = (int)kind;
            variant.FindPropertyRelative("weight").floatValue = 1f;
            variant.FindPropertyRelative("useTextureColor").boolValue = kind == GrassVariantKind.Flower;
            variant.FindPropertyRelative("clumpScale").floatValue = 0.12f;
            variant.FindPropertyRelative("clumpThreshold").floatValue = 0.7f;
            variant.FindPropertyRelative("clumpDensity").floatValue = 0.2f;
            variant.FindPropertyRelative("seed").intValue = index + 1;
            variant.FindPropertyRelative("normalNudgeProbability").floatValue = 0.05f;
            variant.FindPropertyRelative("normalNudgeStrength").floatValue = 0.08f;
        }
        
        private void DrawRenderingLayerMaskField()
        {
            // Get the current mask value
            uint currentMask = (uint)renderingLayerMask.intValue;
            
            // Get rendering layer names from the current render pipeline
            string[] layerNames = GetRenderingLayerNames();
            
            // Create a proper mask field dropdown
            int newMask = EditorGUILayout.MaskField("Rendering Layer Mask", (int)currentMask, layerNames);
            
            // Update if changed
            if (newMask != (int)currentMask)
            {
                renderingLayerMask.intValue = newMask;
            }
        }
        
        private string[] GetRenderingLayerNames()
        {
            string[] layerNames = new string[32];
            
            // Try to get names from the current render pipeline
            var renderPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (renderPipeline != null)
            {
                // Check if it's URP
                if (renderPipeline.GetType().Name.Contains("UniversalRenderPipelineAsset"))
                {
                    // For URP, try to get the layer names
                    try
                    {
                        var property = renderPipeline.GetType().GetProperty("renderingLayerMaskNames");
                        if (property != null)
                        {
                            var names = property.GetValue(renderPipeline) as string[];
                            if (names != null && names.Length > 0)
                            {
                                for (int i = 0; i < Mathf.Min(names.Length, 32); i++)
                                {
                                    layerNames[i] = !string.IsNullOrEmpty(names[i]) ? names[i] : $"Layer {i}";
                                }
                                return layerNames;
                            }
                        }
                    }
                    catch
                    {
                        // Fall through to default names
                    }
                }
            }
            
            // Fallback to default names
            for (int i = 0; i < 32; i++)
            {
                layerNames[i] = $"Layer {i}";
            }
            
            return layerNames;
        }
    }
}
