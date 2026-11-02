using System.Diagnostics;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

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

    private protected RenderWorkerBase(TileWorker tiles, LatestFrameMailbox frames, string threadName, bool useStaApartment)
    {
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

    private protected TileWorker Tiles => _tiles;

    private protected LatestFrameMailbox Frames => _frames;

    private protected WaitHandle StopHandle => _stop;

    private protected bool IsStopping => _stopping;

    private protected int TargetWidth => Volatile.Read(ref _width);

    private protected int TargetHeight => Volatile.Read(ref _height);

    private protected RenderWorkerLoop.Submission? LatestSubmission => Volatile.Read(ref _latestSubmission);

    private protected RenderWorkerLoop.CaptureRequest? PendingCapture => Volatile.Read(ref _captureRequest);

    public void Start() => _thread.Start();

    public void Resize(int width, int height)
    {
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        Invalidate();
    }

    public void Submit(RenderScene scene)
    {
        var submission = new RenderWorkerLoop.Submission(Interlocked.Increment(ref _sceneVersion), scene, Stopwatch.GetTimestamp());
        Volatile.Write(ref _latestSubmission, submission);
        Invalidate();
    }

    public void Capture(string path)
    {
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

    private protected bool WaitForWake()
    {
        _wake.WaitOne();
        return !_stopping;
    }

    private protected string? TryTakeCapture(
        RenderWorkerLoop.Submission? submission,
        GpuImageResources images,
        GpuLabelCache labels,
        bool hasPendingDetails,
        bool requireVisibleTiles)
    {
        return RenderWorkerLoop.TryTakeCapture(
            ref _captureRequest,
            submission,
            images,
            labels,
            hasPendingDetails,
            requireVisibleTiles);
    }

    private protected void RaiseStatusChanged(string status) => StatusChanged?.Invoke(status);

    private protected void RaiseMetricsChanged(RenderMetrics metrics) => MetricsChanged?.Invoke(metrics);

    private protected void RaiseOverlayPresented(OverlayPresentation presentation) => OverlayPresented?.Invoke(presentation);

    private protected Action<RenderMetrics>? MetricsChangedHandler => MetricsChanged;

    private protected void RunTwoPhaseFrameLoop<TSurface, TFrameResources, TReadback>(
        long tileBudgetBytes,
        ITexturePipeline sprites,
        ITextRasterizer rasterizer,
        IFramePreparer preparer,
        TSurface surface,
        TFrameResources[] resources,
        Func<bool> hasPendingRoiDetails,
        Func<TSurface, int, int, bool> tryBeginSurface,
        Action<TFrameResources> resetResources,
        Action<TSurface> beginDrawing,
        Action<SpriteMesh, TSurface, int, Action> drawSprites,
        Action<RenderScene, TSurface, TFrameResources> drawMasksAndRois,
        Action<TSurface> endDrawing,
        Func<TSurface, TReadback> createReadback,
        Action<TReadback, string> saveReadback,
        Func<TSurface, ulong> presentSurface,
        Action<ulong> waitCompletion)
        where TSurface : class, IRenderSurface
        where TReadback : class, IDisposable
    {
        using var labels = new GpuLabelCache(rasterizer);
        using var resultLabels = new GpuResultLabels(rasterizer);
        using var images = new GpuImageResources(tileBudgetBytes, sprites, Tiles, preparer);
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
            while (WaitForWake())
            {
                var width = TargetWidth;
                var height = TargetHeight;
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                var submission = LatestSubmission;
                if (RenderWorkerLoop.IsNothingChanged(submission?.Scene, presented, width, height,
                    surface.Width, surface.Height, Frames, Tiles, labels, hasPendingRoiDetails(), PendingCapture, white))
                {
                    continue;
                }

                var cpuStart = Stopwatch.GetTimestamp();
                if (!tryBeginSurface(surface, width, height))
                {
                    Invalidate();
                    continue;
                }

                var resourcesForFrame = resources[surface.FrameIndex];
                resetResources(resourcesForFrame);
                submission = LatestSubmission;
                var scene = submission?.Scene;
                var whiteTexture = RenderWorkerLoop.EnsureWhiteTexture(ref white, sprites, surface);
                var next = Frames.Take();
                retained = RenderWorkerLoop.UpdateRetainedFrame(scene?.IsLive == true, retained, next, Frames);
                SpriteMesh? mesh = null;
                frameNumber++;
                if (scene is { Width: > 0, Height: > 0 })
                {
                    mesh = RenderWorkerLoop.BuildSceneMesh(
                        scene,
                        retained,
                        surface,
                        sprites,
                        painter,
                        images,
                        filters,
                        labels,
                        resultLabels,
                        whiteTexture,
                        frameNumber,
                        Invalidate);
                    RenderWorkerLoop.FinishSceneLabels(labels, surface, hasPendingDetails: false, Invalidate);
                }

                beginDrawing(surface);
                if (mesh is not null)
                {
                    drawSprites(mesh, surface, painter.RoiBatchBoundary, () => drawMasksAndRois(scene!, surface, resourcesForFrame));
                }

                endDrawing(surface);
                if (hasPendingRoiDetails())
                {
                    Invalidate();
                }

                var capture = TryTakeCapture(submission, images, labels, hasPendingRoiDetails(), requireVisibleTiles: true);
                TReadback? ownedReadback = null;
                TReadback? readback;
                try
                {
                    ownedReadback = capture is null ? null : createReadback(surface);
                    readback = ownedReadback;
                    if (ownedReadback is not null)
                    {
                        surface.Retire(ownedReadback);
                        ownedReadback = null;
                    }
                }
                finally
                {
                    ownedReadback?.Dispose();
                }

                var cpuMilliseconds = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                var presentStart = Stopwatch.GetTimestamp();
                var completion = presentSurface(surface);
                presented = scene;
                var notifyOverlay = RenderWorkerLoop.ShouldNotifyOverlayPresented(scene, overlayId, labels, hasPendingRoiDetails(), out overlayId);
                if (readback is not null || notifyOverlay)
                {
                    waitCompletion(completion);
                    if (notifyOverlay)
                    {
                        RaiseOverlayPresented(new(overlayId, Stopwatch.GetTimestamp()));
                    }

                    if (readback is not null)
                    {
                        saveReadback(readback, capture!);
                    }
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

    private protected abstract void RunCore();

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
    }
}
