using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using IndustrialVision.Filters.Gpu;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Diagnostics;

internal static class IndustrialFilterSmokeScenario
{
    public static async Task Run(MainWindowViewModel model, Dx12Viewport viewport)
    {
        var filters = model.Filters;
        filters.SelectedFilter = RoiFilterKind.GaussianBlackHatThreshold;
        filters.Radius = "2";
        filters.Sigma = "1";
        filters.Threshold = "5";
        filters.Execution = RoiFilterExecution.CpuOnly;
        filters.Preview.Execute(null);
        await filters.WhenIdle;
        var cpu = filters.Result ?? throw new InvalidOperationException(filters.Status);
        Require(cpu.Regions.Count > 0 && cpu.Regions.All(region => region.Execution?.Stages.Count == 3), "CPU recipe stages");

        var window = Window.GetWindow(viewport);
        var panel = Descendants(window).OfType<VisionCanvas.Filters.Wpf.Controls.FilterPanel>().Single();
        var radius = (TextBox)panel.FindName("RadiusInput");
        var binding = radius.GetBindingExpression(TextBox.TextProperty)!;
        Require(binding.ParentBinding.UpdateSourceTrigger == UpdateSourceTrigger.LostFocus, "radius edit commits on focus loss");
        radius.SetCurrentValue(TextBox.TextProperty, "4");
        Require(model.Session.State.Selection.All(id => model.Session.State.Rois[id].Filter.Radius == 2), "draft radius is not registered yet");
        binding.UpdateSource();
        Require(model.Session.State.Selection.All(id => model.Session.State.Rois[id].Filter.Radius == 4), "radius binding registers settings");
        model.Session.Undo();
        await filters.WhenIdle;
        Require(filters.Radius == "2", "one undo restores radius");

        var smoke = await Task.Run(CudaSmoke.Run);
        filters.Execution = RoiFilterExecution.CpuGpu;
        await filters.WhenIdle;
        var cuda = filters.Result ?? throw new InvalidOperationException(filters.Status);
        Require(cpu.Regions.Count == cuda.Regions.Count, "same ROI count");
        foreach (var expected in cpu.Regions)
        {
            var actual = cuda.Regions.Single(region => region.Id == expected.Id);
            Require(expected.Gray8.SequenceEqual(actual.Gray8), "CPU/CUDA final masks match");
            Require(actual.Execution is not null, "diagnostics returned");
            if (smoke.Status == "PASS")
            {
                Require(actual.Execution!.Stages.All(stage => stage.Device != "CPU"), "actual CUDA stages");
                Require(actual.Execution.UploadBytes == actual.Width * (long)actual.Height * 4 &&
                    actual.Execution.DownloadBytes == actual.Execution.UploadBytes, "one upload and download for three stages");
            }
            else
            {
                Require(actual.Execution!.Stages.All(stage => stage.Device == "CPU" && stage.Reason.Length > 0), "GPU unavailable reported");
            }
        }
        Directory.CreateDirectory("artifacts");
        await File.WriteAllTextAsync("artifacts/industrial-filter-smoke.json", JsonSerializer.Serialize(new
        {
            Smoke = smoke,
            CpuMilliseconds = cpu.Milliseconds,
            CpuGpuMilliseconds = cuda.Milliseconds,
            Status = filters.Status,
            Regions = cuda.Regions.Select(region => new { region.Width, region.Height, region.Execution, region.PreparationMilliseconds })
        }, new JsonSerializerOptions { WriteIndented = true }));
        await FilterControlSmokeScenario.Run(model, panel);
        panel.BringIntoView(new Rect(0, 0, panel.ActualWidth, 120));
        await Task.Delay(500);
        viewport.Capture("artifacts/industrial-filter-viewport.png");
        await Task.Delay(500);
        WindowCapture.Save(window, viewport, "artifacts/industrial-filter-viewport.png", "artifacts/industrial-filter-app.png");
        File.AppendAllText("artifacts/smoke.log", "IndustrialVision ROI CPU/CUDA recipe, native parameter bindings, Undo and mask equality passed; " + smoke.Device + Environment.NewLine);
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException("Industrial filter smoke: " + message);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
