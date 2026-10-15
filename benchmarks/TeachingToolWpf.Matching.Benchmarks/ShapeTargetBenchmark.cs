using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

/// <summary>사용자 8K PCB 샘플의 전체 검출·사선 점수·500 ms 조건을 같은 실행에서 검사한다.</summary>
internal static class ShapeTargetBenchmark
{
    private sealed record ScoreSeparation(bool Passed, double CleanMinimum, double DiagonalMaximum,
        double Gap, int CleanCount, int DiagonalCount);

    public static async Task<bool> RunAsync(string outputPath, int repetitions, int parallelism, string modes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);
        var image = new ImageReference("sample:8192:8192", "8K PCB sample", 8192, 8192);
        var roi = new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var loader = new CountingImageLoader(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")));
        using var compute = new VisionCompute.ILGPU.CudaInspectionSession();
        var service = new ShapeMatchingService(loader, () => null,
            compute.Rent, compute.PrepareAsync, pipeline: ShapeSearchPipeline.Paper);
        var expected = PinGroundTruth.Create();
        var rows = new List<object>();
        var summaries = new List<object>();
        var preparations = new List<object>();
        var algorithmHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShapeMatcher).Assembly.Location)));
        var inspectionHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShapeMatchingService).Assembly.Location)));
        var accuracyPassed = true;
        var speedPassed = true;
        var firstSpeedPassed = true;
        var gpuFaster = true;
        var selectedModes = modes == "cpu" ? new[] { InspectionComputeMode.Cpu }
            : modes == "gpu" ? new[] { InspectionComputeMode.GpuPreprocessing }
            : new[] { InspectionComputeMode.Cpu, InspectionComputeMode.GpuPreprocessing };

        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var trainingOptions = new TrainingOptions
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 10,
                BuildClutterModel = true
            };
            var training = await service.TrainAsync(image, false, roi, trainingOptions, cancellation.Token);
            ShapeSearchOutput? reference = null;
            var measuredTimes = selectedModes.ToDictionary(mode => mode, _ => new List<double>());
            for (var repetition = 0; repetition <= repetitions; repetition++)
            {
                var orderedModes = repetition % 2 == 0 ? selectedModes : selectedModes.Reverse().ToArray();
                foreach (var mode in orderedModes)
                {
                    if (mode == InspectionComputeMode.GpuPreprocessing && !compute.IsReady)
                    {
                        var preparationTime = await service.PrepareGpuAsync(cancellation.Token);
                        preparations.Add(new
                        {
                            Method = "paper",
                            Milliseconds = preparationTime
                        });
                        Console.WriteLine($"CUDA session preparation: {preparationTime:F1} ms (separate from inspection)");
                    }
                    var options = new SearchOptions
                    {
                        MaximumMatches = 256,
                        ClutterWeight = 1,
                        MaximumParallelism = parallelism,
                        ComputeMode = mode
                    };
                    loader.Reset();
                    var allocated = GC.GetTotalAllocatedBytes();
                    var timer = Stopwatch.StartNew();
                    var result = await service.SearchAsync(image, false, training.Model, null, options, cancellation.Token);
                    var wall = timer.Elapsed.TotalMilliseconds;
                    reference ??= result;
                    var validation = PinGroundTruth.Validate(expected, result.Matches);
                    var scores = ValidateScores(expected, validation, result);
                    var complete = !result.Truncated && !result.MatchLimitExceeded;
                    var parity = reference.Matches.SequenceEqual(result.Matches);
                    accuracyPassed &= validation.Passed && scores.Passed && complete && parity;
                    if (repetition > 0)
                    {
                        measuredTimes[mode].Add(wall);
                    }
                    else
                    {
                        firstSpeedPassed &= wall <= 500;
                    }
                    rows.Add(new
                    {
                        Method = "paper",
                        Mode = mode.ToString(),
                        Repetition = repetition,
                        Phase = repetition == 0 ? "FIRST_SEARCH" : "REPEATED_SEARCH",
                        Parallelism = parallelism,
                        WallMilliseconds = wall,
                        Within500Milliseconds = wall <= 500,
                        result.TotalMilliseconds,
                        result.ReadMilliseconds,
                        result.Windows,
                        result.Truncated,
                        result.MatchLimitExceeded,
                        result.Preprocessing,
                        result.CandidateScoring,
                        Count = result.Matches.Length,
                        Validation = validation,
                        ScoreSeparation = scores,
                        ExactCpuParity = parity,
                        AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        Reads = loader.Snapshot(),
                        PreprocessingWorkMilliseconds = result.Diagnostics.Sum(value => value.PreprocessingMilliseconds),
                        CandidateWorkMilliseconds = result.Diagnostics.Sum(value => value.CandidateMilliseconds),
                        CoarseCandidateWorkMilliseconds = result.Diagnostics.Where(value => value.TemplatesEvaluated > 0).Sum(value => value.CandidateMilliseconds),
                        OriginalCandidateWorkMilliseconds = result.Diagnostics.Where(value => value.TemplatesEvaluated == 0).Sum(value => value.CandidateMilliseconds),
                        CoarseCandidateCount = result.Diagnostics.Where(value => value.TemplatesEvaluated > 0).Sum(value => value.Candidates),
                        RefinementWorkMilliseconds = result.Diagnostics.Sum(value => value.RefinementMilliseconds),
                        Options = options,
                        TrainingOptions = trainingOptions,
                        MatchDetails = result.Matches
                    });
                    Console.WriteLine($"target method=paper {mode} CPU{parallelism} run{repetition}: {wall:F1} ms, read {result.ReadMilliseconds:F1}, {result.Matches.Length}/256, recall={validation.Passed}, scoreGap={scores.Gap:F4}, complete={complete}, parity={parity}");
                }
            }
            var cpuMedian = double.NaN;
            foreach (var mode in selectedModes)
            {
                var times = measuredTimes[mode];
                times.Sort();
                var median = times[times.Count / 2];
                speedPassed &= times[^1] <= 500;
                if (mode == InspectionComputeMode.Cpu)
                {
                    cpuMedian = median;
                }
                else if (double.IsFinite(cpuMedian))
                {
                    gpuFaster &= median < cpuMedian;
                }
                summaries.Add(new
                {
                    Method = "paper",
                    Mode = mode.ToString(),
                    MedianMilliseconds = median,
                    MaximumMilliseconds = times[^1],
                    MinimumMilliseconds = times[0],
                    EveryRepeatedRunWithin500Milliseconds = times[^1] <= 500,
                    GpuFasterThanCpu = mode == InspectionComputeMode.Cpu || !double.IsFinite(cpuMedian) ? (bool?)null : median < cpuMedian
                });
            }
        }
        Save();
        return accuracyPassed && firstSpeedPassed && speedPassed && gpuFaster;

        void Save()
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                Input = "Unmodified application 8192×8192 PCB procedural source; all 256 pins; both shape modes",
                Timing = "SearchAsync call including independent original reader, source pixel generation, preprocessing, search, original verification, result assembly and GPU lease return. Training and one-time CUDA session preparation are separate. CPU/GPU order alternates. No inspection/result cache. The large JSON report is serialized once after all timed runs to avoid benchmark-output allocations affecting subsequent inspections.",
                AlgorithmSha256 = algorithmHash,
                InspectionSha256 = inspectionHash,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Avx2Supported = System.Runtime.Intrinsics.X86.Avx2.IsSupported,
                VectorAccelerated = System.Numerics.Vector.IsHardwareAccelerated,
                VectorBytes = System.Numerics.Vector<byte>.Count,
                TieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                AccuracyPassed = accuracyPassed,
                FirstSpeedPassed = firstSpeedPassed,
                RepeatedSpeedPassed = speedPassed,
                RepeatedTargetPassed = accuracyPassed && speedPassed && gpuFaster,
                AllRunsTargetPassed = accuracyPassed && firstSpeedPassed && speedPassed && gpuFaster,
                GpuFaster = selectedModes.Length == 2 ? (bool?)gpuFaster : null,
                Results = rows,
                CudaSessionPreparation = preparations,
                Summaries = summaries
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
    }

    private static ScoreSeparation ValidateScores(ExpectedPin[] expected, PinValidation validation, ShapeSearchOutput result)
    {
        var clean = new List<double>();
        var diagonal = new List<double>();
        foreach (var assignment in validation.Assignments)
        {
            if (assignment.PinId is not { } id)
            {
                continue;
            }
            // 원본 샘플의 x=y / x+y=2048 배선이 통과하는 핀 열은 1과 6이다.
            // 판정은 점수로 그룹을 나누지 않고 독립된 샘플 기하 정답을 사용한다.
            var values = expected[id].PinColumn is 1 or 6 ? diagonal : clean;
            values.Add(result.Matches[assignment.Match].Score);
        }
        var cleanMinimum = clean.Count == 0 ? 0 : clean.Min();
        var diagonalMaximum = diagonal.Count == 0 ? 1 : diagonal.Max();
        var gap = cleanMinimum - diagonalMaximum;
        return new(clean.Count == 192 && diagonal.Count == 64 && gap >= .02,
            cleanMinimum, diagonalMaximum, gap, clean.Count, diagonal.Count);
    }
}
