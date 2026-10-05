using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Diagnostics;

/// <summary>현재 사용자 ROI를 재현하는 독립 앱 진단. 사용 중인 다른 앱의 상태는 읽거나 변경하지 않는다.</summary>
internal static class RotationInspectionSmoke
{
    public static void Attach(Window window, EditorViewModel editor)
    {
        window.Title = "Vision Teaching Studio · 회전 검사 검증";
        window.Loaded += async (_, _) =>
        {
            var reports = new List<object>();
            string? failure = null;
            var passed = true;
            try
            {
                await editor.InitializeAsync();
                editor.Session.Add(new(new(760.793, 598.841), 110.787, 245.01, 0));
                var matching = editor.Matching;
                matching.MinimumAngle = 0;
                matching.MaximumAngle = 10;
                matching.InspectRotation = true;
                await matching.Train.ExecuteAsync();
                if (matching.Model is null || !matching.Search.CanExecute(null))
                    throw new InvalidOperationException(matching.Status + matching.OptionsError);
                foreach (var minimum in new[] { 0d, -10d })
                {
                    matching.MinimumAcceptedAngle = minimum;
                    foreach (var mode in new[] { InspectionComputeMode.Cpu, InspectionComputeMode.GpuPreprocessing })
                    {
                        matching.ComputeMode = mode;
                        for (var repetition = 0; repetition < 2; repetition++)
                        {
                            await Task.Delay(750);
                            var timer = Stopwatch.StartNew();
                            await matching.Search.ExecuteAsync();
                            var commandMilliseconds = timer.Elapsed.TotalMilliseconds;
                            var log = matching.InspectionLogs[^1];
                            var displayTimeout = Stopwatch.StartNew();
                            while (matching.InspectionLogs[^1].RendererPresentationMilliseconds is null && displayTimeout.Elapsed.TotalSeconds < 10)
                                await Task.Delay(20);
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                            log = matching.InspectionLogs[^1];
                            var lower = matching.Markers.Where(marker => marker.ReferencePoint.Y % 2048 > 1000).ToArray();
                            var upper = matching.Markers.Where(marker => marker.ReferencePoint.Y % 2048 < 1000).ToArray();
                            var reverseNg = lower.Length == 128 && lower.All(marker => marker.Label.Contains("NG") &&
                                Math.Abs(RotationAcceptance.DeviationDegrees(marker.AngleRadians, 0)) > 179 &&
                                marker.Color == InspectionResultPresentation.Color(InspectionVerdict.Fail));
                            var normalOk = upper.Length == 128 && upper.All(marker => marker.Label.Contains("OK"));
                            var correct = matching.Verdict == InspectionVerdict.Fail && matching.Markers.Length == 256 && reverseNg &&
                                (minimum != -10 || normalOk) && !log.Truncated && !log.MatchLimitExceeded;
                            passed &= correct;
                            reports.Add(new
                            {
                                Mode = mode.ToString(), Repetition = repetition, MinimumAcceptedAngle = minimum,
                                Correct = correct, Reversed128AllNg = reverseNg, Upright128AllOk = normalOk,
                                CommandMilliseconds = commandMilliseconds, log.RendererPresentationMilliseconds,
                                matching.Status, matching.Results, Markers = matching.Markers.Select(marker => new
                                {
                                    marker.ReferencePoint, marker.AngleRadians, marker.Label, marker.Bounds,
                                    Rgba = new[] { marker.Color.X, marker.Color.Y, marker.Color.Z, marker.Color.W }
                                }).ToArray(), Log = log
                            });
                        }
                    }
                }
                // 사용자가 지정한 0~10°를 마지막 화면에 복원한다. 별도 ±10° 비교는 보고서에 남긴다.
                matching.MinimumAcceptedAngle = 0;
                matching.ComputeMode = InspectionComputeMode.Cpu;
                await matching.Search.ExecuteAsync();
            }
            catch (Exception error)
            {
                failure = error.ToString();
                passed = false;
            }
            finally
            {
                Directory.CreateDirectory("artifacts/rotation-diagnosis");
                File.WriteAllText("artifacts/rotation-diagnosis/app.json", JsonSerializer.Serialize(new
                {
                    Passed = passed, Failure = failure,
                    Timing = "VM command through await; renderer Present/fence separately. Physical click, WPF final composition and scan-out not measured.",
                    AppSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RotationInspectionSmoke).Assembly.Location))),
                    Reports = reports
                }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                File.WriteAllText("artifacts/rotation-diagnosis/app.inspection-log.json", InspectionRunLog.Serialize(editor.Matching.InspectionLogs));
                Environment.ExitCode = passed ? 0 : 1;
                if (!Environment.GetCommandLineArgs().Contains("--rotation-inspection-show")) window.Close();
            }
        };
    }
}
