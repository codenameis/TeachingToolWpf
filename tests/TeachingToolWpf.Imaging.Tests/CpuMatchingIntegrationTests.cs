using CpuMatching.Core;
using CpuMatching.Cuda;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;
using SearchOptions = CpuMatching.Core.SearchOptions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class CpuMatchingIntegrationTests
{
    [Fact]
    public async Task AlgorithmParametersStaySeparateAndRequireRetraining()
    {
        var service = new CpuTemplateMatchingService(new Loader(), () => null);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 192), ViewportTransform.Identity);
        editor.Add(new(new(48.5, 48.5), 33, 29, 0));
        using var vm = new ShapeMatchingViewModel(null!, cpuService: service) { Method = PatternMatchingMethod.CpuLine2D };
        vm.Update(editor.State, 1, false, true);
        await vm.Train.ExecuteAsync();
        vm.CpuCoarseFeatures = 80;
        Assert.True(vm.IsTrainingDirty);
        Assert.False(vm.Search.CanExecute(null));
        await vm.Train.ExecuteAsync();
        Assert.False(vm.IsTrainingDirty);
        await vm.Search.ExecuteAsync();
        Assert.Equal(80, vm.CpuInspectionLogs[^1].Parameters.Line2D.CoarseFeatures);
        vm.Method = PatternMatchingMethod.CpuDirectionalChamfer;
        Assert.Equal(64, vm.CpuCoarseFeatures);
        vm.CpuChamferFineTolerance = 1.2;
        await vm.Train.ExecuteAsync();
        await vm.Search.ExecuteAsync();
        Assert.Equal(1.2, vm.CpuInspectionLogs[^1].Parameters.Chamfer.FineTolerance);
        vm.CpuChamferDirectionPenalty = double.NaN;
        Assert.NotEmpty(vm.OptionsError);
        Assert.False(vm.Train.CanExecute(null));
        vm.Method = PatternMatchingMethod.CpuSubNcc;
        vm.CpuNccCoarseSamples = 97;
        Assert.NotEmpty(vm.OptionsError);
        vm.CpuNccCoarseSamples = 128;
        Assert.Empty(vm.OptionsError);
        await vm.Train.ExecuteAsync();
        await vm.Search.ExecuteAsync();
        Assert.Equal(128, vm.CpuInspectionLogs[^1].Parameters.SubNcc.CoarseSamples);
        vm.Method = PatternMatchingMethod.CpuLine2D;
        Assert.Equal(80, vm.CpuCoarseFeatures);
        Assert.False(vm.IsTrainingDirty);
        Assert.True(vm.Search.CanExecute(null));
    }

    [Fact]
    public async Task Full8KReadsEveryOriginalTileAndKeepsFullCoordinates()
    {
        var loader = new FullImageLoader();
        var service = new CpuTemplateMatchingService(loader, () => null);
        var matcher = new FullImageMatcher();
        var template = new CpuTemplate(CpuTemplateMethod.SubNcc, matcher, new(new(20, 20), 20, 20, 0));
        var result = await service.SearchAsync(new("full", "full", 8192, 8192), false, template, null,
            new SearchOptions(), default);
        Assert.Equal(256, loader.Tiles.Count);
        Assert.Contains(new TileKey(0, 15, 15), loader.Tiles);
        Assert.Equal(8000.5, Assert.Single(result.Result.Matches).X);
        Assert.True(loader.Disposed);
    }

    private sealed class FullImageMatcher : ITemplateMatcher
    {
        public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
            Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
        {
            Assert.Equal(8192, image.Width);
            Assert.Equal(8192, image.Height);
            Assert.Null(options!.Region);
            Assert.Null(acceptCenter);
            Assert.Equal(255, image[8191, 8191]);
            return new([new(8000, 8000, 0, 1, .9)], false, false, 1);
        }
    }

    private sealed class FullImageLoader : IImageLoader, ITileSource
    {
        public HashSet<TileKey> Tiles { get; } = [];
        public bool Disposed
        {
            get; private set;
        }
        public ImageInfo Info { get; } = new("full", "full", 8192, 8192, PixelKind.Gray8, 1);
        public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(this);
        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            Assert.Equal(0, key.Level);
            Tiles.Add(key);
            var pixels = new PixelBuffer(512, 512, PixelKind.Gray8);
            pixels.Span.Fill(255);
            return pixels;
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task CpuDoesNotInitializeCudaAndUnavailableCudaDoesNotSilentlyFallBack()
    {
        var calls = 0;
        var service = new CpuTemplateMatchingService(new Loader(), () => null, () =>
        {
            calls++;
            throw new NotSupportedException("CUDA unavailable fixture");
        });
        var image = new ImageReference("fixture", "fixture", 256, 192);
        var roi = new RoiGeometry(new(48.5, 48.5), 33, 29, 0);
        var model = await service.TrainAsync(image, false, roi, CpuTemplateMethod.Line2D, default);
        var options = new SearchOptions { MinimumAngle = -5, MaximumAngle = 5 };
        Assert.Single((await service.SearchAsync(image, false, model, roi, options, default)).Result.Matches);
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SearchAsync(image, false, model, roi, options, default, useCuda: true));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(CpuTemplateMethod.Line2D, false)]
    [InlineData(CpuTemplateMethod.SubNcc, false)]
    [InlineData(CpuTemplateMethod.DirectionalChamfer, false)]
    [InlineData(CpuTemplateMethod.Line2D, true)]
    [InlineData(CpuTemplateMethod.SubNcc, true)]
    [InlineData(CpuTemplateMethod.DirectionalChamfer, true)]
    public async Task OriginalPixelsRotatedTrainingAndTinySearchRegionKeepWorldPose(CpuTemplateMethod method, bool useCuda)
    {
        var loader = new Loader();
        using var cuda = new CudaTemplateScoreSession();
        var service = new CpuTemplateMatchingService(loader, () => null, cuda.Rent);
        var image = new ImageReference("fixture", "fixture", 256, 192);
        var model = await service.TrainAsync(image, false, new(new(48.5, 48.5), 33, 29, 0), method, default);
        var region = RoiGeometry.Create(RoiShapeKind.Circle, new(160.5, 88.5), 4, 4);
        var options = new SearchOptions { MinimumAngle = 80, MaximumAngle = 100, MinimumScore = .8, MaximumMatches = 1 };
        var result = await service.SearchAsync(image, false, model, region, options, default, useCuda: useCuda);
        Assert.Equal(useCuda, result.Acceleration is not null);
        Check(result);
        var rotated = await service.TrainAsync(image, false, new(new(160.5, 88.5), 33, 29, 90), method, default);
        Check(await service.SearchAsync(image, false, rotated, region, options with
        {
            MinimumAngle = -10,
            MaximumAngle = 10
        }, default, useCuda: useCuda));
        Assert.Equal(4, loader.Disposed);
        var mask = new RoiExclusion(model.TeachingRegion, [new(MaskShape.Rectangle, new(.1, .1, .2, .2))]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.TrainAsync(image, false, model.TeachingRegion, method, default, mask));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SearchAsync(image, false, model, region, options, default, mask));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new("huge", "huge", 8193, 8193), false, model, null, options, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(image, false, model, region, options, new CancellationToken(true)));

        static void Check(CpuSearchOutput output)
        {
            Assert.False(output.Result.CandidatesTruncated);
            Assert.False(output.Result.MatchLimitExceeded);
            var found = Assert.Single(output.Result.Matches);
            Assert.InRange(Math.Abs(found.X - 160.5), 0, 1);
            Assert.InRange(Math.Abs(found.Y - 88.5), 0, 1);
            Assert.InRange(PyramidSearch.AngleDistance(found.AngleDegrees, 90), 0, 1);
        }
    }

    [Fact]
    public async Task ModesKeepModelsSettingsMasksAndRejectStaleResults()
    {
        var loader = new Loader();
        var service = new PendingService(new CpuTemplateMatchingService(loader, () => null));
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 192), ViewportTransform.Identity);
        editor.Add(new(new(48.5, 48.5), 33, 29, 0));
        using var vm = new ShapeMatchingViewModel(new ShapeMatchingService(loader, () => null), cpuService: service);
        vm.Update(editor.State, 1, false, true);
        var methods = new[] { PatternMatchingMethod.CpuLine2D, PatternMatchingMethod.CpuSubNcc, PatternMatchingMethod.CpuDirectionalChamfer };
        foreach (var method in methods)
        {
            vm.Method = method;
            Assert.True(vm.UseSelection);
            Assert.False(vm.HasModel);
            vm.MinimumScore = .8;
            vm.CpuMinimumAngle = -5;
            vm.CpuMaximumAngle = 5;
            await vm.Train.ExecuteAsync();
            Assert.True(vm.HasModel);
            await vm.Search.ExecuteAsync();
            Assert.Single(vm.Markers);
            Assert.Equal(InspectionVerdict.Review, vm.Verdict);
            Assert.Contains(method == PatternMatchingMethod.CpuSubNcc ? "SubNcc" : method == PatternMatchingMethod.CpuLine2D ? "Line2D" : "DirectionalChamfer", vm.InspectionLogText);
            vm.RecordOverlayPresentation(new(vm.Overlay!.PresentationId, System.Diagnostics.Stopwatch.GetTimestamp()));
            Assert.NotNull(vm.CpuInspectionLogs[^1].PresentedMilliseconds);
        }
        vm.Method = methods[0];
        vm.MinimumScore = .9;
        vm.CpuMaximumAngle = 20;
        vm.Method = methods[1];
        Assert.Equal(.8, vm.MinimumScore);
        Assert.Equal(5, vm.CpuMaximumAngle);
        Assert.True(vm.HasModel);
        vm.Method = methods[0];
        Assert.Equal(.9, vm.MinimumScore);
        Assert.Equal(20, vm.CpuMaximumAngle);
        vm.ComputeMode = InspectionComputeMode.GpuPreprocessing;
        vm.Method = methods[1];
        Assert.Equal(InspectionComputeMode.Cpu, vm.ComputeMode);
        vm.Method = methods[0];
        Assert.Equal(InspectionComputeMode.GpuPreprocessing, vm.ComputeMode);
        Assert.False(vm.IsTrainingDirty);
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.Search.ExecuteAsync();
        vm.Method = methods[2];
        service.Pending.SetResult(new(new([], false, false, 0), 0, 0, 0));
        await pending;
        Assert.Empty(vm.Markers);
        Assert.Equal("Discarded", vm.CpuInspectionLogs[^1].Outcome);
        var id = editor.State.Selection.Single();
        editor.AddMask(id, new(MaskShape.Rectangle, new(.1, .1, .2, .2)));
        vm.Update(editor.State, 1, false, true);
        Assert.True(vm.IsTrainingDirty);
        Assert.False(vm.Search.CanExecute(null));
        editor.Undo();
        vm.Update(editor.State, 1, false, true);
        Assert.True(vm.Search.CanExecute(null));
        vm.CpuAngleStep = 0;
        Assert.NotEmpty(vm.OptionsError);
        Assert.False(vm.Train.CanExecute(null));
        vm.CpuAngleStep = 10;
        vm.Clear.Execute(null);
        Assert.False(vm.HasModel);
        vm.Method = methods[0];
        Assert.True(vm.HasModel);
    }

    [Theory]
    [InlineData(true, false, 2, "최대 후보 수")]
    [InlineData(false, true, 2, "최대 결과 수")]
    [InlineData(false, false, 0, "패턴이 없습니다")]
    [InlineData(false, false, 2, "자동 판정하지 않습니다")]
    public async Task SidebarExplainsIncompleteAndEmptyResultsWithoutChangingVerdict(bool truncated, bool limited, int count, string guidance)
    {
        var service = new PendingService(new CpuTemplateMatchingService(new Loader(), () => null));
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 192), ViewportTransform.Identity);
        editor.Add(new(new(48.5, 48.5), 33, 29, 0));
        using var vm = new ShapeMatchingViewModel(null!, cpuService: service) { Method = PatternMatchingMethod.CpuLine2D };
        vm.Update(editor.State, 1, false, true);
        await vm.Train.ExecuteAsync();
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pending.SetResult(new(new(Enumerable.Range(0, count).Select(i => new Match(48.5 + (i * 50), 48.5, 0, 1, .9)).ToArray(), truncated, limited, 10), 1, 0, 1));
        await vm.Search.ExecuteAsync();
        Assert.Equal(truncated || limited ? InspectionVerdict.Warning : InspectionVerdict.Review, vm.Verdict);
        Assert.Contains(guidance, vm.PanelHint);
        Assert.DoesNotContain("WARN", vm.PanelTitle);
        Assert.Equal(count, vm.ResultRows.Length);
        Assert.Equal(.75, vm.MinimumScore);
        if (count > 0)
        {
            Assert.DoesNotContain(vm.ResultRows[0], vm.ResultSummary);
        }
        vm.HasInputErrors = true;
        Assert.False(vm.Search.CanExecute(null));
        Assert.Contains("숫자", vm.PanelHint);
        vm.HasInputErrors = false;
        Assert.True(vm.Search.CanExecute(null));
    }

    private sealed class PendingService(ICpuTemplateMatchingService inner) : ICpuTemplateMatchingService
    {
        public TaskCompletionSource<CpuSearchOutput>? Pending
        {
            get; set;
        }
        public Task<CpuTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method, CancellationToken token, RoiExclusion? exclusion = null, CpuTemplateParameters? parameters = null)
            => inner.TrainAsync(image, live, roi, method, token, exclusion, parameters);
        public Task<CpuSearchOutput> SearchAsync(ImageReference image, bool live, CpuTemplate template, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null, bool useCuda = false)
            => Pending?.Task ?? inner.SearchAsync(image, live, template, region, options, token, exclusion, useCuda);
    }

    private sealed class Loader : IImageLoader
    {
        public int Disposed;
        public Task<ITileSource> LoadAsync(ImageReference image, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(this));
    }

    private sealed class Source(Loader loader) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", 256, 192, PixelKind.Gray8, 1);
        public PixelBuffer Read(TileKey key, CancellationToken token)
        {
            Assert.Equal(new TileKey(0, 0, 0), key);
            var pixels = new PixelBuffer(256, 192, PixelKind.Gray8);
            pixels.Span.Fill(20);
            for (var y = -10; y <= 10; y++)
            {
                for (var x = -12; x <= 12; x++)
                {
                    if (x < -7 || y > 6 || (x > 7 && y < -6))
                    {
                        pixels.Span[((48 + y) * pixels.Stride) + 48 + x] = 225;
                        pixels.Span[((88 + x) * pixels.Stride) + 160 - y] = 225;
                    }
                }
            }
            return pixels;
        }
        public void Dispose() => loader.Disposed++;
    }
}
