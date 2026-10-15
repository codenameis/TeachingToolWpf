using System.Text.Json;
using CpuMatching.Core;
using OpenCvSharp;

namespace CpuMatching.Evaluation;

internal static class AccuracyDataset
{
    public static void CreateValidation(string folder, string kensa)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        using var source = Cv2.ImRead(kensa, ImreadModes.Grayscale);
        using var template = new Mat(source, new Rect(156, 158, 89, 52));
        Cv2.ImWrite(Path.Combine(folder, "template.png"), template);
        var cases = new List<Case>();
        Add("blurred-dim-two", 512, [new(124.7, 142.3, -23.4, 1.05, 1), new(369.2, 345.8, 12.7, .95, 1)], true);
        Add("smaller-rotated", 256, [new(131.4, 124.8, 11.4, .8, 1)], false);
        Add("8k-local-two", 8192, [new(6020.4, 4050.7, 7.6, 1, 1), new(6200.6, 4270.2, -8.4, 1, 1)], false,
            new(5888, 3968, 512, 512));
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));

        void Add(string name, int size, Match[] targets, bool degraded, SearchRegion? region = null)
        {
            var offsetX = region?.X ?? 0;
            var offsetY = region?.Y ?? 0;
            var width = region?.Width ?? size;
            var height = region?.Height ?? size;
            using var canvas = new Mat(height, width, MatType.CV_8UC1, Scalar.All(30));
            Cv2.Line(canvas, new Point(8, 18), new Point(width - 12, 18), Scalar.All(100), 2);
            foreach (var target in targets)
            {
                using var matrix = Cv2.GetRotationMatrix2D(new Point2f(44, 25.5f), -target.AngleDegrees, target.Scale);
                matrix.Set(0, 2, matrix.At<double>(0, 2) + target.X - offsetX - 44);
                matrix.Set(1, 2, matrix.At<double>(1, 2) + target.Y - offsetY - 25.5);
                using var warped = new Mat();
                using var maskSource = new Mat(template.Size(), MatType.CV_8UC1, Scalar.All(255));
                using var mask = new Mat();
                Cv2.WarpAffine(template, warped, matrix, canvas.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(30));
                Cv2.WarpAffine(maskSource, mask, matrix, canvas.Size(), InterpolationFlags.Nearest);
                warped.CopyTo(canvas, mask);
            }
            if (degraded)
            {
                Cv2.GaussianBlur(canvas, canvas, new Size(3, 3), .65);
                canvas.ConvertTo(canvas, MatType.CV_8UC1, .75, 10);
                var random = new Random(20261017);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        canvas.Set(y, x, (byte)Math.Clamp(canvas.At<byte>(y, x) + random.Next(-4, 5), 0, 255));
                    }
                }
            }
            using var image = new Mat(size, size, MatType.CV_8UC1, Scalar.All(30));
            using var destination = new Mat(image, new Rect(offsetX, offsetY, width, height));
            canvas.CopyTo(destination);
            Cv2.ImWrite(Path.Combine(folder, name + ".png"), image);
            cases.Add(new(name, "template.png", name + ".png", new SearchOptions
            {
                MinimumAngle = -30,
                MaximumAngle = 30,
                MinimumScale = .75,
                MaximumScale = 1.25,
                ScaleStep = .1,
                MinimumScore = .75,
                MaximumMatches = 256,
                SuppressionRadius = 31.2,
                Region = region
            }, targets, "Known synthetic transform of original Kensa crop; generated after correction. 8K case searches only the explicit 512x512 region.",
                Split: "AccuracyValidation"));
        }
    }

    public static void Create(string folder, string kensa)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        using var source = Cv2.ImRead(kensa, ImreadModes.Grayscale);
        if (source.Width != 1024 || source.Height != 1536)
        {
            throw new InvalidDataException("Expected original Kensa 1024x1536.");
        }
        using var template = new Mat(source, new Rect(156, 158, 89, 52));
        using var local = new Mat(source, new Rect(17, 1, 367, 366));
        using var negative = new Mat(source, new Rect(120, 270, 220, 100));
        Cv2.ImWrite(Path.Combine(folder, "template.png"), template);
        Cv2.ImWrite(Path.Combine(folder, "kensa-parts.png"), local);
        Cv2.ImWrite(Path.Combine(folder, "kensa-other-components.png"), negative);
        var options = new SearchOptions
        {
            MinimumAngle = -30,
            MaximumAngle = 30,
            MinimumScore = .75,
            SuppressionRadius = 31.2,
            MaximumMatches = 256,
            CandidateLimit = 2048
        };
        Match[] targets = [new(183, 182.5, 0, 1, 1), new(279, 182.5, 0, 1, 1), new(183, 242, 0, 1, 1), new(279, 242, 0, 1, 1)];
        Case[] cases =
        [
            new("kensa-four-parts", "template.png", "kensa-parts.png", options, targets,
                "Four visually annotated memory packages. Manual centers approximately +/-1 pixel; first is exact training crop. Not production ground truth.",
                Centers: new(56, 55, 256, 256)),
            new("kensa-other-components", "template.png", "kensa-other-components.png", options, [],
                "Different controller/connectors below memory packages; no full target package present.")
        ];
        using var truth = new Mat();
        Cv2.CvtColor(local, truth, ColorConversionCodes.GRAY2BGR);
        foreach (var target in targets)
        {
            Cv2.Rectangle(truth, new Rect((int)(target.X - 44), (int)(target.Y - 25.5), 89, 52), Scalar.LimeGreen);
        }
        Cv2.ImWrite(Path.Combine(folder, "kensa-parts-truth.png"), truth);
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));
    }
}
