using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>실제 WPF 학습/검사 명령·원본 파일 reader·표시 경로를 통과하는 동일 조건 비교.</summary>
internal static class CpuKensaComparisonRun
{
    public static void Attach(Window window, Dx12Viewport viewport, MainWindowViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        window.Width = 1440;
        window.Height = 960;
        window.Loaded += async (_, _) =>
        {
            var rows = new List<object>();
            string? failure = null;
            try
            {
                var matching = model.Matching;
                foreach (var test in Cases())
                {
                    foreach (var method in new[] { PatternMatchingMethod.CpuLine2D, PatternMatchingMethod.CpuSubNcc, PatternMatchingMethod.CpuDirectionalChamfer })
                    {
                        await Load(test.TeachingFile, test.TeachingWidth, test.TeachingHeight);
                        model.Session.Add(test.Teaching);
                        matching.Method = method;
                        matching.UseSelection = true;
                        matching.MinimumScore = .75;
                        matching.MaximumMatches = 256;
                        matching.MaximumCandidates = 2048;
                        matching.CpuMinimumAngle = test.FullRotation ? -180 : -30;
                        matching.CpuMaximumAngle = test.FullRotation ? 180 : 30;
                        matching.CpuAngleStep = 10;
                        matching.CpuMinimumScale = 1;
                        matching.CpuMaximumScale = 1;
                        matching.CpuPyramidLevels = 3;
                        matching.CpuSuppressionRadius = Math.Min(test.Teaching.Width, test.Teaching.Height) * .6;
                        matching.SearchTimeoutSeconds = 60;
                        if (!matching.Train.CanExecute(null))
                        {
                            throw new InvalidOperationException(matching.OptionsError + " / " + matching.Status);
                        }
                        await matching.Train.ExecuteAsync();
                        if (!matching.HasModel)
                        {
                            throw new InvalidOperationException(matching.Status);
                        }
                        if (test.ImageFile != test.TeachingFile)
                        {
                            await Load(test.ImageFile, test.Width, test.Height);
                            model.Session.Add(new(new(test.Width / 2d, test.Height / 2d), test.Width, test.Height, 0));
                        }
                        else
                        {
                            var id = model.Session.State.Selection.Single();
                            model.Session.SetProperties(id, "CPU 비교", test.Teaching,
                                new((test.SearchWidth - test.Teaching.Width) / 2, (test.SearchHeight - test.Teaching.Height) / 2), true);
                        }
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var selector = Descendants(window).OfType<ComboBox>().Single(control =>
                            ReferenceEquals(control.ItemsSource, matching.Methods));
                        if (!Equals(selector.SelectedValue, method))
                        {
                            throw new InvalidOperationException("WPF 매칭 선택 바인딩 불일치");
                        }
                        for (var repeat = 0; repeat < 3; repeat++)
                        {
                            var started = Stopwatch.GetTimestamp();
                            await matching.Search.ExecuteAsync();
                            var commandMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            var log = matching.CpuInspectionLogs[^1];
                            if (log.Output is null || log.PresentationId is null)
                            {
                                throw new InvalidOperationException(matching.Status);
                            }
                            var wait = Stopwatch.StartNew();
                            while (matching.CpuInspectionLogs[^1].PresentedMilliseconds is null && wait.Elapsed.TotalSeconds < 10)
                            {
                                await Task.Delay(10);
                            }
                            log = matching.CpuInspectionLogs[^1];
                            await ObserveWpfRenderAsync(window);
                            var displayedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            var nearest = log.Output!.Result.Matches.OrderBy(match =>
                                Math.Pow(match.X - test.Expected.X, 2) + Math.Pow(match.Y - test.Expected.Y, 2)).FirstOrDefault();
                            double? positionError = test.ExpectedCount == 0 ? null : Math.Sqrt(Math.Pow(nearest.X - test.Expected.X, 2) + Math.Pow(nearest.Y - test.Expected.Y, 2));
                            double? angleError = test.ExpectedCount == 0 ? null : CpuMatching.Core.PyramidSearch.AngleDistance(nearest.AngleDegrees, test.Angle);
                            var expectedCount = test.ExpectedCount ?? (test.CompleteTruth ? 1 : (int?)null);
                            var posePassed = expectedCount == 0 || (log.Output.Result.Matches.Count > 0 && positionError <= 1 && angleError <= 1);
                            var accuracyPassed = posePassed && (expectedCount is null || log.Output.Result.Matches.Count == expectedCount) &&
                                !log.Output.Result.CandidatesTruncated && !log.Output.Result.MatchLimitExceeded;
                            if (!accuracyPassed || log.PresentedMilliseconds is null)
                            {
                                Environment.ExitCode = 1;
                            }
                            rows.Add(new
                            {
                                test.Name,
                                method = method.ToString(),
                                repeat,
                                test.CompleteTruth,
                                expectedCount,
                                test.Expected,
                                expectedAngle = test.Angle,
                                positionError,
                                angleError,
                                accuracyPassed,
                                commandMs,
                                displayedMs,
                                log,
                                displayConfirmed = log.PresentedMilliseconds is not null,
                                matching.Status
                            });
                            Save();
                            if (repeat == 2)
                            {
                                viewport.Capture(Path.GetFullPath($"artifacts/cpu-wpf-{test.Name}-{method}.png"));
                                await Task.Delay(150);
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                failure = error.ToString();
                Environment.ExitCode = 1;
            }
            finally
            {
                Save();
                window.Close();
            }

            async Task Load(string file, int width, int height)
            {
                var path = Path.GetFullPath(file);
                await model.Images.ReplaceAsync(new(path, file, width, height, ImageFileVersion.Read(path)));
            }

            void Save()
            {
                Directory.CreateDirectory("artifacts");
                File.WriteAllText("artifacts/cpu-wpf-kensa.json", JsonSerializer.Serialize(new
                {
                    created = DateTimeOffset.Now,
                    renderer = model.GpuStatus,
                    failure,
                    rows,
                    measurement = "실제 WPF 명령 진입→원본 읽기/검색/결과 게시/Present fence 및 다음 WPF Rendering 관찰. offscreen 창, 물리 scan-out·입력 메시지 대기 제외. repeat 0은 첫 실행, 1~2 반복. 원본 Kensa는 알려진 대상만 확인하며 전수 정답 아님."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        };
    }

    private sealed record Case(string Name, string TeachingFile, int TeachingWidth, int TeachingHeight, RoiGeometry Teaching,
        string ImageFile, int Width, int Height, int SearchWidth, int SearchHeight, PointD Expected, double Angle, bool CompleteTruth, bool FullRotation = false, int? ExpectedCount = null);

    private static IEnumerable<Case> Cases()
    {
        var text = new RoiGeometry(new(194.5, 371), 19, 12, 0);
        var part = new RoiGeometry(new(200.5, 184), 89, 52, 0);
        yield return new("kensa-18v-original", "kensa.png", 1024, 1536, text, "kensa.png", 1024, 1536, 120, 100, text.Center, 0, false);
        yield return new("kensa-component-original", "kensa.png", 1024, 1536, part, "kensa.png", 1024, 1536, 256, 256, part.Center, 0, false, ExpectedCount: 4);
        yield return new("kensa-other-components", "kensa.png", 1024, 1536, part, "samples/CpuMatching.Accuracy/kensa-other-components.png",
            220, 100, 220, 100, new(0, 0), 0, true, ExpectedCount: 0);
        yield return new("kensa-18v-rotation", "kensa.png", 1024, 1536, text, "samples/CpuMatching.Data/kensa-18v-rotation.png", 128, 128, 128, 128, new(65.75, 64.25), -12, true);
        yield return new("kensa-component-rotation", "kensa.png", 1024, 1536, part, "samples/CpuMatching.Data/kensa-component-rotation.png", 256, 256, 256, 256, new(139.75, 129.25), 14, true);
        yield return new("synthetic-179-degrees", "samples/CpuMatching.Data/part-template.png", 48, 48, new(new(24, 24), 48, 48, 0),
            "samples/CpuMatching.Data/local-upside-down.png", 256, 256, 256, 256, new(125.75, 138.25), 179, true, true);
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

    private static async Task ObserveWpfRenderAsync(Window window)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => rendered.TrySetResult();
        CompositionTarget.Rendering += handler;
        try
        {
            window.InvalidateVisual();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }
    }
}
