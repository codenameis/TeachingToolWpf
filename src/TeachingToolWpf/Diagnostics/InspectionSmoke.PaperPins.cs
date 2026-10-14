using System.Windows;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyPaperPinLimitAsync(Window window, Dx12Viewport viewport, MainWindowViewModel model,
        Func<string, Task> capture, Func<AsyncCommand, Task> invoke)
    {
        var matching = model.Matching;
        Require(matching.Method == PatternMatchingMethod.Shape && matching.MaximumMatches == 256,
            "paper mode has an independent 256-result default");
        var templateModel = matching.TemplateModel;
        model.Session.Add(new(new(759.518, 546.178), 105.827, 129.459, 0));
        matching.MinimumScore = .72;
        matching.UseSelection = false;
        await invoke(matching.Train);
        Require(matching.Model?.Options.BuildGeometricModel == false, "single-pin paper shape model");
        var pinModel = matching.Model;

        matching.MaximumMatches = 16;
        await invoke(matching.Search);
        Require(matching.Markers.Length == 16 && matching.Verdict == InspectionVerdict.Warning &&
            matching.Results.Contains("결과") && matching.Results.Contains("생략"),
            "paper 16-result cap reports omitted matches as warning: " + matching.Status);
        Log("Paper pin cap 16: " + matching.Status + Environment.NewLine + matching.VerdictText);
        await capture("paper-pins-limited");

        matching.MaximumMatches = 256;
        await invoke(matching.Search);
        Require(ReferenceEquals(pinModel, matching.Model), "raising paper result limit does not retrain");
        Require(matching.Markers.Length == 256 && matching.Verdict is InspectionVerdict.Pass or InspectionVerdict.Review,
            "all 256 repeated pins found without incomplete-search warning: " + matching.Status);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var count = matching.Markers.Count(marker =>
                    (int)(marker.ReferencePoint.X / 2048) == column && (int)(marker.ReferencePoint.Y / 2048) == row);
                Require(count == 16, $"paper all 16 pins on chip {column},{row}: {count}");
            }
        }
        Require(!matching.Results.Contains("윤곽 특징 검출비율"), "paper results do not claim geometric quality measurement");
        Require(ReferenceEquals(templateModel, matching.TemplateModel), "paper training preserves OpenCV pin model");
        model.Fit.Execute(null);
        await capture("paper-pins-complete");
        WindowCapture.Save(window, viewport, "artifacts/paper-pins-complete.png", "artifacts/paper-pins-complete-app.png");
        Log("PASS: paper single-pin teaching, output cap 16 WARNING → 256 complete, all 16 chips / 256 pin positions; " +
            matching.Status + Environment.NewLine + matching.VerdictText);
        await VerifyExecutionControlsAsync(window, viewport, model, capture);
        await model.Undo.ExecuteAsync();
    }
}
