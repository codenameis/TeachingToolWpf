using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Controls;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>실제 WPF 학습/검사 명령·원본 파일 reader·표시 경로를 통과하는 동일 조건 비교.</summary>
internal static class CpuKensaComparisonRun
{
    public static void Attach(Window window, Dx12Viewport viewport, MainWindowViewModel model, bool compareCuda = false,
        bool fullImage = false, bool repeatedModules = false)
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
                var arguments = Environment.GetCommandLineArgs();
                var methodArgument = arguments.FirstOrDefault(value => value.StartsWith("--cpu-method=", StringComparison.Ordinal));
                var repeatArgument = arguments.FirstOrDefault(value => value.StartsWith("--cpu-repeats=", StringComparison.Ordinal));
                var repeats = repeatArgument is null ? 3 : int.Parse(repeatArgument.Split('=', 2)[1]);
                if (repeats is < 1 or > 10)
                {
                    throw new ArgumentOutOfRangeException(nameof(repeats));
                }
                var methods = new[] { PatternMatchingMethod.CpuLine2D, PatternMatchingMethod.CpuSubNcc, PatternMatchingMethod.CpuDirectionalChamfer };
                if (methodArgument is not null)
                {
                    methods = methods.Where(method => method.ToString() == methodArgument.Split('=', 2)[1]).ToArray();
                    if (methods.Length == 0)
                    {
                        throw new ArgumentException("Unknown CPU comparison method.");
                    }
                }
                var cases = (repeatedModules ? ModuleCases() : fullImage ? FullImageCases() : Cases()).ToArray();
                var caseArgument = arguments.FirstOrDefault(value => value.StartsWith("--cpu-case=", StringComparison.Ordinal));
                if (caseArgument is not null)
                {
                    cases = cases.Where(test => test.Name == caseArgument.Split('=', 2)[1]).ToArray();
                    if (cases.Length == 0)
                    {
                        throw new ArgumentException("Unknown CPU comparison case.");
                    }
                }
                foreach (var test in cases)
                {
                    foreach (var method in methods)
                    {
                        foreach (var useCuda in compareCuda ? new[] { false, true } : new[] { false })
                        {
                            await Load(test.TeachingFile, test.TeachingWidth, test.TeachingHeight);
                            model.Session.Add(test.Teaching);
                            matching.Method = method;
                            matching.ComputeMode = useCuda ? InspectionComputeMode.GpuPreprocessing : InspectionComputeMode.Cpu;
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
                            matching.SearchTimeoutSeconds = fullImage ? 600 : 60;
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
                                model.Session.Add(test.SearchRegion ?? new(new(test.Width / 2d, test.Height / 2d), test.Width, test.Height, 0));
                            }
                            else
                            {
                                var id = model.Session.State.Selection.Single();
                                model.Session.SetProperties(id, "CPU 비교", test.Teaching,
                                    new((test.SearchWidth - test.Teaching.Width) / 2, (test.SearchHeight - test.Teaching.Height) / 2), true);
                            }
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            if (fullImage)
                            {
                                matching.UseSelection = false;
                            }
                            var selector = Descendants(window).OfType<ComboBox>().Single(control =>
                                ReferenceEquals(control.ItemsSource, matching.Methods));
                            if (!Equals(selector.SelectedValue, method))
                            {
                                throw new InvalidOperationException("WPF 매칭 선택 바인딩 불일치");
                            }
                            if (compareCuda)
                            {
                                var panel = Descendants(window).OfType<ShapeMatchingPanel>().Single();
                                var tabs = (TabControl)panel.FindName("MatchingTabs");
                                tabs.SelectedIndex = 1;
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                Descendants(panel).OfType<Expander>().Single(control => control.Header?.ToString() == "후보 예산 · 실행 제어").IsExpanded = true;
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                var compute = Descendants(panel).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "템플릿 검사 실행 장치");
                                compute.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                                    useCuda ? InspectionComputeMode.Cpu : InspectionComputeMode.GpuPreprocessing);
                                compute.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                                    useCuda ? InspectionComputeMode.GpuPreprocessing : InspectionComputeMode.Cpu);
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                if (matching.ComputeMode == InspectionComputeMode.GpuPreprocessing != useCuda || !compute.IsVisible)
                                {
                                    throw new InvalidOperationException("CPU/CUDA 실행 장치 선택 바인딩 불일치");
                                }
                                Descendants(panel).OfType<Expander>().Single(control => control.Header?.ToString() == "알고리즘별 설정 · 변경 후 다시 학습").IsExpanded = true;
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                var parameterName = matching.IsCpuSubNcc ? "Sub-NCC 축소 표본 수" : "축소 특징 수";
                                var parameter = Descendants(panel).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == parameterName);
                                parameter.SetCurrentValue(TextBox.TextProperty, matching.IsCpuSubNcc ? "128" : "80");
                                parameter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                                if (!matching.IsTrainingDirty || matching.Search.CanExecute(null) || !parameter.IsVisible)
                                {
                                    throw new InvalidOperationException("알고리즘 파라미터/재학습 상태 바인딩 불일치");
                                }
                                parameter.SetCurrentValue(TextBox.TextProperty, matching.IsCpuSubNcc ? "96" : "64");
                                parameter.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                                if (matching.IsTrainingDirty || matching.OptionsError.Length > 0)
                                {
                                    throw new InvalidOperationException("알고리즘 기본값 복원 실패");
                                }
                                tabs.SelectedIndex = 0;
                            }
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            // 측정기는 일반 UI 우선순위로 실행하고 실제 버튼의 Automation Invoke를 사용한다.
                            await window.Dispatcher.InvokeAsync(async () =>
                            {
                                for (var repeat = 0; repeat < repeats; repeat++)
                                {
                                    var started = Stopwatch.GetTimestamp();
                                    await InvokeSearchButtonAsync(window, matching);
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
                                    var maximumPositionError = positionError;
                                    var maximumAngleError = angleError;
                                    if (test.ExpectedMatches is { } expectedMatches)
                                    {
                                        var errors = expectedMatches.Select(expected =>
                                        {
                                            var found = log.Output.Result.Matches.Count == 0 ? default : log.Output.Result.Matches.MinBy(found =>
                                                Math.Pow(found.X - expected.X, 2) + Math.Pow(found.Y - expected.Y, 2));
                                            return (
                                                Position: Math.Sqrt(Math.Pow(found.X - expected.X, 2) + Math.Pow(found.Y - expected.Y, 2)),
                                                Angle: CpuMatching.Core.PyramidSearch.AngleDistance(found.AngleDegrees, expected.AngleDegrees),
                                                Scale: Math.Abs(found.Scale - expected.Scale));
                                        }).ToArray();
                                        maximumPositionError = errors.Max(error => error.Position);
                                        maximumAngleError = errors.Max(error => error.Angle);
                                        accuracyPassed &= errors.All(error => error.Position <= 1 && error.Angle <= 1 && error.Scale <= .03);
                                    }
                                    if (!accuracyPassed || log.PresentedMilliseconds is null)
                                    {
                                        Environment.ExitCode = 1;
                                    }
                                    rows.Add(new
                                    {
                                        test.Name,
                                        method = method.ToString(),
                                        useCuda,
                                        repeat,
                                        test.CompleteTruth,
                                        referenceKind = test.Name == "kensa-full-modules" ? "Independent OpenCV ECC reference; not physical metrology" :
                                            test.CompleteTruth ? "Generator-defined truth" : "Known targets only",
                                        expectedCount,
                                        test.Expected,
                                        expectedAngle = test.Angle,
                                        positionError,
                                        angleError,
                                        maximumPositionError,
                                        maximumAngleError,
                                        accuracyPassed,
                                        commandMs,
                                        displayedMs,
                                        log,
                                        displayConfirmed = log.PresentedMilliseconds is not null,
                                        matching.Status
                                    });
                                    Save();
                                    if (repeat == repeats - 1)
                                    {
                                        viewport.Capture(Path.GetFullPath($"artifacts/cpu-wpf-{test.Name}-{method}-{(useCuda ? "cuda" : "cpu")}.png"));
                                        await Task.Delay(150);
                                    }
                                }
                            }, DispatcherPriority.Normal).Task.Unwrap();
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
                if (file == "sample:8192:8192")
                {
                    await model.InitializeAsync();
                    return;
                }
                var path = Path.GetFullPath(file);
                await model.Images.ReplaceAsync(new(path, file, width, height, ImageFileVersion.Read(path)));
            }

            void Save()
            {
                Directory.CreateDirectory("artifacts");
                File.WriteAllText(repeatedModules ? "artifacts/cpu-modules-wpf.json" : fullImage ? "artifacts/cpu-full8k-wpf.json" :
                    compareCuda ? "artifacts/cpu-cuda-wpf.json" : "artifacts/cpu-wpf-kensa.json", JsonSerializer.Serialize(new
                    {
                        created = DateTimeOffset.Now,
                        renderer = model.GpuStatus,
                        failure,
                        rows,
                        measurement = "실제 검사 버튼 Automation Invoke→원본 읽기/검색/결과 게시/Present fence 및 다음 WPF Rendering 관찰. offscreen 창, 물리 scan-out·하드웨어 입력 메시지 대기 제외. repeat 0은 첫 실행. 원본 Kensa 전체는 독립 ECC 정합 기준, 그 외 원본 Kensa는 알려진 대상만 확인. 물리 교정 정답 아님."
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
        };
    }

    private sealed record Case(string Name, string TeachingFile, int TeachingWidth, int TeachingHeight, RoiGeometry Teaching,
        string ImageFile, int Width, int Height, int SearchWidth, int SearchHeight, PointD Expected, double Angle, bool CompleteTruth, bool FullRotation = false,
        int? ExpectedCount = null, RoiGeometry? SearchRegion = null, CpuMatching.Core.Match[]? ExpectedMatches = null);

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
        yield return new("8k-local-two", "kensa.png", 1024, 1536, part,
            "samples/CpuMatching.AccuracyValidation/8k-local-two.png", 8192, 8192, 512, 512, new(6020.9, 4051.2), 7.6, true,
            ExpectedCount: 2, SearchRegion: new(new(6144, 4224), 512, 512, 0),
            ExpectedMatches: [new(6020.9, 4051.2, 7.6, 1, 1), new(6201.1, 4270.7, -8.4, 1, 1)]);
    }

    private static IEnumerable<Case> FullImageCases()
    {
        var folder = "samples/CpuMatching.FullImage";
        using var document = JsonDocument.Parse(File.ReadAllText(folder + "/cases.json"));
        var expected = document.RootElement[0].GetProperty("Expected").Deserialize<CpuMatching.Core.Match[]>()!
            .Select(match => match with { X = match.X + .5, Y = match.Y + .5 }).ToArray();
        yield return new("8k-full-eight", "kensa.png", 1024, 1536, new(new(200.5, 184), 89, 52, 0),
            folder + "/8k-full-eight.png", 8192, 8192, 8192, 8192, new(expected[0].X, expected[0].Y), expected[0].AngleDegrees,
            true, ExpectedCount: expected.Length, ExpectedMatches: expected);
    }

    private static IEnumerable<Case> ModuleCases()
    {
        var part = new RoiGeometry(new(200.5, 184), 89, 52, 0);
        // 검색과 별도로 만든 48개 OpenCV ECC 정합 기준이다. 물리 교정 정답과는 구별한다.
        using var provenance = JsonDocument.Parse(File.ReadAllText("samples/CpuMatching.Modules/reference-provenance.json"));
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes("kensa.png")));
        if (imageHash != provenance.RootElement.GetProperty("ImageSha256").GetString())
        {
            throw new InvalidOperationException("Kensa 원본과 정합 기준의 해시가 다릅니다. 현재 원본으로 기준 데이터를 다시 생성하세요.");
        }
        var kensaReference = JsonSerializer.Deserialize<CpuMatching.Core.Match[]>(
            File.ReadAllText("samples/CpuMatching.Modules/kensa-reference.json"))!;
        yield return new("kensa-full-modules", "kensa.png", 1024, 1536, part, "kensa.png", 1024, 1536,
            1024, 1536, part.Center, 0, true, ExpectedCount: 48, ExpectedMatches: kensaReference);
        var pin = new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var expected = TdcmAccuracy.SamplePins().Select(pose => new CpuMatching.Core.Match(
            pose.Center.X, pose.Center.Y, pose.AngleDegrees, 1, 1)).ToArray();
        yield return new("8k-full-modules", "sample:8192:8192", 8192, 8192, pin, "sample:8192:8192", 8192, 8192,
            8192, 8192, pin.Center, 0, true, ExpectedCount: 256, ExpectedMatches: expected);
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

    private static async Task InvokeSearchButtonAsync(Window window, ShapeMatchingViewModel matching)
    {
        var previous = matching.CpuInspectionLogs.LastOrDefault()?.RunId;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Updated(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ShapeMatchingViewModel.InspectionLogText))
            {
                CommandChanged(sender, EventArgs.Empty);
            }
        }
        void CommandChanged(object? sender, EventArgs args)
        {
            if (matching.CpuInspectionLogs.LastOrDefault() is { CommandMilliseconds: not null } log &&
                log.RunId != previous && matching.Search.CanExecute(null))
            {
                completed.TrySetResult();
            }
        }
        var button = Descendants(window).OfType<Button>().Single(control => ReferenceEquals(control.Command, matching.Search));
        if (!button.IsEnabled)
        {
            throw new InvalidOperationException($"검사 버튼 비활성: 명령={matching.Search.CanExecute(null)}, Idle={matching.IsIdle}, {matching.OptionsError}, {matching.Status}");
        }
        matching.PropertyChanged += Updated;
        matching.Search.CanExecuteChanged += CommandChanged;
        try
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(matching.SearchTimeoutSeconds == 0 ? 610 : matching.SearchTimeoutSeconds + 10));
        }
        finally
        {
            matching.PropertyChanged -= Updated;
            matching.Search.CanExecuteChanged -= CommandChanged;
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
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }
    }
}
