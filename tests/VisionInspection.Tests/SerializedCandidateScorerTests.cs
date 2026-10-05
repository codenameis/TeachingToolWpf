using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class SerializedCandidateScorerTests
{
    [Fact]
    public async Task CandidateCancellationAndDisposalWaitForActiveProviderCall()
    {
        var inner = new BlockingScorer();
        using var wrapper = new SerializedImagePreprocessor(inner);
        using var cancellation = new CancellationTokenSource();
        var request = new ShapeCandidateScoreRequest(new byte[64], 8, 8, 0, 1, [[new(0, 0, 0)]]);
        Assert.Same(wrapper, wrapper.CandidateScorer);
        var scoring = Task.Run(() => wrapper.Score(request, cancellation.Token));
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = Task.Run(() =>
        {
            disposingStarted.SetResult();
            wrapper.Dispose();
        });
        try
        {
            await disposingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.Equal(0, inner.Disposals);
            Assert.False(scoring.IsCompleted);
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            inner.Release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scoring);
        await disposing;
        Assert.Equal(1, inner.Disposals);
        Assert.False(inner.DisposedDuringScore);
        Assert.Throws<ObjectDisposedException>(() => wrapper.Score(request));
    }

    private sealed class BlockingScorer : IImagePreprocessor, IShapeCandidateScorer
    {
        private int _active;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Disposals;
        public bool DisposedDuringScore;
        public ImagePreprocessingDiagnostics Diagnostics => new("test", "test", 0, 0, 0);
        public CandidateScoringDiagnostics CandidateDiagnostics => new("test", "test", 1, 64, 1, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test must use candidate scoring.");

        public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try
            {
                Entered.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Candidate scoring test did not release the provider.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(8, 8, 1, new ushort[64]);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void Dispose()
        {
            DisposedDuringScore |= Volatile.Read(ref _active) > 0;
            Interlocked.Increment(ref Disposals);
            Release.Dispose();
        }
    }
}
