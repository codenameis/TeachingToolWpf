using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Wgpu.Rendering;

/// <summary>GPU는 이 STA 스레드만 사용한다. UI 장면과 라이브 프레임은 최신 요청 하나로 합친다.</summary>
internal sealed class WgpuRenderWorker : RenderWorkerBase
{
    private const long BytesPerMebibyte = 1024L * 1024;

    private readonly nint _hwnd;
    private readonly ITextRasterizer _rasterizer;
    private readonly IFramePreparer _preparer;

    public WgpuRenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer rasterizer, LatestFrameMailbox frames, IFramePreparer preparer)
        : base(tiles, frames, "Wgpu renderer", useStaApartment: true)
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
            using var gpu = new WgpuDevice(_hwnd);
            using var surface = new WgpuSurface(gpu, TargetWidth, TargetHeight);
            using var first = new WgpuFrameResources(gpu);
            using var second = new WgpuFrameResources(gpu);
            WgpuFrameResources[] resources = [first, second];
            using var sprites = new WgpuSpritePipeline(gpu, surface, resources);
            using var rois = new WgpuRoiPipeline(gpu, surface);
            using var masks = new WgpuMaskPipeline(gpu, surface);
            RaiseStatusChanged($"wgpu · {gpu.Name} · DX12 · {gpu.Driver} · Tile cache {gpu.TileBudgetBytes / BytesPerMebibyte} MiB · FIFO");

            var backend = new WgpuFrameBackend(gpu, surface, resources, sprites, rois, masks, _rasterizer, _preparer);
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
                gpu.WaitIdle();
            }
        }
        catch (Exception error)
        {
            RaiseStatusChanged($"렌더 오류: Wgpu · {error.Message}");
        }
    }

    private sealed class WgpuFrameBackend(
        WgpuDevice gpu,
        WgpuSurface surface,
        WgpuFrameResources[] resources,
        WgpuSpritePipeline sprites,
        WgpuRoiPipeline rois,
        WgpuMaskPipeline masks,
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
            WgpuReadback? readback = null;
            try
            {
                readback = new WgpuReadback(gpu, surface);
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

        public ulong Present() => surface.Present();

        public void WaitForCompletion(ulong completionToken) => gpu.Wait(completionToken);

        public void SaveCapture(IDisposable? readback, string path)
        {
            if (readback is WgpuReadback wgpuReadback)
            {
                wgpuReadback.Save(path);
            }
        }

        public void OnAfterFrame()
        {
        }
    }
}
