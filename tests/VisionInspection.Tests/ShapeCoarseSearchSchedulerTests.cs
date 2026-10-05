using System.Collections.Concurrent;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeCoarseSearchSchedulerTests
{
    [Fact]
    public async Task NextWindowStartsBeforeSlowEarlierWindowFinishesAndPublishesInOrder()
    {
        var loader = new MarkerLoader(256, 32, 32);
        using var reader = await Open(loader);
        using var release = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ProbeProvider((marker, _) =>
        {
            if (marker == 1)
            {
                firstEntered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            if (marker == 3)
            {
                thirdEntered.TrySetResult();
            }
        });
        var published = new ConcurrentQueue<int>();
        var run = Task.Run(() => ShapeCoarseSearchScheduler.Run(Requests(6, 32, 32, 32), reader, 1, Model(),
            Options(2), new(provider), default, completed => published.Enqueue(completed.Index)));
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(published);
        }
        finally
        {
            release.Set();
        }
        await run;
        Assert.Equal(Enumerable.Range(0, 6), published);
        Assert.InRange(provider.MaximumConcurrent, 2, 2);
        Assert.Single(loader.Source.Threads);
    }

    [Fact]
    public async Task OriginalReadWaitsUntilLargeActiveCropReleasesPixelBudget()
    {
        var loader = new MarkerLoader(4096, 1024, 2048);
        using var reader = await Open(loader);
        var firstExited = false;
        loader.Source.ObserveRead = key =>
        {
            if (key.X >= 4)
            {
                Assert.True(Volatile.Read(ref firstExited));
            }
        };
        using var provider = new ProbeProvider((_, pixels) =>
        {
            Assert.True(pixels > PreparedSearchImage.MaximumPixels / 2);
        });
        var published = new List<int>();
        ShapeCoarseSearchScheduler.Run(Requests(2, 1088, 1024, 2048), reader, 1, Model(), Options(4),
            new(provider), default, completed =>
            {
                published.Add(completed.Index);
                if (completed.Index == 0)
                {
                    Volatile.Write(ref firstExited, true);
                }
            });
        Assert.Equal([0, 1], published);
        Assert.Equal(1, provider.MaximumConcurrent);
        Assert.InRange(provider.MaximumPixels, 1, PreparedSearchImage.MaximumPixels);
        Assert.Single(loader.Source.Threads);
    }

    [Fact]
    public async Task WorkerFailureDrainsOtherActiveWindowsBeforeReturning()
    {
        var loader = new MarkerLoader(128, 32, 32);
        using var reader = await Open(loader);
        using var release = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ProbeProvider((marker, _) =>
        {
            if (marker == 1)
            {
                firstEntered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                firstEntered.Task.GetAwaiter().GetResult();
                failed.TrySetResult();
                throw new InvalidOperationException("Injected window failure.");
            }
        });
        var run = Task.Run(() => ShapeCoarseSearchScheduler.Run(Requests(2, 32, 32, 32), reader, 1, Model(),
            Options(2), new(provider), default, _ => { }));
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            Assert.InRange(provider.Active, 1, 2);
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal(0, provider.Active);
    }

    private static ShapeModel Model()
        => ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                PyramidLevels = 1,
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0,
                BuildGeometricModel = false
            });

    private static SearchOptions Options(int parallelism)
        => new()
        {
            MaximumParallelism = parallelism,
            ComputeMode = InspectionComputeMode.GpuPreprocessing
        };

    private static ShapeCoarseSearchScheduler.Request[] Requests(int count, int width, int height, int step)
        => Enumerable.Range(0, count).Select(index => new ShapeCoarseSearchScheduler.Request(index * step, 0, width, height,
            new SearchRegion([new(0, 0), new(width, 0), new(width, height), new(0, height)]))).ToArray();

    private static Task<MatchingImageReader> Open(MarkerLoader loader)
        => MatchingImageReader.OpenAsync(new("fixture", "fixture", loader.Width, loader.Height), false, loader, () => null, default);

    private sealed class MarkerLoader(int width, int height, int markerStep) : IImageLoader
    {
        public int Width => width;
        public int Height => height;
        public MarkerSource Source { get; } = new(width, height, markerStep);

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(Source);
    }

    private sealed class MarkerSource(int width, int height, int markerStep) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", width, height, PixelKind.Gray8, 1);
        public HashSet<int> Threads { get; } = [];
        public Action<TileKey>? ObserveRead
        {
            get; set;
        }

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            ObserveRead?.Invoke(key);
            var left = key.X * 512;
            var top = key.Y * 512;
            var pixels = new PixelBuffer(Math.Min(512, width - left), Math.Min(512, height - top), PixelKind.Gray8);
            for (var y = 0; y < pixels.Height; y++)
            {
                for (var x = 0; x < pixels.Width; x++)
                {
                    pixels.Span[(y * pixels.Stride) + x] = (byte)(1 + ((left + x) / markerStep));
                }
            }
            return pixels;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ProbeProvider(Action<byte, int> observe) : IImagePreprocessor
    {
        private readonly object _gate = new();
        private int _active;
        private int _pixels;
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent;
        public int MaximumPixels;
        public ImagePreprocessingDiagnostics Diagnostics => new("probe", "probe", 0, 0, 0);

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            var count = image.Width * image.Height;
            lock (_gate)
            {
                _active++;
                _pixels += count;
                MaximumConcurrent = Math.Max(MaximumConcurrent, _active);
                MaximumPixels = Math.Max(MaximumPixels, _pixels);
            }
            try
            {
                observe(image.Pixels.Span[0], count);
                cancellationToken.ThrowIfCancellationRequested();
                return new(new float[count], new float[count], new float[count], new byte[count]);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _pixels -= count;
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
