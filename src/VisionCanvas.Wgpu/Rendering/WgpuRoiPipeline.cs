using System.Runtime.InteropServices;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>불변 BVH 블록의 GPU 형상을 보관하고 view/preview는 상수로만 변경한다.</summary>
internal sealed unsafe class WgpuRoiPipeline : RoiPipelineBase<WgpuBuffer, WgpuSurface>
{
    private readonly WgpuDevice _gpu;
    private readonly WgpuPipeline _pipeline;

    public WgpuRoiPipeline(WgpuDevice gpu, WgpuSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Roi.wgsl", false);
    }

    public void Draw(RenderScene scene, WgpuSurface surface, WgpuFrameResources frame)
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
                    new(block.Geometry.Handle, 0, block.Geometry.Size),
                    new(block.RoleBuffer.Handle, 0, block.RoleBuffer.Size));
                wgpuRenderPassEncoderDraw(surface.Pass, RoiDrawConstants.VerticesPerInstance, (uint)block.Count, 0, 0);
            });
    }

    protected override WgpuBuffer CreateStorageBuffer<TElement>(ReadOnlySpan<TElement> values)
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

    protected override ulong GetAllocatedBytes(WgpuBuffer buffer) => (ulong)buffer.AllocatedBytes;

    protected override void DisposeBackendResources() => _pipeline.Dispose();
}
