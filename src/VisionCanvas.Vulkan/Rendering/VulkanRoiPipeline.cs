using System.Runtime.InteropServices;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using Vortice.Vulkan;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>불변 BVH 블록의 GPU 형상을 보관하고 view/preview는 상수로만 변경한다.</summary>
internal sealed unsafe class VulkanRoiPipeline : RoiPipelineBase<VulkanBuffer, VulkanSurface>
{
    private readonly VulkanDevice _gpu;
    private readonly VulkanPipeline _pipeline;

    public VulkanRoiPipeline(VulkanDevice gpu, VulkanSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Roi.hlsl", false);
    }

    public void Draw(RenderScene scene, VulkanSurface surface, VulkanFrameResources frame)
    {
        _pipeline.Bind(surface);
        DrawCached(
            scene,
            surface,
            (block, constants) =>
            {
                frame.BindBuffers(
                    _pipeline,
                    surface,
                    in constants,
                    new() { buffer = block.Geometry.Handle, range = block.Geometry.Size },
                    new() { buffer = block.RoleBuffer.Handle, range = block.RoleBuffer.Size });
                _gpu.Api.vkCmdDraw(surface.Commands, RoiDrawConstants.VerticesPerInstance, (uint)block.Count, 0, 0);
            });
    }

    protected override VulkanBuffer CreateStorageBuffer<TElement>(ReadOnlySpan<TElement> values)
    {
        var bytes = MemoryMarshal.AsBytes(values);
        var buffer = new VulkanBuffer(_gpu, (ulong)bytes.Length, VkBufferUsageFlags.StorageBuffer);
        try
        {
            bytes.CopyTo(new Span<byte>((void*)buffer.Pointer, bytes.Length));
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    protected override ulong GetAllocatedBytes(VulkanBuffer buffer) => buffer.AllocatedBytes;

    protected override void DisposeBackendResources() => _pipeline.Dispose();
}
