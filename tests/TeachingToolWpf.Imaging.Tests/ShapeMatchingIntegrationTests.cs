using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeMatchingIntegrationTests
{
    [Fact]
    public async Task OpenCvModeUsesOriginalPixelsAndKeepsIndependentModelsAndHistory()
    {
        var loader = new Loader();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 0));
        using var viewModel = new ShapeMatchingViewModel(new ShapeMatchingService(loader, () => null), new TemplateMatchingService(loader, () => null));
        viewModel.Update(new(editor.State, 1, false, true));
        await viewModel.Train.ExecuteAsync();
        var shape = viewModel.Model;
        var state = editor.State;
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        viewModel.MinimumScore = .98;
        Assert.False(viewModel.Search.CanExecute(null));
        await viewModel.Train.ExecuteAsync();
        var template = viewModel.TemplateModel;
        Assert.NotNull(template);
        await viewModel.Search.ExecuteAsync();
        var marker = Assert.Single(viewModel.Markers);
        Assert.Equal(new PointD(80, 90), marker.ReferencePoint);
        Assert.Empty(marker.Features);
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
        Assert.Same(state, editor.State);
        Assert.Equal(loader.Opened, loader.Disposed);
        viewModel.Method = PatternMatchingMethod.Shape;
        Assert.Same(shape, viewModel.Model);
        Assert.Empty(viewModel.Markers);
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        Assert.Same(template, viewModel.TemplateModel);
        viewModel.Update(new(editor.State, 2, false, true));
        Assert.Same(template, viewModel.TemplateModel);
    }

    [Fact]
    public async Task ExistingRoiTrainsAndSearchesAcrossTileBoundaryWithoutEditingDocument()
    {
        var loader = new Loader();
        var service = new ShapeMatchingService(loader, () => null);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 17));
        using var model = new ShapeMatchingViewModel(service) { MinimumAngle = -40, MaximumAngle = 40, AngleStep = 5, UseSelection = false };
        model.Update(new(editor.State, 1, false, true));
        var state = editor.State;
        await model.Train.ExecuteAsync();
        Assert.NotNull(model.Model);
        await model.Search.ExecuteAsync();
        Assert.Equal(2, model.Markers.Length);
        Assert.Contains(model.Markers, m => Math.Abs(m.ReferencePoint.X - 769.3) < 0.5);
        Assert.Same(state, editor.State);
        Assert.Equal(loader.Opened, loader.Disposed);
        editor.Select([]);
        model.Update(new(editor.State, 1, false, true));
        Assert.Equal(2, model.Markers.Length);
        model.Update(new(editor.State, 2, false, true));
        Assert.Empty(model.Markers);
        Assert.NotNull(model.Model);
    }

    [Fact]
    public async Task LateTrainingResultIsDiscardedAfterSourceGenerationChanges()
    {
        var delayed = new DelayedService();
        using var viewModel = new ShapeMatchingViewModel(delayed);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 200, 200), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 60, 60, 0));
        viewModel.Update(new(editor.State, 1, false, true));
        var pending = viewModel.Train.ExecuteAsync();
        viewModel.Update(new(editor.State, 2, false, true));
        delayed.Completion.SetResult(new(ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()), 0, 0));
        await pending;
        Assert.Null(viewModel.Model);
        Assert.Empty(viewModel.Markers);
    }

    [Fact]
    public void CircleSearchAdapterUsesExactCircleInsteadOfDisplayPolygon()
    {
        var geometry = RoiGeometry.Create(RoiShapeKind.Circle, new(100, 100), 80, 80);
        var region = ShapeMatchingService.ToSearchRegion(geometry, 30, 20)!;
        Assert.True(region.Contains(70, 120));
        Assert.False(region.Contains(100, 110));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutsideImageRegionFailsBeforeOpeningReader(bool training)
    {
        var loader = new Loader();
        var service = new ShapeMatchingService(loader, () => null);
        var image = new ImageReference("fixture", "fixture", 1536, 256);
        var outside = new RoiGeometry(new(training ? 1530 : 1600, 128), 100, 100, 0);
        if (training)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.TrainAsync(image, false, outside, new(), default));
        }
        else
        {
            var model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
                new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new());
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(image, false, model, outside, new(), default));
        }
        Assert.Equal(0, loader.Opened);
    }

    [Fact]
    public async Task LiveParameterPreviewUpdatesSummaryAndCanvasMarkersBeforeTrainingAndSearching()
    {
        var loader = new Loader();
        var service = new ShapeMatchingService(loader, () => null);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 0));
        using var vm = new ShapeMatchingViewModel(service);
        vm.Update(new(editor.State, 1, false, true));

        Assert.Contains("실시간 설정 미리보기", vm.ParameterPreviewSummary);
        Assert.Contains("최대 특징 160개", vm.ParameterPreviewSummary);

        vm.EnableScale = true;
        vm.MinimumScale = 0.85;
        vm.MaximumScale = 1.15;
        vm.MaximumFeatures = 64;
        await Task.Delay(140);

        // "특징점 시각화" 체크 전에는 파라미터 요약 텍스트만 즉시 갱신되고 백그라운드 특징 추출 미리보기는 실행되지 않는다.
        Assert.Null(vm.Overlay);
        Assert.Equal(0, loader.Opened);
        Assert.Contains("최대 특징 64개", vm.ParameterPreviewSummary);
        Assert.Contains("배율 0.85~1.15x", vm.ParameterPreviewSummary);

        // "특징점 시각화"를 체크하면 ROI 드래그/리사이즈 중(CanExecute: false)에도 UI 블로킹 없이 실시간으로 특징 추출 미리보기가 갱신된다.
        vm.ShowFeatures = true;
        vm.Update(new(editor.State, 1, false, CanExecute: false));
        for (var i = 0; i < 40 && vm.Overlay is null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(vm.Overlay);
        Assert.Equal(1, loader.Opened);
        Assert.Equal(1, loader.Disposed);
        var previewMarker = Assert.Single(vm.Overlay!.Matches);
        Assert.NotEmpty(previewMarker.Features);
        Assert.True(previewMarker.Features.Length <= 64);
        Assert.Equal(3, previewMarker.InnerContours.Length);
        Assert.Contains("추출 특징", vm.ParameterPreviewSummary);
        Assert.Contains("배율 0.85~1.15x", vm.ParameterPreviewSummary);

        // 연속 드래그/파라미터 변경 시에는 인메모리 미리보기 타일 캐시에서 즉시 읽으므로 타일 소스를 다시 열지 않는다.
        vm.MinimumGradient = 18;
        for (var i = 0; i < 40 && !vm.ParameterPreviewSummary.Contains("추출 특징"); i++)
        {
            await Task.Delay(10);
        }
        await Task.Delay(30);
        Assert.Equal(1, loader.Opened);
        Assert.Equal(1, loader.Disposed);
        vm.Update(new(editor.State, 1, false, CanExecute: true));

        await vm.Train.ExecuteAsync();
        Assert.NotNull(vm.Overlay);
        var trainedMarker = Assert.Single(vm.Overlay!.Matches);
        Assert.NotEmpty(trainedMarker.DiagnosticGroups);

        vm.ClutterTolerancePixels = 5.0;
        Assert.NotNull(vm.Overlay);
        Assert.Contains("±5.0px", vm.ParameterPreviewSummary);

        vm.StrictContext = false;
        Assert.NotNull(vm.Overlay);
        Assert.Empty(Assert.Single(vm.Overlay!.Matches).KeyFeatures);
        Assert.Contains("완화", vm.ParameterPreviewSummary);
    }

    [Fact]
    public async Task PatMaxModeLinksParametersToLiveInspectionFilterPreviewAndTrainsAndSearches()
    {
        var loader = new Loader();
        var shapeService = new ShapeMatchingService(loader, () => null);
        var patMaxService = new PatMaxMatchingService(loader, () => null);
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 1536, 256), ViewportTransform.Identity);
        editor.Add(new(new(80, 90), 65, 65, 0));
        using var vm = new ShapeMatchingViewModel(shapeService, patMaxService: patMaxService);
        vm.Method = PatternMatchingMethod.PatMax;
        vm.ShowFeatures = true;
        vm.Update(new(editor.State, 1, false, true));

        for (var i = 0; i < 40 && vm.FilterPreview is null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(vm.FilterPreview);
        var roiFilter = Assert.Single(vm.FilterPreview!.Regions);
        Assert.True(roiFilter.Width > 0 && roiFilter.Height > 0);
        Assert.NotNull(vm.Overlay);

        // Fine/Coarse Granularity 변경 시 FilterSigma가 자동 연동되고 실시간 필터 영상이 즉시 재계산된다.
        vm.PatMaxPreprocessingFilter = VisionAlgorithm.OpenSrc.PatMax.PatMaxPreprocessingFilterKind.EdgeContrastEnhance;
        vm.PatMaxFineGranularity = 2;
        vm.PatMaxCoarseGranularity = 4;
        Assert.InRange(vm.PatMaxFilterSigma, 1.25f, 1.35f);

        await Task.Delay(80);
        Assert.NotNull(vm.FilterPreview);

        await vm.Train.ExecuteAsync();
        Assert.True(vm.HasModel);
        Assert.NotNull(vm.FilterPreview);

        vm.UseSelection = false;
        await vm.Search.ExecuteAsync();
        Assert.NotEmpty(vm.Markers);
        Assert.Equal(InspectionVerdict.Pass, vm.Verdict);
    }

    private sealed class DelayedService : IShapeMatchingService
    {
        public TaskCompletionSource<ShapeTrainingOutput> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => Completion.Task;
        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new NotSupportedException();
    }

    private sealed class Loader : IImageLoader
    {
        public int Opened;
        public int Disposed;
        private readonly GrayImage _image = SyntheticScene.Create(1536, 256, new MatchPose(80, 90, 0.1), new MatchPose(769.3, 140.7, -0.1));
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Opened++;
            return Task.FromResult<ITileSource>(new Source(_image, () => Disposed++));
        }
    }

    private sealed class Source(GrayImage image, Action disposed) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);
        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            var output = new PixelBuffer(Math.Min(512, image.Width - (key.X * 512)), Math.Min(512, image.Height - (key.Y * 512)), PixelKind.Gray8);
            for (var y = 0; y < output.Height; y++)
            {
                image.Pixels.Span.Slice((((key.Y * 512) + y) * image.Stride) + (key.X * 512), output.Width).CopyTo(output.Span.Slice(y * output.Stride));
            }
            return output;
        }
        public void Dispose() => disposed();
    }
}
