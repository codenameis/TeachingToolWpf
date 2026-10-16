using System.Security.Cryptography;
using System.Text.Json;
using CpuMatching.Core;
using OpenCvSharp;
using VisionImaging;

namespace CpuMatching.Evaluation;

internal static class FullModuleDataset
{
    public static void Create(string folder, string kensa)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        using var source = Cv2.ImRead(kensa, ImreadModes.Grayscale);
        using var part = new Mat(source, new Rect(156, 158, 89, 52));
        Cv2.ImWrite(Path.Combine(folder, "kensa-template.png"), part);
        File.Copy(kensa, Path.Combine(folder, "kensa.png"));
        var reference = KensaReference(source, part);
        File.WriteAllText(Path.Combine(folder, "kensa-reference.json"), JsonSerializer.Serialize(reference, Dataset.Json));

        var image = SampleImage();
        var pixels = new byte[106 * 129];
        for (var y = 0; y < 129; y++)
        {
            for (var x = 0; x < 106; x++)
            {
                // 앱의 ROI 학습과 같은 1px 간격. 정답은 아래 생성기 위치로 독립 정의한다.
                pixels[(y * 106) + x] = (byte)Math.Round(image.Sample(759.518 + x - 53, 546.178 + y - 64.5));
            }
        }
        using var sample = Dataset.ToMat(image);
        using var pin = Dataset.ToMat(new GrayImage(106, 129, pixels));
        Cv2.ImWrite(Path.Combine(folder, "8k-modules.png"), sample, new ImageEncodingParam(ImwriteFlags.PngCompression, 9));
        Cv2.ImWrite(Path.Combine(folder, "pin-template.png"), pin);
        var pins = (from cy in Enumerable.Range(0, 4)
                    from cx in Enumerable.Range(0, 4)
                    from row in Enumerable.Range(0, 2)
                    from column in Enumerable.Range(0, 8)
                    select new Match((cx * 2048) + ((column + 2) * 180) + 39.018,
                        (cy * 2048) + (row == 0 ? 545.678 : 1455.678), 0, 1, 1)).ToArray();
        var options = new SearchOptions { MinimumAngle = -30, MaximumAngle = 30, MaximumMatches = 256 };
        Case[] cases =
        [
            new("kensa-full-modules", "kensa-template.png", "kensa.png", options with { SuppressionRadius = 31.2 },
                reference.Select(match => match with { X = match.X - .5, Y = match.Y - .5 }).ToArray(),
                "All 48 manually selected packages, independently registered by OpenCV ECC; a reference comparison, not calibrated physical ground truth.", Split: "FullModuleReference"),
            new("8k-full-modules", "pin-template.png", "8k-modules.png", options with { SuppressionRadius = 63.6 },
                pins, "8192x8192 full search, 256 generator-defined pin poses; synthetic truth, no positions passed to matcher.", Split: "FullModuleValidation")
        ];
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));
        File.WriteAllText(Path.Combine(folder, "reference-provenance.json"), JsonSerializer.Serialize(new
        {
            ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kensa))),
            Method = "OpenCV FindTransformECC / Euclidean / all pixels / 200 iterations / epsilon 1e-7 / Gaussian 5. Manual package centers seed reference registration only; never supplied to search.",
            Interpretation = "Kensa is an independent image-registration reference with all 48 instances. It is not a calibrated position/angle measurement. The 8K file uses generator-defined truth."
        }, Dataset.Json));
    }

    private static Match[] KensaReference(Mat image, Mat template)
    {
        var matches = new List<Match>();
        foreach (var y in new[] { 184d, 245, 499, 560, 819, 879, 1130, 1190 })
        {
            foreach (var x in new[] { 200.5, 296.5, 466.5, 558.5, 726.5, 822.5 })
            {
                var left = (int)Math.Round(x - 44.5);
                var top = (int)Math.Round(y - 26);
                using var crop = new Mat(image, new Rect(left, top, 89, 52));
                using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
                var score = Cv2.FindTransformECC(template, crop, warp, MotionTypes.Euclidean,
                    new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 200, 1e-7), null, 5);
                if (score < .9)
                {
                    throw new InvalidOperationException($"Kensa reference registration failed at {x}/{y}: {score}");
                }
                var px = left + (warp.At<float>(0, 0) * 44) + (warp.At<float>(0, 1) * 25.5) + warp.At<float>(0, 2) + .5;
                var py = top + (warp.At<float>(1, 0) * 44) + (warp.At<float>(1, 1) * 25.5) + warp.At<float>(1, 2) + .5;
                var angle = Math.Atan2(warp.At<float>(1, 0), warp.At<float>(0, 0)) * 180 / Math.PI;
                matches.Add(new(px, py, angle, 1, score));
            }
        }
        return matches.ToArray();
    }

    private static GrayImage SampleImage()
    {
        using var source = new ProceduralTileSource();
        var pixels = new byte[8192 * 8192];
        for (var ty = 0; ty < 16; ty++)
        {
            for (var tx = 0; tx < 16; tx++)
            {
                using var tile = source.ReadOriginalGray8(tx, ty, default);
                for (var y = 0; y < tile.Height; y++)
                {
                    tile.Span.Slice(y * tile.Stride, tile.Width).CopyTo(pixels.AsSpan((((ty * 512) + y) * 8192) + (tx * 512)));
                }
            }
        }
        return new(8192, 8192, pixels);
    }
}
