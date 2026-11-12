using System.Numerics;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed class WgpuMaskPipeline : IDisposable
{
    private const int StackAllocThreshold = 128;
    private const uint FullscreenQuadVertices = 6;
    private readonly WgpuDevice _gpu;
    private readonly WgpuPipeline _pipeline;

    public WgpuMaskPipeline(WgpuDevice gpu, WgpuSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Mask.wgsl", false);
    }

    public void Draw(RenderScene scene, WgpuSurface surface, WgpuFrameResources frame)
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
        frame.BindBuffers(_pipeline, surface, in constants, frame.Write<Vector4>(masks), frame.Write<Vector2>(points));
        wgpuRenderPassEncoderDraw(surface.Pass, FullscreenQuadVertices, 1, 0, 0);
    }

    public void Dispose() => _pipeline.Dispose();
}
