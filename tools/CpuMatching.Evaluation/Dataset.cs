using System.Runtime.InteropServices;
using System.Text.Json;
using CpuMatching.Core;
using OpenCvSharp;

namespace CpuMatching.Evaluation;

internal sealed record Case(string Name, string Template, string Image, SearchOptions Options, Match[] Expected, string Labels,
    bool GroundTruthComplete = true, string Split = "Development", SearchRegion? Centers = null);

internal static class Dataset
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static GrayImage Read(string path)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (image.Empty())
        {
            throw new IOException($"Cannot read {path}");
        }
        var pixels = new byte[image.Width * image.Height];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GrayImage(image.Width, image.Height, pixels);
    }

    public static Mat ToMat(GrayImage image)
    {
        var mat = new Mat(image.Height, image.Width, MatType.CV_8UC1);
        Marshal.Copy(image.Pixels.ToArray(), 0, mat.Data, image.Width * image.Height);
        return mat;
    }

    public static void Create(string folder, string? kensa)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Dataset directory already exists; choose a new directory.");
        }
        Directory.CreateDirectory(folder);
        var cases = new List<Case>();
        using var part = new Mat(48, 48, MatType.CV_8UC1, new Scalar(30));
        Cv2.Rectangle(part, new Rect(8, 7, 8, 33), new Scalar(220), -1);
        Cv2.Rectangle(part, new Rect(8, 32, 30, 8), new Scalar(220), -1);
        Cv2.Circle(part, new Point(31, 15), 5, new Scalar(180), -1);
        Cv2.Rectangle(part, new Rect(22, 23, 8, 4), new Scalar(120), -1);
        Cv2.ImWrite(Path.Combine(folder, "part-template.png"), part);
        Add("local-zero", part, "part-template.png", 256, [new Match(133.25, 117.75, 0, 1, 1)], false, false);
        Add("local-rotation", part, "part-template.png", 256, [new Match(142.25, 121.75, 17, 1, 1)], false, false);
        Add("local-upside-down", part, "part-template.png", 256, [new Match(125.25, 137.75, 179, 1, 1)], false, false, true);
        Add("local-noise", part, "part-template.png", 256, [new Match(139.25, 118.75, -13, 1, 1)], true, false);
        Add("local-occlusion", part, "part-template.png", 256, [new Match(130.25, 122.75, 12, 1, 1)], false, true);
        Add("local-scale", part, "part-template.png", 256, [new Match(134.25, 119.75, -11, 1.1, 1)], false, false, scale: true);
        Add("negative", part, "part-template.png", 256, [], true, false);
        Add("multiple", part, "part-template.png", 512,
            [new Match(120.25, 110.75, 13, 1, 1), new Match(380.25, 130.75, -17, 1, 1), new Match(250.25, 380.75, 7, 1, 1)], true, false);
        Add("alignment-wide", part, "part-template.png", 1024,
            [new Match(723.25, 628.75, 23, 1, 1)], false, false);
        using var text = new Mat(24, 48, MatType.CV_8UC1, new Scalar(30));
        Cv2.PutText(text, "18V", new Point(3, 19), HersheyFonts.HersheySimplex, 0.55, new Scalar(230), 1, LineTypes.AntiAlias);
        Cv2.ImWrite(Path.Combine(folder, "text-template.png"), text);
        Add("text-rotation", text, "text-template.png", 256, [new Match(128.25, 130.75, -16, 1, 1)], true, false);
        if (kensa is not null)
        {
            using var image = Cv2.ImRead(kensa, ImreadModes.Grayscale);
            if (image.Empty() || image.Width != 1024 || image.Height != 1536)
            {
                throw new InvalidDataException("Expected original Kensa 1024x1536.");
            }
            using var component = new Mat(image, new Rect(156, 158, 89, 52)).Clone();
            Cv2.ImWrite(Path.Combine(folder, "kensa-component-template.png"), component);
            Add("kensa-component-rotation", component, "kensa-component-template.png", 256,
                [new Match(139.25, 128.75, 14, 1, 1)], false, false);
            using var letters = new Mat(image, new Rect(185, 365, 19, 12)).Clone();
            Cv2.ImWrite(Path.Combine(folder, "kensa-18v-template.png"), letters);
            Add("kensa-18v-rotation", letters, "kensa-18v-template.png", 128,
                [new Match(65.25, 63.75, -12, 1, 1)], false, false);
            using var local = new Mat(image, new Rect(150, 320, 120, 100)).Clone();
            Cv2.ImWrite(Path.Combine(folder, "kensa-18v-original.png"), local);
            cases.Add(new Case("kensa-18v-original", "kensa-18v-template.png", "kensa-18v-original.png",
                new SearchOptions { MinimumAngle = -20, MaximumAngle = 20, MaximumMatches = 8 },
                [new Match(44, 50.5, 0, 1, 1)], "Known template crop; other possible instances unverified", GroundTruthComplete: false));
        }
        using var validation = new Mat(61, 73, MatType.CV_8UC1, new Scalar(30));
        Cv2.Rectangle(validation, new Rect(9, 10, 11, 39), new Scalar(190), -1);
        Cv2.Rectangle(validation, new Rect(16, 36, 43, 9), new Scalar(190), -1);
        Cv2.Circle(validation, new Point(45, 20), 8, new Scalar(240), 3);
        Cv2.Line(validation, new Point(55, 47), new Point(63, 54), new Scalar(160), 3);
        Cv2.ImWrite(Path.Combine(folder, "validation-template.png"), validation);
        Add("validation-oblique", validation, "validation-template.png", 256,
            [new Match(138.6, 126.2, -24.4, 1, 1)], true, false, validationCase: true);
        Add("validation-two", validation, "validation-template.png", 512,
            [new Match(130.6, 117.2, 6.8, 1, 1), new Match(360.4, 372.7, -21.6, 1, 1)], true, false, validationCase: true);
        Add("validation-negative", validation, "validation-template.png", 256, [], false, false, validationCase: true);
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Json));
        File.WriteAllText(Path.Combine(folder, "README.md"), "# CPU 매칭 공통 테스트 데이터\n\n" +
            "PNG는 Gray8입니다. cases.json에 템플릿, 검색 이미지, 검색 조건, 정답 중심/시계 방향 각도/배율이 있습니다. " +
            "정답 좌표는 픽셀 인덱스입니다. 합성 장면은 고정 seed 8451와 OpenCV WarpAffine로 생성했습니다. " +
            "원본 Kensa crop 시험과 Kensa crop을 변환한 합성 시험을 구분하세요. 현장 검출률 데이터셋이 아닙니다. " +
            "평가 기준은 위치 1px, 각도 1도이며 점수는 각 알고리즘 내에서만 해석합니다.\n");

        void Add(string name, Mat template, string templateName, int size, Match[] targets,
            bool noise, bool occlusion, bool fullRotation = false, bool scale = false, bool validationCase = false)
        {
            using var scene = new Mat(size, size, MatType.CV_8UC1, new Scalar(30));
            // 대상과 모양이 다른 배경을 모든 방법에 동일하게 제공한다.
            Cv2.Line(scene, new Point(9, 22), new Point(size - 15, 22), new Scalar(100), 2);
            Cv2.Circle(scene, new Point(35, size - 38), 11, new Scalar(140), 2);
            foreach (var target in targets)
            {
                using var matrix = Cv2.GetRotationMatrix2D(new Point2f((template.Width - 1) / 2f, (template.Height - 1) / 2f),
                    -target.AngleDegrees, target.Scale);
                matrix.Set(0, 2, matrix.At<double>(0, 2) + target.X - ((template.Width - 1) / 2.0));
                matrix.Set(1, 2, matrix.At<double>(1, 2) + target.Y - ((template.Height - 1) / 2.0));
                using var warped = new Mat();
                using var maskSource = new Mat(template.Size(), MatType.CV_8UC1, Scalar.All(255));
                using var mask = new Mat();
                Cv2.WarpAffine(template, warped, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(30));
                Cv2.WarpAffine(maskSource, mask, matrix, scene.Size(), InterpolationFlags.Nearest);
                warped.CopyTo(scene, mask);
                if (occlusion)
                {
                    Cv2.Rectangle(scene, new Rect((int)target.X - 23, (int)target.Y + 8, 9, 10), new Scalar(30), -1);
                }
            }
            if (noise)
            {
                var random = new Random(validationCase ? 20261016 : 8451);
                var pixels = new byte[size * size];
                Marshal.Copy(scene.Data, pixels, 0, pixels.Length);
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = (byte)Math.Clamp((pixels[i] * 0.8) + 15 + random.Next(-8, 9), 0, 255);
                }
                Marshal.Copy(pixels, 0, scene.Data, pixels.Length);
            }
            Cv2.ImWrite(Path.Combine(folder, name + ".png"), scene);
            using var annotated = new Mat();
            Cv2.CvtColor(scene, annotated, ColorConversionCodes.GRAY2BGR);
            foreach (var target in targets)
            {
                Cv2.Circle(annotated, new Point((int)target.X, (int)target.Y), 4, Scalar.LimeGreen, 1);
                Cv2.PutText(annotated, $"{target.AngleDegrees}deg", new Point((int)target.X + 10, (int)target.Y),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.LimeGreen, 1);
            }
            Cv2.ImWrite(Path.Combine(folder, name + "-truth.png"), annotated);
            cases.Add(new Case(name, templateName, name + ".png", new SearchOptions
            {
                MinimumAngle = fullRotation ? -180 : -30,
                MaximumAngle = fullRotation ? 180 : 30,
                MinimumScale = scale ? 0.9 : 1,
                MaximumScale = scale ? 1.1 : 1,
                MaximumMatches = 16,
                SuppressionRadius = Math.Min(template.Width, template.Height) * 0.6
            }, targets, "Synthetic known transform; no real-scene recall claim", Split: validationCase ? "Validation" : "Development"));
        }
    }
}
