using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;

internal static class InspectionControlsBenchmark
{
    public static async Task<bool> RunAsync(string outputPath)
    {
        var image = new ImageReference("sample:8192:8192", "sample", 8192, 8192);
        var roi = new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var service = new ShapeMatchingService(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")), () => null,
            () => new VisionCompute.ILGPU.CudaImagePreprocessor());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var trained = await service.TrainAsync(image, false, roi, new()
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
            BuildGeometricModel = false,
            BuildClutterModel = true
        }, cancellation.Token);
        var standard = new SearchOptions { MaximumMatches = 1000, EvaluateGeometry = false, ClutterWeight = 1 };
        (string Name, SearchOptions Options)[] cases =
        [
            ("direction-only", standard with { ClutterWeight = 0 }),
            ("balanced-cpu", standard),
            ("legacy-fast-cpu", standard with { MaximumCandidates = 512, MaximumVerifiedCandidates = 32, MaximumCoarseVerifiedCandidates = 64, Refine = false }),
            ("precise-cpu", standard with { MaximumCandidates = 8192, MaximumVerifiedCandidates = 256, MaximumCoarseVerifiedCandidates = 1024 }),
            ("balanced-cpu-4", standard with { MaximumParallelism = 4 }),
            ("balanced-gpu", standard with { ComputeMode = InspectionComputeMode.GpuPreprocessing })
        ];
        var rows = new List<object>();
        ShapeSearchOutput? cpu = null;
        var passed = true;
        foreach (var (name, options) in cases)
        {
            // 설정별 첫 실행은 측정 반복에서 분리한다.
            await service.SearchAsync(image, false, trained.Model, null, options, cancellation.Token);
            for (var repetition = 0; repetition < 3; repetition++)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await service.SearchAsync(image, false, trained.Model, null, options, cancellation.Token);
                    var groundTruth = PinGroundTruth.Validate(PinGroundTruth.Create(), result.Matches);
                    if (name == "balanced-cpu")
                    {
                        cpu = result;
                    }
                    var parity = name != "balanced-gpu" || (cpu is not null && SameMatches(cpu, result));
                    passed &= parity && groundTruth.Passed && !result.Truncated && !result.MatchLimitExceeded;
                    rows.Add(new
                    {
                        Name = name,
                        Repetition = repetition,
                        Status = "COMPLETED",
                        Options = options,
                        WallMilliseconds = watch.Elapsed.TotalMilliseconds,
                        result.TotalMilliseconds,
                        result.ReadMilliseconds,
                        result.Windows,
                        result.Truncated,
                        result.MatchLimitExceeded,
                        result.Preprocessing,
                        Count = result.Matches.Length,
                        MinimumScore = result.Matches.IsEmpty ? (double?)null : result.Matches.Min(match => match.Score),
                        MaximumClutter = result.Matches.IsEmpty ? (double?)null : result.Matches.Max(match => match.ClutterRatio),
                        GroundTruth = groundTruth,
                        GpuCpuParity = parity,
                        MatchDetails = result.Matches
                    });
                    Console.WriteLine($"{name} {repetition}: {result.Matches.Length}개 / {result.TotalMilliseconds:F1} ms / mapped {groundTruth.UniquePins} / accurate {groundTruth.Passed} / parity {parity}");
                }
                catch (Exception error)
                {
                    passed = false;
                    rows.Add(new
                    {
                        Name = name,
                        Repetition = repetition,
                        Status = "FAILED",
                        Error = error.ToString()
                    });
                    Console.WriteLine($"{name}: FAILED {error.Message}");
                }
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new
                {
                    Timestamp = DateTimeOffset.Now,
                    Input = "8192×8192 procedural PCB; one pin ROI; synthetic data, not field accuracy",
                    WarmupRunsPerSetting = 1,
                    MeasuredRunsPerSetting = 3,
                    Threshold = "Minimum final score .72 in every mode; speed profiles never relax acceptance",
                    Results = rows
                }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            }
        }
        return passed;
    }

    private static bool SameMatches(ShapeSearchOutput cpu, ShapeSearchOutput gpu)
    {
        if (cpu.Matches.Length != gpu.Matches.Length || cpu.Truncated != gpu.Truncated || cpu.MatchLimitExceeded != gpu.MatchLimitExceeded)
        {
            return false;
        }
        var left = cpu.Matches.OrderBy(match => match.Pose.Y).ThenBy(match => match.Pose.X).ToArray();
        var right = gpu.Matches.OrderBy(match => match.Pose.Y).ThenBy(match => match.Pose.X).ToArray();
        return left.Zip(right).All(pair => pair.First.Pose == pair.Second.Pose &&
            pair.First.Score == pair.Second.Score && pair.First.DirectionScore == pair.Second.DirectionScore &&
            pair.First.Support == pair.Second.Support && pair.First.ClutterRatio == pair.Second.ClutterRatio);
    }
}
