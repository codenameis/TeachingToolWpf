using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCompute.ILGPU;

namespace VisionCompute.Tests;

public sealed class CudaInspectionSessionTests
{
    [Fact]
    public async Task PreparationIsSharedAndEveryLeaseReportsOnlyItsOwnWork()
    {
        var backend = new RecordingBackend();
        var creations = 0;
        using var session = new CudaInspectionSession(() =>
        {
            Interlocked.Increment(ref creations);
            return backend;
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => session.PrepareAsync()));
        Assert.True(session.IsReady);
        Assert.Equal(1, creations);
        Assert.True(session.InitializationMilliseconds >= 0);
        for (var run = 0; run < 3; run++)
        {
            using var lease = session.Rent();
            var scorer = Assert.IsAssignableFrom<IShapeCandidateScorer>(lease);
            Assert.Equal(0, lease.Diagnostics.Calls);
            Assert.Equal(0, scorer.CandidateDiagnostics.Calls);
            lease.Prepare(Image(), 3);
            scorer.Score(Request());
            Assert.Equal(1, lease.Diagnostics.Calls);
            Assert.Equal(64, lease.Diagnostics.ProcessedPixels);
            Assert.Equal(2.5, lease.Diagnostics.TotalMilliseconds);
            Assert.Equal(1, scorer.CandidateDiagnostics.Calls);
            Assert.Equal(64, scorer.CandidateDiagnostics.ProcessedPixels);
            Assert.Equal(1, scorer.CandidateDiagnostics.TemplateCount);
            Assert.Equal(1.25, scorer.CandidateDiagnostics.TotalMilliseconds);
        }
        Assert.Equal(0, backend.Disposals);
        session.Dispose();
        Assert.Equal(1, backend.Disposals);
    }

    [Fact]
    public void SingleActiveLeaseAndReturnedLeaseAreEnforced()
    {
        var backend = new RecordingBackend();
        using var session = new CudaInspectionSession(() => backend);
        var first = session.Rent();
        Assert.Throws<InvalidOperationException>(() => session.Rent());
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Prepare(Image(), 3));
        Assert.Throws<ObjectDisposedException>(() => ((IShapeCandidateScorer)first).Score(Request()));
        using var next = session.Rent();
        next.Prepare(Image(), 3);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public async Task OwnerDisposalWaitsForTheActiveLeaseWithoutReleasingItsDevice()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend
        {
            OnPrepare = () =>
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var session = new CudaInspectionSession(() => backend);
        var lease = session.Rent();
        var work = Task.Run(() => lease.Prepare(Image(), 3));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        session.Dispose();
        Assert.False(session.IsReady);
        Assert.Equal(0, backend.Disposals);
        Assert.Throws<ObjectDisposedException>(() => session.Rent());
        release.Set();
        await work;
        Assert.Equal(0, backend.Disposals);
        lease.Dispose();
        Assert.Equal(1, backend.Disposals);
        session.Dispose();
    }

