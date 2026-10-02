using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using System.Diagnostics;
using TeachingToolWpf.Diagnostics;

namespace TeachingToolWpf.Rendering;

/// <summary>GPU 자원의 단일 소유 스레드. UI 장면과 영상 요청은 최신 값 하나로 합친다.</summary>
public sealed class RenderWorker : IDisposable
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const int MetricsNotificationFrameInterval = 30;
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private readonly nint hwnd;
    private readonly TileWorker tiles;
    private readonly ITextRasterizer textRasterizer;
    private readonly LatestFrameMailbox frames;
    private readonly IFramePreparer framePreparer;
    private sealed record Submission(long Version, RenderScene Scene);
    private sealed record CaptureRequest(long Version, string Path);
    private Submission? latestSubmission;
    private CaptureRequest? captureRequest;
    private long sceneVersion;
    private volatile bool stopping;
    private int width = 1;
    private int height = 1;
    public event Action<string>? StatusChanged;
    public event Action<RenderMetrics>? MetricsChanged;

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames, IFramePreparer framePreparer)
    {
        this.hwnd = hwnd;
        this.tiles = tiles;
        this.textRasterizer = textRasterizer;
        this.frames = frames;
        this.framePreparer = framePreparer;
        tiles.TileReady += Invalidate;
        frames.Available += Invalidate;
        thread = new Thread(Run) { IsBackground = true, Name = "DX12 renderer" };
        thread.SetApartmentState(ApartmentState.STA);
    }
    public void Start() => thread.Start();
    public void Resize(int width, int height)
    {
        Volatile.Write(ref this.width, width);
        Volatile.Write(ref this.height, height);
        wake.Set();
    }
    public void Invalidate()
    {
        if (stopping)
            return;
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException) when (stopping)
        {
            // 구독 해제 직전에 생산자가 이미 가져간 콜백은 종료 이후 도착할 수 있다.
        }
    }

    public void Submit(RenderScene value)
    {
        var submission = new Submission(Interlocked.Increment(ref sceneVersion), value);
        Volatile.Write(ref latestSubmission, submission);
        Invalidate();
    }

    public void Capture(string path)
    {
        var minimumSceneVersion = Volatile.Read(ref latestSubmission)?.Version ?? 0;
        Interlocked.Exchange(ref captureRequest, new(minimumSceneVersion, path));
        Invalidate();
    }
    private void Run()
    {
        try
        {
            using var gpu = new Dx12Device(Environment.GetCommandLineArgs().Contains("--gpu-debug"));
            using var surface = new Dx12Surface(gpu, hwnd, width, height);
            using var pipeline = new SpritePipeline(gpu);
            using var roiPipeline = new RoiPipeline(gpu);
            using var gpuTimer = new GpuTimer(gpu);
            using var metrics = new FrameMetrics();
            using var profile = new RenderProfile();
            var painter = new ScenePainter();
            using var labels = new GpuLabelCache(textRasterizer, gpu);
            using var images = new GpuImageResources(gpu, pipeline, tiles, framePreparer);
            using var filters = new GpuFilterPreview();
            GpuTexture? white = null;
            VideoFrame? retainedFrame = null;
            long frame = 0;
            RenderScene? presentedScene = null;
            StatusChanged?.Invoke($"DX12 · {gpu.AdapterName} · VRAM {gpu.DedicatedMemoryBytes / BytesPerGibibyte:F1} GiB · Budget {gpu.LocalMemoryBudget / BytesPerGibibyte:F1} GiB · Debug: {gpu.DebugEnabled}");
            try
            {
                while (!stopping)
                {
                    wake.WaitOne();
                    if (stopping)
                        break;
                    var targetWidth = Volatile.Read(ref width);
                    var targetHeight = Volatile.Read(ref height);
                    if (targetWidth <= 0 || targetHeight <= 0)
                        continue;
                    var submission = Volatile.Read(ref latestSubmission);
                    var current = submission?.Scene;
                    var nothingChanged = ReferenceEquals(current, presentedScene)
                        && targetWidth == surface.Width && targetHeight == surface.Height
                        && !frames.HasPending && !tiles.HasReady && !labels.HasPending
                        && Volatile.Read(ref captureRequest) is null && white is not null;
                    if (nothingChanged)
                        continue;

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
                    var videoFrame = frames.Take();
                    if (videoFrame is not null)
                    {
                        retainedFrame?.Dispose();
                        retainedFrame = videoFrame;
                    }
                    if (current?.IsLive == true && retainedFrame is null)
                        retainedFrame = frames.RetainLatest();
                    if (current?.IsLive != true)
                    {
                        retainedFrame?.Dispose();
                        retainedFrame = null;
                    }
                    if (current is not null && current.Width > 0 && current.Height > 0)
                    {
                        if (images.Update(current, retainedFrame, surface, frame))
                            Invalidate();
                        filters.Update(current.FilterPreview, pipeline, surface);
                        labels.Begin();
                        var paintStart = Stopwatch.GetTimestamp();
                        var mesh = painter.Build(current, white, images.Tiles, text => labels.Get(text, current.Dpi, pipeline, surface),
                            text => labels.TryGet(text, current.Dpi, pipeline, surface), images.Live, images.LiveBounds, filters, images.LiveOverview);
                        var submitStart = Stopwatch.GetTimestamp();
                        pipeline.Draw(mesh, surface, painter.RoiBatchBoundary, () => roiPipeline.Draw(current, surface));
                        profile.Record(current, cpuStart, paintStart, submitStart, Stopwatch.GetTimestamp(), allocationStart, mesh, roiPipeline, labels);
                        labels.End(surface);
                        if (labels.HasPending)
                            Invalidate();
                    }
                    var capture = TryTakeReadyCapture(submission, images, labels);
                    using var readback = capture is null ? null : new FrameReadback(gpu, surface);
                    gpuTimer.End(surface);
                    var cpuElapsed = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                    var presentStart = Stopwatch.GetTimestamp();
                    surface.Present();
                    presentedScene = current;
                    var measurement = metrics.Record(cpuElapsed, gpuElapsed, Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds, videoFrame?.Timestamp,
                        frames.Dropped, images.Bytes, videoFrame?.Sequence);
                    if (frame % MetricsNotificationFrameInterval == 0)
                        MetricsChanged?.Invoke(measurement);
                    foreach (var message in gpu.ReadDebugMessages())
                        StatusChanged?.Invoke("D3D12 " + message);
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
        catch (Exception ex) { StatusChanged?.Invoke($"렌더 오류: {ex.Message}"); }
    }

    private string? TryTakeReadyCapture(Submission? submission, GpuImageResources images, GpuLabelCache labels)
    {
        var request = Volatile.Read(ref captureRequest);
        var current = submission?.Scene;
        var overviewReady = current?.Image is not { } image
            || (current.IsLive ? images.LiveOverview is not null : images.Tiles.ContainsKey(new(image.Levels - 1, 0, 0)));

        // 이전 장면을 그리던 중 들어온 캡처 요청에 이전 화면을 저장하면 안 된다.
        if (request is null || (submission?.Version ?? 0) < request.Version || !overviewReady || labels.HasPending)
            return null;

        // 준비 중 새 캡처가 도착했다면 새 요청을 지우지 않고 다음 프레임에 처리한다.
        var claimedRequest = Interlocked.CompareExchange(ref captureRequest, null, request);
        return ReferenceEquals(claimedRequest, request) ? request.Path : null;
    }

    public void Dispose()
    {
        tiles.TileReady -= Invalidate;
        frames.Available -= Invalidate;
        stopping = true;
        wake.Set();
        if (thread.IsAlive)
            thread.Join();
        wake.Dispose();
    }
}
