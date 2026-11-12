using System.Numerics;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Vulkan.Rendering;

internal sealed class VulkanMaskPipeline : IDisposable
{
    private const int StackAllocThreshold = 128;
    private const uint FullscreenQuadVertices = 6;
    private readonly VulkanDevice _gpu;
    private readonly VulkanPipeline _pipeline;

    public VulkanMaskPipeline(VulkanDevice gpu, VulkanSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Mask.hlsl", false);
    }

    public void Draw(RenderScene scene, VulkanSurface surface, VulkanFrameResources frame)
    {
        if (MaskDrawConstants.TryResolveActiveTarget(scene) is not { } target)
        {
            return;
        }

        var maskVectorCount = target.MaskCount * 2;
        Span<Vector4> masks = maskVectorCount <= StackAllocThreshold ? stackalloc Vector4[maskVectorCount] : new Vector4[maskVectorCount];
        Span<Vector2> points = target.VertexCount <= StackAllocThreshold ? stackalloc Vector2[target.VertexCount] : new Vector2[target.VertexCount];
        var constants = MaskDrawConstants.Pack(scene, in target, masks, points);

        _pipeline.Bind(surface);
        frame.BindBuffers(_pipeline, surface, in constants, frame.Write<Vector4>(masks).Descriptor, frame.Write<Vector2>(points).Descriptor);
        _gpu.Api.vkCmdDraw(surface.Commands, FullscreenQuadVertices, 1, 0, 0);
    }

    public void Dispose() => _pipeline.Dispose();
}
