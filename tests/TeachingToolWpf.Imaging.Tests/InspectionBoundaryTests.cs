using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionBoundaryTests
{
    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Polygon)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Circle)]
    public async Task ShapeDetectionPreservesTaughtOutlineAtAbsolutePose(RoiShapeKind kind)
    {
        var teachingRegion = Region(kind);
        var service = new ShapeService();
        var editor = Editor(teachingRegion);
        using var viewModel = new ShapeMatchingViewModel(service);
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        await viewModel.Search.ExecuteAsync();

        AssertBoundary(teachingRegion, service.Pose, Assert.Single(viewModel.Markers).Bounds);
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Polygon)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Circle)]
    public async Task TemplateDetectionTranslatesTaughtOutlineWithoutChangingAngleOrSize(RoiShapeKind kind)
    {
        var teachingRegion = Region(kind);
        var (_, template, _, viewModel) = await CreateAsync(teachingRegion, PatternMatchingMethod.OpenCvTemplate);
        using var lifetime = viewModel;
        await viewModel.Search.ExecuteAsync();

        var marker = Assert.Single(viewModel.Markers);
        AssertBoundary(teachingRegion, TemplatePose(teachingRegion, template), marker.Bounds);
        Assert.Equal(teachingRegion.Angle * Math.PI / 180, marker.AngleRadians, 8);
        Assert.Empty(marker.Features);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.OpenCvTemplate)]
    public async Task RoiEditsAndImageReplacementKeepLearnedFootprint(PatternMatchingMethod method)
    {
        var teachingRegion = Region(RoiShapeKind.Polygon);
        var (shape, template, editor, viewModel) = await CreateAsync(teachingRegion, method);
        using var lifetime = viewModel;
        editor.TranslateSelection(new(100, 70));
        editor.Add(Region(RoiShapeKind.Circle));
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Search.ExecuteAsync();
        var pose = method == PatternMatchingMethod.Shape ? shape.Pose : TemplatePose(teachingRegion, template);
        AssertBoundary(teachingRegion, pose, Assert.Single(viewModel.Markers).Bounds);

        editor.Select([]);
        viewModel.Update(editor.State, 1, false, true);
        AssertBoundary(teachingRegion, pose, Assert.Single(viewModel.Markers).Bounds);
        editor.SetImage(new("replacement", "replacement", 2048, 2048), ViewportTransform.Identity);
        viewModel.Update(editor.State, 2, false, true);
        Assert.Empty(viewModel.Markers);
        await viewModel.Search.ExecuteAsync();
        AssertBoundary(teachingRegion, pose, Assert.Single(viewModel.Markers).Bounds);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape, "failure")]
    [InlineData(PatternMatchingMethod.Shape, "cancel")]
    [InlineData(PatternMatchingMethod.Shape, "generation")]
    [InlineData(PatternMatchingMethod.Shape, "selection")]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, "failure")]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, "cancel")]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, "generation")]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, "selection")]
    public async Task RejectedRetrainingPreservesPreviousModelAndFootprint(PatternMatchingMethod method, string rejection)
    {
        var teachingRegion = Region(RoiShapeKind.Polygon);
        var (shape, template, editor, viewModel) = await CreateAsync(teachingRegion, method);
        using var lifetime = viewModel;
        object previous = method == PatternMatchingMethod.Shape ? viewModel.Model! : viewModel.TemplateModel!;
        editor.Add(Region(RoiShapeKind.Triangle));
        viewModel.Update(editor.State, 1, false, true);
        shape.TrainingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        template.TrainingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Train.ExecuteAsync();
        switch (rejection)
        {
            case "cancel":
                viewModel.Cancel.Execute(null);
                break;
            case "generation":
                viewModel.Update(editor.State, 2, false, true);
                break;
            case "selection":
                editor.Add(Region(RoiShapeKind.Circle));
                viewModel.Update(editor.State, 1, false, true);
                break;
        }
        if (method == PatternMatchingMethod.Shape)
        {
            if (rejection == "failure")
            {
                shape.TrainingCompletion.SetException(new InvalidOperationException("training failed"));
            }
            else
            {
                shape.TrainingCompletion.SetResult(new(new ShapeService().Model, 0, 0));
            }
        }
        else if (rejection == "failure")
        {
            template.TrainingCompletion.SetException(new InvalidOperationException("training failed"));
        }
        else
        {
            template.TrainingCompletion.SetResult(new(new TemplateService().Model, 0));
        }
        await pending;

        Assert.Same(previous, method == PatternMatchingMethod.Shape ? viewModel.Model : viewModel.TemplateModel);
        await viewModel.Search.ExecuteAsync();
        var pose = method == PatternMatchingMethod.Shape ? shape.Pose : TemplatePose(teachingRegion, template);
        AssertBoundary(teachingRegion, pose, Assert.Single(viewModel.Markers).Bounds);
    }

    [Fact]
    public async Task SuccessfulRetrainingUpdatesOnlyTheActiveMethodFootprint()
    {
        var shapeRegion = Region(RoiShapeKind.Polygon);
        var templateRegion = Region(RoiShapeKind.Triangle);
        var (shape, template, editor, viewModel) = await CreateAsync(shapeRegion, PatternMatchingMethod.Shape);
        using var lifetime = viewModel;
        editor.Add(templateRegion);
        viewModel.Update(editor.State, 1, false, true);
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        await viewModel.Train.ExecuteAsync();
        viewModel.Method = PatternMatchingMethod.Shape;
        await viewModel.Search.ExecuteAsync();
        AssertBoundary(shapeRegion, shape.Pose, Assert.Single(viewModel.Markers).Bounds);

        var replacement = Region(RoiShapeKind.Rectangle);
        editor.Add(replacement);
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        await viewModel.Search.ExecuteAsync();
        AssertBoundary(replacement, shape.Pose, Assert.Single(viewModel.Markers).Bounds);
        viewModel.Method = PatternMatchingMethod.OpenCvTemplate;
        await viewModel.Search.ExecuteAsync();
        AssertBoundary(templateRegion, TemplatePose(templateRegion, template), Assert.Single(viewModel.Markers).Bounds);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.OpenCvTemplate)]
    public async Task SearchBoundaryUsesSearchRoiWhileMatchUsesLearnedRoi(PatternMatchingMethod method)
    {
        var teachingRegion = Region(RoiShapeKind.Triangle);
        var (shape, template, editor, viewModel) = await CreateAsync(teachingRegion, method);
        using var lifetime = viewModel;
        var searchRegion = new RoiGeometry(new(1100, 1100), 500, 400, 17);
        editor.Add(searchRegion);
        viewModel.Update(editor.State, 1, false, true);
        viewModel.UseSelection = true;
        await viewModel.Search.ExecuteAsync();

        Assert.Equal(searchRegion.Outline, viewModel.Overlay!.SearchBoundary);
        var pose = method == PatternMatchingMethod.Shape ? shape.Pose : TemplatePose(teachingRegion, template);
        AssertBoundary(teachingRegion, pose, Assert.Single(viewModel.Markers).Bounds);
    }

    [Theory]
    [InlineData(PatternMatchingMethod.Shape, false)]
    [InlineData(PatternMatchingMethod.Shape, true)]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, false)]
    [InlineData(PatternMatchingMethod.OpenCvTemplate, true)]
    public async Task RunningInspectionAlreadyShowsItsSearchBoundary(PatternMatchingMethod method, bool useSelection)
    {
        var teachingRegion = Region(RoiShapeKind.Triangle);
        var (shape, template, editor, viewModel) = await CreateAsync(teachingRegion, method);
        using var lifetime = viewModel;
        Assert.Empty(viewModel.Overlay!.SearchBoundary);
        var searchRegion = new RoiGeometry(new(1100, 1100), 500, 400, 17);
        editor.Add(searchRegion);
        viewModel.Update(editor.State, 1, false, true);
        viewModel.UseSelection = useSelection;
        shape.SearchCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        template.SearchCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = viewModel.Search.ExecuteAsync();

        Assert.True(viewModel.IsBusy);
        PointD[] expected = useSelection ? searchRegion.Outline : [new(0, 0), new(2048, 0), new(2048, 2048), new(0, 2048)];
        Assert.Equal(expected, viewModel.Overlay!.SearchBoundary);
        Assert.Empty(viewModel.Markers);
        shape.SearchCompletion.SetResult(new([], 1, false, 0, 0, []));
        template.SearchCompletion.SetResult(new([], 1, false, 0, 0));
        await pending;
        Assert.Equal(expected, viewModel.Overlay!.SearchBoundary);
    }

    private static async Task<(ShapeService Shape, TemplateService Template, EditorSession Editor, ShapeMatchingViewModel ViewModel)> CreateAsync(
        RoiGeometry region, PatternMatchingMethod method)
    {
        var shape = new ShapeService();
        var template = new TemplateService();
        var editor = Editor(region);
        var viewModel = new ShapeMatchingViewModel(shape, template) { Method = method };
        viewModel.Update(editor.State, 1, false, true);
        await viewModel.Train.ExecuteAsync();
        return (shape, template, editor, viewModel);
    }

    private static MatchPose TemplatePose(RoiGeometry region, TemplateService template)
        => new(template.ReferencePoint.X, template.ReferencePoint.Y, region.Angle * Math.PI / 180, 1);

    private static RoiGeometry Region(RoiShapeKind kind)
    {
        if (kind == RoiShapeKind.Polygon)
        {
            var polygon = RoiGeometry.Polygon([new(180, 190), new(250, 190), new(250, 215), new(210, 215), new(210, 250), new(180, 250)]);
            return polygon.RotateAround(polygon.Center, 31.7);
        }
        return new(new(200.25, 220.75), 64.5, kind == RoiShapeKind.Circle ? 64.5 : 38.25, 31.7,
            kind switch
            {
                RoiShapeKind.Circle => RoiShape.Circle,
                RoiShapeKind.Triangle => RoiShape.Triangle,
                _ => RoiShape.Rectangle
            });
    }

    private static EditorSession Editor(RoiGeometry region)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 2048, 2048), ViewportTransform.Identity);
        editor.Add(region);
        return editor;
    }

    private static void AssertBoundary(RoiGeometry teachingRegion, MatchPose pose, ImmutableArray<PointD> actual)
    {
        var taught = teachingRegion.Outline;
        Assert.Equal(taught.Length, actual.Length);
        var relativeAngle = pose.Angle - (teachingRegion.Angle * Math.PI / 180);
        var cosine = Math.Cos(relativeAngle) * pose.Scale;
        var sine = Math.Sin(relativeAngle) * pose.Scale;
        for (var index = 0; index < taught.Length; index++)
        {
            var relative = taught[index] - teachingRegion.Center;
            Assert.Equal(pose.X + (cosine * relative.X) - (sine * relative.Y), actual[index].X, 8);
            Assert.Equal(pose.Y + (sine * relative.X) + (cosine * relative.Y), actual[index].Y, 8);
        }
    }

    private sealed class ShapeService : IShapeMatchingService
    {
        public MatchPose Pose { get; } = new(1200.3, 1100.7, Math.PI / 3, 1.25);
        public ShapeModel Model
        {
            get;
        } = ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0
            });
        public TaskCompletionSource<ShapeTrainingOutput>? TrainingCompletion
        {
            get; set;
        }
        public TaskCompletionSource<ShapeSearchOutput>? SearchCompletion
        {
            get; set;
        }

        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => TrainingCompletion?.Task ?? Task.FromResult(new ShapeTrainingOutput(Model, 0, 0));

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            var match = new ShapeMatch(Pose, .95, .96, .9, 4, new(RefinementStatus.Converged, Pose, 1, .1, 3, 100, true));
            return SearchCompletion?.Task ?? Task.FromResult(new ShapeSearchOutput([match], 1, false, 0, 0, []));
        }
    }

    private sealed class TemplateService : ITemplateMatchingService
    {
        public ImagePoint ReferencePoint { get; } = new(1200.3, 1100.7);
        public CorrelationTemplate Model
        {
            get;
        } = CorrelationTemplate.Train(new(new byte[] { 10, 80, 30, 170, 60, 200 }, 3, 2, 3),
            new(ReadOnlyMemory<byte>.Empty, new(1.5, 1), Math.PI / 6));
        public TaskCompletionSource<TemplateTrainingOutput>? TrainingCompletion
        {
            get; set;
        }
        public TaskCompletionSource<TemplateSearchOutput>? SearchCompletion
        {
            get; set;
        }

        public Task<TemplateTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CancellationToken token, RoiExclusion? exclusion = null)
            => TrainingCompletion?.Task ?? Task.FromResult(new TemplateTrainingOutput(Model, 0));

        public Task<TemplateSearchOutput> SearchAsync(ImageReference image, bool live, CorrelationTemplate model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => SearchCompletion?.Task ?? Task.FromResult(new TemplateSearchOutput([new(ReferencePoint, .95)], 1, false, 0, 0));
    }
}
