using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeachingToolWpf.Presentation;
using VisionCanvas.Filters.Wpf.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static class FilterControlSmokeScenario
{
    public static async Task Run(EditorViewModel model, FilterPanel panel)
    {
        var filters = model.Filters;
        var toggle = (ToggleButton)panel.FindName("PreviewToggle");
        var selection = model.Session.State.Selection;
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        await filters.WhenIdle;
        Require(!filters.IsPreviewEnabled && filters.Result is null, "toggle OFF removes output");
        model.Session.Select([]);
        model.Session.Select(selection);
        await filters.WhenIdle;
        Require(!filters.IsPreviewEnabled && filters.Result is null, "selection does not turn preview back on");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
        await filters.WhenIdle;
        Require(filters.Result is not null, "toggle ON restores registered filters");

        var radius = (TextBox)panel.FindName("RadiusInput");
        var slider = (ParameterSlider)panel.FindName("RadiusSlider");
        panel.BringIntoView();
        var before = model.Session.State;
        var initial = double.Parse(filters.Radius, System.Globalization.CultureInfo.CurrentCulture);
        RaiseKey(slider, Keyboard.PreviewKeyDownEvent, Key.Right);
        slider.SetCurrentValue(RangeBase.ValueProperty, initial + 1);
        slider.SetCurrentValue(RangeBase.ValueProperty, initial + 2);
        await filters.WhenIdle;
        Require(ReferenceEquals(before, model.Session.State), "slider previews without changing history");
        Require(filters.RadiusValue == initial + 2, "slider two-way parameter binding");
        RaiseKey(slider, Keyboard.PreviewKeyUpEvent, Key.Right);
        await filters.WhenIdle;
        Require(model.Session.State.Selection.All(id => model.Session.State.Rois[id].Filter.Radius == initial + 2), "gesture end registers final radius");
        model.Session.Undo();
        await filters.WhenIdle;
        Require(filters.RadiusValue == initial, "single Undo restores slider gesture");
        RaiseKey(slider, Keyboard.PreviewKeyDownEvent, Key.Right);
        slider.SetCurrentValue(RangeBase.ValueProperty, initial + 4);
        RaiseKey(slider, Keyboard.PreviewKeyDownEvent, Key.Escape);
        await filters.WhenIdle;
        Require(filters.RadiusValue == initial, "Escape restores draft");

        var unchanged = model.Session.State;
        radius.SetCurrentValue(TextBox.TextProperty, "1.5");
        radius.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Require(ReferenceEquals(unchanged, model.Session.State) && filters.ValidationMessage.Length > 0, "invalid integer is not coerced by slider");
        radius.SetCurrentValue(TextBox.TextProperty, initial.ToString(System.Globalization.CultureInfo.CurrentCulture));
        radius.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        await filters.WhenIdle;

        var frames = 0;
        void Published()
        {
            if (model.Session.IsPreviewing && filters.Result is not null)
            {
                frames++;
            }
        }
        filters.Changed += Published;
        var watch = Stopwatch.StartNew();
        var baseline = model.Session.State;
        model.Session.BeginPreview();
        try
        {
            for (var i = 1; i <= 40; i++)
            {
                model.Session.PreviewTranslation(new(i * .25, 0));
                await Task.Delay(25);
            }
            var duringMotion = frames;
            Require(duringMotion >= 2, "multiple completed filter frames before drag stops");
            await filters.WhenIdle;
            var preview = model.Session.TransformPreview!;
            Require(filters.Result!.Regions.All(region => region.Geometry == preview.Geometry(baseline.Rois[region.Id])), "latest pose eventually displayed");
            await File.WriteAllTextAsync("artifacts/filter-control-smoke.json", JsonSerializer.Serialize(new
            {
                Status = "PASS",
                CompletedFramesWhileMoving = duringMotion,
                DurationMilliseconds = watch.Elapsed.TotalMilliseconds,
                Mode = filters.Execution.ToString(),
                PreviewStatus = filters.Status,
                Note = "Programmatic continuous ROI input plus actual WPF routed key/toggle bindings; not a 60 FPS guarantee."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            filters.Changed -= Published;
            model.Session.CancelPreview();
            await filters.WhenIdle;
        }
        File.AppendAllText("artifacts/smoke.log", "WPF filter control toggle, slider draft/commit/Undo/Escape, invalid input and continuous moving preview passed" + Environment.NewLine);
    }

    private static void RaiseKey(UIElement target, RoutedEvent routedEvent, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
        {
            RoutedEvent = routedEvent
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Filter control smoke: " + message);
        }
    }
}
