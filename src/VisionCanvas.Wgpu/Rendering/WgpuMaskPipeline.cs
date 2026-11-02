using System.Numerics;
using VisionCanvas.Rendering;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed class WgpuMaskPipeline : IDisposable
{
    private readonly WgpuDevice _gpu;
    private readonly WgpuPipeline _pipeline;

    public WgpuMaskPipeline(WgpuDevice gpu, WgpuSurface surface)
    {
        _gpu = gpu;
        _pipeline = new(gpu, surface, "Mask.wgsl", false);
    }

    public void Draw(RenderScene scene, WgpuSurface surface, WgpuFrameResources frame)
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
        frame.BindBuffers(_pipeline, surface, in constants, frame.Write<Vector4>(masks), frame.Write<Vector2>(points));
        wgpuRenderPassEncoderDraw(surface.Pass, 6, 1, 0, 0);
    }

    public void Dispose() => _pipeline.Dispose();
}
