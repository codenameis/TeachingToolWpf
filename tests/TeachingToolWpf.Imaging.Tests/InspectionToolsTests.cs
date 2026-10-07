using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionToolsTests
{
    [Fact]
    public async Task OnlySelectedToolRunsAndSourceChangeRejectsItsLateResult()
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        session.Add(new(new(100, 100), 70, 60, 0));
        var shapeService = new ShapeService();
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(shapeService));
        tools.Update(session.State, 1, false, true);
        Assert.True(tools.Pattern.Train.CanExecute(null));
        Assert.False(tools.Shapes.Detect.CanExecute(null));
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        Assert.False(tools.Pattern.Train.CanExecute(null));
        Assert.True(tools.Shapes.Detect.CanExecute(null));
        var pending = tools.Shapes.Detect.ExecuteAsync();
        Assert.False(tools.IsIdle);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.Equal(InspectionToolKind.PrimitiveShapes, tools.Selected);
        Assert.False(tools.Pattern.Train.CanExecute(null));
        tools.Update(session.State, 2, false, true);
        Assert.True(shapeService.Token.IsCancellationRequested);
        shapeService.Completion.SetResult(new([], 1, false, 0, 1));
        await pending;
        Assert.True(tools.IsIdle);
        Assert.Null(tools.Shapes.LastOutput);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.True(tools.Pattern.Train.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedShapeResultsFollowSelectedToolAndDoNotChangeHistory(bool stoppedLiveFrame)
    {
        var session = new EditorSession();
        session.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        var history = session.HistoryBytes;
        var service = new ShapeService();
        service.Completion.SetResult(new([new(PrimitiveShapeKind.Rectangle, new(50, 50), 0, 1, 1600,
            [new(30, 30), new(70, 30), new(70, 70), new(30, 70)])], 1, false, 0, 1));
        using var tools = new InspectionToolsViewModel(new(new PatternService()), new(service));
        tools.Update(session.State, 1, stoppedLiveFrame, true);
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        await tools.Shapes.Detect.ExecuteAsync();
        Assert.Single(tools.Overlay!.Matches);
        tools.Selected = InspectionToolKind.Pattern;
        Assert.Null(tools.Overlay);
        tools.Selected = InspectionToolKind.PrimitiveShapes;
        Assert.Single(tools.Overlay!.Matches);
        Assert.Equal(history, session.HistoryBytes);
    }

    private sealed class ShapeService : IPrimitiveShapeDetectionService
    {
        public CancellationToken Token
        {
            get; private set;
        }
        public TaskCompletionSource<PrimitiveDetectionOutput> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region, PrimitiveDetectionOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            Token = token;
            return Completion.Task;
        }
    }

    private sealed class PatternService : IShapeMatchingService
    {
        public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new InvalidOperationException("패턴 서비스 실행은 이 조정 테스트의 대상이 아닙니다.");

        public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
            => throw new InvalidOperationException("동시 실행되면 안 됩니다.");
    }
}
