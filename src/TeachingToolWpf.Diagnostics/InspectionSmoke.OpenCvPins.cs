using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyOpenCvPinLimitAsync(Window window, Dx12Viewport viewport, MainWindowViewModel model,
        Func<string, Task> capture, Func<AsyncCommand, Task> invoke)
    {
        var matching = model.Matching;
        var paperModel = matching.Model;
        // 사용자가 남겨둔 8K 샘플의 핀 ROI를 속성 패널 표시 정밀도로 재현한다.
        model.Session.Add(new(new(759.518, 546.178), 105.827, 129.459, 0));
        matching.Method = PatternMatchingMethod.OpenCvTemplate;
        Require(matching.MaximumMatches == 256, "OpenCV has an independent 256-result default");
        matching.MinimumScore = .72;
        matching.UseSelection = false;
        await VerifyNumericInputAsync(window, matching);
        await invoke(matching.Train);
        Require(matching.TemplateModel is { Width: 107, Height: 130 }, "single-pin teaching crop");
        var pinModel = matching.TemplateModel;
        matching.MaximumMatches = 16;
        await invoke(matching.Search);
        Require(matching.Markers.Length == 16 && matching.Verdict == InspectionVerdict.Warning &&
            matching.Results.Contains("결과") && matching.Results.Contains("생략"), "16-result cap reports omitted matches as warning");
        Log("OpenCV pin cap 16: " + matching.Status + Environment.NewLine + matching.VerdictText);
        await capture("opencv-pins-limited");
        matching.MaximumMatches = 256;
        await invoke(matching.Search);
        Require(ReferenceEquals(pinModel, matching.TemplateModel), "raising result limit does not retrain");
        Require(matching.Markers.Length == 256 && matching.Verdict == InspectionVerdict.Pass, "all 256 repeated pins found at exact output limit: " + matching.Status);
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var count = matching.Markers.Count(marker =>
                    (int)(marker.ReferencePoint.X / 2048) == column && (int)(marker.ReferencePoint.Y / 2048) == row);
                Require(count == 16, $"all 16 pins on chip {column},{row}: {count}");
            }
        }
        model.Fit.Execute(null);
        await capture("opencv-pins-complete");
        WindowCapture.Save(window, viewport, "artifacts/opencv-pins-complete.png", "artifacts/opencv-pins-complete-app.png");
        Log("PASS: one 107×130 template, output cap 16 WARNING → 256 PASS, all 16 chips / 256 pin positions; " + matching.Status);
        matching.Method = PatternMatchingMethod.Shape;
        Require(ReferenceEquals(paperModel, matching.Model) && matching.MaximumMatches == 256, "paper model and independent settings restored");
        await model.Undo.ExecuteAsync();
    }

    private static async Task VerifyNumericInputAsync(Window window, ShapeMatchingViewModel matching)
    {
        var score = Descendants(window).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "최소 최종 매칭 점수");
        await VerifyFractionInputAsync(score, "0.720");
        Require(matching.MinimumScore == .72, "fractional score reaches typed settings without losing the decimal separator");
        foreach (var invalid in new[] { "", "0..72", "NaN", "Infinity", "1.1" })
        {
            score.SetCurrentValue(TextBox.TextProperty, invalid);
            score.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Require(!matching.Train.CanExecute(null), "invalid score cannot train: " + invalid);
        }
        score.SetCurrentValue(TextBox.TextProperty, "0.72");
        score.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        var input = Descendants(window).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "최대 매칭 결과 수");
        var train = Descendants(window).OfType<Button>().Single(button => ReferenceEquals(button.Command, matching.Train));
        input.SetCurrentValue(TextBox.TextProperty, "abc");
        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Require(Validation.GetHasError(input) && !train.IsEnabled && !matching.Train.CanExecute(null), "invalid numeric text cannot train using an old value");
        input.SetCurrentValue(TextBox.TextProperty, "256");
        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Require(!Validation.GetHasError(input) && train.IsEnabled && matching.MaximumMatches == 256, "corrected numeric input restores command");
        input.SetCurrentValue(TextBox.TextProperty, "1001");
        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Require(!matching.Train.CanExecute(null), "out-of-range numeric option blocks execution");
        input.SetCurrentValue(TextBox.TextProperty, "256");
        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Require(matching.Train.CanExecute(null), "valid option restores training");
        input.SetCurrentValue(TextBox.TextProperty, "abc");
        input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        matching.Method = PatternMatchingMethod.Shape;
        await Dispatcher.Yield(DispatcherPriority.Background);
        Require(!matching.HasInputErrors && !Validation.GetHasError(input) && input.Text == "256", "mode switch discards invalid text and restores its own setting");
        matching.Method = PatternMatchingMethod.OpenCvTemplate;
        await Dispatcher.Yield(DispatcherPriority.Background);
        Require(matching.MaximumMatches == 256 && matching.Train.CanExecute(null), "OpenCV option survives invalid draft in another mode");
        Log("PASS: WPF character-by-character decimal entry, numeric parse/range guard, correction and mode switching");
    }

    private static async Task VerifyFractionInputAsync(TextBox input, string text)
    {
        input.SetCurrentValue(TextBox.TextProperty, "");
        var expected = "";
        foreach (var character in text)
        {
            input.CaretIndex = input.Text.Length;
            input.SelectedText = character.ToString();
            expected += character;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(input.Text == expected, $"fractional edit draft remains intact: expected '{expected}', actual '{input.Text}'");
        }
    }
}
