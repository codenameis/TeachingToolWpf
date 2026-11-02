using VisionCanvas.Imaging;
using VisionCanvas.Rendering;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>GPU는 이 STA 스레드만 사용한다. UI 장면과 라이브 프레임은 최신 요청 하나로 합친다.</summary>
internal sealed class VulkanRenderWorker : RenderWorkerBase
{
    private readonly nint _hwnd;
    private readonly ITextRasterizer _rasterizer;
    private readonly IFramePreparer _preparer;

    public VulkanRenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer rasterizer, LatestFrameMailbox frames, IFramePreparer preparer)
        : base(tiles, frames, "Vulkan renderer", useStaApartment: true)
    {
        _hwnd = hwnd;
        _rasterizer = rasterizer;
        _preparer = preparer;
    }

    private protected override void RunCore()
    {
        try
        {
            using var gpu = new VulkanDevice(_hwnd);
            using var surface = new VulkanSurface(gpu, TargetWidth, TargetHeight);
            using var first = new VulkanFrameResources(gpu);
            using var second = new VulkanFrameResources(gpu);
            VulkanFrameResources[] resources = [first, second];
            using var sprites = new VulkanSpritePipeline(gpu, surface, resources);
            using var rois = new VulkanRoiPipeline(gpu, surface);
            using var masks = new VulkanMaskPipeline(gpu, surface);
            var budget = gpu.LocalMemoryBudget is { } bytes ? $"Budget {bytes / (1024d * 1024 * 1024):F1} GiB" : "Budget unavailable";
            RaiseStatusChanged($"Vulkan · {gpu.Name} · API {gpu.Properties.apiVersion} · {budget} · Tile cache {gpu.TileBudgetBytes / (1024 * 1024)} MiB · FIFO");
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
                    s => new VulkanReadback(gpu, s),
                    static (readback, path) => readback.Save(path),
                    static s => (ulong)s.Present().Handle,
                    fence => gpu.Wait(new(fence)));
            }
            finally
            {
                gpu.Api.vkDeviceWaitIdle();
            }
        }
        catch (Exception error)
        {
            RaiseStatusChanged($"렌더 오류: Vulkan · {error.Message}");
        }
    }
}
