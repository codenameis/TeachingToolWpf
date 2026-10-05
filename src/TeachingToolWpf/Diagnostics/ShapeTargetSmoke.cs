using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Rendering;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Diagnostics;

/// <summary>정상 표시 중인 앱의 실제 학습·검사 명령을 첫 검사부터 기록하는 명시적 진단이다.</summary>
internal static class ShapeTargetSmoke
{
    private sealed record VisibleMatch(PointD ReferencePoint, double AngleRadians, string Label, PointD[] Boundary);
    private sealed record Accuracy(bool Passed, int UniquePins, int DuplicateMatches, int UnmappedMatches,
        int InaccurateMatches, double MaximumPositionErrorPixels, double DisplayedCleanMinimum,
        double DisplayedDiagonalMaximum, double DisplayedScoreGap);
    private sealed record SearchRun(string Method, string Mode, int Repetition, string Phase,
        double CommandWallMilliseconds, double? ServiceMilliseconds, double? ReadMilliseconds,
        double CudaSessionInitializationMilliseconds, double? DisplayedCudaPreparationMilliseconds,
        bool CommandWithin500Milliseconds, bool ServiceWithin500Milliseconds, bool Complete,
        bool ExactVisibleCpuParity, Accuracy Accuracy, string Status, string Results, VisibleMatch[] Matches)
    {
        public double? PresentationMilliseconds
        {
            get; init;
        }
        public double? CommandThroughDisplayMilliseconds
        {
            get; init;
        }
        public string Verdict { get; init; } = "";
    }

    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model,
        Func<double> cudaInitializationMilliseconds)
    {
        var show = Environment.GetCommandLineArgs().Contains("--shape-target-show");
        var paperLatency = Environment.GetCommandLineArgs().Contains("--paper-latency-smoke");
        var kensaText = paperLatency && Environment.GetCommandLineArgs().Contains("--paper-kensa-text");
        var kensa = paperLatency && (kensaText || Environment.GetCommandLineArgs().Contains("--paper-kensa"));
        var logSmoke = Environment.GetCommandLineArgs().Contains("--inspection-log-smoke");
        var outputPath = Path.GetFullPath(logSmoke ? "artifacts/kensa-text-work/app-8k.json" : paperLatency
            ? kensaText ? "artifacts/kensa-text-work/app-text.json" : kensa ? "artifacts/paper-125ms-work/app-kensa.json" : "artifacts/paper-125ms-work/app-latency.json"
            : "artifacts/shape-target-smoke.json");
        var rows = new List<SearchRun>();
        var training = new List<object>();
        var errors = new List<string>();
        var roi = kensaText ? new RoiGeometry(new(194.5, 371), 19, 12, 0) : kensa ? new RoiGeometry(new(200.5, 184), 89, 52, 0)
            : new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var lastVisualActivity = Stopwatch.GetTimestamp();
        var startupMilliseconds = 0d;
        string? failure = null;
        object? input = null;
        object? runtime = null;

        void TileReady() => Interlocked.Exchange(ref lastVisualActivity, Stopwatch.GetTimestamp());
        void MetricsChanged(RenderMetrics _) => TileReady();
        void RendererStatusChanged(string status)
        {
            TileReady();
            if (status.StartsWith("렌더 오류:", StringComparison.Ordinal) || status.StartsWith("D3D12 ", StringComparison.Ordinal))
            {
                errors.Add(status);
            }
        }

        viewport.Tiles.TileReady += TileReady;
        viewport.MetricsChanged += MetricsChanged;
        viewport.RendererStatusChanged += RendererStatusChanged;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var startup = Stopwatch.StartNew();
                if (kensa)
                {
                    var path = Path.GetFullPath("kensa.png");
                    await model.Images.ReplaceAsync(new(path, "kensa", 1024, 1536, DiskPyramidSource.Version(path)));
                    Require(model.Session.State.Image is { Width: 1024, Height: 1536 }, "kensa 원본 로딩 실패");
                }
                else
                {
                    await model.InitializeAsync();
                    Require(model.Session.State.Image is { Width: 8192, Height: 8192 }, "기본 8K 원본 로딩 실패");
                }
                model.Session.Add(roi);
                await WaitForVisualQuietAsync();
                startupMilliseconds = startup.Elapsed.TotalMilliseconds;
                input = new
                {
                    Image = model.Session.State.Image,
                    TeachingRegion = roi,
                    window.Width,
                    window.Height,
                    window.WindowState,
                    viewport.ViewWidth,
                    viewport.ViewHeight,
                    viewport.Dpi,
                    Renderer = model.GpuStatus
                };
                runtime = new
                {
                    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Environment.ProcessorCount,
                    Avx2Supported = System.Runtime.Intrinsics.X86.Avx2.IsSupported,
                    VectorAccelerated = System.Numerics.Vector.IsHardwareAccelerated,
                    VectorBytes = System.Numerics.Vector<byte>.Count,
                    TieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                    RuntimeConfiguration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TeachingToolWpf.runtimeconfig.json")),
                    AlgorithmSha256 = Hash(typeof(ShapeMatcher).Assembly.Location),
                    InspectionSha256 = Hash(typeof(ShapeMatchingService).Assembly.Location)
                };
                var matching = model.Matching;
                foreach (var method in paperLatency ? new[] { PatternMatchingMethod.Shape } : new[] { PatternMatchingMethod.Shape, PatternMatchingMethod.GeometricShape })
                {
                    matching.Method = method;
                    await WaitForVisualQuietAsync();
                    Require(matching.Train.CanExecute(null), "학습 명령 비활성: " + matching.Status);
                    var trainTimer = Stopwatch.StartNew();
                    await matching.Train.ExecuteAsync();
                    var trainWall = trainTimer.Elapsed.TotalMilliseconds;
                    Require(matching.Model is not null && !matching.IsTrainingDirty, "학습 실패: " + matching.Status);
                    training.Add(new
                    {
                        Method = method.ToString(),
                        CommandWallMilliseconds = trainWall,
                        matching.Status,
                        matching.ModelSummary,
                        Options = matching.Model!.Options,
                        matching.MaximumParallelism,
                        matching.MinimumScore,
                        matching.MaximumMatches,
                        matching.CandidateThreshold,
                        matching.MaximumCandidates,
                        matching.MaximumVerifiedCandidates,
                        matching.MaximumCoarseVerifiedCandidates,
                        matching.MaximumOverlap,
                        matching.MinimumSupport,
                        matching.ClutterWeight,
                        matching.Refine,
                        matching.GeometricTolerancePixels,
                        matching.MinimumCoverage,
                        matching.LimitClutter,
                        matching.MaximumClutterRatio,
                        matching.LimitFitError,
                        matching.MaximumFitErrorPixels,
                        matching.SearchTimeoutSeconds,
                        matching.UseSelection
                    });
                    VisibleMatch[]? cpuReference = null;
                    foreach (var mode in new[] { InspectionComputeMode.Cpu, InspectionComputeMode.GpuPreprocessing })
                    {
                        matching.ComputeMode = mode;
                        for (var repetition = 0; repetition < 4; repetition++)
                        {
                            await WaitForVisualQuietAsync();
                            Require(matching.Search.CanExecute(null), "검사 명령 비활성: " + matching.Status);
                            var initializationBefore = cudaInitializationMilliseconds();
                            var presentation = new TaskCompletionSource<OverlayPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
                            void Presented(OverlayPresentation value) => presentation.TrySetResult(value);
                            if (paperLatency)
                            {
                                viewport.OverlayPresented += Presented;
                            }

                            var started = Stopwatch.GetTimestamp();
                            var commandTimer = Stopwatch.StartNew();
                            double commandWall;
                            double? displayWall = null;
                            double? rendererWall = null;
                            try
                            {
                                await matching.Search.ExecuteAsync();
                                commandWall = commandTimer.Elapsed.TotalMilliseconds;
                                if (paperLatency)
                                {
                                    var completed = await presentation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                                    Require(completed.PresentationId == matching.Overlay?.PresentationId, "오래된 overlay의 표시 완료");
                                    rendererWall = Stopwatch.GetElapsedTime(started, completed.CompletedTimestamp).TotalMilliseconds;
                                    await ObserveWpfRenderAsync(window);
                                    displayWall = commandTimer.Elapsed.TotalMilliseconds;
                                }
                            }
                            finally
                            {
                                if (paperLatency)
                                {
                                    viewport.OverlayPresented -= Presented;
                                }
                            }
                            // 명령의 await 완료까지 측정한다. 결과 검증·JSON 기록·정지 대기는 측정 뒤에 수행한다.
                            var initializationDelta = cudaInitializationMilliseconds() - initializationBefore;
                            var service = ReadDisplayedNumber(matching.Status, @"개 · (?<value>[\d.,]+) ms");
                            var read = ReadDisplayedNumber(matching.Status, @"읽기/변환 (?<value>[\d.,]+)");
                            var preparation = ReadDisplayedNumber(matching.Results, @"CUDA 장치·커널 준비 (?<value>[\d.,]+) ms");
                            var matches = matching.Markers.Select(marker => new VisibleMatch(marker.ReferencePoint,
                                marker.AngleRadians, marker.Label, marker.Bounds.ToArray())).ToArray();
                            cpuReference ??= matches;
                            rows.Add(new(method.ToString(), mode.ToString(), repetition,
                                repetition == 0 ? "FIRST_SEARCH_IN_MODE" : "REPEATED_SEARCH",
                                commandWall, service, read, initializationDelta, preparation,
                                commandWall <= 500, service is <= 500,
                                matching.IsIdle && matching.Verdict == InspectionVerdict.Pass && matches.Length == (kensaText ? 12 : kensa ? 48 : 256),
                                SameMatches(cpuReference, matches), kensa ? ValidateKensaMatches(matches, kensaText) : ValidateMatches(matches),
                                matching.Status, matching.Results, matches)
                            {
                                PresentationMilliseconds = rendererWall,
                                CommandThroughDisplayMilliseconds = displayWall,
                                Verdict = matching.Verdict?.ToString() ?? "None"
                            });
                        }
                    }
                }
                if (kensaText)
                {
                    var logPanel = Descendants(window).OfType<System.Windows.Controls.Expander>().Single(expander =>
                        expander.Header?.ToString() == "검사 시간 로그 · 최근 20회");
                    logPanel.IsExpanded = true;
                    logPanel.BringIntoView();
                    await ObserveWpfRenderAsync(window);
                    var capture = "artifacts/kensa-text-work/text-viewport.png";
                    File.Delete(capture);
                    viewport.Capture(capture);
                    var captureTimer = Stopwatch.StartNew();
                    while (!File.Exists(capture) && captureTimer.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await Task.Delay(50);
                    }
                    Require(File.Exists(capture), "18V 로그 화면 캡처 시간 초과");
                    WindowCapture.Save(window, viewport, capture, "artifacts/kensa-text-work/text-log-panel.png");
                }
                if (!kensaText && paperLatency && Environment.GetCommandLineArgs().Contains("--paper-latency-capture"))
                {
                    var resultList = Descendants(window).OfType<System.Windows.Controls.ListBox>().Single(list =>
                        System.Windows.Automation.AutomationProperties.GetName(list) == "논문 형상 전체 검사 결과");
                    Require(resultList.Items.Count == (kensa ? 48 : 256), "전체 결과 목록 누락");
                    resultList.BringIntoView();
                    await ObserveWpfRenderAsync(window);
                    var capture = kensa ? "artifacts/paper-125ms-work/kensa-viewport.png" : "artifacts/paper-125ms-work/app-viewport.png";
                    File.Delete(capture);
                    viewport.Capture(capture);
                    var captureTimer = Stopwatch.StartNew();
                    while (!File.Exists(capture) && captureTimer.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await Task.Delay(50);
                    }

                    Require(File.Exists(capture), "결과 화면 캡처 시간 초과");
                    WindowCapture.Save(window, viewport, capture, kensa ? "artifacts/paper-125ms-work/kensa-results.png" : "artifacts/paper-125ms-work/app-results.png");
                }
            }
            catch (Exception error)
            {
                failure = error.ToString();
            }
            finally
            {
                viewport.Tiles.TileReady -= TileReady;
                viewport.MetricsChanged -= MetricsChanged;
                viewport.RendererStatusChanged -= RendererStatusChanged;
                var expectedRuns = paperLatency ? 8 : 16;
                var accuracyPassed = rows.Count == expectedRuns && rows.All(row => row.Complete && row.Accuracy.Passed && row.ExactVisibleCpuParity);
                var servicePassed = rows.Count == expectedRuns && rows.All(row => row.ServiceWithin500Milliseconds);
                var commandPassed = rows.Count == expectedRuns && rows.All(row => paperLatency
                    ? row.CommandThroughDisplayMilliseconds is { } elapsed && (row.Mode == "Cpu" ? elapsed <= 125 : elapsed < 80)
                    : row.CommandWithin500Milliseconds);
                var passed = failure is null && errors.Count == 0 && accuracyPassed && servicePassed && commandPassed;
                Environment.ExitCode = passed ? 0 : 1;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(Path.ChangeExtension(outputPath, ".inspection-log.json"), InspectionRunLog.Serialize(model.Matching.InspectionLogs));
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = passed ? "PASS" : "FAIL",
                        Paper125MillisecondsTarget = paperLatency,
                        Fixture = kensaText ? "kensa-18v-1024x1536" : kensa ? "kensa-1024x1536" : "sample-8192x8192",
                        KensaSha256 = kensa ? Hash(Path.GetFullPath("kensa.png")) : null,
                        PaperTiming = paperLatency ? "VM 검사 명령 진입 → 같은 overlay의 모든 라벨·Present·GPU fence 완료 → WPF Rendering 이벤트 이후 Dispatcher ContextIdle 관측. 최초 장치 준비 포함. 물리 입력 도착/모니터 scan-out 및 WPF DWM 합성 완료를 보장하지 않는다." : null,
                        AccuracyPassed = accuracyPassed,
                        DetectionCorrespondencePassed = rows.Count == expectedRuns && rows.All(row => row.Accuracy.Passed && row.ExactVisibleCpuParity),
                        AllVerdictsPass = rows.Count == expectedRuns && rows.All(row => row.Complete),
                        AllServiceRunsWithin500Milliseconds = paperLatency ? (bool?)null : servicePassed,
                        AllCommandRunsWithin500Milliseconds = paperLatency ? (bool?)null : commandPassed,
                        AllPaperRunsWithin125Or80Milliseconds = paperLatency ? (bool?)commandPassed : null,
                        Timing = "실제 VM 명령 호출 직전부터 await 완료까지의 wall. 최초 GPU 명령에는 장치 준비를 포함한다. ServiceMilliseconds/ReadMilliseconds는 앱이 표시한 0.1 ms 정밀도의 수치다. Training/초기 화면 로딩은 별도이며 사전 검사는 없다. 명령 완료는 최종 화면 Present 완료를 뜻하지 않는다.",
                        VisualQuietPolicy = "각 명령 전 공개 TileReady/HasReady 및 renderer metrics 통지의 750 ms 정지 관찰. GPU fence idle 보장은 아니며 렌더러/파일 캐시/검사 작업을 중지하지 않는다. 측정 중 화면 캡처·UI 자동화 탐색·로그 직렬화 없음.",
                        RunOrder = paperLatency ? "논문 CPU 최초+반복3 → CUDA 최초+반복3. 사전 검사 없음." :
                            "논문 CPU 최초+반복3, GPU 최초+반복3 → 기하 CPU 최초+반복3, GPU 최초+반복3. 프로세스 최초 검사는 첫 행이며 나머지는 모드별 최초다.",
                        StartupAndVisualQuietMilliseconds = startupMilliseconds,
                        Input = input,
                        Runtime = runtime,
                        Training = training,
                        Runs = rows,
                        RendererErrors = errors,
                        Failure = failure
                    }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                }
                catch (Exception error)
                {
                    Environment.ExitCode = 1;
                    model.Fail(error);
                }
                if (!show)
                {
                    window.Close();
                }
            }
        };

        async Task WaitForVisualQuietAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var wait = Stopwatch.StartNew();
            var observedSince = Stopwatch.GetTimestamp();
            while (wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50);
                var last = Math.Max(observedSince, Interlocked.Read(ref lastVisualActivity));
                if (model.GpuStatus.StartsWith("DX12 ·", StringComparison.Ordinal) &&
                    !viewport.Tiles.HasReady && Stopwatch.GetElapsedTime(last).TotalMilliseconds >= 750)
                {
                    return;
                }
            }
            throw new TimeoutException("표시 타일/렌더 통지가 15초 안에 정지하지 않았습니다. 검사 시간을 측정하지 않습니다.");
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

    private static Accuracy ValidateMatches(VisibleMatch[] matches)
    {
        var found = new HashSet<int>();
        var duplicate = 0;
        var unmapped = 0;
        var inaccurate = 0;
        var maximumError = 0d;
        var clean = new List<double>();
        var diagonal = new List<double>();
        foreach (var match in matches)
        {
            var point = match.ReferencePoint;
            var chipX = (int)Math.Floor(point.X / 2048);
            var chipY = (int)Math.Floor(point.Y / 2048);
            var localX = point.X - (chipX * 2048);
            var localY = point.Y - (chipY * 2048);
            var column = (int)Math.Floor(localX / 180) - 2;
            var row = localY is >= 491 and < 640 ? 0 : localY is >= 1401 and < 1550 ? 1 : -1;
            var left = (column + 2) * 180;
            if (chipX is < 0 or > 3 || chipY is < 0 or > 3 || column is < 0 or > 7 || row < 0 || localX < left || localX >= left + 80)
            {
                unmapped++;
                continue;
            }
            var pinId = (((chipY * 4) + chipX) * 16) + (row * 8) + column;
            if (!found.Add(pinId))
            {
                duplicate++;
            }
            var cosine = Math.Cos(match.AngleRadians);
            var sine = Math.Sin(match.AngleRadians);
            var expected = new PointD((chipX * 2048) + left + 40 + (cosine * -.482) - (sine * -19.322),
                (chipY * 2048) + (row == 0 ? 565.5 : 1475.5) + (sine * -.482) + (cosine * -19.322));
            var error = (point - expected).Length;
            maximumError = Math.Max(maximumError, error);
            inaccurate += error > 1 ? 1 : 0;
            var score = ReadDisplayedNumber(match.Label, @"점수 (?<value>[\d.,]+)");
            if (score is { } value)
            {
                (column is 1 or 6 ? diagonal : clean).Add(value);
            }
        }
        var cleanMinimum = clean.Count == 0 ? 0 : clean.Min();
        var diagonalMaximum = diagonal.Count == 0 ? 1 : diagonal.Max();
        var gap = cleanMinimum - diagonalMaximum;
        return new(found.Count == 256 && duplicate == 0 && unmapped == 0 && inaccurate == 0 &&
            clean.Count == 192 && diagonal.Count == 64 && gap >= .02, found.Count, duplicate, unmapped,
            inaccurate, maximumError, cleanMinimum, diagonalMaximum, gap);
    }

    private static Accuracy ValidateKensaMatches(VisibleMatch[] matches, bool text = false)
    {
        // 서비스 benchmark와 같은 독립 육안 annotation. 12 px는 개체 대응 반경이며 정밀 위치 오차 기준이 아니다.
        double[] columns = text ? [194.5, 459, 723] : [200, 296, 466, 558, 726, 822];
        double[] rows = text ? [371, 686, 1000, 1315] : [184, 245, 499, 560, 819, 879, 1130, 1190];
        var expected = rows.SelectMany(y => columns.Select(x => new PointD(x, y))).ToArray();
        var found = new HashSet<int>();
        var duplicate = 0;
        var unmapped = 0;
        foreach (var match in matches)
        {
            var closest = Enumerable.Range(0, expected.Length).MinBy(index => (expected[index] - match.ReferencePoint).Length);
            if ((expected[closest] - match.ReferencePoint).Length > (text ? 3 : 12))
            {
                unmapped++;
            }
            else if (!found.Add(closest))
            {
                duplicate++;
            }
        }
        return new(found.Count == expected.Length && duplicate == 0 && unmapped == 0,
            found.Count, duplicate, unmapped, 0, double.NaN, double.NaN, double.NaN, double.NaN);
    }

    private static bool SameMatches(VisibleMatch[] first, VisibleMatch[] second)
        => first.Length == second.Length && first.Zip(second).All(pair =>
            pair.First.ReferencePoint == pair.Second.ReferencePoint && pair.First.AngleRadians == pair.Second.AngleRadians &&
            pair.First.Label == pair.Second.Label && pair.First.Boundary.SequenceEqual(pair.Second.Boundary));

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

    private static double? ReadDisplayedNumber(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
            CultureInfo.CurrentCulture, out var number) && double.IsFinite(number) ? number : null;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Shape target smoke: " + message);
        }
    }
}
