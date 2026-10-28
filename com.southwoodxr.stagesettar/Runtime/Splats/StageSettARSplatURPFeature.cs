using Gsplat;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace StageSettAR
{
    /// <summary>
    /// Sorts Gaussian splats for the camera, like UnitySplats' own URP feature, but injected at
    /// <see cref="RenderPassEvent.BeforeRendering"/>. UnitySplats' feature is internal, so the
    /// event cannot be changed from outside; the import dialog deactivates it and adds this one.
    /// </summary>
    [DisallowMultipleRendererFeature("StageSettAR Splat Sort")]
    public sealed class StageSettARSplatURPFeature : ScriptableRendererFeature
    {
        sealed class SortPass : ScriptableRenderPass
        {
            // Unity 6000.0-6000.3 still call Execute in URP compatibility mode; 6000.4 removed it.
            public CommandBuffer CommandBuffer;

            sealed class PassData
            {
                public UniversalCameraData CameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(GsplatSorter.k_passName, out PassData passData);
                passData.CameraData = frameData.Get<UniversalCameraData>();
                builder.AllowPassCulling(false);
                // DispatchSort sets camera-local shader globals; RenderGraph must not reorder or cull it.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    GsplatSorter.Instance.DispatchSort(
                        CommandBufferHelpers.GetNativeCommandBuffer(context.cmd), data.CameraData.camera));
            }

#if !UNITY_6000_4_OR_NEWER
#pragma warning disable CS0672 // Compatibility-mode override is obsolete in RenderGraph mode.
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (CommandBuffer == null)
                    return;
                GsplatSorter.Instance.DispatchSort(CommandBuffer, renderingData.cameraData.camera);
                context.ExecuteCommandBuffer(CommandBuffer);
            }
#pragma warning restore CS0672
#endif
        }

        SortPass m_pass;
        bool m_hasGsplats;

        public override void Create()
        {
            // The sort is an unsafe pass without render attachments, so RenderGraph cannot merge it
            // into the surrounding native render pass. Injected at BeforeRenderingTransparents it
            // splits the frame's render pass in two, and on a tile-based GPU (Vulkan on Quest, with
            // Native RenderPass enabled) the camera colour attachment comes back from the split
            // without its previous contents -- the clear colour and every opaque draw are lost, so
            // anything the splats do not cover reads black. Desktop D3D11 ignores Native RenderPass,
            // which is why the Editor looks correct.
            //
            // BeforeRendering puts the split ahead of the clear, where there is nothing in the tile
            // to lose. DispatchSort only needs the camera transform and to have set its globals
            // before the Transparent-queue draw, and both still hold here.
            m_pass = new SortPass { renderPassEvent = RenderPassEvent.BeforeRendering };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_hasGsplats = GsplatSorter.Instance.GatherGsplatsForCamera(cameraData.camera);
            m_pass.CommandBuffer ??= new CommandBuffer { name = GsplatSorter.k_passName };
            m_pass.CommandBuffer.Clear();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (GsplatSettings.Instance.Valid && m_hasGsplats)
                renderer.EnqueuePass(m_pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_pass?.CommandBuffer?.Dispose();
            m_pass = null;
        }
    }
}
