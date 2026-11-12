using System.Diagnostics;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>GPU 자원의 단일 소유 스레드. UI 장면과 영상 요청은 최신 값 하나로 합친다.</summary>
public sealed class RenderWorker : RenderWorkerBase
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const long BytesPerMebibyte = 1024L * 1024;
    private const int PacingWaitTimeoutMilliseconds = 1000;

    private readonly nint _hwnd;
    private readonly ITextRasterizer _textRasterizer;
    private readonly IFramePreparer _framePreparer;
    private readonly RenderDevicePreference _devicePreference;

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames, IFramePreparer framePreparer)
        : this(hwnd, tiles, textRasterizer, frames, framePreparer, RenderDevicePreference.Automatic)
    {
    }

    public RenderWorker(
        nint hwnd,
        TileWorker tiles,
        ITextRasterizer textRasterizer,
        LatestFrameMailbox frames,
        IFramePreparer framePreparer,
        RenderDevicePreference devicePreference)
        : base(tiles, frames, "DX12 renderer", useStaApartment: true)
    {
        ArgumentNullException.ThrowIfNull(textRasterizer);
        ArgumentNullException.ThrowIfNull(framePreparer);
        if (!Enum.IsDefined(devicePreference))
        {
            throw new ArgumentOutOfRangeException(nameof(devicePreference));
        }

        _hwnd = hwnd;
        _textRasterizer = textRasterizer;
        _framePreparer = framePreparer;
        _devicePreference = devicePreference;
    }

    protected override void RunCore()
    {
        try
        {
            using var gpu = new Dx12Device(Environment.GetCommandLineArgs().Contains("--gpu-debug"), _devicePreference);
            using var surface = new Dx12Surface(gpu, _hwnd, TargetWidth, TargetHeight);
            using var pipeline = new SpritePipeline(gpu);
            using var profile = new RenderProfile();
            using var roiPipeline = new RoiPipeline(gpu, profile.IsEnabled);
            using var maskPipeline = new MaskPipeline(gpu);
            using var gpuTimer = gpu.IsSoftware ? null : new GpuTimer(gpu);

            RaiseStatusChanged(gpu.IsSoftware
                ? $"DX12 · CPU (WARP) · {gpu.AdapterName} · Tile cache {gpu.TileBudgetBytes / BytesPerMebibyte} MiB · Debug: {gpu.DebugEnabled}"
                : $"DX12 · GPU · {gpu.AdapterName} · VRAM {gpu.DedicatedMemoryBytes / BytesPerGibibyte:F1} GiB · Budget {gpu.LocalMemoryBudget / BytesPerGibibyte:F1} GiB · Debug: {gpu.DebugEnabled}");

            var backend = new Dx12FrameBackend(
                gpu,
                surface,
                pipeline,
                roiPipeline,
                maskPipeline,
                profile,
                gpuTimer,
                _textRasterizer,
                _framePreparer,
                StopHandle,
                RaiseStatusChanged);

            try
            {
                FrameLoop.Run(
                    this,
                    backend,
                    new FrameLoopOptions(
                        gpu.TileBudgetBytes,
                        RequireVisibleTilesBeforeCapture: false,
                        NotifyFirstFrame: false,
                        CaptureBeforePresent: true));
            }
            finally
            {
                gpu.Flush();
            }
        }
        catch (Exception ex)
        {
            RaiseStatusChanged($"렌더 오류: {ex.Message}");
        }
    }

    private sealed class Dx12FrameBackend(
        Dx12Device gpu,
        Dx12Surface surface,
        SpritePipeline sprites,
        RoiPipeline roiPipeline,
        MaskPipeline maskPipeline,
        RenderProfile profile,
        GpuTimer? gpuTimer,
        ITextRasterizer textRasterizer,
        IFramePreparer framePreparer,
        WaitHandle stopHandle,
        Action<string> reportStatus)
        : IFrameBackend, IFramePacing, IFrameProfiler
    {
        private readonly WaitHandle[] _frameSignals = [surface.FrameReady, stopHandle];
        private bool _frameReady;

        public IRenderSurface Surface => surface;

        public ITexturePipeline Textures => sprites;

        public ITextRasterizer TextRasterizer => textRasterizer;

        public IFramePreparer FramePreparer => framePreparer;

        public bool HasPendingRoiDetails => roiPipeline.HasPendingDetails;

        public double LastFenceWaitMilliseconds => surface.FenceWaitMilliseconds;

        public bool IsProfileEnabled => profile.IsEnabled;

        public FramePacingResult WaitForNextFrame()
        {
            var pacingStart = Stopwatch.GetTimestamp();
            if (!_frameReady && WaitHandle.WaitAny(_frameSignals, PacingWaitTimeoutMilliseconds) != 0)
            {
                gpu.Device.DeviceRemovedReason.CheckError();
                return new FramePacingResult(false, Stopwatch.GetElapsedTime(pacingStart).TotalMilliseconds);
            }

            _frameReady = true;
            return new FramePacingResult(true, Stopwatch.GetElapsedTime(pacingStart).TotalMilliseconds);
        }

        public bool TryBeginFrame(int width, int height)
        {
            surface.Resize(width, height);
            surface.Begin();
            sprites.BeginFrame(surface);
            return true;
        }

        public double? BeginGpuTiming() => gpuTimer?.Begin(surface);

        public void BeginDrawing()
        {
        }

        public void DrawScene(RenderScene scene, SpriteMesh mesh, int roiBatchBoundary)
        {
            sprites.Draw(mesh, surface, roiBatchBoundary, () =>
            {
                maskPipeline.Draw(scene, surface);
                roiPipeline.Draw(scene, surface);
            });
        }

        public void EndDrawing()
        {
        }

        public void RecordScene(
            RenderScene scene,
            long cpuStartTicks,
            long paintStartTicks,
            long submitStartTicks,
            long submitEndTicks,
            long allocatedBytesStart,
            TimeSpan gcPauseStart,
            SpriteMesh mesh,
            LabelCacheStats labels)
        {
            var gcPauseDelta = profile.IsEnabled ? GC.GetTotalPauseDuration() - gcPauseStart : default;
            var roiStats = new RoiRenderStats(
                roiPipeline.InstanceCount,
                roiPipeline.UploadedBytes,
                roiPipeline.CacheBytes,
                roiPipeline.PackMilliseconds,
                roiPipeline.CreateMilliseconds,
                roiPipeline.UploadMilliseconds,
                roiPipeline.HasPendingDetails,
                roiPipeline.DeferredBlocks);
            profile.Record(scene, cpuStartTicks, paintStartTicks, submitStartTicks, submitEndTicks, allocatedBytesStart, mesh, roiStats, labels, gcPauseDelta);
        }

        public IDisposable? CreateCaptureReadback() => new FrameReadback(gpu, surface);

        public void EndGpuTiming() => gpuTimer?.End(surface);

        public ulong Present()
        {
            var fence = surface.Present();
            _frameReady = false;
            return fence;
        }

        public void WaitForCompletion(ulong completionToken) => gpu.Wait(completionToken);

        public DisplayPresentStatistics? ReadPresentationStatistics() => surface.ReadPresentationStatistics();

        public void OnAfterFrame()
        {
            foreach (var message in gpu.ReadDebugMessages())
            {
                reportStatus("D3D12 " + message);
            }
        }

        public void SaveCapture(IDisposable? readback, string path)
        {
            if (readback is not FrameReadback dx12Readback)
            {
                readback?.Dispose();
                return;
            }

            try
            {
                gpu.Flush();
                dx12Readback.Save(path);
            }
            finally
            {
                dx12Readback.Dispose();
            }
        }
    }
}
