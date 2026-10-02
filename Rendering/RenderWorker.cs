using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using System.Diagnostics;
using TeachingToolWpf.Diagnostics;

namespace TeachingToolWpf.Rendering;

/// <summary>Single owner of GPU objects. UI requests are coalesced, never queued without bounds.</summary>
public sealed class RenderWorker : IDisposable
{
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private readonly nint hwnd;
    private readonly TileWorker tiles;
    private readonly ITextRasterizer textRasterizer;
    private readonly LatestFrameMailbox frames;
    private readonly IFramePreparer framePreparer;
    private sealed record Submission(long Version, RenderScene Scene);
    private sealed record CaptureRequest(long Version, string Path);
    private Submission? scene;
    private CaptureRequest? captureRequest;
    private long sceneVersion;
    private volatile bool stopping;
    private int width = 1, height = 1;
    public event Action<string>? StatusChanged;
    public event Action<RenderMetrics>? MetricsChanged;

    public RenderWorker(nint hwnd, TileWorker tiles, ITextRasterizer textRasterizer, LatestFrameMailbox frames, IFramePreparer framePreparer)
    {
        this.hwnd = hwnd; this.tiles = tiles; this.textRasterizer = textRasterizer; this.frames = frames;
        this.framePreparer = framePreparer;
        tiles.TileReady += Invalidate;
        frames.Available += Invalidate;
        thread = new Thread(Run) { IsBackground = true, Name = "DX12 renderer" };
        thread.SetApartmentState(ApartmentState.STA);
    }
    public void Start() => thread.Start();
    public void Resize(int width, int height)
    {
        Volatile.Write(ref this.width, width); Volatile.Write(ref this.height, height); wake.Set();
    }
    public void Invalidate()
    {
        if (stopping) return;
        try { wake.Set(); }
        catch (ObjectDisposedException) when (stopping) { /* A producer captured this callback just before unsubscription. */ }
    }
    public void Submit(RenderScene value) { Volatile.Write(ref scene, new(Interlocked.Increment(ref sceneVersion), value)); Invalidate(); }
    public void Capture(string path) { Interlocked.Exchange(ref captureRequest, new(Volatile.Read(ref scene)?.Version ?? 0, path)); Invalidate(); }
    private void Run()
    {
        try
        {
            using var gpu = new Dx12Device(Environment.GetCommandLineArgs().Contains("--gpu-debug"));
            using var surface = new Dx12Surface(gpu, hwnd, width, height);
            using var pipeline = new SpritePipeline(gpu);
            using var gpuTimer = new GpuTimer(gpu);
            using var metrics = new FrameMetrics();
            var painter = new ScenePainter();
            using var labels = new GpuLabelCache(textRasterizer);
            using var images = new GpuImageResources(gpu, pipeline, tiles, framePreparer);
            using var filters = new GpuFilterPreview();
            GpuTexture? white = null;
            VideoFrame? retainedFrame = null;
            long frame = 0;
            RenderScene? presentedScene = null;
            StatusChanged?.Invoke($"DX12 · {gpu.AdapterName} · VRAM {gpu.DedicatedMemoryBytes / 1073741824d:F1} GiB · Budget {gpu.LocalMemoryBudget / 1073741824d:F1} GiB · Debug: {gpu.DebugEnabled}");
            try
            {
                while (!stopping)
                {
                    wake.WaitOne();
                    if (stopping) break;
                    var w = Volatile.Read(ref width); var h = Volatile.Read(ref height);
                    if (w <= 0 || h <= 0) continue;
                    var submission = Volatile.Read(ref scene);
                    var current = submission?.Scene;
                    if (ReferenceEquals(current, presentedScene) && w == surface.Width && h == surface.Height &&
                        !frames.HasPending && !tiles.HasReady && Volatile.Read(ref captureRequest) is null && white is not null) continue;
                    var cpuStart = Stopwatch.GetTimestamp();
                    surface.Resize(w, h); surface.Begin(); frame++;
                    var gpuElapsed = gpuTimer.Begin(surface);
                    pipeline.BeginFrame(surface);
                    if (white is null)
                    {
                        using var pixel = new PixelBuffer(1, 1, PixelKind.Rgba32); pixel.Span.Fill(255);
                        white = pipeline.Upload(pixel, surface);
                    }
                    var videoFrame = frames.Take();
                    if (videoFrame is not null) { retainedFrame?.Dispose(); retainedFrame = videoFrame; }
                    if (current?.IsLive == true && retainedFrame is null) retainedFrame = frames.RetainLatest();
                    if (current?.IsLive != true) { retainedFrame?.Dispose(); retainedFrame = null; }
                    if (current is not null && current.Width > 0 && current.Height > 0)
                    {
                        if (images.Update(current, retainedFrame, surface, frame)) Invalidate();
                        filters.Update(current.FilterPreview, pipeline, surface);
                        labels.Begin();
                        pipeline.Draw(painter.Build(current, white, images.Tiles, text => labels.Get(text, current.Dpi, pipeline, surface), images.Live, images.LiveBounds, filters, images.LiveOverview), surface);
                        labels.End(surface);
                    }
                    // A request arriving while an older scene is being painted must not capture that old frame.
                    var request = Volatile.Read(ref captureRequest);
                    var overviewReady = current?.Image is not { } image || (current.IsLive ? images.LiveOverview is not null : images.Tiles.ContainsKey(new(image.Levels - 1, 0, 0)));
                    var capture = request is not null && (submission?.Version ?? 0) >= request.Version && overviewReady &&
                        ReferenceEquals(Interlocked.CompareExchange(ref captureRequest, null, request), request) ? request.Path : null;
                    using var readback = capture is null ? null : new FrameReadback(gpu, surface);
                    gpuTimer.End(surface);
                    var cpuElapsed = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                    var presentStart = Stopwatch.GetTimestamp();
                    surface.Present();
                    presentedScene = current;
                    var measurement = metrics.Record(cpuElapsed, gpuElapsed, Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds, videoFrame?.Timestamp,
                        frames.Dropped, images.Bytes, videoFrame?.Sequence);
                    if (frame % 30 == 0) MetricsChanged?.Invoke(measurement);
                    foreach (var message in gpu.ReadDebugMessages()) StatusChanged?.Invoke("D3D12 " + message);
                    if (readback is not null) { gpu.Flush(); readback.Save(capture!); }
                }
            }
            finally
            {
                try { gpu.Flush(); }
                finally { white?.Dispose(); retainedFrame?.Dispose(); }
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke($"렌더 오류: {ex.Message}"); }
    }
    public void Dispose()
    {
        tiles.TileReady -= Invalidate;
        frames.Available -= Invalidate;
        stopping = true; wake.Set();
        if (thread.IsAlive) thread.Join();
        wake.Dispose();
    }
}
