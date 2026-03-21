using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace SuperSamplingResolve
{
    /// <summary>
    /// Resolves supersampled color, depth and normals to native resolution
    /// after transparents but before any post-processing features (volumetric fog, bloom, etc.),
    /// using the pixel-perfect mask for edge-aware color downsample.
    /// </summary>
    public class SuperSamplingResolveFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader resolveShader;

        private Material resolveMaterial;
        private SuperSamplingResolvePass resolvePass;

        public override void Create()
        {
            resolvePass = new SuperSamplingResolvePass();
            // Run right after transparents (500+1=501), before any BeforeRenderingPostProcessing (550) features
            resolvePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 1;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (resolvePass == null)
                return;

            var cameraData = renderingData.cameraData;

            // Only apply when render scale > 1 (super sampling is active)
            if (cameraData.renderScale <= 1.0f)
                return;

            // Skip non-game cameras
            if (cameraData.cameraType != CameraType.Game)
                return;

            // No downsampling needed at 1x
            if (Mathf.RoundToInt(cameraData.renderScale) <= 1)
                return;

            if (resolveShader == null)
                return;

            if (resolveMaterial == null)
                resolveMaterial = new Material(resolveShader);

            resolvePass.Setup(resolveMaterial, cameraData.renderScale);
            renderer.EnqueuePass(resolvePass);
        }

        protected override void Dispose(bool disposing)
        {
            if (Application.isPlaying)
                Destroy(resolveMaterial);
            else
                DestroyImmediate(resolveMaterial);
        }
    }

    public class SuperSamplingResolvePass : ScriptableRenderPass
    {
        private Material material;

        private static readonly int SuperSamplingScaleID = Shader.PropertyToID("_SuperSamplingScale");
        private static readonly int CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int PixelPerfectTextureID = Shader.PropertyToID("_PixelPerfectTexture");

        private class PassData
        {
            internal Material material;
            internal bool resolveNormals;
        }

        public void Setup(Material material, float renderScale)
        {
            this.material = material;
            material.SetInt(SuperSamplingScaleID, Mathf.RoundToInt(renderScale));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            if (resourceData.isActiveTargetBackBuffer)
                return;

            var srcColor = resourceData.activeColorTexture;
            if (!srcColor.IsValid())
                return;

            var cam = cameraData.camera;
            int outW = cam.pixelWidth;
            int outH = cam.pixelHeight;

            // --- Create 1x output textures ---

            // Color (same format as supersampled source)
            var srcDesc = srcColor.GetDescriptor(renderGraph);
            var colorDesc = new TextureDesc(outW, outH, false, false)
            {
                format = srcDesc.format,
                depthBufferBits = 0,
                clearBuffer = false,
                msaaSamples = MSAASamples.None,
                name = "_ResolvedColor"
            };
            var resolvedColor = renderGraph.CreateTexture(colorDesc);

            // Depth (R32_SFloat to store raw depth values)
            var depthDesc = new TextureDesc(outW, outH, false, false)
            {
                format = GraphicsFormat.R32_SFloat,
                depthBufferBits = 0,
                clearBuffer = false,
                msaaSamples = MSAASamples.None,
                name = "_ResolvedDepth"
            };
            var resolvedDepth = renderGraph.CreateTexture(depthDesc);

            // Normals (only if a normals texture exists)
            bool hasNormals = resourceData.cameraNormalsTexture.IsValid();
            TextureHandle resolvedNormals = TextureHandle.nullHandle;
            if (hasNormals)
            {
                var srcNormalsDesc = resourceData.cameraNormalsTexture.GetDescriptor(renderGraph);
                var normalsDesc = new TextureDesc(outW, outH, false, false)
                {
                    format = srcNormalsDesc.format,
                    depthBufferBits = 0,
                    clearBuffer = false,
                    msaaSamples = MSAASamples.None,
                    name = "_ResolvedNormals"
                };
                resolvedNormals = renderGraph.CreateTexture(normalsDesc);
            }

            // --- Record the resolve pass ---
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Super Sampling Resolve", out var passData))
            {
                passData.material = material;
                passData.resolveNormals = hasNormals;

                // Input: supersampled color
                builder.UseTexture(srcColor, AccessFlags.Read);

                // Input: supersampled depth (global)
                builder.UseGlobalTexture(CameraDepthTextureID);

                // Input: pixel-perfect mask (global)
                builder.UseGlobalTexture(PixelPerfectTextureID);

                // Input: supersampled normals (global, optional)
                if (hasNormals)
                    builder.UseGlobalTexture(CameraNormalsTextureID);

                // MRT outputs
                builder.SetRenderAttachment(resolvedColor, 0);
                builder.SetRenderAttachment(resolvedDepth, 1);
                if (hasNormals)
                    builder.SetRenderAttachment(resolvedNormals, 2);

                // Override global texture bindings after this pass so post-processing
                // reads the resolved 1x textures instead of the supersampled ones
                builder.SetGlobalTextureAfterPass(resolvedDepth, CameraDepthTextureID);
                if (hasNormals)
                    builder.SetGlobalTextureAfterPass(resolvedNormals, CameraNormalsTextureID);

                builder.SetRenderFunc<PassData>(static (data, context) =>
                {
                    if (data.resolveNormals)
                        data.material.EnableKeyword("_RESOLVE_NORMALS");
                    else
                        data.material.DisableKeyword("_RESOLVE_NORMALS");

                    Blitter.BlitTexture(context.cmd, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // Replace the active camera color so post-processing reads our 1x result
            resourceData.cameraColor = resolvedColor;
        }
    }
}
