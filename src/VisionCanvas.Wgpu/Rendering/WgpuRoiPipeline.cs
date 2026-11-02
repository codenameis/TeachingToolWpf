using System.Runtime.InteropServices;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering;
using VisionGeometry;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>불변 BVH 블록의 GPU 형상을 보관하고 view/preview는 상수로만 변경한다.</summary>
internal sealed unsafe class WgpuRoiPipeline : IDisposable
{
    private readonly WgpuDevice _gpu;
    private readonly WgpuPipeline _pipeline;
    private readonly RoiBlockCache<DualBufferRoiBlock<WgpuBuffer>> _cache = new();

    public bool HasPendingDetails => _cache.HasPendingDetails;

    public WgpuRoiPipeline(WgpuDevice gpu, WgpuSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Roi.wgsl", false);
    }

    public void Draw(RenderScene scene, WgpuSurface surface, WgpuFrameResources frame)
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
                    new(block.Geometry.Handle, 0, block.Geometry.Size),
                    new(block.RoleBuffer.Handle, 0, block.RoleBuffer.Size));
                wgpuRenderPassEncoderDraw(surface.Pass, 24, (uint)block.Count, 0, 0);
            });
    }

    private DualBufferRoiBlock<WgpuBuffer> UploadBlock(
        RoiSpatialBlock region,
        double aggregateSize,
        ReadOnlySpan<RoiInstance> instances,
        RoiRoleCache roles)
    {
        WgpuBuffer? geometry = null;
        WgpuBuffer? roleBuffer = null;
        try
        {
            geometry = Upload(instances);
            roleBuffer = Upload<uint>(roles.Values);
            var block = new DualBufferRoiBlock<WgpuBuffer>(
                geometry,
                roleBuffer,
                region.Bounds.Center,
                instances.Length,
                roles,
                aggregateSize,
                static b => (ulong)b.AllocatedBytes);
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

    private void UploadRoleChanges(DualBufferRoiBlock<WgpuBuffer> block, WgpuSurface surface)
    {
        var roleBuffer = Upload<uint>(block.Roles.Values);
        surface.Retire(block.RoleBuffer);
        block.RoleBuffer = roleBuffer;
    }

    private WgpuBuffer Upload<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(values);
        var buffer = new WgpuBuffer(_gpu, (ulong)bytes.Length, WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
        try
        {
            buffer.Write(bytes);
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
