using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Views;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyExecutionControlsAsync(Window window, Dx12Viewport viewport, EditorViewModel model,
        Func<string, Task> capture)
    {
        var matching = model.Matching;
        var panel = Descendants(window).OfType<ShapeMatchingPanel>().Single();
        var selector = Descendants(panel).OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "검사 실행 장치");
        selector.SetCurrentValue(Selector.SelectedValueProperty, InspectionComputeMode.GpuPreprocessing);
        Require(matching.ComputeMode == InspectionComputeMode.GpuPreprocessing, "GPU selector binding");
        var run = matching.Search.ExecuteAsync();
        Require(!selector.IsEnabled && !model.Inspections.IsIdle, "execution settings locked during GPU search");
        await run;
        Require(matching.Markers.Length == 256 && matching.Results.Contains("CUDA") &&
            matching.Results.Contains("NVIDIA") && matching.Results.Contains("추가 윤곽"), "GPU real 8K paper search: " + matching.Status);
        Log("GPU execution controls: " + matching.Status + "\n" + matching.Results.Split("\n\n")[^1]);
        await capture("inspection-gpu-pins");
        WindowCapture.Save(window, viewport, "artifacts/inspection-gpu-pins.png", "artifacts/inspection-gpu-pins-app.png");
        selector.SetCurrentValue(Selector.SelectedValueProperty, InspectionComputeMode.Cpu);
    }

    private static async Task VerifyPrimitiveControlsAsync(Window window, Dx12Viewport viewport, EditorViewModel model,
        Func<string, Task> capture)
    {
        var originalImage = model.Session.State.Image;
        await model.PrimitiveSample.ExecuteAsync();
        Require(model.Inspections.Selected == InspectionToolKind.PrimitiveShapes && model.Session.State.Rois.Count == 0,
            "shape sample uses atomic image replacement and selected tool");
        var shapes = model.Inspections.Shapes;
        window.UpdateLayout();
        var panel = Descendants(window).OfType<PrimitiveShapeDetectionPanel>().Single();
        var advanced = Descendants(panel).OfType<Expander>().Single(expander => Equals(expander.Header, "도형 검출 고급 설정"));
        advanced.SetCurrentValue(Expander.IsExpandedProperty, true);
        window.UpdateLayout();
        var score = Descendants(panel).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "최소 도형 점수");
        var originalScore = shapes.MinimumScore;
        await VerifyFractionInputAsync(score, "0.850");
        Require(shapes.MinimumScore == .85, "primitive fractional score binding");
        shapes.MinimumScore = originalScore;
        advanced.SetCurrentValue(Expander.IsExpandedProperty, false);
        var detect = Descendants(panel).OfType<Button>().Single(button => ReferenceEquals(button.Command, shapes.Detect));
        Require(detect.IsEnabled && !model.Matching.Search.CanExecute(null), "only selected shape tool can execute");
        var history = model.Session.HistoryBytes;
        await ((AsyncCommand)detect.Command).ExecuteAsync();
        Require(shapes.LastOutput is { Incomplete: false, Shapes.Length: 4 }, "four real shape detections: " + shapes.Status);
        Require(shapes.LastOutput!.Shapes.Select(shape => shape.Kind).Distinct().Count() == 4, "triangle rectangle circle cross each found");
        Require(shapes.Overlay!.Matches.All(marker => marker.Bounds.Length >= 3), "actual closed contours rendered");
        Require(history == model.Session.HistoryBytes, "shape detection preserves editing history");
        Log("Primitive controls: " + shapes.Status + "\n" + shapes.Results);
        model.Fit.Execute(null);
        await capture("inspection-primitives");
        WindowCapture.Save(window, viewport, "artifacts/inspection-primitives.png", "artifacts/inspection-primitives-app.png");
        await model.Undo.ExecuteAsync();
        Require(model.Session.State.Image == originalImage && shapes.LastOutput is null, "sample undo restores source and invalidates detections");
        await model.Redo.ExecuteAsync();
        await shapes.Detect.ExecuteAsync();
        Require(shapes.LastOutput?.Shapes.Length == 4, "redo source can be detected again");
        Log("PASS: GPU selection/busy protection, primitive sample/4 kinds/actual contours/history/source invalidation");
    }
}
