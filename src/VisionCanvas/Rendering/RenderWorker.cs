using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>GPU 자원의 단일 소유 스레드. UI 장면과 영상 요청은 최신 값 하나로 합친다.</summary>
public sealed class RenderWorker : RenderWorkerBase
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private readonly nint _hwnd;
    private readonly ITextRasterizer _textRasterizer;
    private readonly IFramePreparer _framePreparer;
    private readonly RenderDevicePreference _devicePreference;

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames, IFramePreparer framePreparer)
        : this(hwnd, tiles, textRasterizer, frames, framePreparer, RenderDevicePreference.Automatic)
    {
    }

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames,
        IFramePreparer framePreparer, RenderDevicePreference devicePreference)
        : base(tiles, frames, "DX12 renderer", useStaApartment: true)
    {
        if (!Enum.IsDefined(devicePreference))
        {
            throw new ArgumentOutOfRangeException(nameof(devicePreference));
        }

        _hwnd = hwnd;
        _textRasterizer = textRasterizer;
        _framePreparer = framePreparer;
        _devicePreference = devicePreference;
    }

    private protected override void RunCore()
    {
        try
        {
            using var gpu = new Dx12Device(Environment.GetCommandLineArgs().Contains("--gpu-debug"), _devicePreference);
            using var surface = new Dx12Surface(gpu, _hwnd, TargetWidth, TargetHeight);
            WaitHandle[] frameSignals = [surface.FrameReady, StopHandle];
            using var pipeline = new SpritePipeline(gpu);
            using var profile = new RenderProfile();
            using var roiPipeline = new RoiPipeline(gpu, profile.Enabled);
            using var maskPipeline = new MaskPipeline(gpu);
            using var gpuTimer = gpu.IsSoftware ? null : new GpuTimer(gpu);
            using var metrics = new FrameMetrics();
            var painter = new ScenePainter();
            using var labels = new GpuLabelCache(_textRasterizer);
            using var resultLabels = new GpuResultLabels(_textRasterizer);
            using var images = new GpuImageResources(gpu.TileBudgetBytes, pipeline, Tiles, _framePreparer);
            using var filters = new GpuFilterPreview();
            IRenderTexture? white = null;
            VideoFrame? retainedFrame = null;
            long frame = 0;
            var frameReady = false;
            RenderScene? presentedScene = null;
            var presentedOverlay = Guid.Empty;
            RaiseStatusChanged(gpu.IsSoftware
                ? $"DX12 · CPU (WARP) · {gpu.AdapterName} · Tile cache {gpu.TileBudgetBytes / (1024 * 1024)} MiB · Debug: {gpu.DebugEnabled}"
                : $"DX12 · GPU · {gpu.AdapterName} · VRAM {gpu.DedicatedMemoryBytes / BytesPerGibibyte:F1} GiB · Budget {gpu.LocalMemoryBudget / BytesPerGibibyte:F1} GiB · Debug: {gpu.DebugEnabled}");
            try
            {
                while (WaitForWake())
                {
                    var targetWidth = TargetWidth;
                    var targetHeight = TargetHeight;
                    if (targetWidth <= 0 || targetHeight <= 0)
                    {
                        continue;
                    }

                    var submission = LatestSubmission;
                    var current = submission?.Scene;
                    if (RenderWorkerLoop.IsNothingChanged(current, presentedScene, targetWidth, targetHeight,
                        surface.Width, surface.Height, Frames, Tiles, labels, roiPipeline.HasPendingDetails, PendingCapture, white))
                    {
                        continue;
                    }

                    var cpuStart = Stopwatch.GetTimestamp();
                    var allocationStart = profile.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    var gcPauseStart = profile.Enabled ? GC.GetTotalPauseDuration() : default;
                    var pacingStart = Stopwatch.GetTimestamp();
                    if (!frameReady && WaitHandle.WaitAny(frameSignals, 1000) != 0)
                    {
                        gpu.Device.DeviceRemovedReason.CheckError();
                        Invalidate();
                        continue;
                    }

                    var pacingElapsed = Stopwatch.GetElapsedTime(pacingStart).TotalMilliseconds;
                    if (IsStopping)
                    {
                        break;
                    }

                    frameReady = true;
                    targetWidth = TargetWidth;
                    targetHeight = TargetHeight;
                    if (targetWidth <= 0 || targetHeight <= 0)
                    {
                        continue;
                    }

                    surface.Resize(targetWidth, targetHeight);
                    surface.Begin();
                    submission = LatestSubmission;
                    current = submission?.Scene;
                    frame++;
                    var gpuElapsed = gpuTimer?.Begin(surface);
                    pipeline.BeginFrame(surface);
                    var whiteTexture = RenderWorkerLoop.EnsureWhiteTexture(ref white, pipeline, surface);
                    var videoFrame = Frames.Take();
                    retainedFrame = RenderWorkerLoop.UpdateRetainedFrame(current?.IsLive == true, retainedFrame, videoFrame, Frames);
                    if (current is { Width: > 0, Height: > 0 })
                    {
                        DrawScene(current, retainedFrame, surface, pipeline, roiPipeline, maskPipeline,
                            painter, labels, resultLabels, images, filters, whiteTexture, profile, cpuStart, allocationStart, gcPauseStart, frame);
                    }

                    var capture = TryTakeCapture(submission, images, labels, roiPipeline.HasPendingDetails, requireVisibleTiles: false);
                    using var readback = capture is null ? null : new FrameReadback(gpu, surface);
                    gpuTimer?.End(surface);
                    var cpuElapsed = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                    var presentStart = Stopwatch.GetTimestamp();
                    var completionFence = surface.Present();
                    frameReady = false;
                    presentedScene = current;
                    if (RenderWorkerLoop.ShouldNotifyOverlayPresented(current, presentedOverlay, labels, roiPipeline.HasPendingDetails, out presentedOverlay))
                    {
                        gpu.Wait(completionFence);
                        RaiseOverlayPresented(new(presentedOverlay, Stopwatch.GetTimestamp()));
                    }

                    RenderWorkerLoop.RecordAndNotifyMetrics(
                        metrics,
                        frame,
                        notifyFirstFrame: false,
                        MetricsChangedHandler,
                        cpuElapsed,
                        gpuElapsed,
                        Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds,
                        videoFrame,
                        Frames.Dropped,
                        images.Bytes,
                        surface.FenceWaitMilliseconds,
                        submission,
                        metrics.Enabled ? surface.ReadPresentationStatistics() : null,
                        pacingElapsed);

                    foreach (var message in gpu.ReadDebugMessages())
                    {
                        RaiseStatusChanged("D3D12 " + message);
                    }

                    if (readback is not null)
                    {
                        gpu.Flush();
                        readback.Save(capture!);
                    }
                }
            }
            finally
            {
                try
                {
                    gpu.Flush();
                }
                finally
                {
                    white?.Dispose();
                    retainedFrame?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            RaiseStatusChanged($"렌더 오류: {ex.Message}");
        }
    }

    private void DrawScene(
        RenderScene current,
        VideoFrame? retainedFrame,
        Dx12Surface surface,
        SpritePipeline pipeline,
        RoiPipeline roiPipeline,
        MaskPipeline maskPipeline,
        ScenePainter painter,
        GpuLabelCache labels,
        GpuResultLabels resultLabels,
        GpuImageResources images,
        GpuFilterPreview filters,
        IRenderTexture white,
        RenderProfile profile,
        long cpuStart,
        long allocationStart,
        TimeSpan gcPauseStart,
        long frame)
    {
        var paintStart = Stopwatch.GetTimestamp();
        var mesh = RenderWorkerLoop.BuildSceneMesh(
            current,
            retainedFrame,
            surface,
            pipeline,
            painter,
            images,
            filters,
            labels,
            resultLabels,
            white,
            frame,
            Invalidate);
        var submitStart = Stopwatch.GetTimestamp();
        pipeline.Draw(mesh, surface, painter.RoiBatchBoundary, () =>
        {
            maskPipeline.Draw(current, surface);
            roiPipeline.Draw(current, surface);
        });
        var submitEnd = Stopwatch.GetTimestamp();
        var gcPauseDelta = profile.Enabled ? GC.GetTotalPauseDuration() - gcPauseStart : default;
        var roiStats = new RoiRenderStats(
            roiPipeline.InstanceCount,
            roiPipeline.UploadedBytes,
            roiPipeline.CacheBytes,
            roiPipeline.PackMilliseconds,
            roiPipeline.CreateMilliseconds,
            roiPipeline.UploadMilliseconds,
            roiPipeline.HasPendingDetails,
            roiPipeline.DeferredBlocks);
        profile.Record(current, cpuStart, paintStart, submitStart, submitEnd, allocationStart, mesh, roiStats, labels, gcPauseDelta);
        RenderWorkerLoop.FinishSceneLabels(labels, surface, roiPipeline.HasPendingDetails, Invalidate);
    }
}
