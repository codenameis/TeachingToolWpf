using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionCanvas.Controls;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>패널 재배치의 실제 높이·숫자 오류·탭 수명과 Kensa 결과를 검사한다.</summary>
internal static class SidebarLayoutRun
{
    public static void Attach(Window window, Dx12Viewport viewport, MainWindowViewModel model)
    {
        window.Left = -10000;
        window.Top = -10000;
        window.ShowInTaskbar = false;
        window.Loaded += async (_, _) =>
        {
            var measurements = new List<object>();
            string? failure = null;
            try
            {
                var file = Path.GetFullPath("kensa.png");
                await model.Images.ReplaceAsync(new(file, "Kensa", 1024, 1536, ImageFileVersion.Read(file)));
                model.Session.Add(new(new(200.5, 184), 89, 52, 0));
                var matching = model.Matching;
                var panel = Descendants(window).OfType<ShapeMatchingPanel>().Single();
                var tabs = (TabControl)panel.FindName("MatchingTabs");
                var inspector = Descendants(window).OfType<InspectorPanel>().Single();
                var workspace = (TabControl)inspector.FindName("WorkspaceTabs");
                foreach (var size in new[] { new Size(1440, 880), new Size(1180, 680) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    foreach (var method in matching.Methods)
                    {
                        matching.Method = method.Method;
                        tabs.SelectedIndex = 0;
                        await Settle();
                        var button = (Button)panel.FindName("SearchButton");
                        var origin = button.TranslatePoint(new(), panel);
                        Require(origin.Y >= 0 && origin.Y + button.ActualHeight <= panel.ActualHeight + 1, "검사 버튼이 패널 안에 고정되어야 합니다.");
                        var scroll = (ScrollViewer)panel.FindName("BasicScroll");
                        measurements.Add(new
                        {
                            size.Width,
                            size.Height,
                            method.Method,
                            panel.ActualHeight,
                            scroll.ScrollableHeight,
                            ButtonY = origin.Y
                        });
                        if (size.Height == 880)
                        {
                            Require(scroll.ScrollableHeight <= 1, "기본 높이에서 기본 작업에 스크롤이 필요합니다: " + method.Name);
                        }
                    }
                }
                window.Width = 1440;
                window.Height = 880;
                matching.Method = PatternMatchingMethod.CpuDirectionalChamfer;
                matching.CpuMinimumAngle = -30;
                matching.CpuMaximumAngle = 30;
                matching.CpuSuppressionRadius = 31.2;
                await matching.Train.ExecuteAsync();
                Require(matching.HasModel, "Kensa 학습 실패: " + matching.Status);
                var roiId = model.Session.State.Selection.Single();
                var roi = model.Session.State.Rois[roiId].Geometry;
                model.Session.SetProperties(roiId, "CPU 비교", roi, new(83.5, 102), true);
                tabs.SelectedIndex = 1;
                await Settle();
                var advanced = Descendants(panel).OfType<Expander>().First(item => item.Header?.ToString() == "CPU · 배율과 정밀 검색");
                advanced.IsExpanded = true;
                await Settle();
                var input = Descendants(panel).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "검색 각도 간격 (0.25~30°)");
                input.SetCurrentValue(TextBox.TextProperty, "잘못된 숫자");
                await Settle();
                Require(matching.HasInputErrors && !matching.Search.CanExecute(null), "잘못된 상세 입력이 검사를 막아야 합니다.");
                tabs.SelectedIndex = 0;
                await Settle();
                Require(matching.HasInputErrors && !matching.Search.CanExecute(null), "탭 이동으로 숫자 오류가 사라지면 안 됩니다.");
                workspace.SelectedIndex = 1;
                await Settle();
                Require(matching.HasInputErrors && !matching.Search.CanExecute(null), "검사 탭이 숨겨진 동안에도 숫자 오류가 검사를 막아야 합니다.");
                workspace.SelectedIndex = 0;
                await Settle();
                Require(matching.HasInputErrors && !matching.Search.CanExecute(null), "영역 탭 왕복 후 숫자 오류를 유지해야 합니다.");
                tabs.SelectedIndex = 1;
                await Settle();
                input.SetCurrentValue(TextBox.TextProperty, "10");
                await Settle();
                Require(!matching.HasInputErrors && matching.Search.CanExecute(null), "숫자 수정 후 다시 검사할 수 있어야 합니다.");
                tabs.SelectedIndex = 0;
                await matching.Search.ExecuteAsync();
                Require(matching.Markers.Length == 4 && matching.ResultRows.Length == 4, "Kensa 네 부품과 결과 행이 일치해야 합니다.");
                await Settle();
                Require(((ScrollViewer)panel.FindName("BasicScroll")).ScrollableHeight <= 1, "검사 완료 후에도 기본 높이에서는 기본 입력이 한 화면에 있어야 합니다.");
                await Capture("basic");
                tabs.SelectedIndex = 2;
                await Capture("results");
                tabs.SelectedIndex = 0;
                matching.MaximumMatches = 1;
                await matching.Search.ExecuteAsync();
                Require(matching.Verdict == InspectionVerdict.Warning && matching.PanelHint.Contains("최대 결과 수"), "잘린 결과를 정상으로 바꾸지 않고 해결 방법을 표시해야 합니다.");
                await Capture("warning");
            }
            catch (Exception error)
            {
                failure = error.ToString();
                Environment.ExitCode = 1;
            }
            finally
            {
                Directory.CreateDirectory("artifacts");
                File.WriteAllText("artifacts/sidebar-layout.json", JsonSerializer.Serialize(new
                {
                    failure,
                    measurements
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }

            async Task Capture(string name)
            {
                await Settle();
                var capture = Path.GetFullPath($"artifacts/sidebar-{name}-viewport.png");
                var requested = DateTime.UtcNow;
                viewport.Capture(capture);
                var complete = false;
                for (var attempt = 0; attempt < 100 && !complete; attempt++)
                {
                    await Task.Delay(50);
                    if (!File.Exists(capture) || File.GetLastWriteTimeUtc(capture) < requested)
                    {
                        continue;
                    }
                    try
                    {
                        using var ready = File.Open(capture, FileMode.Open, FileAccess.Read, FileShare.Read);
                        complete = ready.Length > 0;
                    }
                    catch (IOException error) when ((error.HResult & 0xffff) == 32)
                    {
                        // 렌더 worker의 PNG 쓰기가 끝나면 다음 시도에서 읽을 수 있다.
                    }
                }
                Require(complete, "현재 장면 캡처가 완료되어야 합니다.");
                WindowCapture.Save(window, viewport, capture, $"artifacts/sidebar-{name}.png");
            }
        };
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
