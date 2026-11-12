using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Vulkan.Rendering;

/// <summary>GPU는 이 STA 스레드만 사용한다. UI 장면과 라이브 프레임은 최신 요청 하나로 합친다.</summary>
internal sealed class VulkanRenderWorker : RenderWorkerBase
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const long BytesPerMebibyte = 1024L * 1024;

    private readonly nint _hwnd;
    private readonly ITextRasterizer _rasterizer;
    private readonly IFramePreparer _preparer;

    public VulkanRenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer rasterizer, LatestFrameMailbox frames, IFramePreparer preparer)
        : base(tiles, frames, "Vulkan renderer", useStaApartment: true)
    {
        ArgumentNullException.ThrowIfNull(rasterizer);
        ArgumentNullException.ThrowIfNull(preparer);
        _hwnd = hwnd;
        _rasterizer = rasterizer;
        _preparer = preparer;
    }

    protected override void RunCore()
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
            var budget = gpu.LocalMemoryBudget is { } bytes ? $"Budget {bytes / BytesPerGibibyte:F1} GiB" : "Budget unavailable";
            RaiseStatusChanged($"Vulkan · {gpu.Name} · API {gpu.Properties.apiVersion} · {budget} · Tile cache {gpu.TileBudgetBytes / BytesPerMebibyte} MiB · FIFO");

            var backend = new VulkanFrameBackend(gpu, surface, resources, sprites, rois, masks, _rasterizer, _preparer);
            try
            {
                FrameLoop.Run(
                    this,
                    backend,
                    new FrameLoopOptions(
                        gpu.TileBudgetBytes,
                        RequireVisibleTilesBeforeCapture: true,
                        NotifyFirstFrame: true,
                        CaptureBeforePresent: true));
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

    private sealed class VulkanFrameBackend(
        VulkanDevice gpu,
        VulkanSurface surface,
        VulkanFrameResources[] resources,
        VulkanSpritePipeline sprites,
        VulkanRoiPipeline rois,
        VulkanMaskPipeline masks,
        ITextRasterizer textRasterizer,
        IFramePreparer framePreparer)
        : IFrameBackend
    {
        public IRenderSurface Surface => surface;

        public ITexturePipeline Textures => sprites;

        public ITextRasterizer TextRasterizer => textRasterizer;

        public IFramePreparer FramePreparer => framePreparer;

        public bool HasPendingRoiDetails => rois.HasPendingDetails;

        public double LastFenceWaitMilliseconds => 0;

        public bool TryBeginFrame(int width, int height)
        {
            if (!surface.Begin(width, height))
            {
                return false;
            }

            resources[surface.FrameIndex].Reset();
            return true;
        }

        public void BeginDrawing() => surface.BeginDrawing();

        public void DrawScene(RenderScene scene, SpriteMesh mesh, int roiBatchBoundary)
        {
            var frameResources = resources[surface.FrameIndex];
            sprites.Draw(mesh, surface, roiBatchBoundary, () =>
            {
                masks.Draw(scene, surface, frameResources);
                rois.Draw(scene, surface, frameResources);
            });
        }

        public void EndDrawing() => surface.EndDrawing();

        public IDisposable? CreateCaptureReadback()
        {
            VulkanReadback? readback = null;
            try
            {
                readback = new VulkanReadback(gpu, surface);
                surface.Retire(readback);
                var retired = readback;
                readback = null;
                return retired;
            }
            finally
            {
                readback?.Dispose();
            }
        }

        public ulong Present() => (ulong)surface.Present().Handle;

        public void WaitForCompletion(ulong completionToken) => gpu.Wait(new(completionToken));

        public void SaveCapture(IDisposable? readback, string path)
        {
            if (readback is VulkanReadback vulkanReadback)
            {
                vulkanReadback.Save(path);
            }
        }

        public void OnAfterFrame()
        {
        }
    }
}
