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

internal static class KensaTextBenchmark
{
    public static async Task RunAsync(string outputPath, string variant)
    {
        if (variant is not ("tight" or "margin" or "line"))
        {
            throw new ArgumentException("ROI variant must be tight, margin, or line.", nameof(variant));
        }
        var loader = new ImageLoader(Path.GetFullPath("artifacts/kensa-text-work/cache"));
        var path = Path.GetFullPath("kensa.png");
        var image = new ImageReference(path, "kensa", 1024, 1536, DiskPyramidSource.Version(path));
        var roi = variant == "line" ? new RoiGeometry(new(198, 372), 76, 14, 0)
            : variant == "margin" ? new RoiGeometry(new(194.5, 371), 25, 16, 0)
            : new RoiGeometry(new(194.5, 371), 19, 12, 0);
        var service = new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper);
        var training = new TrainingOptions
        {
            PyramidLevels = 3,
            BuildClutterModel = true,
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10
        };
        var options = new SearchOptions
        {
            MaximumParallelism = 12,
            MaximumMatches = 256,
            ClutterWeight = 1
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var trained = await service.TrainAsync(image, false, roi, training, cancellation.Token);

        var rows = new List<object>();
        foreach (var scope in new[] { "self", "full", "repeat1", "repeat2", "repeat3" })
        {
            var region = scope == "self" ? new RoiGeometry(roi.Center, roi.Width + 16, roi.Height + 16, 0) : null;
            var timer = Stopwatch.StartNew();
            var result = await service.SearchAsync(image, false, trained.Model, region, options, cancellation.Token);
            var wall = timer.Elapsed.TotalMilliseconds;
            rows.Add(new
            {
                Scope = scope,
                WallMilliseconds = wall,
                Result = result
            });
            Console.WriteLine($"{variant}/{scope}: {result.Matches.Length} matches, {timer.Elapsed.TotalMilliseconds:F1} ms, truncated={result.Truncated}");
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            InputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            AlgorithmSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShapeMatcher).Assembly.Location))),
            InspectionSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShapeMatchingService).Assembly.Location))),
            Roi = roi,
            RequestedTraining = training,
            Training = trained.Model.Options,
            Options = options,
            trained.Model.HasFineDetailModel,
            Levels = trained.Model.Levels.Select(level => new { level.Factor, Features = level.Features.Length }),
            Results = rows
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }
}
