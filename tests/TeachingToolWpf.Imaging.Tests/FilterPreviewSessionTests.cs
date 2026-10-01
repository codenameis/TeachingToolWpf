using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Filters;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Imaging.Tests;

public class FilterPreviewSessionTests
{
    private sealed class Processor : IRoiFilterPreview
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
        {
            Calls++;
            if (Calls == 1) { Started.SetResult(); await Release.Task; } // Deliberately ignore cancellation like a native operation.
            return new(request.Rois.Select(r => new FilteredRoi(r.Id, r.Geometry, 1, 1, [255])).ToArray(), 1);
        }
    }
    [Fact] public async Task PreviewIsExplicitAndSelectionChangesDiscardStaleNativeResult()
    {
        var editor = new EditorSession(); editor.InitializeImage(new("sample:100:100", "Image", 100, 100), ViewportTransform.Identity);
        var a = editor.Add(new(new(20, 20), 10, 10, 0)); var b = editor.Add(new(new(60, 60), 10, 10, 0)); editor.Select([a]);
        var processor = new Processor(); using var model = new FilterPreviewViewModel(processor);
        model.Update(editor.State, false); model.SelectedFilter = RoiFilterKind.Binarize;
        Assert.False(model.IsActive); Assert.Null(model.Result); Assert.Equal(0, processor.Calls);
        model.Preview.Execute(null); await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        editor.Select([b]); model.Update(editor.State, false); Assert.Null(model.Result);
        processor.Release.SetResult(); await model.WhenIdle;
        Assert.Equal(b, Assert.Single(model.Result!.Regions).Id); Assert.Equal(2, processor.Calls);
        editor.Select([a, b]); model.Update(editor.State, false); await model.WhenIdle; Assert.Equal(2, model.Result!.Regions.Count);
        model.SelectedFilter = RoiFilterKind.None; Assert.False(model.IsActive); Assert.Null(model.Result);
    }
    [Fact] public async Task TurningOffDuringProcessingCannotRestorePreview()
    {
        var editor = new EditorSession(); editor.InitializeImage(new("sample:100:100", "Image", 100, 100), ViewportTransform.Identity);
        editor.Add(new(new(20, 20), 10, 10, 0)); var processor = new Processor();
        using var model = new FilterPreviewViewModel(processor); model.Update(editor.State, false); model.SelectedFilter = RoiFilterKind.Edge;
        model.Preview.Execute(null); await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.Stop.Execute(null);
        processor.Release.SetResult(); await model.WhenIdle; Assert.Null(model.Result); Assert.False(model.IsActive);
    }
}
