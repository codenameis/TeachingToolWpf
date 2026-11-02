using System.Runtime.InteropServices;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;
using Vortice.Vulkan;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>불변 BVH 블록의 GPU 형상을 보관하고 view/preview는 상수로만 변경한다.</summary>
internal sealed unsafe class VulkanRoiPipeline : IDisposable
{
    private readonly VulkanDevice _gpu;
    private readonly VulkanPipeline _pipeline;
    private readonly RoiBlockCache<DualBufferRoiBlock<VulkanBuffer>> _cache = new();

    public bool HasPendingDetails => _cache.HasPendingDetails;

    public VulkanRoiPipeline(VulkanDevice gpu, VulkanSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Roi.hlsl", false);
    }

    public void Draw(RenderScene scene, VulkanSurface surface, VulkanFrameResources frame)
    {
        _pipeline.Bind(surface);
        RoiSpatialTraversal.DrawCachedBlocks(
            _cache,
            scene,
            surface,
            UploadBlock,
            UploadRoleChanges,
            (block, constants) =>
            {
                frame.BindBuffers(
                    _pipeline,
                    surface,
                    in constants,
                    new() { buffer = block.Geometry.Handle, range = block.Geometry.Size },
                    new() { buffer = block.RoleBuffer.Handle, range = block.RoleBuffer.Size });
                _gpu.Api.vkCmdDraw(surface.Commands, 24, (uint)block.Count, 0, 0);
            });
    }

    private DualBufferRoiBlock<VulkanBuffer> UploadBlock(
        RoiSpatialBlock region,
        double aggregateSize,
        ReadOnlySpan<RoiInstance> instances,
        RoiRoleCache roles)
    {
        VulkanBuffer? geometry = null;
        VulkanBuffer? roleBuffer = null;
        try
        {
            geometry = Upload(instances);
            roleBuffer = Upload<uint>(roles.Values);
            var block = new DualBufferRoiBlock<VulkanBuffer>(
                geometry,
                roleBuffer,
                region.Bounds.Center,
                instances.Length,
                roles,
                aggregateSize,
                static b => b.AllocatedBytes);
            geometry = null;
            roleBuffer = null;
            return block;
        }
        finally
        {
            geometry?.Dispose();
            roleBuffer?.Dispose();
        }
    }

    private void UploadRoleChanges(DualBufferRoiBlock<VulkanBuffer> block, VulkanSurface surface)
    {
        var roleBuffer = Upload<uint>(block.Roles.Values);
        surface.Retire(block.RoleBuffer);
        block.RoleBuffer = roleBuffer;
    }

    private VulkanBuffer Upload<T>(ReadOnlySpan<T> values) where T : unmanaged
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

    public void Dispose()
    {
        _cache.Dispose();
        _pipeline.Dispose();
    }
}
