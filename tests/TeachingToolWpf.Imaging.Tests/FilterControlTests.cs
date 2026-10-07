using System.Windows;
using System.Windows.Controls.Primitives;
using VisionCanvas.Core.Editing;
using VisionCanvas.Filters.Wpf.Controls;
using VisionCanvas.Filters.Wpf.ViewModels;
using VisionCanvas.Imaging.Filters;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class FilterControlTests
{
    [Fact]
    public async Task SliderPreviewsWithoutHistoryThenCommitsOnceAndEscapeRestores()
    {
        var editor = CreateEditor();
        var id = editor.State.Selection.Single();
        var processor = new RecordingProcessor();
        using var model = new FilterPreviewViewModel(processor, editor);
        editor.Changed += (_, _) => model.Update(editor.State, false, editor.TransformPreview);
        model.Update(editor.State, false);
        await model.WhenIdle;
        var original = editor.State;
        var history = editor.HistoryBytes;
        model.BeginParameterEdit.Execute(null);
        for (var radius = 4; radius <= 9; radius++)
        {
            model.RadiusValue = radius;
        }
        await model.WhenIdle;
        Assert.Same(original, editor.State);
        Assert.Equal(history, editor.HistoryBytes);
        Assert.Equal(9, processor.Last!.OverrideSettings!.Radius);
        model.EndParameterEdit.Execute(null);
        await model.WhenIdle;
        Assert.Equal(9, editor.State.Rois[id].Filter.Radius);
        editor.Undo();
        await model.WhenIdle;
        Assert.Equal(3, editor.State.Rois[id].Filter.Radius);
        Assert.Equal("3", model.Radius);
        model.BeginParameterEdit.Execute(null);
        model.RadiusValue = 17;
        await model.WhenIdle;
        model.CancelParameterEdit.Execute(null);
        await model.WhenIdle;
        Assert.Equal("3", model.Radius);
        Assert.Null(processor.Last!.OverrideSettings);
        Assert.Equal(3, editor.State.Rois[id].Filter.Radius);
    }

    [Fact]
    public async Task ContinuousMovesDoNotCancelTheRunningFrameAndLatestPoseFinishes()
    {
        var editor = CreateEditor();
        var original = editor.State.SelectionFacts.First!.Geometry;
        var processor = new RecordingProcessor(blockFirst: true);
        using var model = new FilterPreviewViewModel(processor, editor);
        editor.Changed += (_, _) => model.Update(editor.State, false, editor.TransformPreview);
        model.Update(editor.State, false);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        editor.BeginPreview();
        for (var i = 1; i <= 60; i++)
        {
            editor.PreviewTranslation(new(i, 0));
        }
        Assert.False(processor.FirstToken.IsCancellationRequested);
        var published = new TaskCompletionSource<FilterPreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Changed += () =>
        {
            if (model.Result is { } result)
            {
                published.TrySetResult(result);
            }
        };
        processor.Release.SetResult();
        var first = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, Assert.Single(first.Regions).Geometry);
        Assert.True(editor.IsPreviewing);
        await model.WhenIdle;
        Assert.Equal(2, processor.Calls);
        Assert.Equal(original.Translate(new(60, 0)), Assert.Single(model.Result!.Regions).Geometry);
        editor.CancelPreview();
        await model.WhenIdle;
        Assert.Equal(original, Assert.Single(model.Result!.Regions).Geometry);
    }

    [Fact]
    public async Task ToggleOffKeepsOriginalWhileParametersAreAppliedAndCancelsLateOutput()
    {
        var editor = CreateEditor();
        var processor = new RecordingProcessor(blockFirst: true);
        using var model = new FilterPreviewViewModel(processor, editor);
        model.Update(editor.State, false);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.IsPreviewEnabled = false;
        model.Radius = "5";
        model.Apply.Execute(null);
        processor.Release.SetResult();
        await model.WhenIdle;
        Assert.Null(model.Result);
        Assert.Equal(5, editor.State.SelectionFacts.First!.Filter.Radius);
        Assert.Equal(1, processor.Calls);
        editor.TranslateSelection(new(3, 0));
        model.Update(editor.State, false);
        Assert.False(model.IsPreviewEnabled);
        model.IsPreviewEnabled = true;
        await model.WhenIdle;
        Assert.Equal(5, processor.Last!.Settings(processor.Last.Rois[0]).Radius);
        Assert.NotNull(model.Result);
    }

    [Fact]
    public async Task ActualPreviewUsesTransientSettingsWithoutChangingRoi()
    {
        var pixels = new PixelBuffer(8, 8, PixelKind.Gray8);
        pixels.Span.Fill(100);
        using var frame = new VideoFrame(1, 1, pixels);
        using var preview = new RoiFilterPreview(new ImageLoader("unused"), frame.Retain);
        var roi = new Roi(Guid.NewGuid(), "test", new(new(4, 4), 8, 8, 0), null, false, 1)
        {
            Filter = new(RoiFilterKind.NativeThreshold, Threshold: 200)
        };
        var request = new FilterRequest(new("live", "live", 8, 8), true, [roi])
        {
            OverrideSettings = roi.Filter with { Threshold = 50 }
        };
        var result = await preview.ProcessAsync(request, default);
        Assert.All(Assert.Single(result.Regions).Gray8, value => Assert.Equal(255, value));
        Assert.Equal(200, roi.Filter.Threshold);
        result = await preview.ProcessAsync(request with
        {
            OverrideSettings = null
        }, default);
        Assert.All(Assert.Single(result.Regions).Gray8, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ControlLoadsWithoutApplicationResourcesAndKeepsInjectedDataContext()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var model = new FilterPreviewViewModel(new RecordingProcessor(), CreateEditor());
                var control = new FilterPanel { DataContext = model };
                control.Measure(new Size(340, 1400));
                control.Arrange(new Rect(0, 0, 340, 1400));
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Same(model, control.DataContext);
                var toggle = (ToggleButton)control.FindName("PreviewToggle");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                Assert.False(model.IsPreviewEnabled);
                model.SelectedFilter = RoiFilterKind.NativeThreshold;
                model.Threshold = "4095";
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                model.SelectedFilter = RoiFilterKind.Binarize;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal("4095", model.Threshold);
                var slider = (ParameterSlider)control.FindName("RadiusSlider");
                Assert.Same(model.BeginParameterEdit, slider.BeginEditCommand);
                Assert.DoesNotContain(typeof(FilterPanel).Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "TeachingToolWpf");
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static EditorSession CreateEditor()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 1000, 1000), ViewportTransform.Identity);
        editor.Add(new(new(300, 300), 80, 80, 0));
        editor.SetFilters(new(RoiFilterKind.Gaussian));
        return editor;
    }

    private sealed class RecordingProcessor(bool blockFirst = false) : IRoiFilterPreview
    {
        public int Calls;
        public FilterRequest? Last;
        public CancellationToken FirstToken;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
        {
            Last = request;
            Calls++;
            if (Calls == 1)
            {
                FirstToken = token;
                Started.SetResult();
                if (blockFirst)
                {
                    await Release.Task;
                }
            }
            return new(request.Rois.Select(roi => new FilteredRoi(roi.Id, request.Geometry(roi), 1, 1, [255])).ToArray(), 1);
        }
    }
}
