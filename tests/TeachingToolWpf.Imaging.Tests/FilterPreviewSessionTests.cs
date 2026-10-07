using VisionCanvas.Core.Editing;
using VisionCanvas.Filters.Wpf.ViewModels;
using VisionCanvas.Imaging.Filters;
using VisionGeometry;
using RoiPropertiesViewModel = TeachingToolWpf.Presentation.RoiPropertiesViewModel;

namespace TeachingToolWpf.Imaging.Tests;

public class FilterPreviewSessionTests
{
    [Fact]
    public async Task IndustrialParametersRegisterAndInvalidTextDoesNotReplaceTheRecipe()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        var id = editor.Add(new(new(30, 30), 10, 10, 0));
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        editor.Changed += (_, _) => model.Update(editor.State, false);
        model.Update(editor.State, false);
        model.SelectedFilter = RoiFilterKind.GaussianBlackHatThreshold;
        model.Radius = "5";
        model.Sigma = 1.75.ToString(System.Globalization.CultureInfo.CurrentCulture);
        model.Threshold = "4095";
        model.Execution = RoiFilterExecution.CpuGpu;
        model.Preview.Execute(null);
        await model.WhenIdle;
        Assert.Equal(new(RoiFilterKind.GaussianBlackHatThreshold, 4095, Radius: 5, Sigma: 1.75,
            Execution: RoiFilterExecution.CpuGpu), editor.State.Rois[id].Filter);
        var before = editor.State;
        model.Radius = "1.5";
        Assert.Same(before, editor.State);
        Assert.Contains("반경 정수", model.Status);
        model.Radius = "7";
        await model.WhenIdle;
        Assert.Equal(7, editor.State.Rois[id].Filter.Radius);
        editor.Undo();
        await model.WhenIdle;
        Assert.Equal("5", model.Radius);
        Assert.Equal(RoiFilterExecution.CpuGpu, model.Execution);
    }

    [Fact]
    public async Task RotationPreviewUsesRotatedRegionAndCancellationRestoresFilter()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 500, 500), ViewportTransform.Identity);
        var parent = editor.Add(new(new(200, 200), 150, 150, 10));
        var child = editor.AddChild(parent, new(new(220, 210), 20, 12, 25));
        editor.SetFilters(new(RoiFilterKind.Edge));
        editor.Select([parent]);
        editor.SetFilters(new(RoiFilterKind.Binarize));
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        editor.Changed += (_, _) => model.Update(editor.State, false, editor.TransformPreview);
        model.Update(editor.State, false);
        await model.WhenIdle;
        var before = editor.State;
        var properties = new RoiPropertiesViewModel(editor);
        editor.BeginPreview();
        editor.PreviewRotation(new(200, 200), 30);
        properties.Refresh();
        await model.WhenIdle;
        Assert.Equal("40", properties.Angle);
        Assert.Equal(editor.TransformPreview!.Geometry(before.Rois[parent]), Assert.Single(model.Result!.Regions).Geometry);
        Assert.Equal(before.Rois[child].Geometry, editor.State.Rois[child].Geometry);
        editor.CancelPreview();
        await model.WhenIdle;
        Assert.Equal(before.Rois[parent].Geometry, Assert.Single(model.Result!.Regions).Geometry);
    }

    [Fact]
    public async Task NoOpDragReenablesFilterCommandsAndRegisteredDragKeepsRequestAllocationBounded()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100000, 100), ViewportTransform.Identity);
        editor.AddRange(Enumerable.Range(0, 10000).Select(i => new RoiGeometry(new((i * 8) + 10, 30), 5, 5, 0)));
        editor.Select(editor.State.Rois.Keys);
        editor.SetFilters(new(RoiFilterKind.Edge));
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        editor.Changed += (_, _) => model.Update(editor.State, false, editor.TransformPreview);
        model.Update(editor.State, false);
        await model.WhenIdle;
        editor.BeginPreview();
        editor.PreviewTranslation(new(1, 1));
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 2; i < 52; i++)
        {
            editor.PreviewTranslation(new(i, 1));
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - bytes, 0, 1024 * 1024);
        Assert.False(model.Preview.CanExecute(null));
        Assert.False(model.ClearRegistration.CanExecute(null));
        var ended = 0;
        model.Preview.CanExecuteChanged += (_, _) =>
        {
            if (model.Preview.CanExecute(null))
            {
                ended++;
            }
        };
        editor.PreviewTranslation(default);
        editor.CommitPreview("이동");
        Assert.True(ended > 0);
        Assert.True(model.ClearRegistration.CanExecute(null));
        await model.WhenIdle;
        Assert.Equal(editor.State.Rois.Count, model.Result!.Regions.Count);
        Assert.All(model.Result.Regions, r => Assert.Equal(editor.State.Rois[r.Id].Geometry, r.Geometry));
    }

    [Fact]
    public async Task TransformPreviewUsesMovedPixelsAndCancelRestoresOriginalRegion()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        var id = editor.Add(new(new(30, 30), 10, 10, 0));
        editor.SetFilters(new(RoiFilterKind.Edge));
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        await model.WhenIdle;
        var before = editor.State.Rois[id].Geometry;
        editor.BeginPreview();
        editor.PreviewTranslation(new(20, 15));
        model.Update(editor.State, false, editor.TransformPreview);
        await model.WhenIdle;
        Assert.Equal(before.Translate(new(20, 15)), Assert.Single(model.Result!.Regions).Geometry);
        Assert.Equal(before, editor.State.Rois[id].Geometry);
        editor.CancelPreview();
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Equal(before, Assert.Single(model.Result!.Regions).Geometry);
    }

    [Fact]
    public async Task NavigationRetainsPreviewAndDoesNotNotifyFilterBindings()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        editor.Add(new(new(30, 30), 10, 10, 0));
        editor.SetFilters(new(RoiFilterKind.Edge));
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        await model.WhenIdle;
        var result = model.Result;
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        editor.SetViewport(editor.State.Viewport.Pan(new(20, 0)));
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Same(result, model.Result);
        Assert.Equal(1, processor.Calls);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task MixedSelectionCanClearAllRegistrationsEvenWhenFirstRoiHasNone()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        var first = editor.Add(new(new(20, 20), 10, 10, 0));
        var second = editor.Add(new(new(60, 60), 10, 10, 0));
        editor.SetFilters(new(RoiFilterKind.Edge));
        editor.Select([first, second]);
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.True(model.HasMixedFilters);
        Assert.Equal(RoiFilterKind.None, model.SelectedFilter);
        Assert.Equal(second, Assert.Single(model.Result!.Regions).Id);
        Assert.True(model.ClearRegistration.CanExecute(null));
        model.ClearRegistration.Execute(null);
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.False(model.HasMixedFilters);
        editor.Undo();
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Equal(second, Assert.Single(model.Result!.Regions).Id);
    }

    [Fact]
    public async Task PreviewToggleRemainsOffAcrossSelectionsAndEnablingRestoresRegisteredSettings()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        var registered = editor.Add(new(new(20, 20), 10, 10, 0));
        var original = editor.Add(new(new(60, 60), 10, 10, 0));
        editor.Select([registered]);
        var processor = new Processor();
        processor.Release.SetResult();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        model.SelectedFilter = RoiFilterKind.Binarize;
        model.Threshold = "77";
        model.Preview.Execute(null);
        await model.WhenIdle;
        model.Stop.Execute(null);
        Assert.Null(model.Result);
        Assert.Equal(77, editor.State.Rois[registered].Filter.Threshold);
        editor.Select([original]);
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.Equal(RoiFilterKind.None, model.SelectedFilter);
        editor.Select([registered]);
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.False(model.IsPreviewEnabled);
        model.IsPreviewEnabled = true;
        await model.WhenIdle;
        Assert.Equal(registered, Assert.Single(model.Result!.Regions).Id);
        Assert.Equal("77", model.Threshold);
        model.SelectedFilter = RoiFilterKind.None;
        await model.WhenIdle;
        editor.Select([original]);
        model.Update(editor.State, false);
        editor.Select([registered]);
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.Equal(RoiFilterKind.None, editor.State.Rois[registered].Filter.Kind);
        editor.Undo();
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Equal(registered, Assert.Single(model.Result!.Regions).Id);
    }

    private sealed class Processor : IRoiFilterPreview
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
        {
            Calls++;
            if (Calls == 1)
            {
                Started.SetResult();
                await Release.Task;
            } // Deliberately ignore cancellation like a native operation.
            return new(request.Rois.Select(r => new FilteredRoi(r.Id, request.Geometry(r), 1, 1, [255])).ToArray(), 1);
        }
    }

    [Fact]
    public async Task PreviewIsExplicitAndSelectionChangesDiscardStaleNativeResult()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample:100:100", "Image", 100, 100), ViewportTransform.Identity);
        var a = editor.Add(new(new(20, 20), 10, 10, 0));
        var b = editor.Add(new(new(60, 60), 10, 10, 0));
        editor.SetFilters(new(RoiFilterKind.Edge));
        editor.Select([a]);
        var processor = new Processor();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        model.SelectedFilter = RoiFilterKind.Binarize;
        Assert.False(model.IsActive);
        Assert.Null(model.Result);
        Assert.Equal(0, processor.Calls);
        model.Preview.Execute(null);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        editor.Select([b]);
        model.Update(editor.State, false);
        Assert.Null(model.Result);
        processor.Release.SetResult();
        await model.WhenIdle;
        Assert.Equal(b, Assert.Single(model.Result!.Regions).Id);
        Assert.Equal(2, processor.Calls);
        editor.Select([a, b]);
        model.Update(editor.State, false);
        await model.WhenIdle;
        Assert.Equal(2, model.Result!.Regions.Count);
        model.SelectedFilter = RoiFilterKind.None;
        Assert.False(model.IsActive);
        Assert.Null(model.Result);
    }

    [Fact]
    public async Task TurningOffDuringProcessingCannotRestorePreview()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample:100:100", "Image", 100, 100), ViewportTransform.Identity);
        editor.Add(new(new(20, 20), 10, 10, 0));
        var processor = new Processor();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        model.SelectedFilter = RoiFilterKind.Edge;
        model.Preview.Execute(null);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Stop.Execute(null);
        processor.Release.SetResult();
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.False(model.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerCancellationIsHandledAndTheNextPreviewCompletes(bool replaceSettings)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 100, 100), ViewportTransform.Identity);
        var id = editor.Add(new(new(30, 30), 10, 10, 0));
        editor.SetFilters(new(RoiFilterKind.BlackHat));
        var processor = new CancelingProcessor();
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        try
        {
            await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (replaceSettings)
            {
                model.Radius = "7";
            }
            else
            {
                model.Stop.Execute(null);
            }
            await model.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(processor.Canceled);
            Assert.DoesNotContain("실패", model.Status);
            Assert.False(model.IsBusy);
            if (!replaceSettings)
            {
                Assert.Null(model.Result);
                model.IsPreviewEnabled = true;
                await model.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(id, Assert.Single(model.Result!.Regions).Id);
            Assert.Equal(2, processor.Calls);
            Assert.Equal(replaceSettings ? 7 : 3, processor.Last!.Settings(editor.State.Rois[id]).Radius);
        }
        finally
        {
            model.Dispose();
            await model.WhenIdle;
        }
    }

    private sealed class CancelingProcessor : IRoiFilterPreview
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Canceled;
        public FilterRequest? Last;

        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token) => Task.Run(() =>
        {
            Last = request;
            if (Interlocked.Increment(ref Calls) == 1)
            {
                Started.SetResult();
                Canceled = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The preview did not cancel its obsolete worker.");
            }
            return new FilterPreviewResult(request.Rois.Select(r =>
                new FilteredRoi(r.Id, request.Geometry(r), 1, 1, [255])).ToArray(), 1);
        }, token);
    }
}
