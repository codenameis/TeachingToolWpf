using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>4개 렌더 워커가 공유하는 스레드 수명, 최신 장면 병합, 크기 변경 및 캡처 요청 관리 기반 클래스.</summary>
public abstract class RenderWorkerBase : IRenderWorker
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private readonly TileWorker _tiles;
    private readonly LatestFrameMailbox _frames;
    private RenderWorkerLoop.Submission? _latestSubmission;
    private RenderWorkerLoop.CaptureRequest? _captureRequest;
    private long _sceneVersion;
    private volatile bool _stopping;
    private int _disposed;
    private int _width = 1;
    private int _height = 1;

    public event Action<string>? StatusChanged;
    public event Action<RenderMetrics>? MetricsChanged;
    public event Action<OverlayPresentation>? OverlayPresented;

    protected RenderWorkerBase(TileWorker tiles, LatestFrameMailbox frames, string threadName, bool useStaApartment)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);

        _tiles = tiles;
        _frames = frames;
        tiles.TileReady += Invalidate;
        frames.Available += Invalidate;
        _thread = new Thread(RunCore) { IsBackground = true, Name = threadName };
        if (useStaApartment)
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }
    }

    internal TileWorker Tiles => _tiles;

    internal LatestFrameMailbox Frames => _frames;

    protected WaitHandle StopHandle => _stop;

    protected internal bool IsStopping => _stopping;

    protected internal int TargetWidth => Volatile.Read(ref _width);

    protected internal int TargetHeight => Volatile.Read(ref _height);

    internal RenderWorkerLoop.Submission? LatestSubmission => Volatile.Read(ref _latestSubmission);

    internal RenderWorkerLoop.CaptureRequest? PendingCapture => Volatile.Read(ref _captureRequest);

    internal Action<RenderMetrics>? MetricsChangedHandler => MetricsChanged;

    public void Start() => _thread.Start();

    public void Resize(int width, int height)
    {
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        Invalidate();
    }

    public void Submit(RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var submission = new RenderWorkerLoop.Submission(Interlocked.Increment(ref _sceneVersion), scene, Stopwatch.GetTimestamp());
        Volatile.Write(ref _latestSubmission, submission);
        Invalidate();
    }

    public void Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var minimumSceneVersion = Volatile.Read(ref _latestSubmission)?.Version ?? 0;
        Interlocked.Exchange(ref _captureRequest, new(minimumSceneVersion, path));
        Invalidate();
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
            // 구독 해제 직전 생산자가 가져간 콜백은 종료 이후 도착할 수 있다.
        }
    }

    internal bool WaitForWake()
    {
        _wake.WaitOne();
        return !_stopping;
    }

    internal string? TryTakeCapture(
        RenderWorkerLoop.Submission? submission,
        GpuImageResources images,
        GpuLabelCache labels,
        bool hasPendingDetails,
        bool requireVisibleTiles)
        => RenderWorkerLoop.TryTakeCapture(
            ref _captureRequest,
            submission,
            images,
            labels,
            hasPendingDetails,
            requireVisibleTiles);

    protected void RaiseStatusChanged(string status) => StatusChanged?.Invoke(status);

    protected void RaiseMetricsChanged(RenderMetrics metrics) => MetricsChanged?.Invoke(metrics);

    internal void NotifyOverlayPresented(OverlayPresentation presentation) => OverlayPresented?.Invoke(presentation);

    protected abstract void RunCore();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tiles.TileReady -= Invalidate;
        _frames.Available -= Invalidate;
        _stopping = true;
        _stop.Set();
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _wake.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
