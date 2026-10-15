using CpuMatching.Core;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class CpuMatchingIntegrationTests
{
    [Theory]
    [InlineData(CpuTemplateMethod.Line2D)]
    [InlineData(CpuTemplateMethod.SubNcc)]
    [InlineData(CpuTemplateMethod.DirectionalChamfer)]
    public async Task OriginalPixelsRotatedTrainingAndTinySearchRegionKeepWorldPose(CpuTemplateMethod method)
    {
        var loader = new Loader();
        var service = new CpuTemplateMatchingService(loader, () => null);
        var image = new ImageReference("fixture", "fixture", 256, 192);
        var model = await service.TrainAsync(image, false, new(new(48.5, 48.5), 33, 29, 0), method, default);
        var region = RoiGeometry.Create(RoiShapeKind.Circle, new(160.5, 88.5), 4, 4);
        var options = new SearchOptions { MinimumAngle = 80, MaximumAngle = 100, MinimumScore = .8, MaximumMatches = 1 };
        var result = await service.SearchAsync(image, false, model, region, options, default);
        Check(result);
        var rotated = await service.TrainAsync(image, false, new(new(160.5, 88.5), 33, 29, 90), method, default);
        Check(await service.SearchAsync(image, false, rotated, region, options with
        {
            MinimumAngle = -10,
            MaximumAngle = 10
        }, default));
        Assert.Equal(4, loader.Disposed);
        var mask = new RoiExclusion(model.TeachingRegion, [new(MaskShape.Rectangle, new(.1, .1, .2, .2))]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.TrainAsync(image, false, model.TeachingRegion, method, default, mask));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SearchAsync(image, false, model, region, options, default, mask));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new("huge", "huge", 8192, 8192), false, model, null, options, default));
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
        public Task<CpuTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method, CancellationToken token, RoiExclusion? exclusion = null)
            => inner.TrainAsync(image, live, roi, method, token, exclusion);
        public Task<CpuSearchOutput> SearchAsync(ImageReference image, bool live, CpuTemplate template, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => Pending?.Task ?? inner.SearchAsync(image, live, template, region, options, token, exclusion);
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
