using System.Text.Json;
using System.Text.Json.Serialization;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

/// <summary>사용자가 실행 중인 PCB 티칭 영역의 180도 오인식을 재현한다.</summary>
internal static class RotationDiagnosisBenchmark
{
    public static async Task RunAsync(string path)
    {
        var image = new ImageReference("sample:2048:2048", "PCB single period", 2048, 2048);
        var roi = new RoiGeometry(new(760.793, 598.841), 110.787, 245.01, 0);
        var service = new ShapeMatchingService(new ImageLoader(Path.GetFullPath("artifacts/rotation-diagnosis/cache")),
            () => null, pipeline: ShapeSearchPipeline.Paper);
        var results = new List<object>();
        foreach (var contrast in new[] { .2, 0d })
        {
            foreach (var wide in new[] { false, true })
            {
                var training = new TrainingOptions
                {
                    MinimumAngleDegrees = wide ? -180 : 0,
                    MaximumAngleDegrees = wide ? 180 : 10,
                    AngleStepDegrees = 10,
                    MinimumFeatureContrastRatio = contrast,
                    BuildGeometricModel = false,
                    BuildClutterModel = true
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var trained = await service.TrainAsync(image, false, roi, training, timeout.Token);
                var options = new SearchOptions
                {
                    EvaluateGeometry = false,
                    ClutterWeight = 1,
                    MaximumMatches = 256,
                    MaximumParallelism = Math.Clamp(Environment.ProcessorCount, 1, 16)
                };
                var output = await service.SearchAsync(image, false, trained.Model, null, options, timeout.Token);
                var lower = output.Matches.Where(match => match.Pose.Y > 1000).ToArray();
                results.Add(new
                {
                    training,
                    options,
                    trained.Model.Levels,
                    Output = output
                });
                Console.WriteLine($"contrast={contrast} wide={wide} count={output.Matches.Length} lower={lower.Length} time={output.TotalMilliseconds:F1} truncated={output.Truncated}");
                foreach (var match in lower)
                {
                    Console.WriteLine($"lower ({match.Pose.X:F2},{match.Pose.Y:F2}) angle={match.Pose.Angle * 180 / Math.PI:F3} score={match.Score:F5} direction={match.DirectionScore:F5} clutter={match.ClutterPenaltyRatio:F5}");
                }
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Image = image,
            TeachingRoi = roi,
            Results = results
        },
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }
}
