using System.Numerics;
using VisionCanvas.Rendering;

namespace VisionCanvas.Vulkan.Rendering;

internal sealed class VulkanMaskPipeline : IDisposable
{
    private readonly VulkanDevice _gpu;
    private readonly VulkanPipeline _pipeline;

    public VulkanMaskPipeline(VulkanDevice gpu, VulkanSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Mask.hlsl", false);
    }

    public void Draw(RenderScene scene, VulkanSurface surface, VulkanFrameResources frame)
    {
        if (!MaskDrawConstants.TryResolveActiveMaskTarget(scene, out var roi, out var geometry, out var draft, out var maskCount, out var vertexCount))
        {
            return;
        }

        var maskVectorCount = maskCount * 2;
        Span<Vector4> masks = maskVectorCount <= 128 ? stackalloc Vector4[maskVectorCount] : new Vector4[maskVectorCount];
        Span<Vector2> points = vertexCount <= 128 ? stackalloc Vector2[vertexCount] : new Vector2[vertexCount];
        MaskDrawConstants.Pack(scene, roi, geometry, draft, maskCount, masks, points, out var constants);

        _pipeline.Bind(surface);
        frame.BindBuffers(_pipeline, surface, in constants, frame.Write<Vector4>(masks).Descriptor, frame.Write<Vector2>(points).Descriptor);
        _gpu.Api.vkCmdDraw(surface.Commands, 6, 1, 0, 0);
    }

    public void Dispose() => _pipeline.Dispose();
}
