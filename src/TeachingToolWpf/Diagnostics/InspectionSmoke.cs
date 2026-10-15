using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionCanvas.Controls;
using VisionCanvas.Rendering;
using VisionImaging;

namespace TeachingToolWpf.Diagnostics;

/// <summary>실제 파일 reader→학습/검사→WPF 바인딩→DX12 캡처를 검증한다.</summary>
internal static partial class InspectionSmoke
{
    public static void Attach(Window window, Dx12Viewport viewport, MainWindowViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        window.Width = 1440;
        window.Height = 960;
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/inspection-smoke.log", "");
        var renderErrors = new List<string>();
        viewport.RendererStatusChanged += status =>
        {
            Log(status);
            if (status.StartsWith("렌더 오류:") || status.StartsWith("D3D12 "))
            {
                renderErrors.Add(status);
            }
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await VerifyStartupAsync(window, viewport, model, Capture);
                model.Session.Add(new(new(160, 160), 80, 80, 0));
                var matching = model.Matching;
                matching.MinimumAngle = -30;
                matching.MaximumAngle = 30;
                matching.AngleStep = 5;
                matching.EnableScale = true;
                matching.MinimumScale = 1;
                matching.MaximumScale = 1.2;
                var panel = Descendants(window).OfType<ShapeMatchingPanel>().Single();
                Require(ReferenceEquals(panel.DataContext, matching), "matching panel binding");
                await Invoke(matching.Train);
                Require(matching.Model is not null, "real file training: " + matching.Status);
                await Invoke(matching.Search);
                Log(matching.Status + Environment.NewLine + matching.Results);
                Require(matching.Markers.Length == 2, "real two-pattern search");
                Require(matching.Markers.Any(marker => (marker.ReferencePoint - new VisionGeometry.PointD(420, 230)).Length < .6), "source coordinate pose");
                var result = matching.Overlay!;
                var originalState = model.Session.State;
                var historyBytes = model.Session.HistoryBytes;
                await Capture("inspection-features-off");
                var checkbox = Descendants(panel).OfType<CheckBox>().Single(box => Equals(box.Content, "특징점 표시"));
                checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Require(matching.ShowFeatures && matching.Overlay!.ShowFeatures, "feature checkbox binding");
                await Capture("inspection-features-on");
                var off = PixelCounts("artifacts/inspection-features-off.png", result.Color);
                var on = PixelCounts("artifacts/inspection-features-on.png", result.Color);
                Require(on > off + 50, $"visible feature pixels: off={off} on={on}");
                Require(ReferenceEquals(originalState, model.Session.State) && historyBytes == model.Session.HistoryBytes, "display options preserve document/history");
                model.Session.Select([]);
                Require(matching.Markers.Length == 2, "selection change preserves results");
                var locate = Descendants(panel).OfType<Button>().Single(button => ReferenceEquals(button.Command, matching.Locate));
                Require(locate.IsEnabled, "locate button enabled");
                locate.Command.Execute(null);
                Require(matching.Markers.Length == 2 && historyBytes == model.Session.HistoryBytes, "locate changes only viewport");
                panel.BringIntoView();
                window.UpdateLayout();
                await Capture("inspection-located");
                WindowCapture.Save(window, viewport, "artifacts/inspection-located.png", "artifacts/inspection-app.png");

                // 네 가지 지정색은 동일한 실제 검출 윤곽을 이용해 GPU 출력까지 개별 확인한다.
                foreach (var verdict in Enum.GetValues<InspectionVerdict>())
                {
                    var color = InspectionResultPresentation.Color(verdict);
                    var displayed = result with
                    {
                        Summary = InspectionResultPresentation.Name(verdict) + " · 표시 검증",
                        Color = color,
                        ShowFeatures = true,
                        Matches = result.Matches.Select(marker => marker with { Color = color }).ToImmutableArray()
                    };
                    viewport.Submit(new(model.Session.State, model.Images.Image, model.Images.Generation, viewport.ViewWidth, viewport.ViewHeight,
                        viewport.Dpi, model.Input.Overlay, Inspection: displayed));
                    var name = "inspection-color-" + verdict;
                    await Capture(name);
                    var count = PixelCounts("artifacts/" + name + ".png", color);
                    Require(count > 200, $"GPU {verdict} pixels: {count}");
                    Log($"GPU {verdict} {InspectionResultPresentation.Hex(verdict)}: {count} pixels");
                }

                model.Session.Select([model.Session.State.Rois.Keys.Single()]);
                var methodSelector = Descendants(panel).OfType<ComboBox>().Single(box =>
                    System.Windows.Automation.AutomationProperties.GetName(box) == "매칭 방식");
                Require(methodSelector.Items.Count == 3, "paper, OpenCV and PoseMatch-TDCM pattern modes");
                Require(!Descendants(panel).OfType<Expander>().Any(item => item.Header?.ToString()?.Contains("기하 검증") == true), "removed geometric quality controls are absent");
                methodSelector.SetCurrentValue(Selector.SelectedValueProperty, PatternMatchingMethod.OpenCvTemplate);
                Require(matching.Method == PatternMatchingMethod.OpenCvTemplate && !checkbox.IsEnabled, "OpenCV method selector disables unsupported features");
                matching.MinimumScore = .98;
                await Invoke(matching.Train);
                await Invoke(matching.Search);
                Require(matching.TemplateModel is not null && matching.Verdict == InspectionVerdict.Pass && matching.Markers.Length == 1, "OpenCV real template search");
                Require((matching.Markers[0].ReferencePoint - new VisionGeometry.PointD(160, 160)).Length < .01, "OpenCV source reference point");
                Log(matching.Status);
                await Capture("inspection-opencv");
                WindowCapture.Save(window, viewport, "artifacts/inspection-opencv.png", "artifacts/inspection-opencv-app.png");
                methodSelector.SetCurrentValue(Selector.SelectedValueProperty, PatternMatchingMethod.PoseMatchTdcm);
                await Invoke(matching.Train);
                Require(matching.HasModel && matching.ModelSummary.Contains("PoseMatch-TDCM"), "TDCM original ROI training");
                await Invoke(matching.Search);
                Require(matching.Verdict is InspectionVerdict.Review or InspectionVerdict.Warning, "TDCM real ONNX inference completes without pretending quality OK");
                Require(matching.Status.Contains("추론/후처리"), "TDCM measured inference timing");
                Log(matching.Status);
                await Capture("inspection-tdcm");
                methodSelector.SetCurrentValue(Selector.SelectedValueProperty, PatternMatchingMethod.Shape);
                matching.MinimumScore = .72;
                Require(matching.Model is not null, "switching methods preserves shape model");
                model.Session.Add(new(new(65, 330), 80, 80, 0));
                matching.UseSelection = true;
                await Invoke(matching.Search);
                Require(matching.Verdict == InspectionVerdict.Fail && matching.Markers.IsEmpty, "real empty region yields NG without fabricated position");
                model.Fit.Execute(null);
                await Capture("inspection-no-match");
                Require(PixelCounts("artifacts/inspection-no-match.png", InspectionResultPresentation.Color(InspectionVerdict.Fail)) > 100, "NG search boundary/banner pixels");
                await VerifyBoundariesAsync(window, viewport, model, Capture, Invoke);
                await VerifyPrimitiveControlsAsync(window, viewport, model, Capture);
                Require(renderErrors.Count == 0, string.Join(Environment.NewLine, renderErrors));
                Log($"PASS: file training/search, 2 poses, feature toggle ({off}→{on}), selection, navigation, four GPU colors, actual no-match, no renderer errors");

                async Task Invoke(AsyncCommand command)
                {
                    var button = Descendants(panel).OfType<Button>().Single(item => ReferenceEquals(item.Command, command));
                    Require(button.IsEnabled, "visible command enabled");
                    button.Command.Execute(null);
                    var timer = Stopwatch.StartNew();
                    while (!matching.IsIdle)
                    {
                        Require(timer.Elapsed < TimeSpan.FromSeconds(40), "inspection command timeout");
                        await Task.Delay(50);
                    }
                }
            }
            catch (Exception error)
            {
                Log(error.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };

        async Task Capture(string name)
        {
            await Task.Delay(350);
            var path = "artifacts/" + name + ".png";
            viewport.Capture(path);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                Require(timer.Elapsed < TimeSpan.FromSeconds(10), "capture timeout: " + name);
                await Task.Delay(50);
            }
            await Task.Delay(100);
        }
    }

    private static int PixelCounts(string path, System.Numerics.Vector4 color)
    {
        using var stream = File.OpenRead(path);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (Math.Abs(pixels[index] - (color.Z * 255)) < 2 && Math.Abs(pixels[index + 1] - (color.Y * 255)) < 2 &&
                Math.Abs(pixels[index + 2] - (color.X * 255)) < 2)
            {
                count++;
            }
        }
        return count;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Inspection smoke: " + message);
        }
    }

    private static void Log(string message) => File.AppendAllText("artifacts/inspection-smoke.log", message + Environment.NewLine);
}
