using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionResultTests
{
    [Theory]
    [InlineData(false, 1, false, InspectionVerdict.Fail)]
    [InlineData(true, 1, false, InspectionVerdict.Warning)]
    [InlineData(true, 1, true, InspectionVerdict.Warning)]
    [InlineData(false, 0, false, InspectionVerdict.Warning)]
    [InlineData(false, 1, true, InspectionVerdict.Pass)]
    public void IncompleteSearchNeverClaimsPassOrProductFailure(bool truncated, int windows, bool found, InspectionVerdict expected)
    {
        var output = new ShapeSearchOutput(found ? [Match()] : [], windows, truncated, 0, 0, []);
        Assert.Equal(expected, InspectionDecision.Evaluate(output).Verdict);
    }

    [Theory]
    [InlineData(RefinementStatus.Converged, true, InspectionVerdict.Pass)]
    [InlineData(RefinementStatus.Converged, false, InspectionVerdict.Review)]
    [InlineData(RefinementStatus.Disabled, true, InspectionVerdict.Review)]
    [InlineData(RefinementStatus.IterationLimit, true, InspectionVerdict.Review)]
    [InlineData(RefinementStatus.InsufficientSupport, true, InspectionVerdict.Review)]
    [InlineData(RefinementStatus.IllConditioned, true, InspectionVerdict.Review)]
    [InlineData(RefinementStatus.Rejected, true, InspectionVerdict.Review)]
    public void UncertainPoseRequiresHumanReview(RefinementStatus status, bool observable, InspectionVerdict expected)
    {
        var match = Match(status, observable);
        Assert.Equal(expected, InspectionDecision.Evaluate(new ShapeSearchOutput([match], 1, false, 0, 0, [])).Verdict);
    }

    [Theory]
    [InlineData(InspectionVerdict.Pass, "#32CD32", 50, 205, 50)]
    [InlineData(InspectionVerdict.Fail, "#FF6347", 255, 99, 71)]
    [InlineData(InspectionVerdict.Warning, "#FFA500", 255, 165, 0)]
    [InlineData(InspectionVerdict.Review, "#FFD700", 255, 215, 0)]
    public void PanelAndCanvasUseRequestedColors(InspectionVerdict verdict, string hex, int red, int green, int blue)
    {
        Assert.Equal(hex, InspectionResultPresentation.Hex(verdict));
        Assert.Equal(new Vector4(red / 255f, green / 255f, blue / 255f, 1), InspectionResultPresentation.Color(verdict));
    }

    [Fact]
    public async Task FeatureToggleReusesInspectionAndPlacesModelFeaturesAtAbsolutePose()
    {
        var (service, editor, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        var state = editor.State;
        await viewModel.Search.ExecuteAsync();
        var markers = viewModel.Markers;
        var first = Assert.Single(markers).Features[0];
        var feature = service.Model.Levels[0].Features[0];
        Assert.Equal(120 - (feature.Y * 1.5), first.X, 8);
        Assert.Equal(130 + (feature.X * 1.5), first.Y, 8);
        Assert.False(viewModel.Overlay!.ShowFeatures);
        viewModel.ShowFeatures = true;
        Assert.True(viewModel.Overlay!.ShowFeatures);
        Assert.Equal(markers, viewModel.Markers);
        viewModel.ShowOverlay = false;
        Assert.Null(viewModel.Overlay);
        viewModel.ShowOverlay = true;
        Assert.Equal(markers, viewModel.Overlay!.Matches);
        Assert.Equal(1, service.Searches);
        Assert.Same(state, editor.State);
        Assert.Contains("X 120.000 / Y 130.000", viewModel.Results);
        Assert.Contains("90.000°", viewModel.Results);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("score")]
    [InlineData("limit")]
    [InlineData("region")]
    public async Task LateResultsCannotSurviveChangedInspectionInput(string change)
    {
        var (service, editor, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();
        switch (change)
        {
            case "generation":
                viewModel.Update(editor.State, 2, false, true);
                break;
            case "score":
                viewModel.MinimumScore = .8;
                break;
            case "limit":
                viewModel.MaximumMatches = 2;
                break;
            case "region":
                viewModel.UseSelection = true;
                break;
        }
        service.Pending.SetResult(service.Output);
        await pending;
        Assert.Empty(viewModel.Markers);
        Assert.Null(viewModel.Overlay);
        Assert.Null(viewModel.Verdict);
    }

    [Fact]
    public async Task NewSearchClearsSuccessAndCancellationCannotLookSuccessful()
    {
        var (service, _, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Pass, viewModel.Verdict);
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();
        Assert.Empty(viewModel.Markers);
        Assert.Null(viewModel.Verdict);
        viewModel.Cancel.Execute(null);
        service.Pending.SetResult(service.Output);
        await pending;
        Assert.Equal(InspectionVerdict.Warning, viewModel.Verdict);
        Assert.Empty(viewModel.Markers);
    }

    [Fact]
    public async Task FailureAndNoMatchBothReplacePriorMarkersWithDistinctVerdicts()
    {
        var (service, _, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        service.Error = new InvalidOperationException("reader failed");
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Warning, viewModel.Verdict);
        Assert.Empty(viewModel.Markers);
        Assert.Contains("reader failed", viewModel.Results);
        service.Error = null;
        service.Output = service.Output with
        {
            Matches = []
        };
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Fail, viewModel.Verdict);
        Assert.Empty(viewModel.Markers);
        Assert.Equal(4, viewModel.Overlay!.SearchBoundary.Length);
        Assert.False(viewModel.Locate.CanExecute(null));
        viewModel.MinimumScore = .9;
        Assert.Null(viewModel.Overlay);
        Assert.Null(viewModel.Verdict);
    }

    [Fact]
    public async Task SelectedSearchGeometryChangeAndLiveResumeInvalidateResult()
    {
        var (_, editor, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        viewModel.UseSelection = true;
        await viewModel.Search.ExecuteAsync();
        editor.TranslateSelection(new(5, 0));
        viewModel.Update(editor.State, 1, false, true);
        Assert.Null(viewModel.Overlay);
        viewModel.Update(editor.State, 2, true, true);
        await viewModel.Search.ExecuteAsync();
        Assert.NotEmpty(viewModel.Markers);
        viewModel.Update(editor.State, 2, true, false);
        Assert.Null(viewModel.Overlay);
    }

    [Fact]
    public async Task LocateRequestsRealMatchBoundsWithoutEditingDocument()
    {
        var (_, editor, viewModel) = await CreateAsync();
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();
        var state = editor.State;
        BoundsD? requested = null;
        viewModel.LocateRequested += bounds => requested = bounds;
        viewModel.Locate.Execute(null);
        Assert.NotNull(requested);
        Assert.True(requested.Value.Left < 120 && requested.Value.Right > 120);
        Assert.Same(state, editor.State);
    }

    private static ShapeMatch Match(RefinementStatus status = RefinementStatus.Converged, bool observable = true)
    {
        var pose = new MatchPose(120, 130, Math.PI / 2, 1.5);
        return new(pose, .95, .96, .9, 4, new(status, pose, 1, .1, 3, 100, observable));
    }

    private static async Task<(Service Service, EditorSession Editor, ShapeMatchingViewModel ViewModel)> CreateAsync()
    {
        var service = new Service();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        editor.Add(new(new(100, 100), 70, 70, 0));
        var viewModel = new ShapeMatchingViewModel(service);
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        return (service, editor, viewModel);
    }

    private sealed class Service : IShapeMatchingService
    {
        public ShapeModel Model
        {
            get;
        } = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0
            });
        public ShapeSearchOutput Output { get; set; } = new([Match()], 1, false, 1, 2, []);
        public TaskCompletionSource<ShapeSearchOutput>? Pending
        {
            get; set;
        }
        public Exception? Error
        {
            get; set;
        }
        public int Searches
        {
            get; private set;
        }

        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token)
            => Task.FromResult(new ShapeTrainingOutput(Model, 0, 0));

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token)
        {
            Searches++;
            return Error is { } error ? Task.FromException<ShapeSearchOutput>(error) : Pending?.Task ?? Task.FromResult(Output);
        }
    }
}
