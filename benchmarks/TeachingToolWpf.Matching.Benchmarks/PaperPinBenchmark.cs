using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

internal static class PaperPinBenchmark
{
    public static async Task<bool> RunAsync(string outputPath, int repetitions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);
        var reference = new ImageReference("sample:8192:8192", "sample", 8192, 8192);
        var roi = new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var trainingOptions = new TrainingOptions
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
        };
        var searchOptions = new SearchOptions
        {
            MaximumMatches = 1000,
        };
        var loader = new CountingImageLoader(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")));
        var service = new ShapeMatchingService(loader, () => null);
        using var trainingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var training = await service.TrainAsync(reference, false, roi, trainingOptions, trainingCancellation.Token);
        var trainingReads = loader.Snapshot();
        var expected = PinGroundTruth.Create();
        var rows = new List<object>();
        var successful = true;
        for (var repetition = -1; repetition < repetitions; repetition++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            loader.Reset();
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var timer = Stopwatch.StartNew();
            try
            {
                var output = await service.SearchAsync(reference, false, training.Model, null, searchOptions, cancellation.Token);
                var wallMilliseconds = timer.Elapsed.TotalMilliseconds;
                process.Refresh();
                var validation = PinGroundTruth.Validate(expected, output.Matches);
                var complete = !output.Truncated && !output.MatchLimitExceeded;
                successful &= validation.Passed && complete;
                rows.Add(new
                {
                    Kind = repetition < 0 ? "WARMUP" : "MEASURED",
                    Repetition = repetition,
                    Status = "COMPLETED",
                    CorrectnessStatus = validation.Passed ? "PASS" : "FAIL",
                    CompletenessStatus = complete ? "PASS" : "FAIL",
                    WallMilliseconds = wallMilliseconds,
                    CpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    AllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
                    ProcessPeakWorkingSetBytes = process.PeakWorkingSet64,
                    output.TotalMilliseconds,
                    output.ReadMilliseconds,
                    output.Windows,
                    output.Truncated,
                    output.MatchLimitExceeded,
                    Reads = loader.Snapshot(),
                    Matches = output.Matches.Length,
                    PreprocessingMilliseconds = output.Diagnostics.Sum(value => value.PreprocessingMilliseconds),
                    CandidateMilliseconds = output.Diagnostics.Sum(value => value.CandidateMilliseconds),
                    VerificationMilliseconds = output.Diagnostics.Sum(value => value.VerificationMilliseconds),
                    RefinementMilliseconds = output.Diagnostics.Sum(value => value.RefinementMilliseconds),
                    SuppressionMilliseconds = output.Diagnostics.Sum(value => value.SuppressionMilliseconds),
                    CoarseWindows = output.Diagnostics.Count(value => value.TemplatesEvaluated > 0),
                    OriginalRefinementCrops = output.Diagnostics.Count(value => value.TemplatesEvaluated == 0),
                    CoarseCandidates = output.Diagnostics.Where(value => value.TemplatesEvaluated > 0).Sum(value => value.Candidates),
                    CandidateBudgetExceededWindows = output.Diagnostics.Count(value => value.CandidateBudgetExceeded),
                    VerificationBudgetExceededWindows = output.Diagnostics.Count(value => value.VerificationBudgetExceeded),
                    InternalMatchLimitExceededWindows = output.Diagnostics.Count(value => value.MatchLimitExceeded),
                    Validation = validation,
                    output.Diagnostics,
                    MatchDetails = output.Matches,
                    Poses = output.Matches.Select(value => value.Pose).ToArray()
                });
                Console.WriteLine($"paper-pins {(repetition < 0 ? "warmup" : $"run {repetition}")}: {output.TotalMilliseconds:F1} ms, read {output.ReadMilliseconds:F1} ms, tiles {loader.Snapshot().TotalTileReads}, matches {output.Matches.Length}, mapped {validation.UniquePins}/256, accuracy={validation.Passed}, complete={complete}");
            }
            catch (Exception error)
            {
                successful = false;
                rows.Add(new
                {
                    Kind = repetition < 0 ? "WARMUP" : "MEASURED",
                    Repetition = repetition,
                    Status = "INCOMPLETE",
                    CorrectnessStatus = "NOT_EVALUATED",
                    WallMilliseconds = timer.Elapsed.TotalMilliseconds,
                    Reads = loader.Snapshot(),
                    Error = error.ToString()
                });
                Console.WriteLine($"paper-pins incomplete: {error.Message}");
            }
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                Profile = "paper-pins",
                Input = "8192×8192 application procedural PCB, original reader, full-image search; synthetic input",
                Roi = new
                {
                    roi.Center.X,
                    roi.Center.Y,
                    roi.Width,
                    roi.Height,
                    roi.Angle
                },
                TrainingOptions = trainingOptions,
                SearchOptions = searchOptions,
                WarmupRuns = 1,
                MeasuredRuns = repetitions,
                TimeoutSecondsPerRun = 120,
                Training = new
                {
                    training.ReadMilliseconds,
                    training.TrainingMilliseconds,
                    Reads = trainingReads,
                    training.Model.TemplateCount,
                    training.Model.EstimatedBytes,
                    EffectiveOptions = training.Model.Options,
                    Levels = training.Model.Levels.Select(value => new { value.Factor, Features = value.Features.Length }).ToArray()
                },
                GroundTruth = new
                {
                    Count = expected.Length,
                    PositionTolerancePixels = 1,
                    Policy = "One-to-one mapping to each complete 80×149 pin; reference offset follows returned rotation because the teaching ROI contains the upper part of a symmetric pin. No claimed field accuracy.",
                    ExpectedPins = expected
                },
                Results = rows
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
        }
        return successful;
    }
}
