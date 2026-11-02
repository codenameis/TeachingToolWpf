using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;
using VisionImaging.Abstractions;

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
        _window = window;
        _preparer = preparer;
    }

    private protected override void RunCore()
    {
        try
        {
            using var surface = new SkiaSurface(_window);
            var textures = new SkiaTexturePipeline();
            using var sprites = new SkiaSprites();
            using var rois = new SkiaRois();
            using var masks = new SkiaMasks();
            using var text = new SkiaTextRasterizer();
            using var labels = new GpuLabelCache(text);
            using var resultLabels = new GpuResultLabels(text);
            using var images = new GpuImageResources(TileBudgetBytes, textures, Tiles, _preparer);
            using var filters = new GpuFilterPreview();
            using var metrics = new FrameMetrics();
            var painter = new ScenePainter();
            IRenderTexture? white = null;
            VideoFrame? retained = null;
            RenderScene? presented = null;
            var overlayId = Guid.Empty;
            long frameNumber = 0;
            RaiseStatusChanged("Skia · CPU raster · 타일 캐시 256 MiB · Gray16 원본 보존 / 8비트 표시");
            try
            {
                while (WaitForWake())
                {
                    var width = TargetWidth;
                    var height = TargetHeight;
                    if (width <= 0 || height <= 0)
                    {
                        continue;
                    }

                    var submission = LatestSubmission;
                    var scene = submission?.Scene;
                    if (RenderWorkerLoop.IsNothingChanged(scene, presented, width, height,
                        surface.Width, surface.Height, Frames, Tiles, labels, rois.HasPendingDetails, PendingCapture, white))
                    {
                        continue;
                    }

                    var started = Stopwatch.GetTimestamp();
                    var canvas = surface.Begin(width, height);
                    var whiteTexture = RenderWorkerLoop.EnsureWhiteTexture(ref white, textures, surface);
                    var next = Frames.Take();
                    retained = RenderWorkerLoop.UpdateRetainedFrame(scene?.IsLive == true, retained, next, Frames);
                    frameNumber++;
                    if (scene is { Width: > 0, Height: > 0 })
                    {
                        var mesh = RenderWorkerLoop.BuildSceneMesh(
                            scene,
                            retained,
                            surface,
                            textures,
                            painter,
                            images,
                            filters,
                            labels,
                            resultLabels,
                            whiteTexture,
                            frameNumber,
                            Invalidate);
                        canvas.Scale((float)(width / scene.Width), (float)(height / scene.Height));
                        sprites.Draw(canvas, mesh, 0, painter.RoiBatchBoundary, scene.Width, scene.Height);
                        masks.Draw(canvas, scene);
                        rois.Draw(canvas, scene);
                        sprites.Draw(canvas, mesh, painter.RoiBatchBoundary, mesh.Batches.Count, scene.Width, scene.Height);
                        RenderWorkerLoop.FinishSceneLabels(labels, surface, rois.HasPendingDetails, Invalidate);
                    }

                    var cpuMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var presentStart = Stopwatch.GetTimestamp();
                    surface.Present();
                    presented = scene;
                    if (RenderWorkerLoop.ShouldNotifyOverlayPresented(scene, overlayId, labels, rois.HasPendingDetails, out overlayId))
                    {
                        RaiseOverlayPresented(new(overlayId, Stopwatch.GetTimestamp()));
                    }

                    if (TryTakeCapture(submission, images, labels, rois.HasPendingDetails, requireVisibleTiles: true) is { } path)
                    {
                        surface.Save(path);
                    }

                    RenderWorkerLoop.RecordAndNotifyMetrics(
                        metrics,
                        frameNumber,
                        notifyFirstFrame: true,
                        MetricsChangedHandler,
                        cpuMilliseconds,
                        gpuMilliseconds: null,
                        Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds,
                        next,
                        Frames.Dropped,
                        images.Bytes,
                        fenceWaitMilliseconds: 0,
                        submission);
                }
            }
            finally
            {
                white?.Dispose();
                retained?.Dispose();
            }
        }
        catch (Exception error)
        {
            RaiseStatusChanged($"렌더 오류: Skia · {error.Message}");
        }
    }
}
