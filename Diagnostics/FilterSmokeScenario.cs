using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Imaging.Filters;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

internal static class FilterSmokeScenario
{
    private static void Require(bool value, string description)
    { if (!value) throw new InvalidOperationException("Filter smoke assertion: " + description); }

    public static async Task Run(EditorViewModel model, Dx12Viewport viewport)
    {
        var before = model.Session.State;
        model.Filters.SelectedFilter = RoiFilterKind.Binarize;
        Require(model.Filters.Result is null, "choosing a filter does not activate it");
        model.Filters.Preview.Execute(null); await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == 1, model.Filters.Status);
        VerifyThresholdEdit(model, viewport);
        await model.Filters.WhenIdle;
        await model.SelectAll.ExecuteAsync(); await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == 1, "unregistered ROI stays original in multi-selection");
        model.Filters.SelectedFilter = RoiFilterKind.Edge; await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == before.Rois.Count, model.Filters.Status);
        var rotationStart = model.Session.State;
        model.Session.BeginPreview();
        model.Session.PreviewRotation(rotationStart.SelectedSpatial.Bounds.Center, 10);
        await model.Filters.WhenIdle;
        var rotation = model.Session.TransformPreview!;
        Require(model.Filters.Result?.Regions.Count == rotationStart.Rois.Count &&
            model.Filters.Result.Regions.All(r => r.Geometry == rotation.Geometry(rotationStart.Rois[r.Id])),
            "native OpenCV regions follow retained rotation preview");
        viewport.Capture("artifacts/filter-rotation-smoke.png"); await Task.Delay(500);
        model.Session.CancelPreview(); await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == rotationStart.Rois.Count &&
            model.Filters.Result.Regions.All(r => r.Geometry == rotationStart.Rois[r.Id].Geometry),
            "cancel restores original filter regions");
        await Task.Delay(500); viewport.Capture("artifacts/filter-smoke.png"); await Task.Delay(500);
        model.Filters.Stop.Execute(null); Require(model.Filters.Result is null, "original view clears preview");
        await model.Images.ReplaceAsync(new("sample:24576:32768", "24K", 24576, 32768));
        Require(model.Session.State.Rois.Count == 0 && model.Session.State.Selection.Count == 0, "image replacement clears ROI");
        await MinimapSmokeScenario.Run(model, viewport, "24k");
        await model.Images.HistoryAsync(false);
        Require(model.Session.State.Rois.Count == before.Rois.Count && model.Images.Image?.Width == 8192, "image undo restores ROI and source");
        await model.StartLive.ExecuteAsync();
        Require(model.Live.IsRunning && model.Session.State.Image?.Width == 7680 && model.Session.State.Rois.Count == 0, "live switch resets ROI and image bounds");
        await Task.Delay(800);
        model.AddRoi.Execute(null); model.Filters.SelectedFilter = RoiFilterKind.Binarize; model.Filters.Preview.Execute(null); await model.Filters.WhenIdle;
        Require(model.Filters.Result?.Regions.Count == 1, model.Filters.Status);
        await Task.Delay(600);
        Require(model.Filters.Result?.Regions.Count == 1, "periodic live filter refresh");
        await MinimapSmokeScenario.Run(model, viewport, "live");
    }
    private static void VerifyThresholdEdit(EditorViewModel model, Dx12Viewport viewport)
    {
        var window = System.Windows.Window.GetWindow(viewport);
        var panel = Descendants(window).OfType<Views.FilterPanel>().Single();
        var input = (System.Windows.Controls.TextBox)panel.FindName("ThresholdInput");
        var property = System.Windows.Controls.TextBox.TextProperty;
        var binding = input.GetBindingExpression(property)!;
        Require(binding.ParentBinding.UpdateSourceTrigger == System.Windows.Data.UpdateSourceTrigger.LostFocus, "numeric edits commit on leaving the field");
        var id = model.Session.State.Selection.Single();
        var original = model.Session.State.Rois[id].Filter.Threshold;
        foreach (var value in new[] { "2", "20", "200" }) input.SetCurrentValue(property, value);
        Require(model.Session.State.Rois[id].Filter.Threshold == original, "intermediate numeric input does not add edits");
        binding.UpdateSource();
        Require(model.Session.State.Rois[id].Filter.Threshold == 200, "completed numeric edit registers its final value");
        model.Session.Undo();
        Require(model.Session.State.Rois[id].Filter.Threshold == original, "one undo restores the whole numeric edit");
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
