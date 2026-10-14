using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PrimitiveShapeDetectionViewModelTests
{
    [Fact]
    public async Task SelectionUsesSearchRangeAndRejectsGroupingParent()
    {
        var (service, editor, model) = Create();
        using var lifetime = model;
        var id = editor.State.Selection.Single();
        var geometry = editor.State.Rois[id].Geometry;
        editor.SetProperties(id, "search", geometry, new(10, 15), true);
        model.Update(editor.State, 1, false, true);
        model.UseSelection = true;
        await model.Detect.ExecuteAsync();
        Assert.Equal(new RoiSearchRange(10, 15).Expand(geometry), service.Region);
        editor.AddChild(id, new(new(128, 128), 20, 20, 0));
        editor.Select([id]);
        model.Update(editor.State, 1, false, true);
        Assert.False(model.Detect.CanExecute(null));
        model.UseSelection = false;
        Assert.True(model.Detect.CanExecute(null));
    }

    [Fact]
    public async Task DetectionUsesIndependentOptionsAndActualClosedContourWithoutEditingDocument()
    {
        var (service, editor, model) = Create();
        using var lifetime = model;
        model.FindCircles = false;
        model.MaximumResults = 500;
        model.UseSelection = true;
        var state = editor.State;
        await model.Detect.ExecuteAsync();
        Assert.Equal(500, service.Options!.MaximumResults);
        Assert.False(service.Options.Shapes.HasFlag(PrimitiveShapeKind.Circle));
        Assert.Equal(state.Rois.Values.Single().Geometry, service.Region);
        Assert.Same(state, editor.State);
        Assert.Equal(4, Assert.Single(model.Overlay!.Matches).Bounds.Length);
        Assert.NotNull(model.LastOutput);
        model.ShowOverlay = false;
        Assert.Null(model.Overlay);
        model.ShowOverlay = true;
        Assert.NotNull(model.Overlay);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("options")]
    [InlineData("selection")]
    public async Task ChangedInputCancelsAndRejectsLateDetection(string change)
    {
        var (service, editor, model) = Create();
        using var lifetime = model;
        model.UseSelection = true;
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.Detect.ExecuteAsync();
        Assert.True(model.IsBusy);
        switch (change)
        {
            case "generation":
                model.Update(editor.State, 2, false, true);
                break;
            case "options":
                model.MinimumScore = .9;
                break;
            case "selection":
                editor.Select([]);
                model.Update(editor.State, 1, false, true);
                break;
        }
        Assert.True(service.Token.IsCancellationRequested);
        service.Pending.SetResult(service.Output);
        await pending;
        Assert.False(model.IsBusy);
        Assert.Null(model.Overlay);
        Assert.Null(model.LastOutput);
    }

    [Fact]
    public async Task ManualCancelAndFailureCannotKeepRunningOrSuccessfulOverlay()
    {
        var (service, _, model) = Create();
        using var lifetime = model;
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.Detect.ExecuteAsync();
        model.Cancel.Execute(null);
        service.Pending.SetResult(service.Output);
        await pending;
        Assert.Contains("취소", model.Status);
        Assert.Empty(model.Overlay!.Matches);
        Assert.Null(model.LastOutput);
        service.Pending = null;
        service.Error = new InvalidOperationException("fixture failure");
        await model.Detect.ExecuteAsync();
        Assert.Contains("실패", model.Status);
        Assert.Empty(model.Overlay!.Matches);
    }

    [Fact]
    public void ValidationAndReentrantHostUpdatesKeepCommandsConsistent()
    {
        var (_, editor, model) = Create();
        using var lifetime = model;
        var changes = 0;
        model.Changed += () =>
        {
            Assert.True(++changes < 10, "host Update must not recursively republish unchanged input");
            model.Update(editor.State, 1, false, true);
        };
        model.MaximumSize = 513;
        Assert.False(model.Detect.CanExecute(null));
        Assert.NotEmpty(model.OptionsError);
        model.MaximumSize = 256;
        Assert.True(model.Detect.CanExecute(null));
        model.HasInputErrors = true;
        Assert.False(model.Detect.CanExecute(null));
        model.HasInputErrors = false;
        model.Update(editor.State, 1, false, false);
        Assert.False(model.Detect.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeLimitRejectsLateOutputAndLateNativeFailure(bool lateFailure)
    {
        var (service, _, model) = Create();
        using var lifetime = model;
        model.SearchTimeoutSeconds = .02;
        service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.Detect.ExecuteAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.Infinite, service.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        if (lateFailure)
        {
            service.Pending.SetException(new InvalidOperationException("late native failure"));
        }
        else
        {
            service.Pending.SetResult(service.Output);
        }
        await pending;
        Assert.Contains("시간 한도", model.Status);
        Assert.Contains("불완전", model.Status);
        Assert.False(model.IsBusy);
        Assert.Empty(model.Overlay!.Matches);
        Assert.Null(model.LastOutput);
        model.SearchTimeoutSeconds = 601;
        Assert.False(model.Detect.CanExecute(null));
    }

    private static (Service Service, EditorSession Editor, PrimitiveShapeDetectionViewModel Model) Create()
    {
        var service = new Service();
        var editor = new EditorSession();
        editor.InitializeImage(new("fixture", "fixture", 256, 256), ViewportTransform.Identity);
        editor.Add(new(new(128, 128), 128, 128, 0));
        var model = new PrimitiveShapeDetectionViewModel(service);
        model.Update(editor.State, 1, false, true);
        return (service, editor, model);
    }

    private sealed class Service : IPrimitiveShapeDetectionService
    {
        public PrimitiveDetectionOptions? Options
        {
            get; private set;
        }
        public RoiGeometry? Region
        {
            get; private set;
        }
        public CancellationToken Token
        {
            get; private set;
        }
        public TaskCompletionSource<PrimitiveDetectionOutput>? Pending
        {
            get; set;
        }
        public Exception? Error
        {
            get; set;
        }
        public PrimitiveDetectionOutput Output
        {
            get;
        } = new([new(PrimitiveShapeKind.Rectangle, new(120, 120), 0, .99, 1600,
            [new(100, 100), new(140, 100), new(140, 140), new(100, 140)])], 1, false, 0, 1);

        public Task<PrimitiveDetectionOutput> DetectAsync(ImageReference image, bool live, RoiGeometry? region,
            PrimitiveDetectionOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        {
            Options = options;
            Region = region;
            Token = token;
            return Error is { } error ? Task.FromException<PrimitiveDetectionOutput>(error) : Pending?.Task ?? Task.FromResult(Output);
        }
    }
}
