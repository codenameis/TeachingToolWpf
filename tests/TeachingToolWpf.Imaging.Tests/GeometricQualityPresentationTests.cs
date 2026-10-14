using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class GeometricQualityPresentationTests
{
    [Fact]
    public async Task DefaultGeometryMeasuresWithoutAddingAcceptanceLimits()
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();

        var options = Assert.IsType<SearchOptions>(service.Options);
        Assert.True(options.EvaluateGeometry);
        Assert.Equal(3, options.GeometricTolerancePixels);
        Assert.Equal(.6, options.MinimumSupport);
        Assert.Equal(0, options.MinimumCoverage);
        Assert.Equal(double.PositiveInfinity, options.MaximumFitErrorPixels);
        Assert.Equal(1, options.MaximumClutterRatio);
        Assert.Equal(.72, options.MinimumScore);
        Assert.Equal(2048, options.MaximumCandidates);
        Assert.Equal(64, options.MaximumVerifiedCandidates);
    }

    [Fact]
    public async Task ExpandedCandidateBudgetIsExplicitAndRestoresExistingDefaults()
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        viewModel.UseExpandedCandidateBudget = true;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(4096, service.Options!.MaximumCandidates);
        Assert.Equal(256, service.Options.MaximumVerifiedCandidates);
        Assert.Equal(256, service.Options.MaximumCoarseVerifiedCandidates);
        viewModel.UseExpandedCandidateBudget = false;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(2048, service.Options.MaximumCandidates);
        Assert.Equal(64, service.Options.MaximumVerifiedCandidates);
    }

    [Fact]
    public async Task OptionalGeometryLimitsReachTheServiceAndCanBeDisabled()
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        viewModel.GeometricTolerancePixels = 2.25;
        viewModel.MinimumSupport = .65;
        viewModel.MinimumCoverage = .8;
        viewModel.LimitFitError = true;
        viewModel.MaximumFitErrorPixels = .75;
        viewModel.LimitClutter = true;
        viewModel.MaximumClutterRatio = .2;
        await viewModel.Search.ExecuteAsync();

        Assert.Equal(2.25, service.Options!.GeometricTolerancePixels);
        Assert.Equal(.65, service.Options.MinimumSupport);
        Assert.Equal(.8, service.Options.MinimumCoverage);
        Assert.Equal(.75, service.Options.MaximumFitErrorPixels);
        Assert.Equal(.2, service.Options.MaximumClutterRatio);
        viewModel.LimitFitError = false;
        viewModel.LimitClutter = false;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(double.PositiveInfinity, service.Options.MaximumFitErrorPixels);
        Assert.Equal(1, service.Options.MaximumClutterRatio);
        Assert.Equal(.75, viewModel.MaximumFitErrorPixels);
        Assert.Equal(.2, viewModel.MaximumClutterRatio);
    }

    [Theory]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumSupport))]
    [InlineData(nameof(ShapeMatchingViewModel.GeometricTolerancePixels))]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumCoverage))]
    [InlineData(nameof(ShapeMatchingViewModel.LimitFitError))]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumFitErrorPixels))]
    [InlineData(nameof(ShapeMatchingViewModel.LimitClutter))]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumClutterRatio))]
    [InlineData(nameof(ShapeMatchingViewModel.UseExpandedCandidateBudget))]
    public async Task ChangedAcceptanceSettingInvalidatesResultsAndRejectsLateCompletion(string property)
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        Assert.NotEmpty(viewModel.Markers);
        Change(viewModel, property, false);
        Assert.Empty(viewModel.Markers);
        Assert.Null(viewModel.Verdict);

        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();
        Assert.True(viewModel.IsBusy);
        Change(viewModel, property, true);
        Assert.True(service.Token.IsCancellationRequested);
        service.Pending.SetResult(service.Output);
        await pending;
        Assert.False(viewModel.IsBusy);
        Assert.Null(viewModel.Overlay);
        Assert.Null(viewModel.Verdict);
        Assert.Contains("입력 변경", viewModel.Results);
    }

    [Fact]
    public async Task AssigningUnchangedGeometrySettingsKeepsCompletedResult()
    {
        var (_, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        var overlay = viewModel.Overlay;
        viewModel.MinimumSupport = .6;
        viewModel.GeometricTolerancePixels = 3;
        viewModel.MinimumCoverage = 0;
        viewModel.LimitFitError = false;
        viewModel.MaximumFitErrorPixels = 3;
        viewModel.LimitClutter = false;
        viewModel.MaximumClutterRatio = .3;
        viewModel.UseExpandedCandidateBudget = false;
        Assert.Same(overlay, viewModel.Overlay);
    }

    [Fact]
    public async Task ResultsSeparateDirectionScoreFromMeasuredGeometry()
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        service.Geometry = new(.875, .375, .25, 140, 160, 200)
        {
            ClutterEnergyRatio = .04
        };
        await viewModel.Search.ExecuteAsync();
        Assert.Contains("방향 점수 0.960", viewModel.Results);
        Assert.Contains("최종 점수 0.922", viewModel.Results);
        Assert.Contains($"방향 지지율 {.9:P0}", viewModel.Results);
        Assert.Contains($"윤곽 특징 검출비율 {.875:P1} (140/160)", viewModel.Results);
        Assert.Contains("위치 오차 0.375 px RMS", viewModel.Results);
        Assert.Contains($"추가 윤곽 감점(대비) {.04:P1} · 개수 {.25:P1}", viewModel.Results);
        Assert.Contains($"불필요 윤곽비율(개수) {.25:P1}", viewModel.Results);
        Assert.Contains("영역 에지 200개", viewModel.Results);
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
    }

    [Fact]
    public async Task MissingGeometryIsReportedAsNotCalculated()
    {
        var (_, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        Assert.Contains("기하 검증 미계산", viewModel.Results);
        Assert.DoesNotContain("위치 오차 0", viewModel.Results);
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
    }

    [Fact]
    public async Task OpenCvSearchDoesNotUseShapeAcceptanceLimits()
    {
        var templateService = new TemplateService();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        editor.Add(new(new(100, 100), 70, 70, 0));
        using var viewModel = new ShapeMatchingViewModel(new Service(), templateService)
        {
            MinimumSupport = .8,
            MinimumCoverage = .95,
            LimitFitError = true,
            MaximumFitErrorPixels = .1,
            LimitClutter = true,
            MaximumClutterRatio = .1,
            UseExpandedCandidateBudget = true,
            Method = PatternMatchingMethod.OpenCvTemplate
        };
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        await viewModel.Search.ExecuteAsync();

        Assert.False(viewModel.IsShapeMethod);
        Assert.Equal(.6, templateService.Options!.MinimumSupport);
        Assert.Equal(0, templateService.Options.MinimumCoverage);
        Assert.Equal(double.PositiveInfinity, templateService.Options.MaximumFitErrorPixels);
        Assert.Equal(1, templateService.Options.MaximumClutterRatio);
        Assert.Equal(2048, templateService.Options.MaximumCandidates);
        Assert.Equal(64, templateService.Options.MaximumVerifiedCandidates);
        Assert.Contains("NCC 점수", viewModel.Results);
        Assert.DoesNotContain("기하", viewModel.Results);
    }

    [Theory]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(0, 0)]
    [InlineData(10, double.NaN)]
    [InlineData(10, double.PositiveInfinity)]
    public async Task UnmeasurableGeometryRequiresReviewWithoutFalseZeroPrecision(int matched, double error)
    {
        var (service, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        service.Geometry = new(matched / 160d, error, 0, matched, 160, matched);
        await viewModel.Search.ExecuteAsync();
        Assert.Contains("위치 오차 측정 불가", viewModel.Results);
        Assert.Equal(InspectionVerdict.Review, viewModel.Verdict);
        Assert.Equal(InspectionVerdict.Review, InspectionDecision.Evaluate(service.Output.Matches[0]).Verdict);
    }

    private static void Change(ShapeMatchingViewModel model, string property, bool alternate)
    {
        switch (property)
        {
            case nameof(ShapeMatchingViewModel.MinimumSupport):
                model.MinimumSupport = alternate ? .8 : .7;
                break;
            case nameof(ShapeMatchingViewModel.GeometricTolerancePixels):
                model.GeometricTolerancePixels = alternate ? 4 : 2;
                break;
            case nameof(ShapeMatchingViewModel.MinimumCoverage):
                model.MinimumCoverage = alternate ? .8 : .7;
                break;
            case nameof(ShapeMatchingViewModel.LimitFitError):
                model.LimitFitError = !alternate;
                break;
            case nameof(ShapeMatchingViewModel.MaximumFitErrorPixels):
                model.MaximumFitErrorPixels = alternate ? 1 : 2;
                break;
            case nameof(ShapeMatchingViewModel.LimitClutter):
                model.LimitClutter = !alternate;
                break;
            case nameof(ShapeMatchingViewModel.MaximumClutterRatio):
                model.MaximumClutterRatio = alternate ? .1 : .2;
                break;
            case nameof(ShapeMatchingViewModel.UseExpandedCandidateBudget):
                model.UseExpandedCandidateBudget = !alternate;
                break;
        }
    }

    private static async Task<(Service Service, ShapeMatchingViewModel ViewModel)> CreateAsync()
    {
        var service = new Service();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        editor.Add(new(new(100, 100), 70, 70, 0));
        var viewModel = new ShapeMatchingViewModel(service) { Method = PatternMatchingMethod.GeometricShape };
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        return (service, viewModel);
    }

    private sealed class Service : IShapeMatchingService
    {
        private readonly ShapeModel _model = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0
            });

        public SearchOptions? Options
        {
            get; private set;
        }
        public CancellationToken Token
        {
            get; private set;
        }
        public TaskCompletionSource<ShapeSearchOutput>? Pending
        {
            get; set;
        }
        public GeometricMatchQuality? Geometry
        {
            get; set;
        }

        public ShapeSearchOutput Output
        {
            get
            {
                var pose = new MatchPose(120, 130, 0);
                var match = new ShapeMatch(pose, .95, .96, .9, 4, new(RefinementStatus.Converged, pose, 1, .1, 3, 100, true))
                {
                    Geometry = Geometry,
                    ClutterRatio = Geometry?.ClutterRatio,
                    ClutterPenaltyRatio = Geometry?.ClutterEnergyRatio,
                    Score = .96 * (1 - (Geometry?.ClutterEnergyRatio ?? 0))
                };
                return new([match], 1, false, 0, 0, []);
            }
        }

        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => Task.FromResult(new ShapeTrainingOutput(_model, 0, 0));

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            Options = options;
            Token = token;
            return Pending?.Task ?? Task.FromResult(Output);
        }
    }

    private sealed class TemplateService : ITemplateMatchingService
    {
        private readonly CorrelationTemplate _model = CorrelationTemplate.Train(new(new byte[] { 10, 80, 30, 170, 60, 200 }, 3, 2, 3),
            new(ReadOnlyMemory<byte>.Empty, new(1.5, 1)));

        public SearchOptions? Options
        {
            get; private set;
        }

        public Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token, RoiExclusion? exclusion = null)
            => Task.FromResult(new TemplateTrainingOutput(_model, 0));

        public Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            Options = options;
            return Task.FromResult(new TemplateSearchOutput([new(new(120, 130), .95)], 1, false, 0, 0));
        }
    }
}
