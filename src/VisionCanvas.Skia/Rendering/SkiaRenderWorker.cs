using SkiaSharp;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Skia.Rendering;

/// <summary>Skia 자원은 이 스레드만 사용한다. 입력·타일·프레임 요청은 최신 장면 하나로 합친다.</summary>
internal sealed class SkiaRenderWorker : RenderWorkerBase
{
    private const long TileBudgetBytes = 256L * 1024 * 1024;
    private readonly nint _window;
    private readonly IFramePreparer _preparer;

    public SkiaRenderWorker(nint window, TileWorker tiles, LatestFrameMailbox frames, IFramePreparer preparer)
        : base(tiles, frames, "Skia renderer", useStaApartment: false)
    {
        ArgumentNullException.ThrowIfNull(preparer);
        _window = window;
        _preparer = preparer;
    }

    protected override void RunCore()
    {
        try
        {
            using var surface = new SkiaSurface(_window);
            var textures = new SkiaTexturePipeline();
            using var sprites = new SkiaSprites();
            using var rois = new SkiaRois();
            using var masks = new SkiaMasks();
            using var text = new SkiaTextRasterizer();

            RaiseStatusChanged("Skia · CPU raster · 타일 캐시 256 MiB · Gray16 원본 보존 / 8비트 표시");

            var backend = new SkiaFrameBackend(surface, textures, sprites, rois, masks, text, _preparer);
            FrameLoop.Run(
                this,
                backend,
                new FrameLoopOptions(
                    TileBudgetBytes,
                    RequireVisibleTilesBeforeCapture: true,
                    NotifyFirstFrame: true,
                    CaptureBeforePresent: false));
        }
        catch (Exception error)
        {
            RaiseStatusChanged($"렌더 오류: Skia · {error.Message}");
        }
    }

    private sealed class SkiaFrameBackend(
        SkiaSurface surface,
        SkiaTexturePipeline textures,
        SkiaSprites sprites,
        SkiaRois rois,
        SkiaMasks masks,
        SkiaTextRasterizer textRasterizer,
        IFramePreparer framePreparer)
        : IFrameBackend
    {
        private SKCanvas? _canvas;
        private int _width;
        private int _height;

        public IRenderSurface Surface => surface;

        public ITexturePipeline Textures => textures;

        public ITextRasterizer TextRasterizer => textRasterizer;

        public IFramePreparer FramePreparer => framePreparer;

        public bool HasPendingRoiDetails => rois.HasPendingDetails;

        public double LastFenceWaitMilliseconds => 0;

        public bool TryBeginFrame(int width, int height)
        {
            _width = width;
            _height = height;
            _canvas = surface.Begin(width, height);
            return true;
        }

        public void BeginDrawing()
        {
        }

        public void DrawScene(RenderScene scene, SpriteMesh mesh, int roiBatchBoundary)
        {
            var canvas = _canvas!;
            canvas.Scale((float)(_width / scene.Width), (float)(_height / scene.Height));
            sprites.Draw(canvas, mesh, 0, roiBatchBoundary, scene.Width, scene.Height);
            masks.Draw(canvas, scene);
            rois.Draw(canvas, scene);
            sprites.Draw(canvas, mesh, roiBatchBoundary, mesh.Batches.Count, scene.Width, scene.Height);
        }

        public void EndDrawing()
        {
        }

        public IDisposable? CreateCaptureReadback() => null;

        public ulong Present()
        {
            surface.Present();
            return 0;
        }

        public void WaitForCompletion(ulong completionToken)
        {
        }

        public void SaveCapture(IDisposable? readback, string path)
        {
            readback?.Dispose();
            surface.Save(path);
        }

        public void OnAfterFrame()
        {
        }
    }
}
