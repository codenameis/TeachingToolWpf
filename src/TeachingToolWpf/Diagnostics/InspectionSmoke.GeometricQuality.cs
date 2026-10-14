using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static partial class InspectionSmoke
{
    private static async Task VerifyGeometricQualityAsync(Window window, Dx12Viewport viewport, MainWindowViewModel model,
        Func<string, Task> capture, Func<AsyncCommand, Task> invoke)
    {
        var matching = model.Matching;
        var paperModel = matching.Model;
        var teaching = model.Session.State.Rois.Values.Single(roi => roi.Geometry.Center.X < 250);
        model.Session.Select([teaching.Id]);
        matching.Method = PatternMatchingMethod.GeometricShape;
        matching.MinimumAngle = -30;
        matching.MaximumAngle = 30;
        matching.AngleStep = 5;
        matching.EnableScale = true;
        matching.MinimumScale = 1;
        matching.MaximumScale = 1.2;
        await invoke(matching.Train);
        Require(matching.Model?.Options.BuildGeometricModel == true && !ReferenceEquals(paperModel, matching.Model), "geometric mode trains an independent contour model");
        var panel = Descendants(window).OfType<ShapeMatchingPanel>().Single();
        var quality = Descendants(panel).OfType<Expander>().Single(item => Equals(item.Header, "기하 검증 · 원본 윤곽 확인"));
        var candidates = Descendants(panel).OfType<Expander>().Single(item => Equals(item.Header, "공통 검색 고급 · 후보 수와 중복"));
        quality.IsExpanded = true;
        candidates.IsExpanded = true;
        window.UpdateLayout();
        var budget = Descendants(panel).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "최대 검색 후보 수");
        var fit = Descendants(quality).OfType<CheckBox>().Single(item => Equals(item.Content, "위치 오차 제한"));
        var clutter = Descendants(quality).OfType<CheckBox>().Single(item => Equals(item.Content, "불필요 윤곽 제한"));
        matching.UseSelection = false;
        budget.SetCurrentValue(TextBox.TextProperty, "4096");
        budget.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        matching.MaximumVerifiedCandidates = 256;
        fit.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        clutter.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        matching.MaximumFitErrorPixels = 3;
        matching.MaximumClutterRatio = .5;
        matching.MinimumCoverage = .6;
        Require(matching.UseExpandedCandidateBudget && matching.LimitFitError && matching.LimitClutter, "geometry control bindings");

        var pending = invoke(matching.Search);
        Require(matching.IsBusy && !budget.IsEnabled && !fit.IsEnabled && !clutter.IsEnabled, "geometry inputs disabled during real search");
        await pending;
        Require(matching.Markers.Length == 2, "geometric gates preserve two real file poses: " + matching.Status);
        Require(matching.Results.Contains("윤곽 특징 검출비율") && matching.Results.Contains("px RMS") &&
            matching.Results.Contains("불필요 윤곽비율") && !matching.Results.Contains("미계산") &&
            !matching.Results.Contains("측정 불가"), "original-pixel geometry measurements displayed");
        Require(budget.IsEnabled && fit.IsEnabled && clutter.IsEnabled, "geometry inputs restored after search");
        Log("Geometric quality search: " + matching.Status + Environment.NewLine + matching.Results);

        quality.IsExpanded = false;
        candidates.IsExpanded = false;
        model.Fit.Execute(null);
        panel.BringIntoView();
        window.UpdateLayout();
        await capture("inspection-geometric-quality");
        WindowCapture.Save(window, viewport, "artifacts/inspection-geometric-quality.png", "artifacts/inspection-geometric-quality-app.png");
        Log("PASS: geometry WPF bindings, busy protection, original-pixel measurements, real two-pose search with explicit quality gates");
    }
}
