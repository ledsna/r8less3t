using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace SuperSamplingResolve
{
    public class SuperSamplingResolveFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader resolveShader;

        private Material resolveMaterial;
        private SuperSamplingResolvePass resolvePass;

        public override void Create()
        {
            resolvePass = new SuperSamplingResolvePass();
            resolvePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 1;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (resolvePass == null)
                return;

            var cameraData = renderingData.cameraData;

            if (cameraData.renderScale <= 1.0f)
                return;

            if (cameraData.cameraType != CameraType.Game)
                return;

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

        private class PassData
        {
            internal Material material;
            internal TextureHandle srcColorTex;
            internal TextureHandle pixelPerfectDetailTex;
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

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Super Sampling Resolve", out var passData))
            {
                passData.material = material;

                passData.srcColorTex = srcColor;
                builder.UseTexture(srcColor, AccessFlags.Read);

                builder.UseGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"));
                builder.UseGlobalTexture(Shader.PropertyToID("_CameraObjectIDTexture"));

                var pixelPerfectDetailTex = resourceData.pixelPerfectDetailTexture;
                if (pixelPerfectDetailTex.IsValid())
                {
                    passData.pixelPerfectDetailTex = pixelPerfectDetailTex;
                    builder.UseTexture(pixelPerfectDetailTex, AccessFlags.Read);
                }

                builder.SetRenderAttachment(resolvedColor, 0, AccessFlags.Write);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc<PassData>(static (data, context) =>
                {
                    if (data.pixelPerfectDetailTex.IsValid())
                        context.cmd.SetGlobalTexture(Shader.PropertyToID("_PixelPerfectDetailTexture"), data.pixelPerfectDetailTex);

                    Blitter.BlitTexture(context.cmd, data.srcColorTex, Vector2.one, data.material, 0);
                });
            }

            resourceData.cameraColor = resolvedColor;
        }
    }
}
