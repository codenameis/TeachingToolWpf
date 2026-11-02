using VisionCanvas.Imaging;
using VisionCanvas.Rendering;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>GPU는 이 STA 스레드만 사용한다. UI 장면과 라이브 프레임은 최신 요청 하나로 합친다.</summary>
internal sealed class WgpuRenderWorker : RenderWorkerBase
{
    private readonly nint _hwnd;
    private readonly ITextRasterizer _rasterizer;
    private readonly IFramePreparer _preparer;

    public WgpuRenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer rasterizer, LatestFrameMailbox frames, IFramePreparer preparer)
        : base(tiles, frames, "Wgpu renderer", useStaApartment: true)
    {
        _hwnd = hwnd;
        _rasterizer = rasterizer;
        _preparer = preparer;
    }

    private protected override void RunCore()
    {
        try
        {
            using var gpu = new WgpuDevice(_hwnd);
            using var surface = new WgpuSurface(gpu, TargetWidth, TargetHeight);
            using var first = new WgpuFrameResources(gpu);
            using var second = new WgpuFrameResources(gpu);
            WgpuFrameResources[] resources = [first, second];
            using var sprites = new WgpuSpritePipeline(gpu, surface, resources);
            using var rois = new WgpuRoiPipeline(gpu, surface);
            using var masks = new WgpuMaskPipeline(gpu, surface);
            RaiseStatusChanged($"wgpu · {gpu.Name} · DX12 · {gpu.Driver} · Tile cache {gpu.TileBudgetBytes / (1024 * 1024)} MiB · FIFO");
            try
            {
                RunTwoPhaseFrameLoop(
                    gpu.TileBudgetBytes,
                    sprites,
                    _rasterizer,
                    _preparer,
                    surface,
                    resources,
                    () => rois.HasPendingDetails,
                    static (s, w, h) => s.Begin(w, h),
                    static r => r.Reset(),
                    static s => s.BeginDrawing(),
                    (mesh, s, boundary, drawRois) => sprites.Draw(mesh, s, boundary, drawRois),
                    (scene, s, frameResources) =>
                    {
                        masks.Draw(scene, s, frameResources);
                        rois.Draw(scene, s, frameResources);
                    },
                    static s => s.EndDrawing(),
                    s => new WgpuReadback(gpu, s),
                    static (readback, path) => readback.Save(path),
                    static s => s.Present(),
                    gpu.Wait);
            }
            finally
            {
                gpu.WaitIdle();
            }
        }
        catch (Exception error)
        {
            RaiseStatusChanged($"렌더 오류: Wgpu · {error.Message}");
        }
    }
}