    [Fact]
    public async Task OwnerDisposalDuringInitializationReleasesTheCreatedDevice()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend();
        var session = new CudaInspectionSession(() =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return backend;
        });
        var preparation = session.PrepareAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        session.Dispose();
        Assert.Equal(0, backend.Disposals);
        release.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => preparation);
        Assert.Equal(1, backend.Disposals);
        Assert.False(session.IsReady);
    }

    [Fact]
    public async Task CancelingTheWaitKeepsInitializationOwnedAndReusable()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var backend = new RecordingBackend();
        using var session = new CudaInspectionSession(() =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return backend;
        });
        var preparation = session.PrepareAsync(cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.False(session.IsReady);
        release.Set();
        await session.PrepareAsync();
        Assert.True(session.IsReady);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public void PreCanceledPreparationDoesNotCreateADevice()
    {
        var creations = 0;
        using var session = new CudaInspectionSession(() =>
        {
            creations++;
            return new RecordingBackend();
        });
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = session.PrepareAsync(new(true));
        });
        Assert.Equal(0, creations);
        Assert.False(session.IsReady);
    }

    [Fact]
    public void CanceledInspectionDoesNotPoisonTheSession()
    {
        var backend = new RecordingBackend();
        using var session = new CudaInspectionSession(() => backend);
        using (var first = session.Rent())
        {
            Assert.Throws<OperationCanceledException>(() => first.Prepare(Image(), 3, new(true)));
        }
        Assert.True(session.IsReady);
        using var second = session.Rent();
        second.Prepare(Image(), 3);
        Assert.Equal(1, second.Diagnostics.Calls);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public void DeviceFailureRejectsReuseAndReleasesTheDeviceOnLeaseReturn()
    {
        var backend = new RecordingBackend { OnPrepare = () => throw new InvalidOperationException("device failure") };
        using var session = new CudaInspectionSession(() => backend);
        var lease = session.Rent();
        Assert.Throws<InvalidOperationException>(() => lease.Prepare(Image(), 3));
        Assert.False(session.IsReady);
        Assert.Throws<InvalidOperationException>(() => session.Rent());
        Assert.Equal(0, backend.Disposals);
        backend.OnPrepare = null;
        Assert.Throws<InvalidOperationException>(() => lease.Prepare(Image(), 3));
        lease.Dispose();
        Assert.Equal(1, backend.Disposals);
        Assert.Throws<InvalidOperationException>(() => session.Rent());
    }

    [Fact]
    public async Task InitializationFailureIsReportedAndDoesNotRetryImplicitly()
    {
        var creations = 0;
        using var session = new CudaInspectionSession(() =>
        {
            creations++;
            throw new NotSupportedException("CUDA unavailable");
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => session.PrepareAsync());
        Assert.Throws<InvalidOperationException>(() => session.Rent());
        Assert.Equal(1, creations);
        Assert.False(session.IsReady);
    }

    [Fact]
    public void DeferredCleanupFailureIsPropagatedByLeaseReturn()
    {
        var backend = new RecordingBackend { OnDispose = () => throw new IOException("cleanup failure") };
        var session = new CudaInspectionSession(() => backend);
        var lease = session.Rent();
        session.Dispose();
        Assert.Throws<IOException>(lease.Dispose);
        lease.Dispose();
        session.Dispose();
        Assert.Equal(1, backend.Disposals);
    }

    private static GrayImage Image() => new(new byte[64], 8, 8, 8);

    private static ShapeCandidateScoreRequest Request() => new(new byte[64], 8, 8, 2, 4, [[new(0, 0, 0)]]);

    private sealed class RecordingBackend : IImagePreprocessor, IShapeCandidateScorer
    {
        public Action? OnPrepare
        {
            get; set;
        }
        public Action? OnDispose
        {
            get; init;
        }
        public int Disposals
        {
            get; private set;
        }
        public ImagePreprocessingDiagnostics Diagnostics { get; private set; } = new("recording", "test", 0, 0, 0);
        public CandidateScoringDiagnostics CandidateDiagnostics { get; private set; } = new("recording", "test", 0, 0, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnPrepare?.Invoke();
            var count = image.Width * image.Height;
            Diagnostics = Diagnostics with
            {
                Calls = Diagnostics.Calls + 1,
                ProcessedPixels = Diagnostics.ProcessedPixels + count,
                TotalMilliseconds = Diagnostics.TotalMilliseconds + 2.5
            };
            return new(new float[count], new float[count], new float[count], new byte[count]);
        }

        public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateDiagnostics = CandidateDiagnostics with
            {
                Calls = CandidateDiagnostics.Calls + 1,
                ProcessedPixels = CandidateDiagnostics.ProcessedPixels + (request.Width * request.Height),
                TemplateCount = CandidateDiagnostics.TemplateCount + request.Templates.Length,
                TotalMilliseconds = CandidateDiagnostics.TotalMilliseconds + 1.25
            };
            return new(request.GridWidth, request.GridHeight, request.Templates.Length,
                new ushort[request.GridWidth * request.GridHeight * request.Templates.Length]);
        }

        public void Dispose()
        {
            Disposals++;
            OnDispose?.Invoke();
        }
    }
}
