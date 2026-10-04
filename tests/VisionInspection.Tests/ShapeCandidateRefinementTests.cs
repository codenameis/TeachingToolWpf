using System.Collections.Concurrent;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeCandidateRefinementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelOriginalRefinementPreservesExactMatchesOrderingAndBudgetWarnings(bool constrainBudget)
    {
        var (scene, model) = CreateScene();
        var loader = new ObservedLoader(scene);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var service = new ShapeMatchingService(loader, () => null);
        var options = new SearchOptions
        {
            MaximumMatches = 16,
            MaximumCandidates = constrainBudget ? 1 : 2048,
            ClutterWeight = 1
        };
        var serial = await service.SearchAsync(reference, false, model, null, options, default);
        var parallel = await service.SearchAsync(reference, false, model, null,
            options with
            {
                MaximumParallelism = 4
            }, default);

        Assert.Equal(serial.Matches.ToArray(), parallel.Matches.ToArray());
        Assert.Equal(serial.Windows, parallel.Windows);
        Assert.Equal(serial.Truncated, parallel.Truncated);
        Assert.Equal(serial.MatchLimitExceeded, parallel.MatchLimitExceeded);
        Assert.Equal(serial.Diagnostics.Select(BudgetState), parallel.Diagnostics.Select(BudgetState));
        Assert.Equal(constrainBudget, serial.Truncated);
        if (!constrainBudget)
        {
            Assert.Equal(4, serial.Matches.Length);
            Assert.All(serial.Matches, match => Assert.NotNull(match.Geometry));
        }
        Assert.Equal(2, loader.Sources.Count);
        Assert.All(loader.Sources, source =>
        {
            Assert.Equal(1, source.MaximumConcurrentReads);
            Assert.Single(source.ReadThreadIds);
            Assert.Equal(1, source.Disposals);
            Assert.False(source.DisposedDuringRead);
        });
    }

    [Fact]
    public async Task CropsExceedingCombinedPixelBudgetAreReadAndExecutedSeparately()
    {
        var patch = SyntheticScene.Create(160, 160, new MatchPose(80, 80, 0, 3));
        var model = ShapeTrainer.Train(patch, new(ReadOnlyMemory<byte>.Empty, new(80, 80)), new()
        {
            PyramidLevels = 1,
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            MinimumScale = 8,
            MaximumScale = 8,
            BuildGeometricModel = false
        });
        var scene = new GrayImage(new byte[4096 * 1600], 4096, 1600, 4096);
        var loader = new ObservedLoader(scene);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        using var reader = await MatchingImageReader.OpenAsync(reference, false, loader, () => null, default);
        using var provider = new ProbePreprocessor((image, call) =>
        {
            Assert.InRange(image.Width * image.Height, (ShapeCandidateRefinement.ParallelPixelBudget / 2) + 1,
                ShapeCandidateRefinement.ParallelPixelBudget);
            var keys = Assert.Single(loader.Sources).ReadKeys.ToArray();
            if (call == 1)
            {
                Assert.DoesNotContain(keys, key => key.X >= 4);
            }
            else
            {
                Assert.Contains(keys, key => key.X >= 4);
            }
        });
        var options = new SearchOptions
        {
            MaximumParallelism = 4,
            ComputeMode = InspectionComputeMode.GpuPreprocessing,
            Verify = false,
            Refine = false,
            EvaluateGeometry = false
        };
        // 후보 순서는 공간 순서의 반대다. 읽기는 공간 순서, 반환은 원래 후보 순서를 지킨다.
        ShapeMatch[] seeds = [Seed(new(2816, 800, 0, 8)), Seed(new(768, 800, 0, 8))];
        var result = ShapeCandidateRefinement.Run(reader, reference, model, null, options,
            seeds, default, new(provider));

        Assert.Equal(2, provider.Calls);
        Assert.Equal(seeds.Select(seed => seed.Pose), result.Results.Select(result => Assert.Single(result.Matches).Pose));
        Assert.Equal(1, Assert.Single(loader.Sources).MaximumConcurrentReads);
    }

    [Fact]
    public async Task CancellationWaitsForProviderExitBeforeInspectionDisposesResources()
    {
        var (scene, model) = CreateScene();
        var loader = new ObservedLoader(scene);
        using var provider = new BlockingPreprocessor();
        using var cancellation = new CancellationTokenSource();
        var service = new ShapeMatchingService(loader, () => null, () => provider);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var search = service.SearchAsync(reference, false, model, null,
            new()
            {
                MaximumParallelism = 4,
                ComputeMode = InspectionComputeMode.GpuPreprocessing
            }, cancellation.Token);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(search.IsCompleted);
            Assert.Equal(0, provider.Disposals);
            Assert.Equal(0, Assert.Single(loader.Sources).Disposals);
        }
        finally
        {
            provider.Release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.Equal(1, provider.Disposals);
        Assert.False(provider.DisposedDuringPrepare);
        Assert.Equal(1, Assert.Single(loader.Sources).Disposals);
    }

    [Fact]
    public async Task SerializedProviderDefersDisposalUntilActiveNativeWorkReturns()
    {
        using var provider = new BlockingPreprocessor();
        using var serialized = new SerializedImagePreprocessor(provider);
        using var cancellation = new CancellationTokenSource();
        var pixels = new GrayImage(new byte[64], 8, 8, 8);
        var prepare = Task.Run(() => serialized.Prepare(pixels, 8, cancellation.Token));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = Task.Run(() =>
        {
            disposing.SetResult();
            serialized.Dispose();
        });
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.Equal(0, provider.Disposals);
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            provider.Release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        await dispose;
        Assert.Equal(1, provider.Disposals);
        Assert.False(provider.DisposedDuringPrepare);
        Assert.Throws<ObjectDisposedException>(() => serialized.Prepare(pixels, 8));
    }

    private static object BudgetState(SearchDiagnostics diagnostics)
        => (diagnostics.Candidates, diagnostics.CandidateBudgetExceeded,
            diagnostics.VerificationBudgetExceeded, diagnostics.MatchLimitExceeded);

    private static ShapeMatch Seed(MatchPose pose)
        => new(pose, 1, 1, 1, 4, new(RefinementStatus.Disabled, pose, double.NaN, double.NaN, 0, 0, true));

    private static (GrayImage Scene, ShapeModel Model) CreateScene()
    {
        var patch = SyntheticScene.Create(160, 160, new MatchPose(80, 80, 0, 3));
        var model = ShapeTrainer.Train(patch, new(ReadOnlyMemory<byte>.Empty, new(80, 80)), new()
        {
            MinimumAngleDegrees = -5,
            MaximumAngleDegrees = 5,
            BuildGeometricModel = true,
            BuildClutterModel = true
        });
        const int Width = 2049;
        const int Height = 1153;
        var pixels = new byte[Width * Height];
        pixels.AsSpan().Fill(25);
        (int X, int Y)[] locations = [(240, 260), (1450, 300), (430, 840), (1640, 900)];
        foreach (var (x, y) in locations)
        {
            for (var row = 0; row < patch.Height; row++)
            {
                patch.Pixels.Span.Slice(row * patch.Stride, patch.Width)
                    .CopyTo(pixels.AsSpan(((y - 80 + row) * Width) + x - 80, patch.Width));
            }
        }
        Assert.True(model.Levels.Length >= 3);
        return (new(pixels, Width, Height, Width), model);
    }

    private sealed class ObservedLoader(GrayImage image) : IImageLoader
    {
        public ConcurrentBag<ObservedSource> Sources { get; } = [];

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var source = new ObservedSource(image);
            Sources.Add(source);
            return Task.FromResult<ITileSource>(source);
        }
    }

    private sealed class ObservedSource(GrayImage image) : ITileSource
    {
        private int _activeReads;
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);
        public ConcurrentDictionary<int, byte> ReadThreadIds { get; } = new();
        public ConcurrentQueue<TileKey> ReadKeys { get; } = new();
        public int MaximumConcurrentReads;
        public int Disposals;
        public bool DisposedDuringRead;

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeReads);
            Interlocked.Exchange(ref MaximumConcurrentReads, Math.Max(active, MaximumConcurrentReads));
            try
            {
                Assert.Equal(1, active);
                Assert.Equal(0, Disposals);
                ReadThreadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                ReadKeys.Enqueue(key);
                var left = key.X * 512;
                var top = key.Y * 512;
                var pixels = new PixelBuffer(Math.Min(512, image.Width - left), Math.Min(512, image.Height - top), PixelKind.Gray8);
                for (var y = 0; y < pixels.Height; y++)
                {
                    image.Pixels.Span.Slice(((top + y) * image.Stride) + left, pixels.Width)
                        .CopyTo(pixels.Span.Slice(y * pixels.Stride, pixels.Width));
                }
                return pixels;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void Dispose()
        {
            DisposedDuringRead |= Volatile.Read(ref _activeReads) != 0;
            Interlocked.Increment(ref Disposals);
        }
    }

    private sealed class ProbePreprocessor(Action<GrayImage, int> observe) : IImagePreprocessor
    {
        public int Calls;
        public ImagePreprocessingDiagnostics Diagnostics => new("probe", "test", Calls, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            observe(image, Interlocked.Increment(ref Calls));
            var length = image.Width * image.Height;
            return new(new float[length], new float[length], new float[length], new byte[length]);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingPreprocessor : IImagePreprocessor
    {
        private int _active;
        private int _disposed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Disposals;
        public bool DisposedDuringPrepare;
        public ImagePreprocessingDiagnostics Diagnostics => new("blocking", "test", 0, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("검사 취소 수명 테스트가 공급자 해제를 기다리는 동안 끝나지 않았습니다.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var length = image.Width * image.Height;
                return new(new float[length], new float[length], new float[length], new byte[length]);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            DisposedDuringPrepare |= Volatile.Read(ref _active) != 0;
            Interlocked.Increment(ref Disposals);
            Release.Dispose();
        }
    }
}
