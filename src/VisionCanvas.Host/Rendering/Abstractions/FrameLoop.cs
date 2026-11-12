using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>DX12, Vulkan, Wgpu, Skia 4개 렌더러가 공유하는 단일 프레임 루프 실행기.</summary>
public static class FrameLoop
{
    public static void Run(RenderWorkerBase worker, IFrameBackend backend, FrameLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(backend);

        var pacing = backend as IFramePacing;
        var profiler = backend as IFrameProfiler;
        using var labels = new GpuLabelCache(backend.TextRasterizer);
        using var resultLabels = new GpuResultLabels(backend.TextRasterizer);
        using var images = new GpuImageResources(options.TileBudgetBytes, backend.Textures, worker.Tiles, backend.FramePreparer);
        using var filters = new GpuFilterPreview();
        using var metrics = new FrameMetrics();
        var painter = new ScenePainter();
        IRenderTexture? white = null;
        VideoFrame? retained = null;
        RenderScene? presented = null;
        var overlayId = Guid.Empty;
        long frameNumber = 0;

        try
        {
            while (worker.WaitForWake())
            {
                var width = worker.TargetWidth;
                var height = worker.TargetHeight;
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                var submission = worker.LatestSubmission;
                if (RenderWorkerLoop.IsNothingChanged(
                    submission?.Scene,
                    presented,
                    width,
                    height,
                    backend.Surface.Width,
                    backend.Surface.Height,
                    worker.Frames,
                    worker.Tiles,
                    labels,
                    backend.HasPendingRoiDetails,
                    worker.PendingCapture,
                    white))
                {
                    continue;
                }

                var cpuStart = Stopwatch.GetTimestamp();
                var allocationStart = profiler is { IsProfileEnabled: true } ? GC.GetAllocatedBytesForCurrentThread() : 0;
                var gcPauseStart = profiler is { IsProfileEnabled: true } ? GC.GetTotalPauseDuration() : default;
                var pacingWaitMilliseconds = 0d;

                if (pacing is not null)
                {
                    var pacingResult = pacing.WaitForNextFrame();
                    pacingWaitMilliseconds = pacingResult.WaitMilliseconds;
                    if (!pacingResult.ShouldContinue)
                    {
                        if (worker.IsStopping)
                        {
                            break;
                        }

                        worker.Invalidate();
                        continue;
                    }

                    if (worker.IsStopping)
                    {
                        break;
                    }

                    width = worker.TargetWidth;
                    height = worker.TargetHeight;
                    if (width <= 0 || height <= 0)
                    {
                        continue;
                    }
                }

                if (!backend.TryBeginFrame(width, height))
                {
                    worker.Invalidate();
                    continue;
                }

                submission = worker.LatestSubmission;
                var scene = submission?.Scene;
                frameNumber++;
                var gpuElapsed = profiler?.BeginGpuTiming();
                var whiteTexture = RenderWorkerLoop.EnsureWhiteTexture(ref white, backend.Textures, backend.Surface);
                var nextFrame = worker.Frames.Take();
                retained = RenderWorkerLoop.UpdateRetainedFrame(scene?.IsLive == true, retained, nextFrame, worker.Frames);

                SpriteMesh? mesh = null;
                long paintStart = 0;
                if (scene is { Width: > 0, Height: > 0 })
                {
                    paintStart = Stopwatch.GetTimestamp();
                    mesh = RenderWorkerLoop.BuildSceneMesh(
                        scene,
                        retained,
                        backend.Surface,
                        backend.Textures,
                        painter,
                        images,
                        filters,
                        labels,
                        resultLabels,
                        whiteTexture,
                        frameNumber,
                        worker.Invalidate);
                }

                backend.BeginDrawing();
                var submitStart = Stopwatch.GetTimestamp();
                if (mesh is not null)
                {
                    backend.DrawScene(scene!, mesh, painter.RoiBatchBoundary);
                }

                var submitEnd = Stopwatch.GetTimestamp();
                backend.EndDrawing();

                if (scene is { Width: > 0, Height: > 0 })
                {
                    profiler?.RecordScene(
                        scene,
                        cpuStart,
                        paintStart,
                        submitStart,
                        submitEnd,
                        allocationStart,
                        gcPauseStart,
                        mesh!,
                        new LabelCacheStats(labels.HasPending, labels.Created, labels.RetainedCount, labels.RetainedBytes, labels.Evicted));
                    RenderWorkerLoop.FinishSceneLabels(labels, backend.Surface, backend.HasPendingRoiDetails, worker.Invalidate);
                }
                else if (backend.HasPendingRoiDetails)
                {
                    worker.Invalidate();
                }

                string? captureBeforePresentPath = null;
                IDisposable? readback = null;
                if (options.CaptureBeforePresent)
                {
                    captureBeforePresentPath = worker.TryTakeCapture(submission, images, labels, backend.HasPendingRoiDetails, options.RequireVisibleTilesBeforeCapture);
                    if (captureBeforePresentPath is not null)
                    {
                        readback = backend.CreateCaptureReadback();
                    }
                }

                profiler?.EndGpuTiming();
                var cpuMilliseconds = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                var presentStart = Stopwatch.GetTimestamp();
                var completion = backend.Present();
                presented = scene;

                var notifyOverlay = RenderWorkerLoop.ShouldNotifyOverlayPresented(scene, overlayId, labels, backend.HasPendingRoiDetails, out overlayId);
                if (notifyOverlay || readback is not null)
                {
                    backend.WaitForCompletion(completion);
                }

                if (notifyOverlay)
                {
                    worker.NotifyOverlayPresented(new(overlayId, Stopwatch.GetTimestamp()));
                }

                if (!options.CaptureBeforePresent)
                {
                    var captureAfterPresentPath = worker.TryTakeCapture(submission, images, labels, backend.HasPendingRoiDetails, options.RequireVisibleTilesBeforeCapture);
                    if (captureAfterPresentPath is not null)
                    {
                        backend.SaveCapture(null, captureAfterPresentPath);
                    }
                }

                RenderWorkerLoop.RecordAndNotifyMetrics(
                    metrics,
                    frameNumber,
                    options.NotifyFirstFrame,
                    worker.MetricsChangedHandler,
                    cpuMilliseconds,
                    gpuElapsed,
                    Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds,
                    nextFrame,
                    worker.Frames.Dropped,
                    images.Bytes,
                    backend.LastFenceWaitMilliseconds,
                    submission,
                    metrics.Enabled ? profiler?.ReadPresentationStatistics() : null,
                    pacingWaitMilliseconds);

                backend.OnAfterFrame();

                if (options.CaptureBeforePresent && captureBeforePresentPath is not null)
                {
                    backend.SaveCapture(readback, captureBeforePresentPath);
                }
            }
        }
        finally
        {
            white?.Dispose();
            retained?.Dispose();
        }
    }
}
