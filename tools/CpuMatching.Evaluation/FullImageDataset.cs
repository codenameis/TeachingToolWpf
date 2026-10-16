using System.Text.Json;
using CpuMatching.Core;
using OpenCvSharp;

namespace CpuMatching.Evaluation;

internal static class FullImageDataset
{
    public static void CreatePhaseCases(string sourceFolder, string folder)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        var original = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(sourceFolder, "cases.json")))![0];
        using var source = Cv2.ImRead(Path.Combine(sourceFolder, original.Image), ImreadModes.Grayscale);
        File.Copy(Path.Combine(sourceFolder, original.Template), Path.Combine(folder, "template.png"));
        var cases = new List<Case>();
        for (var index = 0; index < original.Expected.Length; index++)
        {
            var target = original.Expected[index];
            // 4픽셀 정렬로 두 번의 2×2 평균 위상을 전체 영상과 동일하게 보존한다.
            var left = Math.Clamp(((int)(target.X / 4) * 4) - 128, 0, source.Width - 256);
            var top = Math.Clamp(((int)(target.Y / 4) * 4) - 128, 0, source.Height - 256);
            var name = $"phase-{index}";
            using var crop = new Mat(source, new Rect(left, top, 256, 256));
            Cv2.ImWrite(Path.Combine(folder, name + ".png"), crop);
            cases.Add(new(name, "template.png", name + ".png", original.Options,
                [target with { X = target.X - left, Y = target.Y - top }],
                "8K full-search alias regression; same pyramid phase and score .75. Full-image coverage is tested separately.", Split: "PhaseValidation"));
        }
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));
    }

    public static void Create(string folder, string kensa)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        using var source = Cv2.ImRead(kensa, ImreadModes.Grayscale);
        using var template = new Mat(source, new Rect(156, 158, 89, 52));
        Cv2.ImWrite(Path.Combine(folder, "template.png"), template);
        Match[] targets =
        [
            new(80.4, 80.7, 7.6, 1, 1), new(8105.6, 80.2, -8.4, 1, 1),
            new(80.4, 8105.7, 12.7, 1, 1), new(8105.6, 8105.2, -12.7, 1, 1),
            new(4095.4, 4095.7, 7.6, 1, 1), new(2048.6, 6144.2, -8.4, 1, 1),
            new(6144.4, 2048.7, 12.7, 1, 1), new(6144.6, 6144.2, -12.7, 1, 1)
        ];
        using var canvas = new Mat(8192, 8192, MatType.CV_8UC1, Scalar.All(30));
        foreach (var target in targets)
        {
            var left = Math.Clamp((int)target.X - 128, 0, 8192 - 256);
            var top = Math.Clamp((int)target.Y - 128, 0, 8192 - 256);
            using var matrix = Cv2.GetRotationMatrix2D(new Point2f(44, 25.5f), -target.AngleDegrees, 1);
            matrix.Set(0, 2, matrix.At<double>(0, 2) + target.X - left - 44);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + target.Y - top - 25.5);
            using var warped = new Mat();
            Cv2.WarpAffine(template, warped, matrix, new Size(256, 256), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(30));
            using var destination = new Mat(canvas, new Rect(left, top, 256, 256));
            warped.CopyTo(destination);
        }
        Cv2.ImWrite(Path.Combine(folder, "8k-full-eight.png"), canvas);
        Case[] cases = [new("8k-full-eight", "template.png", "8k-full-eight.png", new SearchOptions
        {
            MinimumAngle = -30, MaximumAngle = 30, MinimumScore = .75,
            CandidateLimit = 2048, MaximumMatches = 256, SuppressionRadius = 31.2
        }, targets, "8192x8192 FULL search. Eight known Kensa transforms at corners, center and both sides of 4096. Synthetic truth, not production recall.", Split: "FullImageValidation")];
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));
    }
}
