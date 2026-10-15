using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

internal static class InspectionSpeedBenchmark
{
    public static async Task<bool> RunAsync(string outputPath, int parallelism)
    {
        var loader = new CountingImageLoader(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")));
        var service = new ShapeMatchingService(loader, () => null,
            () => new VisionCompute.ILGPU.CudaImagePreprocessor());
        var roi = new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0);
        var rows = new List<object>();
        var passed = true;
        foreach (var width in new[] { 8192, 24576 })
        {
            var image = new ImageReference($"sample:{width}:8192", "sample", width, 8192);
            var expected = PinGroundTruth.Create(width / 2048, 4);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var training = await service.TrainAsync(image, false, roi, new()
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 10,
                BuildClutterModel = true
            }, cancellation.Token);
            ShapeSearchOutput? cpu = null;
            foreach (var mode in new[] { InspectionComputeMode.Cpu, InspectionComputeMode.GpuPreprocessing })
            {
                var options = new SearchOptions
                {
                    MaximumMatches = 1000,
                    MaximumCandidates = width > 8192 ? 8192 : 2048,
                    ClutterWeight = 1,
                    MaximumParallelism = parallelism,
                    ComputeMode = mode
                };
                for (var repetition = -1; repetition < 3; repetition++)
                {
                    loader.Reset();
                    var allocated = GC.GetTotalAllocatedBytes();
                    var watch = Stopwatch.StartNew();
                    var result = await service.SearchAsync(image, false, training.Model, null, options, cancellation.Token);
                    var wall = watch.Elapsed.TotalMilliseconds;
                    var validation = PinGroundTruth.Validate(expected, result.Matches);
                    if (mode == InspectionComputeMode.Cpu)
                    {
                        cpu = result;
                    }
                    var parity = cpu is not null && cpu.Matches.SequenceEqual(result.Matches);
                    passed &= validation.Passed && !result.Truncated && !result.MatchLimitExceeded && parity;
                    rows.Add(new
                    {
                        Width = width,
                        Height = image.Height,
                        Mode = mode.ToString(),
                        Repetition = repetition,
                        Warmup = repetition < 0,
                        Options = options,
                        WallMilliseconds = wall,
                        result.TotalMilliseconds,
                        result.ReadMilliseconds,
                        PreprocessingMilliseconds = result.Diagnostics.Sum(value => value.PreprocessingMilliseconds),
                        CandidateMilliseconds = result.Diagnostics.Sum(value => value.CandidateMilliseconds),
                        VerificationMilliseconds = result.Diagnostics.Sum(value => value.VerificationMilliseconds),
                        RefinementMilliseconds = result.Diagnostics.Sum(value => value.RefinementMilliseconds),
                        result.Preprocessing,
                        result.Windows,
                        OriginalCrops = result.Diagnostics.Count(value => value.TemplatesEvaluated == 0),
                        result.Truncated,
                        result.MatchLimitExceeded,
                        Reads = loader.Snapshot(),
                        AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        Count = result.Matches.Length,
                        Validation = validation,
                        GpuCpuParity = parity,
                        MatchDetails = result.Matches
                    });
                    Console.WriteLine($"{width} {mode} {repetition}: {wall:F1} ms, read {result.ReadMilliseconds:F1}, prepare {result.Diagnostics.Sum(value => value.PreprocessingMilliseconds):F1}, candidate {result.Diagnostics.Sum(value => value.CandidateMilliseconds):F1}, verify {result.Diagnostics.Sum(value => value.VerificationMilliseconds):F1}, refine {result.Diagnostics.Sum(value => value.RefinementMilliseconds):F1}, {result.Matches.Length}/{expected.Length}, complete={!result.Truncated && !result.MatchLimitExceeded}, accuracy={validation.Passed}, parity={parity}");
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
                    {
                        Timestamp = DateTimeOffset.Now,
                        Input = "Full original procedural PCB, 8192×8192 and 24576×8192; synthetic accuracy only",
                        WarmupRuns = 1,
                        MeasuredRuns = 3,
                        Results = rows
                    }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                }
            }
        }
        return passed;
    }
}
