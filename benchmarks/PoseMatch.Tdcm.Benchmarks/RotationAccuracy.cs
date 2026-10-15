using System.Diagnostics;
using System.Text.Json;
using PoseMatch.Tdcm;

internal static class RotationAccuracy
{
    public static void Run(ITdcmMatcher matcher, TdcmDevice device, byte[] scene, int width, int height,
        PixelImage template, string outputPath)
    {
        const int size = 512;
        var rows = new List<object>();
        foreach (var turns in new[] { 0, 1, 2, 3 })
        {
            var pixels = new byte[size * size * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (dx, dy) = Rotate(x, y, turns);
                    scene.AsSpan((y * width + x) * 3, 3).CopyTo(pixels.AsSpan((dy * size + dx) * 3));
                }
            }
            var (expectedX, expectedY) = Rotate(45, 95, turns);
            foreach (var refine in new[] { false, true })
            {
                var watch = Stopwatch.StartNew();
                var output = matcher.Find(new(pixels, size, size, size * 3, 3), template, new() { RefinePose = refine });
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var nearest = output.Matches.MinBy(m => Math.Pow(m.X - expectedX, 2) + Math.Pow(m.Y - expectedY, 2));
                var positionError = nearest is null ? (double?)null : Math.Sqrt(Math.Pow(nearest.X - expectedX, 2) + Math.Pow(nearest.Y - expectedY, 2));
                var angleError = nearest is null ? (double?)null : Math.Abs(((nearest.AngleDegrees - 50 - turns * 90 + 1080 + 180) % 360) - 180);
                rows.Add(new
                {
                    device = device.ToString(), turns, refine, elapsed,
                    expectedX, expectedY, expectedAngle = (50 + turns * 90) % 360,
                    positionError, angleError, expectedCount = 1, actualCount = output.Matches.Count,
                    acceptance = positionError <= 1 && angleError <= 1 && output.Matches.Count == 1 && !output.CandidatesTruncated,
                    output
                });
            }
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

        static (int X, int Y) Rotate(int x, int y, int turns) => turns switch
        {
            0 => (x, y), 1 => (size - 1 - y, x), 2 => (size - 1 - x, size - 1 - y), _ => (y, size - 1 - x)
        };
    }
}
