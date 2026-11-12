using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>렌더 워커 스레드가 공통으로 사용하는 장면 제출·수명·조기 스킵·장면 준비·메트릭 기록 보조.</summary>
internal static class RenderWorkerLoop
{
    public const int MetricsNotificationFrameInterval = 30;

    internal sealed record Submission(long Version, RenderScene Scene, long Received);

    internal sealed record CaptureRequest(long Version, string Path);

    public static VideoFrame? UpdateRetainedFrame(bool isLive, VideoFrame? retained, VideoFrame? next, LatestFrameMailbox frames)
    {
        if (next is not null)
        {
            retained?.Dispose();
            retained = next;
        }

        if (!isLive)
        {
            retained?.Dispose();
            return null;
        }

        return retained ?? frames.RetainLatest();
    }

    public static bool IsNothingChanged(
        RenderScene? current,
        RenderScene? presented,
        int targetWidth,
        int targetHeight,
        int surfaceWidth,
        int surfaceHeight,
        LatestFrameMailbox frames,
        TileWorker tiles,
        GpuLabelCache labels,
        bool hasPendingDetails,
        CaptureRequest? capture,
        IRenderTexture? white)
    {
        return ReferenceEquals(current, presented)
            && targetWidth == surfaceWidth
            && targetHeight == surfaceHeight
            && !frames.HasPending
            && !tiles.HasReady
            && !labels.HasPending
            && !hasPendingDetails
            && capture is null
            && white is not null;
    }

    public static IRenderTexture EnsureWhiteTexture(ref IRenderTexture? white, ITexturePipeline textures, IRenderSurface surface)
    {
        if (white is not null)
        {
            return white;
        }

        using var pixel = new PixelBuffer(1, 1, PixelKind.Rgba32);
        pixel.Span.Fill(255);
        white = textures.Upload(pixel, surface);
        return white;
    }

    public static SpriteMesh BuildSceneMesh(
        RenderScene scene,
        VideoFrame? retainedFrame,
        IRenderSurface surface,
        ITexturePipeline textures,
        ScenePainter painter,
        GpuImageResources images,
        GpuFilterPreview filters,
        GpuLabelCache labels,
        GpuResultLabels resultLabels,
        IRenderTexture white,
        long frameNumber,
        Action invalidate)
    {
        if (images.Update(scene, retainedFrame, surface, frameNumber))
        {
            invalidate();
        }

        filters.Update(scene.FilterPreview, textures, surface);
        labels.Begin();
        resultLabels.Update(scene, textures, surface);
        return painter.Build(
            scene,
            white,
            images.Tiles,
            text => labels.Get(text, scene.Dpi, textures, surface),
            text => labels.TryGet(text, scene.Dpi, textures, surface),
            images.Live,
            images.LiveBounds,
            filters,
            images.LiveOverview,
            resultLabels.Get);
    }

    public static void FinishSceneLabels(
        GpuLabelCache labels,
        IRenderSurface surface,
        bool hasPendingDetails,
        Action invalidate)
    {
        labels.End(surface);
        if (labels.HasPending || hasPendingDetails)
        {
            invalidate();
        }
    }

    public static bool ShouldNotifyOverlayPresented(
        RenderScene? scene,
        Guid presentedOverlay,
        GpuLabelCache labels,
        bool hasPendingDetails,
        out Guid nextOverlayId)
    {
        if (scene?.Inspection is { BatchLabels: true } overlay
            && overlay.PresentationId != presentedOverlay
            && !labels.HasPending
            && !hasPendingDetails)
        {
            nextOverlayId = overlay.PresentationId;
            return true;
        }

        nextOverlayId = presentedOverlay;
        return false;
    }

    public static void RecordAndNotifyMetrics(
        FrameMetrics metrics,
        long frameNumber,
        bool notifyFirstFrame,
        Action<RenderMetrics>? metricsChanged,
        double cpuMilliseconds,
        double? gpuMilliseconds,
        double presentMilliseconds,
        VideoFrame? nextFrame,
        long droppedFrames,
        long imageBytes,
        double fenceWaitMilliseconds,
        Submission? submission,
        DisplayPresentStatistics? presentationStatistics = null,
        double pacingWaitMilliseconds = 0)
    {
        var sceneAgeMilliseconds = submission is null
            ? 0
            : Stopwatch.GetElapsedTime(submission.Received).TotalMilliseconds;
        var measurement = metrics.Record(
            cpuMilliseconds,
            gpuMilliseconds,
            presentMilliseconds,
            nextFrame?.Timestamp,
            droppedFrames,
            imageBytes,
            nextFrame?.Sequence,
            fenceWaitMilliseconds,
            sceneAgeMilliseconds,
            presentationStatistics,
            pacingWaitMilliseconds);
        if ((notifyFirstFrame && frameNumber == 1) || frameNumber % MetricsNotificationFrameInterval == 0)
        {
            metricsChanged?.Invoke(measurement);
        }
    }

    public static string? TryTakeCapture(
        ref CaptureRequest? captureField,
        Submission? submission,
        GpuImageResources images,
        GpuLabelCache labels,
        bool hasPendingDetails,
        bool requireVisibleTiles)
    {
        if (hasPendingDetails)
        {
            return null;
        }

        var request = Volatile.Read(ref captureField);
        if (request is null || (submission?.Version ?? 0) < request.Version || labels.HasPending)
        {
            return null;
        }

        if (submission?.Scene is { Image: { } image } scene)
        {
            var ready = false;
            if (scene.IsLive)
            {
                ready = images.LiveOverview is not null;
            }
            else if (images.Tiles.ContainsKey(new(image.Levels - 1, 0, 0)))
            {
                ready = true;
                if (requireVisibleTiles)
                {
                    var visible = TilePlanner.Visible(image, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi);
                    for (var i = 0; i < visible.Count; i++)
                    {
                        if (!images.Tiles.ContainsKey(visible[i]))
                        {
                            ready = false;
                            break;
                        }
                    }
                }
            }

            if (!ready)
            {
                return null;
            }
        }

        return ReferenceEquals(Interlocked.CompareExchange(ref captureField, null, request), request) ? request.Path : null;
    }
}
