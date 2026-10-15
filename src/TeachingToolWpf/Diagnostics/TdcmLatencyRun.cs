using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PoseMatch.Tdcm;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>8K 원본의 실제 검사 명령과 렌더 완료를 기록한다. 목표 미달도 그대로 저장한다.</summary>
internal static class TdcmLatencyRun
{
    public static void Attach(Window window, MainWindowViewModel model)
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
                var kensa = Environment.GetCommandLineArgs().Contains("--tdcm-kensa");
                if (kensa)
                {
                    var path = Path.GetFullPath("kensa.png");
                    await model.Images.ReplaceAsync(new(path, "kensa", 1024, 1536, ImageFileVersion.Read(path)));
                    model.Session.Add(new(new(200.5, 184), 89, 52, 0));
                }
                else
                {
                    await model.InitializeAsync();
                    model.Session.Add(new(new(759.518, 546.178), 105.827, 129.459, 0));
                }
                var matching = model.Matching;
                matching.Method = PatternMatchingMethod.PoseMatchTdcm;
                matching.TdcmBackend = Environment.GetCommandLineArgs().Contains("--tdcm-torch") ? TdcmBackend.TorchSharp : TdcmBackend.OnnxRuntime;
                matching.TdcmWeightsPath = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tdcm-checkpoint=", StringComparison.Ordinal))?.Split('=', 2)[1];
                matching.SearchTimeoutSeconds = 180;
                var refinedPins = Environment.GetCommandLineArgs().Contains("--tdcm-refined-pins");
                if (refinedPins)
                {
                    matching.MinimumScore = .2;
                    matching.MaximumMatches = 1000;
                    matching.TdcmRefinePose = true;
                }
                await matching.Train.ExecuteAsync();
                if (!matching.HasModel) throw new InvalidOperationException(matching.Status);
                var accuracyGrid = Environment.GetCommandLineArgs().Contains("--tdcm-accuracy-grid");
                var repeatArgument = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tdcm-repeats=", StringComparison.Ordinal));
                var repetitions = repeatArgument is null ? 2 : int.Parse(repeatArgument.Split('=', 2)[1], System.Globalization.CultureInfo.InvariantCulture);
                if (repetitions is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repetitions));
                foreach (var device in accuracyGrid ? new[] { TdcmDevice.Cuda } : new[] { TdcmDevice.Cpu, TdcmDevice.Cuda })
                {
                    matching.TdcmDevice = device;
                    for (var repeat = 0; repeat < (accuracyGrid ? 8 : repetitions); repeat++)
                    {
                        if (accuracyGrid)
                        {
                            matching.MinimumScore = new[] { .1, .2, .3, .5 }[repeat % 4];
                            matching.TdcmWindowSize = repeat < 4 ? 256 : 512;
                            matching.MaximumMatches = 1000;
                            matching.MaximumCandidates = 16384;
                        }
                        var started = Stopwatch.GetTimestamp();
                        await matching.Search.ExecuteAsync();
                        var commandMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var wait = Stopwatch.StartNew();
                        while (matching.TdcmInspectionLogs[^1].PresentedMilliseconds is null && wait.Elapsed.TotalSeconds < 10)
                        {
                            await Task.Delay(10);
                        }
                        var log = matching.TdcmInspectionLogs[^1];
                        await ObserveWpfRenderAsync(window);
                        var displayedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        rows.Add(new
                        {
                            device = device.ToString(), repeat, commandMs, displayedMs, log,
                            matching.MinimumScore, matching.TdcmWindowSize, matching.TdcmRefinePose,
                            backend = matching.TdcmBackend.ToString(), matching.TdcmWeightsPath,
                            expectedCount = kensa ? 48 : 256,
                            accuracy = TdcmAccuracy.Evaluate(kensa ? TdcmAccuracy.ApproximateKensaParts() : TdcmAccuracy.SamplePins(),
                                matching.Markers.Select(marker => new TdcmAccuracy.Pose(marker.ReferencePoint, marker.AngleRadians * 180 / Math.PI)).ToArray(),
                                kensa ? 25 : 60, !kensa),
                            withinTimeBudget = log.PresentedMilliseconds is not null && displayedMs < (device == TdcmDevice.Cpu ? 125 : 80),
                            matching.Status,
                            matches = matching.Markers.Select(marker => new { marker.ReferencePoint, marker.AngleRadians, marker.Label }).ToArray()
                        });
                        Save();
                        if (log.PresentationId is null) throw new InvalidOperationException(matching.Status);
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

            void Save()
            {
                Directory.CreateDirectory("artifacts");
                var output = Environment.GetCommandLineArgs().Contains("--tdcm-accuracy-grid") ? "artifacts/tdcm-app-accuracy-grid.json" : "artifacts/tdcm-app-latency.json";
                if (Environment.GetCommandLineArgs().Contains("--tdcm-refined-pins")) output = "artifacts/tdcm-app-refined-pins.json";
                if (Environment.GetCommandLineArgs().Contains("--tdcm-kensa")) output = "artifacts/tdcm-app-kensa.json";
                if (Environment.GetCommandLineArgs().Contains("--tdcm-torch")) output = output.Replace("tdcm-app", "tdcm-torch");
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    input = model.Session.State.Image, renderer = model.GpuStatus, failure, rows,
                    measurement = "명령 진입→필수 라벨 준비/Present 제출/GPU fence. 물리 scan-out 및 입력 메시지 대기 제외. 정확도 합격 판정 아님."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        };
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
