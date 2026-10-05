using System.Diagnostics;
using System.Text.Json;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;

if (args.Length > 1 && args[1] == "rotation-diagnosis")
{
    await RotationDiagnosisBenchmark.RunAsync(args[0]);
    return;
}

if (args.Length > 1 && args[1] == "kensa-text")
{
    await KensaTextBenchmark.RunAsync(args[0], args.Length > 2 ? args[2] : "tight");
    return;
}

if (args.Length > 1 && args[1] == "paper-latency")
{
    await PaperLatencyBenchmark.RunAsync(args[0], args.Length > 2 ? args[2] : "sample",
        args.Length > 3 ? int.Parse(args[3]) : 3, args.Length > 4 ? args[4] : "both",
        args.Length > 5 && args[5] == "paper");
    return;
}

if (args.Length > 1 && args[1] == "shape-target")
{
    Environment.ExitCode = await ShapeTargetBenchmark.RunAsync(args[0],
        args.Length > 2 ? int.Parse(args[2]) : 3, args.Length > 3 ? int.Parse(args[3]) : 4,
        args.Length > 4 ? args[4] : "both") ? 0 : 1;
    return;
}

if (args.Length > 1 && args[1] == "shape-regression")
{
    Environment.ExitCode = await ShapeRegressionBenchmark.RunAsync(args[0],
        args.Length > 2 ? int.Parse(args[2]) : 1, args.Length > 3 ? args[3] : "all") ? 0 : 1;
    return;
}

if (args.Length > 1 && args[1] == "inspection-speed")
{
    Environment.ExitCode = await InspectionSpeedBenchmark.RunAsync(args[0], args.Length > 2 ? int.Parse(args[2]) : 1) ? 0 : 1;
    return;
}

if (args.Length > 1 && args[1] == "inspection-controls")
{
    Environment.ExitCode = await InspectionControlsBenchmark.RunAsync(args[0]) ? 0 : 1;
    return;
}

if (args.Length > 1 && args[1] == "paper-pins")
{
    Environment.ExitCode = await PaperPinBenchmark.RunAsync(args[0], args.Length > 2 ? int.Parse(args[2]) : 3) ? 0 : 1;
    return;
}

var outputPath = args[0];
var dimension = args.Length > 1 ? int.Parse(args[1]) : 8192;
var size = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 512;
var repetitions = args.Length > 3 ? int.Parse(args[3]) : 3;
var height = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : size;
var method = args.Length > 5 ? args[5] : "shape";
var centerX = args.Length > 6 ? double.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : dimension / 2d;
var centerY = args.Length > 7 ? double.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture) : dimension / 2d;
var reference = new ImageReference($"sample:{dimension}:{dimension}", "sample", dimension, dimension);
var roi = new RoiGeometry(new(centerX, centerY), size, height, 0);
var service = new ShapeMatchingService(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")), () => null);
var rows = new List<object>();
for (var repetition = 0; repetition < repetitions; repetition++)
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var timer = Stopwatch.StartNew();
    try
    {
        if (method == "opencv")
        {
            var templateService = new TemplateMatchingService(new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache")), () => null);
            var model = await templateService.TrainAsync(reference, false, roi, cancellation.Token);
            var output = await templateService.SearchAsync(reference, false, model.Model, null, new(), cancellation.Token);
            rows.Add(new
            {
                Repetition = repetition,
                Status = "COMPLETED",
                CorrectnessStatus = "NOT_EVALUATED",
                TrainingMilliseconds = model.Milliseconds,
                output.TotalMilliseconds,
                output.ReadMilliseconds,
                output.Windows,
                output.Truncated,
                output.MatchLimitExceeded,
                Matches = output.Matches.Length,
                Poses = output.Matches.Select(value => value.ReferencePoint).ToArray()
            });
            Console.WriteLine($"OpenCV {dimension}px ROI{size}: {output.TotalMilliseconds:F1}ms, {output.Windows} windows, {output.Matches.Length} matches, incomplete={output.Truncated}, outputLimited={output.MatchLimitExceeded}");
        }
        else
        {
            var model = await service.TrainAsync(reference, false, roi,
                new()
                {
                    MinimumAngleDegrees = -180,
                    MaximumAngleDegrees = 180,
                    AngleStepDegrees = 10
                }, cancellation.Token);
            var trained = timer.Elapsed.TotalMilliseconds;
            var output = await service.SearchAsync(reference, false, model.Model, null, new(), cancellation.Token);
            rows.Add(new
            {
                Repetition = repetition,
                Status = "COMPLETED",
                CorrectnessStatus = "NOT_EVALUATED",
                TrainingMilliseconds = trained,
                output.TotalMilliseconds,
                output.ReadMilliseconds,
                output.Windows,
                output.Truncated,
                output.MatchLimitExceeded,
                Matches = output.Matches.Length,
                Diagnostics = output.Diagnostics,
                Levels = model.Model.Levels.Select(value => new { value.Factor, Features = value.Features.Length }),
                Preprocessing = output.Diagnostics.Sum(value => value.PreprocessingMilliseconds),
                Candidates = output.Diagnostics.Sum(value => value.CandidateMilliseconds),
                Verification = output.Diagnostics.Sum(value => value.VerificationMilliseconds),
                Refinement = output.Diagnostics.Sum(value => value.RefinementMilliseconds),
                Poses = output.Matches.Select(value => value.Pose).ToArray()
            });
            Console.WriteLine($"{dimension}px ROI{size}: {output.TotalMilliseconds:F1}ms, {output.Windows} windows, {output.Matches.Length} matches, incomplete={output.Truncated}, outputLimited={output.MatchLimitExceeded}");
        }
    }
    catch (Exception error)
    {
        rows.Add(new
        {
            Repetition = repetition,
            Status = "INCOMPLETE",
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Error = error.Message
        });
        Console.WriteLine(error.Message);
    }
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
    {
        Timestamp = DateTimeOffset.Now,
        Method = method,
        Dimension = dimension,
        RoiSize = size,
        RoiHeight = height,
        CenterX = centerX,
        CenterY = centerY,
        Input = "8192-class application procedural sample, original reader, full-image search",
        TimeoutSeconds = 120,
        PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
        Results = rows
    }, new JsonSerializerOptions { WriteIndented = true }));
}
