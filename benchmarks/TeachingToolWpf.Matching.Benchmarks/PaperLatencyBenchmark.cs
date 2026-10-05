using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>동일 빌드에서 두 논문 입력의 원본 검사 시간과 독립 위치 정답을 비교한다. 화면 완료 시험은 별도다.</summary>
internal static class PaperLatencyBenchmark
{
    public static async Task RunAsync(string path, string fixture, int repetitions, string mode, bool paper)
    {
        var loader = new CountingImageLoader(new ImageLoader(Path.GetFullPath("artifacts/paper-125ms-work/cache")));
        using var compute = new VisionCompute.ILGPU.CudaInspectionSession();
        var service = new ShapeMatchingService(loader, () => null, compute.Rent, compute.PrepareAsync,
            paper ? ShapeSearchPipeline.Paper : ShapeSearchPipeline.Classic);
        var image = fixture == "sample"
            ? new ImageReference("sample:8192:8192", "PCB", 8192, 8192)
            : new ImageReference(Path.GetFullPath("kensa.png"), "kensa", 1024, 1536, DiskPyramidSource.Version(Path.GetFullPath("kensa.png")));
        var roi = fixture == "sample" ? new RoiGeometry(new(759.518, 546.178), 105.827, 129.459, 0)
            : new RoiGeometry(new(200.5, 184), 89, 52, 0);
        var training = new TrainingOptions
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
            BuildGeometricModel = false,
            BuildClutterModel = true
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var trained = await service.TrainAsync(image, false, roi, training, cancellation.Token);
        var modes = mode == "cpu" ? new[] { InspectionComputeMode.Cpu }
            : mode == "gpu" ? new[] { InspectionComputeMode.GpuPreprocessing }
            : new[] { InspectionComputeMode.Cpu, InspectionComputeMode.GpuPreprocessing };
        var rows = new List<object>();
        ShapeSearchOutput? reference = null;
        for (var iteration = 0; iteration <= repetitions; iteration++)
        {
            foreach (var backend in iteration % 2 == 0 ? modes : modes.Reverse())
            {
                var preparation = backend == InspectionComputeMode.GpuPreprocessing
                    ? await service.PrepareGpuAsync(cancellation.Token) : 0;
                var options = new SearchOptions
                {
                    MaximumMatches = 256,
                    EvaluateGeometry = false,
                    ClutterWeight = 1,
                    MaximumParallelism = Math.Clamp(Environment.ProcessorCount, 1, 16),
                    ComputeMode = backend
                };
                loader.Reset();
                var allocated = GC.GetTotalAllocatedBytes();
                var timer = Stopwatch.StartNew();
                var output = await service.SearchAsync(image, false, trained.Model, null, options, cancellation.Token);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                reference ??= output;
                object validation = fixture == "sample" ? PinGroundTruth.Validate(PinGroundTruth.Create(), output.Matches)
                    : ValidateKensa(output);
                rows.Add(new
                {
                    Fixture = fixture,
                    PaperPipeline = paper,
                    Iteration = iteration,
                    Mode = backend.ToString(),
                    WallMilliseconds = elapsed,
                    DevicePreparationMilliseconds = preparation,
                    options,
                    training,
                    Validation = validation,
                    ExactFirstResultParity = reference.Matches.SequenceEqual(output.Matches),
                    AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    Reads = loader.Snapshot(),
                    Output = output
                });
                Console.WriteLine($"{fixture} {backend} #{iteration}: {elapsed:F2} ms, read={output.ReadMilliseconds:F2}, count={output.Matches.Length}, incomplete={output.Truncated}, outputLimit={output.MatchLimitExceeded}, prepare={preparation:F1}");
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Timing = "Service call wall time includes original input and resource return; UI/Present excluded. Device preparation separate. No result cache.",
            AlgorithmSha256 = Hash(typeof(ShapeMatcher).Assembly.Location),
            InspectionSha256 = Hash(typeof(ShapeMatchingService).Assembly.Location),
            ImagingSha256 = Hash(typeof(ImageLoader).Assembly.Location),
            KensaSha256 = Hash(Path.GetFullPath("kensa.png")),
            TrainingRoi = roi,
            Results = rows
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    private static object ValidateKensa(ShapeSearchOutput output)
    {
        // 원본 육안 annotation: 보드 3열×4행, 각 보드 메모리 칩 2열×2행. 검출 위치에서 만들지 않는다.
        double[] columns = [200, 296, 466, 558, 726, 822];
        double[] rows = [184, 245, 499, 560, 819, 879, 1130, 1190];
        var expected = rows.SelectMany(y => columns.Select(x => new PointD(x, y))).ToArray();
        var assignments = new int[expected.Length];
        var unmatched = 0;
        foreach (var match in output.Matches)
        {
            var nearest = Enumerable.Range(0, expected.Length).OrderBy(index => Distance(expected[index], match)).First();
            if (Distance(expected[nearest], match) <= 12)
            {
                assignments[nearest]++;
            }
            else
            {
                unmatched++;
            }
        }
        return new
        {
            Annotation = "Independent visual memory-chip centers; assignment radius 12 original pixels; not a subpixel pose-accuracy claim.",
            Expected = expected,
            Missing = assignments.Count(count => count == 0),
            Duplicates = assignments.Sum(count => Math.Max(0, count - 1)),
            Unmatched = unmatched,
            Passed = assignments.All(count => count == 1) && unmatched == 0
        };
    }

    private static double Distance(PointD point, ShapeMatch match)
        => Math.Sqrt(Math.Pow(point.X - match.Pose.X, 2) + Math.Pow(point.Y - match.Pose.Y, 2));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
