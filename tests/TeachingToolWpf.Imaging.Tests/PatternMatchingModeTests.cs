using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PatternMatchingModeTests
{
    [Fact]
    public async Task RotationInspectionExpandsSearchAndKeepsAcceptanceIndependentAndPaperOnly()
    {
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        viewModel.MinimumAngle = 0;
        viewModel.MaximumAngle = 10;
        await viewModel.Train.ExecuteAsync();
        var previous = viewModel.Model;
        viewModel.InspectRotation = true;
        Assert.Same(previous, viewModel.Model);
        Assert.True(viewModel.IsTrainingDirty);
        Assert.False(viewModel.Search.CanExecute(null));
        await viewModel.Train.ExecuteAsync();
        Assert.Equal(-180, service.TrainingOptions!.MinimumAngleDegrees);
        Assert.Equal(180, service.TrainingOptions.MaximumAngleDegrees);
        Assert.False(viewModel.CanEditSearchAngle);
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Fail, viewModel.Verdict);
        Assert.Contains("NG", Assert.Single(viewModel.ResultRows));
        Assert.Contains("NG", Assert.Single(viewModel.Markers).Label);
        Assert.Equal(InspectionResultPresentation.Color(InspectionVerdict.Fail), viewModel.Markers[0].Color);
        Assert.Equal(1, viewModel.InspectionLogs[^1].RotationFailureCount);
        Assert.Equal(new RotationAcceptance(), viewModel.InspectionLogs[^1].RotationAcceptance);
        viewModel.MaximumAcceptedAngle = 20;
        Assert.False(viewModel.IsTrainingDirty);
        Assert.Null(viewModel.Overlay);
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
        service.PendingSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();
        viewModel.MinimumAcceptedAngle = -10;
        service.PendingSearch.SetResult(service.Output);
        await pending;
        Assert.Null(viewModel.Verdict);
        service.PendingSearch = null;
        viewModel.MaximumAcceptedAngle = -20;
        Assert.NotEmpty(viewModel.OptionsError);
        Assert.False(viewModel.Search.CanExecute(null));
        viewModel.MaximumAcceptedAngle = 20;
        viewModel.Method = PatternMatchingMethod.GeometricShape;
        Assert.False(viewModel.InspectRotation);
        Assert.Equal(10, viewModel.MaximumAcceptedAngle);
        viewModel.InspectRotation = true;
        Assert.False(viewModel.InspectRotation);
        viewModel.Method = PatternMatchingMethod.Shape;
        Assert.True(viewModel.InspectRotation);
        Assert.Equal(20, viewModel.MaximumAcceptedAngle);
        viewModel.InspectRotation = false;
        Assert.True(viewModel.IsTrainingDirty);
        await viewModel.Train.ExecuteAsync();
        Assert.Equal(0, service.TrainingOptions.MinimumAngleDegrees);
        Assert.Equal(10, service.TrainingOptions.MaximumAngleDegrees);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.GeometricShape)]
    public async Task ShapeModesDefaultTo256ResultsWithoutChangingLibraryOrOtherModeSettings(PatternMatchingMethod method)
    {
        Assert.Equal(16, new SearchOptions().MaximumMatches);
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        viewModel.Method = method;
        Assert.Equal(256, viewModel.MaximumMatches);
        await viewModel.Train.ExecuteAsync();
        var model = viewModel.Model;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(256, service.SearchOptions!.MaximumMatches);

        viewModel.MaximumMatches = 17;
        viewModel.Method = method == PatternMatchingMethod.Shape
            ? PatternMatchingMethod.GeometricShape : PatternMatchingMethod.Shape;
        Assert.Equal(256, viewModel.MaximumMatches);
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        Assert.Equal(256, viewModel.MaximumMatches);
        viewModel.Method = method;
        Assert.Equal(17, viewModel.MaximumMatches);
        Assert.Same(model, viewModel.Model);
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(17, service.SearchOptions.MaximumMatches);
    }

    [Fact]
    public async Task ThreeModesRetainIndependentModelsSettingsAndLearnedFootprints()
    {
        var (service, editor, viewModel) = Create();
        using var lifetime = viewModel;
        Assert.Equal(PatternMatchingMethod.Shape, viewModel.Method);
        viewModel.MaximumFeatures = 180;
        viewModel.MinimumFeatureContrastRatio = .35;
        viewModel.MinimumScore = .8;
        viewModel.MaximumCandidates = 2500;
        viewModel.MaximumOverlap = .2;
        viewModel.MaximumMatches = 20;
        viewModel.UseSelection = true;
        await viewModel.Train.ExecuteAsync();
        var paper = viewModel.Model;
        Assert.False(service.TrainingOptions!.BuildGeometricModel);
        await viewModel.Search.ExecuteAsync();
        Assert.False(service.SearchOptions!.EvaluateGeometry);
        var paperBoundary = Assert.Single(viewModel.Markers).Bounds;
        Assert.Single(viewModel.ResultRows);
        Assert.True(viewModel.HasResultRows);
        Assert.Contains(viewModel.ResultRows[0], viewModel.Results);
        Assert.DoesNotContain(viewModel.ResultRows[0], viewModel.ResultSummary);

        viewModel.Method = PatternMatchingMethod.GeometricShape;
        Assert.Empty(viewModel.ResultRows);
        Assert.False(viewModel.HasResultRows);
        Assert.Equal(viewModel.Results, viewModel.ResultSummary);
        Assert.Null(viewModel.Model);
        Assert.False(viewModel.Search.CanExecute(null));
        Assert.Equal(160, viewModel.MaximumFeatures);
        Assert.Equal(.2, viewModel.MinimumFeatureContrastRatio);
        Assert.Equal(256, viewModel.MaximumMatches);
        Assert.False(viewModel.UseSelection);
        editor.Add(RoiGeometry.Create(RoiShapeKind.Circle, new(160, 150), 80, 80));
        viewModel.Update(editor.State, 1, false, true);
        viewModel.MaximumFeatures = 200;
        viewModel.MinimumFeatureContrastRatio = .1;
        viewModel.MinimumCoverage = .7;
        viewModel.MaximumMatches = 24;
        await viewModel.Train.ExecuteAsync();
        var geometric = viewModel.Model;
        Assert.NotSame(paper, geometric);
        Assert.True(service.TrainingOptions.BuildGeometricModel);
        await viewModel.Search.ExecuteAsync();
        Assert.True(service.SearchOptions!.EvaluateGeometry);
        Assert.Equal(.7, service.SearchOptions.MinimumCoverage);
        var geometricBoundary = Assert.Single(viewModel.Markers).Bounds;

        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        Assert.Equal(256, viewModel.MaximumMatches);
        Assert.False(viewModel.HasModel);
        viewModel.MinimumScore = .95;
        viewModel.MaximumCandidates = 3000;
        viewModel.MaximumOverlap = .1;
        await viewModel.Train.ExecuteAsync();
        var template = viewModel.TemplateModel;
        Assert.NotNull(template);
        editor.SetImage(new("replacement", "replacement", 512, 512), ViewportTransform.Identity);
        viewModel.Update(editor.State, 2, false, true);
        Assert.Same(template, viewModel.TemplateModel);

        viewModel.Method = PatternMatchingMethod.Shape;
        Assert.Same(paper, viewModel.Model);
        Assert.Equal(180, viewModel.MaximumFeatures);
        Assert.Equal(.35, viewModel.MinimumFeatureContrastRatio);
        Assert.Equal(.8, viewModel.MinimumScore);
        Assert.Equal(2500, viewModel.MaximumCandidates);
        Assert.Equal(.2, viewModel.MaximumOverlap);
        Assert.Equal(20, viewModel.MaximumMatches);
        Assert.True(viewModel.UseSelection);
        viewModel.UseSelection = false;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(paperBoundary.ToArray(), Assert.Single(viewModel.Markers).Bounds.ToArray());
        viewModel.Clear.Execute(null);
        Assert.Null(viewModel.Model);

        viewModel.Method = PatternMatchingMethod.GeometricShape;
        Assert.Same(geometric, viewModel.Model);
        Assert.Equal(200, viewModel.MaximumFeatures);
        Assert.Equal(.1, viewModel.MinimumFeatureContrastRatio);
        Assert.Equal(.7, viewModel.MinimumCoverage);
        Assert.Equal(24, viewModel.MaximumMatches);
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(geometricBoundary.ToArray(), Assert.Single(viewModel.Markers).Bounds.ToArray());
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        Assert.Same(template, viewModel.TemplateModel);
        Assert.Equal(.95, viewModel.MinimumScore);
        Assert.Equal(3000, viewModel.MaximumCandidates);
        Assert.Equal(.1, viewModel.MaximumOverlap);
    }

    [Theory]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumAngle), -90)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumAngle), 90)]
    [InlineData(nameof(ShapeMatchingViewModel.AngleStep), 5)]
    [InlineData(nameof(ShapeMatchingViewModel.EnableScale), 1)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumScale), .8)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumScale), 1.2)]
    [InlineData(nameof(ShapeMatchingViewModel.ScaleStep), .05)]
    [InlineData(nameof(ShapeMatchingViewModel.IgnorePolarity), 1)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumFeatures), 200)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumGradient), 10)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumFeatureContrastRatio), .35)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumTemplates), 4096)]
    public async Task TrainingChangesBlockSearchAndRestoringSettingsReusesModel(string name, double changed)
    {
        var (_, _, viewModel) = Create();
        using var lifetime = viewModel;
        await viewModel.Train.ExecuteAsync();
        var model = viewModel.Model;
        Assert.False(viewModel.IsTrainingDirty);
        var property = typeof(ShapeMatchingViewModel).GetProperty(name)!;
        var original = property.GetValue(viewModel);
        property.SetValue(viewModel, Convert.ChangeType(changed, property.PropertyType));
        Assert.True(viewModel.IsTrainingDirty);
        Assert.False(viewModel.Search.CanExecute(null));
        Assert.Same(model, viewModel.Model);
        Assert.Contains("다시 학습", viewModel.Status);
        property.SetValue(viewModel, original);
        Assert.False(viewModel.IsTrainingDirty);
        Assert.True(viewModel.Search.CanExecute(null));
        Assert.Same(model, viewModel.Model);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.GeometricShape)]
    public async Task UserParametersReachTrainingAndSearchWithoutSharingQualityPolicy(PatternMatchingMethod method)
    {
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        viewModel.Method = method;
        viewModel.MinimumAngle = -20;
        viewModel.MaximumAngle = 20;
        viewModel.AngleStep = 5;
        viewModel.EnableScale = true;
        viewModel.MinimumScale = .8;
        viewModel.MaximumScale = 1.2;
        viewModel.ScaleStep = .05;
        viewModel.MaximumFeatures = 200;
        viewModel.MinimumGradient = 9;
        viewModel.MinimumFeatureContrastRatio = .35;
        viewModel.MaximumTemplates = 4096;
        viewModel.IgnorePolarity = true;
        await viewModel.Train.ExecuteAsync();
        var training = Assert.IsType<TrainingOptions>(service.TrainingOptions);
        Assert.Equal(-20, training.MinimumAngleDegrees);
        Assert.Equal(20, training.MaximumAngleDegrees);
        Assert.Equal(5, training.AngleStepDegrees);
        Assert.Equal(.8, training.MinimumScale);
        Assert.Equal(1.2, training.MaximumScale);
        Assert.Equal(.05, training.ScaleStep);
        Assert.Equal(200, training.MaximumFeatures);
        Assert.Equal(9, training.MinimumGradient);
        Assert.Equal(.35, training.MinimumFeatureContrastRatio);
        Assert.Equal(4096, training.MaximumTemplates);
        Assert.Equal(ContrastPolarity.IgnoreLocal, training.Polarity);
        Assert.Equal(method == PatternMatchingMethod.GeometricShape, training.BuildGeometricModel);
        // 서비스가 피라미드 단계를 조정하더라도 UI 학습 조건은 변경되지 않았다.
        Assert.NotEqual(training.PyramidLevels, viewModel.Model!.Options.PyramidLevels);
        Assert.False(viewModel.IsTrainingDirty);

        viewModel.CandidateThreshold = .5;
        viewModel.MaximumCandidates = 5000;
        viewModel.MaximumVerifiedCandidates = 300;
        viewModel.MaximumCoarseVerifiedCandidates = 400;
        viewModel.MaximumOverlap = .15;
        viewModel.MinimumScore = .82;
        viewModel.MinimumSupport = .75;
        viewModel.MaximumMatches = 40;
        viewModel.Refine = false;
        viewModel.MinimumCoverage = .8;
        viewModel.LimitFitError = true;
        viewModel.MaximumFitErrorPixels = 1;
        viewModel.LimitClutter = true;
        viewModel.MaximumClutterRatio = .2;
        Assert.False(viewModel.IsTrainingDirty);
        await viewModel.Search.ExecuteAsync();
        var search = Assert.IsType<SearchOptions>(service.SearchOptions);
        Assert.Equal(.5, search.CandidateThreshold);
        Assert.Equal(5000, search.MaximumCandidates);
        Assert.Equal(300, search.MaximumVerifiedCandidates);
        Assert.Equal(400, search.MaximumCoarseVerifiedCandidates);
        Assert.Equal(.15, search.MaximumOverlap);
        Assert.Equal(.82, search.MinimumScore);
        Assert.Equal(.75, search.MinimumSupport);
        Assert.Equal(40, search.MaximumMatches);
        Assert.False(search.Refine);
        var geometric = method == PatternMatchingMethod.GeometricShape;
        Assert.Equal(geometric, search.EvaluateGeometry);
        Assert.Equal(geometric ? .8 : 0, search.MinimumCoverage);
        Assert.Equal(geometric ? 1 : double.PositiveInfinity, search.MaximumFitErrorPixels);
        Assert.Equal(geometric ? .2 : 1, search.MaximumClutterRatio);
    }

    [Fact]
    public async Task TrainingChangeCancelsLateReplacementAndPreservesSuccessfulModel()
    {
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        await viewModel.Train.ExecuteAsync();
        var model = viewModel.Model;
        service.PendingTraining = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Train.ExecuteAsync();
        viewModel.MaximumFeatures = 200;
        Assert.True(service.Token.IsCancellationRequested);
        service.PendingTraining.SetResult(service.CreateModel(service.TrainingOptions!));
        await pending;
        Assert.Same(model, viewModel.Model);
        Assert.True(viewModel.IsTrainingDirty);
        Assert.False(viewModel.Search.CanExecute(null));
        Assert.Null(viewModel.Overlay);
    }

    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidFeatureContrastBlocksShapeExecutionButDoesNotAffectOpenCv(double value)
    {
        foreach (var method in new[] { PatternMatchingMethod.Shape, PatternMatchingMethod.GeometricShape })
        {
            var (_, _, viewModel) = Create();
            using var lifetime = viewModel;
            viewModel.Method = method;
            await viewModel.Train.ExecuteAsync();
            var model = viewModel.Model;
            viewModel.MinimumFeatureContrastRatio = value;
            Assert.NotEmpty(viewModel.OptionsError);
            Assert.False(viewModel.Train.CanExecute(null));
            Assert.False(viewModel.Search.CanExecute(null));
            Assert.Same(model, viewModel.Model);
            viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
            Assert.Empty(viewModel.OptionsError);
            Assert.True(viewModel.Train.CanExecute(null));
            viewModel.Method = method;
            viewModel.MinimumFeatureContrastRatio = 0;
            Assert.Empty(viewModel.OptionsError);
            Assert.True(viewModel.Train.CanExecute(null));
            viewModel.MinimumFeatureContrastRatio = 1;
            Assert.Empty(viewModel.OptionsError);
        }
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.GeometricShape)]
    [InlineData(PatternMatchingMethod.OpenCvTemplate)]
    public async Task InvalidTypedOrUnparseableInputBlocksBothCommandsAndKeepsModel(PatternMatchingMethod method)
    {
        var (_, _, viewModel) = Create();
        using var lifetime = viewModel;
        viewModel.Method = method;
        await viewModel.Train.ExecuteAsync();
        await viewModel.Search.ExecuteAsync();
        Assert.NotEmpty(viewModel.Markers);
        var limit = viewModel.MaximumMatches;
        viewModel.MaximumMatches = 1001;
        Assert.False(viewModel.Train.CanExecute(null));
        Assert.False(viewModel.Search.CanExecute(null));
        Assert.NotEmpty(viewModel.OptionsError);
        Assert.True(viewModel.HasModel);
        Assert.Empty(viewModel.Markers);
        viewModel.MaximumMatches = limit;
        Assert.True(viewModel.Train.CanExecute(null));
        Assert.True(viewModel.Search.CanExecute(null));
        Assert.Empty(viewModel.OptionsError);
        viewModel.HasInputErrors = true;
        Assert.False(viewModel.Train.CanExecute(null));
        Assert.False(viewModel.Search.CanExecute(null));
        Assert.NotEmpty(viewModel.OptionsError);
        viewModel.HasInputErrors = false;
        Assert.True(viewModel.Train.CanExecute(null));
        Assert.True(viewModel.Search.CanExecute(null));
        Assert.Empty(viewModel.OptionsError);
    }

    [Fact]
    public async Task InputParsingFailureCancelsPendingSearchAndRejectsItsResult()
    {
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        await viewModel.Train.ExecuteAsync();
        service.PendingSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();
        viewModel.HasInputErrors = true;
        Assert.True(service.Token.IsCancellationRequested);
        service.PendingSearch.SetResult(service.Output);
        await pending;
        Assert.Empty(viewModel.Markers);
        Assert.Null(viewModel.Verdict);
        Assert.True(viewModel.HasModel);
        Assert.False(viewModel.Search.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchingModeRejectsLateTrainingAndInspection(bool training)
    {
        var (service, _, viewModel) = Create();
        using var lifetime = viewModel;
        Task pending;
        if (training)
        {
            service.PendingTraining = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = viewModel.Train.ExecuteAsync();
        }
        else
        {
            await viewModel.Train.ExecuteAsync();
            service.PendingSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = viewModel.Search.ExecuteAsync();
        }
        viewModel.Method = PatternMatchingMethod.GeometricShape;
        Assert.True(service.Token.IsCancellationRequested);
        if (training)
        {
            service.PendingTraining!.SetResult(service.CreateModel(service.TrainingOptions!));
        }
        else
        {
            service.PendingSearch!.SetResult(service.Output);
        }
        await pending;
        Assert.Null(viewModel.Model);
        Assert.Empty(viewModel.Markers);
        Assert.Null(viewModel.Verdict);
        viewModel.Method = PatternMatchingMethod.Shape;
        Assert.Equal(!training, viewModel.HasModel);
        Assert.Empty(viewModel.Markers);
    }

    private static (ShapeService Service, EditorSession Editor, ShapeMatchingViewModel ViewModel) Create()
    {
        var service = new ShapeService();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        editor.Add(new(new(100, 100), 70, 60, 15));
        var viewModel = new ShapeMatchingViewModel(service, new TemplateService());
        viewModel.Update(editor.State, 1, false, true);
        return (service, editor, viewModel);
    }

    private sealed class ShapeService : IShapeMatchingService
    {
        public TrainingOptions? TrainingOptions
        {
            get; private set;
        }
        public SearchOptions? SearchOptions
        {
            get; private set;
        }
        public CancellationToken Token
        {
            get; private set;
        }
        public TaskCompletionSource<ShapeTrainingOutput>? PendingTraining
        {
            get; set;
        }
        public TaskCompletionSource<ShapeSearchOutput>? PendingSearch
        {
            get; set;
        }
        public ShapeSearchOutput Output
        {
            get
            {
                var pose = new MatchPose(120, 130, .3);
                var match = new ShapeMatch(pose, .95, .96, .9, 4,
                    new(RefinementStatus.Converged, pose, 1, .1, 3, 100, true));
                return new([match], 1, false, 0, 0, []);
            }
        }

        public ShapeTrainingOutput CreateModel(TrainingOptions options)
            => new(ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
                new(ReadOnlyMemory<byte>.Empty, new(48, 48)), options with
                {
                    PyramidLevels = 4
                }), 0, 0);

        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token)
        {
            TrainingOptions = options;
            Token = token;
            return PendingTraining?.Task ?? Task.FromResult(CreateModel(options));
        }

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token)
        {
            options.Validate();
            SearchOptions = options;
            Token = token;
            return PendingSearch?.Task ?? Task.FromResult(Output);
        }
    }

    private sealed class TemplateService : ITemplateMatchingService
    {
        public Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token)
            => Task.FromResult(new TemplateTrainingOutput(CorrelationTemplate.Train(new(new byte[] { 10, 80, 30, 170, 60, 200 }, 3, 2, 3),
                new(ReadOnlyMemory<byte>.Empty, new(1.5, 1))), 0));

        public Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model, RoiGeometry? region, SearchOptions options, CancellationToken token)
            => Task.FromResult(new TemplateSearchOutput([new(new(120, 130), .95)], 1, false, 0, 0));
    }
}
