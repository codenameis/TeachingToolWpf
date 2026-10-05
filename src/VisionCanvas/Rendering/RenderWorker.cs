using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>GPU 자원의 단일 소유 스레드. UI 장면과 영상 요청은 최신 값 하나로 합친다.</summary>
public sealed class RenderWorker : IDisposable
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const int MetricsNotificationFrameInterval = 30;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly nint _hwnd;
    private readonly TileWorker _tiles;
    private readonly ITextRasterizer _textRasterizer;
    private readonly LatestFrameMailbox _frames;
    private readonly IFramePreparer _framePreparer;
    private sealed record Submission(long Version, RenderScene Scene);
    private sealed record CaptureRequest(long Version, string Path);
    private Submission? _latestSubmission;
    private CaptureRequest? _captureRequest;
    private long _sceneVersion;
    private volatile bool _stopping;
    private int _width = 1;
    private int _height = 1;
    public event Action<string>? StatusChanged;
    public event Action<RenderMetrics>? MetricsChanged;
    public event Action<OverlayPresentation>? OverlayPresented;

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames, IFramePreparer framePreparer)
    {
        _hwnd = hwnd;
        _tiles = tiles;
        _textRasterizer = textRasterizer;
        _frames = frames;
        _framePreparer = framePreparer;
        tiles.TileReady += Invalidate;
        frames.Available += Invalidate;
        _thread = new Thread(Run) { IsBackground = true, Name = "DX12 renderer" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start() => _thread.Start();

    public void Resize(int width, int height)
    {
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        _wake.Set();
    }

    public void Invalidate()
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException) when (_stopping)
        {
            // 구독 해제 직전에 생산자가 이미 가져간 콜백은 종료 이후 도착할 수 있다.
        }
    }

    public void Submit(RenderScene value)
    {
        var submission = new Submission(Interlocked.Increment(ref _sceneVersion), value);
        Volatile.Write(ref _latestSubmission, submission);
        Invalidate();
    }

    public void Capture(string path)
    {
        var minimumSceneVersion = Volatile.Read(ref _latestSubmission)?.Version ?? 0;
        Interlocked.Exchange(ref _captureRequest, new(minimumSceneVersion, path));
        Invalidate();
    }

    private void Run()
    {
        try
        {
            using var gpu = new Dx12Device(Environment.GetCommandLineArgs().Contains("--gpu-debug"));
            using var surface = new Dx12Surface(gpu, _hwnd, _width, _height);
            using var pipeline = new SpritePipeline(gpu);
            using var roiPipeline = new RoiPipeline(gpu);
            using var gpuTimer = new GpuTimer(gpu);
            using var metrics = new FrameMetrics();
            using var profile = new RenderProfile();
            var painter = new ScenePainter();
            using var labels = new GpuLabelCache(_textRasterizer, gpu);
            using var resultLabels = new GpuResultLabels(_textRasterizer);
            using var images = new GpuImageResources(gpu, pipeline, _tiles, _framePreparer);
            using var filters = new GpuFilterPreview();
            GpuTexture? white = null;
            VideoFrame? retainedFrame = null;
            long frame = 0;
            RenderScene? presentedScene = null;
            var presentedOverlay = Guid.Empty;
            StatusChanged?.Invoke($"DX12 · {gpu.AdapterName} · VRAM {gpu.DedicatedMemoryBytes / BytesPerGibibyte:F1} GiB · Budget {gpu.LocalMemoryBudget / BytesPerGibibyte:F1} GiB · Debug: {gpu.DebugEnabled}");
            try
            {
                while (!_stopping)
                {
                    _wake.WaitOne();
                    if (_stopping)
                    {
                        break;
                    }

                    var targetWidth = Volatile.Read(ref _width);
                    var targetHeight = Volatile.Read(ref _height);
                    if (targetWidth <= 0 || targetHeight <= 0)
                    {
                        continue;
                    }

                    var submission = Volatile.Read(ref _latestSubmission);
                    var current = submission?.Scene;
                    var nothingChanged = ReferenceEquals(current, presentedScene)
                        && targetWidth == surface.Width && targetHeight == surface.Height
                        && !_frames.HasPending && !_tiles.HasReady && !labels.HasPending
                        && Volatile.Read(ref _captureRequest) is null && white is not null;
                    if (nothingChanged)
                    {
                        continue;
                    }

                    // Begin은 프레임 슬롯의 GPU 완료를 기다린다. 이후에만 그 슬롯의 업로드 공간을 재사용한다.
                    var cpuStart = Stopwatch.GetTimestamp();
                    var allocationStart = profile.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    surface.Resize(targetWidth, targetHeight);
                    surface.Begin();
                    frame++;
                    var gpuElapsed = gpuTimer.Begin(surface);
                    pipeline.BeginFrame(surface);
                    if (white is null)
                    {
                        using var pixel = new PixelBuffer(1, 1, PixelKind.Rgba32);
                        pixel.Span.Fill(255);
                        white = pipeline.Upload(pixel, surface);
                    }
                    var videoFrame = _frames.Take();
                    if (videoFrame is not null)
                    {
                        retainedFrame?.Dispose();
                        retainedFrame = videoFrame;
                    }
                    if (current?.IsLive == true && retainedFrame is null)
                    {
                        retainedFrame = _frames.RetainLatest();
                    }

                    if (current?.IsLive != true)
                    {
                        retainedFrame?.Dispose();
                        retainedFrame = null;
                    }
                    if (current is not null && current.Width > 0 && current.Height > 0)
                    {
                        if (images.Update(current, retainedFrame, surface, frame))
                        {
                            Invalidate();
                        }

                        filters.Update(current.FilterPreview, pipeline, surface);
                        labels.Begin();
                        resultLabels.Update(current, pipeline, surface);
                        var paintStart = Stopwatch.GetTimestamp();
                        var mesh = painter.Build(current, white, images.Tiles, text => labels.Get(text, current.Dpi, pipeline, surface),
                            text => labels.TryGet(text, current.Dpi, pipeline, surface), images.Live, images.LiveBounds, filters, images.LiveOverview,
                            resultLabels.Get);
                        var submitStart = Stopwatch.GetTimestamp();
                        pipeline.Draw(mesh, surface, painter.RoiBatchBoundary, () => roiPipeline.Draw(current, surface));
                        profile.Record(current, cpuStart, paintStart, submitStart, Stopwatch.GetTimestamp(), allocationStart, mesh, roiPipeline, labels);
                        labels.End(surface);
                        if (labels.HasPending)
                        {
                            Invalidate();
                        }
                    }
                    var capture = TryTakeReadyCapture(submission, images, labels);
                    using var readback = capture is null ? null : new FrameReadback(gpu, surface);
                    gpuTimer.End(surface);
                    var cpuElapsed = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                    var presentStart = Stopwatch.GetTimestamp();
                    var completionFence = surface.Present();
                    presentedScene = current;
                    if (current?.Inspection is { BatchLabels: true } overlay &&
                        overlay.PresentationId != presentedOverlay && !labels.HasPending)
                    {
                        // 렌더 소유 스레드에서 이번 결과의 fence를 관찰한다. 다음 프레임이나 UI의 동기 대기에 의존하지 않는다.
                        gpu.Wait(completionFence);
                        presentedOverlay = overlay.PresentationId;
                        OverlayPresented?.Invoke(new(overlay.PresentationId, Stopwatch.GetTimestamp()));
                    }
                    var measurement = metrics.Record(cpuElapsed, gpuElapsed, Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds, videoFrame?.Timestamp,
                        _frames.Dropped, images.Bytes, videoFrame?.Sequence);
                    if (frame % MetricsNotificationFrameInterval == 0)
                    {
                        MetricsChanged?.Invoke(measurement);
                    }

                    foreach (var message in gpu.ReadDebugMessages())
                    {
                        StatusChanged?.Invoke("D3D12 " + message);
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
            StatusChanged?.Invoke($"렌더 오류: {ex.Message}");
        }
    }

    private string? TryTakeReadyCapture(Submission? submission, GpuImageResources images, GpuLabelCache labels)
    {
        var request = Volatile.Read(ref _captureRequest);
        var current = submission?.Scene;
        var overviewReady = current?.Image is not { } image
            || (current.IsLive ? images.LiveOverview is not null : images.Tiles.ContainsKey(new(image.Levels - 1, 0, 0)));

        // 이전 장면을 그리던 중 들어온 캡처 요청에 이전 화면을 저장하면 안 된다.
        if (request is null || (submission?.Version ?? 0) < request.Version || !overviewReady || labels.HasPending)
        {
            return null;
        }

        // 준비 중 새 캡처가 도착했다면 새 요청을 지우지 않고 다음 프레임에 처리한다.
        var claimedRequest = Interlocked.CompareExchange(ref _captureRequest, null, request);
        return ReferenceEquals(claimedRequest, request) ? request.Path : null;
    }

    public void Dispose()
    {
        _tiles.TileReady -= Invalidate;
        _frames.Available -= Invalidate;
        _stopping = true;
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _wake.Dispose();
    }
}
