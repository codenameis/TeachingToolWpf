using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using OpenCvSharp;

namespace CpuMatching.Evaluation;

/// <summary>매 호출마다 영상과 모델을 바꾸는 작은 ROI의 준비 포함 CPU 측정.</summary>
internal static class NarrowRoiBenchmark
{
    public static int Run(string[] args)
    {
        Cv2.SetNumThreads(1);
        if (args[0] == "narrow-data")
        {
            CreateData(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return 0;
        }
        var folder = Path.GetFullPath(args[1]);
        var repeats = args.Length > 3 ? int.Parse(args[3]) : 7;
        var warmups = args.Length > 4 ? int.Parse(args[4]) : 4;
        if (repeats is < 1 or > 30 || warmups is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(repeats));
        }
        var cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(folder, "cases.json")))!;
        var inputs = cases.Select(item => (Case: item, Image: Dataset.Read(Path.Combine(folder, item.Image)),
            Template: Dataset.Read(Path.Combine(folder, item.Template)))).ToArray();
        var rows = new List<object>();
        string[] methods = ["Line2D", "SubNcc"];
        // 첫 순회 및 워밍업도 기록한다. 측정 중 디코딩/강제 GC/모델 결과 캐시는 없다.
        for (var round = -warmups; round < repeats; round++)
        {
            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[(index + round + warmups) % inputs.Length];
                for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    var method = methods[(methodIndex + round + warmups) % methods.Length];
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var watch = Stopwatch.StartNew();
                    ITemplateMatcher matcher = method switch
                    {
                        "Line2D" => new Line2DMatcher(input.Template),
                        _ => new SubNccMatcher(input.Template)
                    };
                    var trainMs = watch.Elapsed.TotalMilliseconds;
                    var result = matcher.Find(input.Image, input.Case.Options, acceptCenter: input.Case.Centers is not { } centers ? null :
                        (x, y) => x >= centers.X && y >= centers.Y && x < centers.X + centers.Width && y < centers.Y + centers.Height);
                    var totalMs = watch.Elapsed.TotalMilliseconds;
                    var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result with
                    {
                        Timings = null
                    })));
                    rows.Add(new
                    {
                        input.Case.Name,
                        Method = method,
                        Round = round,
                        Phase = round < 0 ? round == -warmups ? "First" : "Warmup" : "Measured",
                        TrainMs = trainMs,
                        TotalMs = totalMs,
                        SearchMs = totalMs - trainMs,
                        AllocatedBytes = allocatedBytes,
                        ResultFingerprint = fingerprint,
                        Result = result,
                        input.Case.Options
                    });
                    Console.WriteLine($"{round,2} {input.Case.Name} {method}: {totalMs:F2} ms " +
                        $"prep={result.Timings!.Preparation:F2} coarse={result.Timings.CoarseSearch:F2} " +
                        $"refine={result.Timings.Refinement:F2} verify={result.Timings.Verification:F2}");
                }
            }
        }
        File.WriteAllText(Path.GetFullPath(args[2]), JsonSerializer.Serialize(new
        {
            Scope = "CPU model creation + Find; decoding/UI excluded; fresh matcher every call; no previous-frame result reuse",
            WarmupRounds = warmups,
            MeasuredRounds = repeats,
            Assemblies = new[] { typeof(PyramidSearch), typeof(Line2DMatcher), typeof(SubNccMatcher) }
                .Select(type => new { Name = type.Assembly.GetName().Name, Sha256 = Hash(type.Assembly.Location) }),
            Inputs = cases.Select(item => new { item.Name, ImageSha256 = Hash(Path.Combine(folder, item.Image)), TemplateSha256 = Hash(Path.Combine(folder, item.Template)) }),
            Rows = rows
        }, Dataset.Json));
        return 0;
    }

    private static void CreateData(string root, string folder)
    {
        if (Directory.Exists(folder))
        {
            throw new IOException("Choose a new dataset directory.");
        }
        Directory.CreateDirectory(folder);
        var cases = new List<Case>();
        var crops = new List<object>();
        using (var source = Cv2.ImRead(Path.Combine(root, "ken32.png"), ImreadModes.Grayscale))
        {
            if (source.Empty())
            {
                throw new IOException("Cannot read ken32.png.");
            }
            int[] sizes = [32, 48, 64, 96];
            for (var index = 0; index < sizes.Length; index++)
            {
                var size = sizes[index];
                var seedX = source.Width * (index + 1) / 5;
                var seedY = source.Height * (4 - index) / 5;
                var best = new Rect();
                var bestContrast = -1d;
                // 사전에 고정한 위치 주변에서 명암이 있는 패치를 고른다. 검출 결과로 정답을 만들지 않는다.
                for (var dy = -8; dy <= 8; dy++)
                {
                    for (var dx = -8; dx <= 8; dx++)
                    {
                        var rect = new Rect(seedX + (dx * 96), seedY + (dy * 96), size, size);
                        using var patch = new Mat(source, rect);
                        Cv2.MeanStdDev(patch, out _, out Scalar deviation);
                        if (deviation.Val0 > bestContrast && HasEnoughEdges(patch))
                        {
                            best = rect;
                            bestContrast = deviation.Val0;
                        }
                    }
                }
                if (bestContrast < 0)
                {
                    throw new InvalidDataException($"No trainable {size}px patch in the configured ken32 region.");
                }
                var name = $"ken32-roi-{size}";
                var sceneRect = new Rect(best.X - 48, best.Y - 48, size + 96, size + 96);
                using var template = new Mat(source, best);
                using var scene = new Mat(source, sceneRect);
                Cv2.ImWrite(Path.Combine(folder, name + "-template.png"), template);
                Cv2.ImWrite(Path.Combine(folder, name + ".png"), scene);
                cases.Add(new(name, name + "-template.png", name + ".png", new CpuSearchOptions
                {
                    MinimumAngle = -30,
                    MaximumAngle = 30,
                    SuppressionRadius = size * .6
                }, [new(48 + ((size - 1) / 2d), 48 + ((size - 1) / 2d), 0, 1, 1)],
                    "Original ken32 crop; self-match location known; other targets unlabelled", GroundTruthComplete: false));
                crops.Add(new
                {
                    Name = name,
                    Template = best,
                    Scene = sceneRect,
                    Contrast = bestContrast
                });
            }
        }
        var existingFolder = Path.Combine(root, "samples", "CpuMatching.Data");
        var existing = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(existingFolder, "cases.json")))!;
        foreach (var item in existing.Where(item => item.Name is "local-rotation" or "text-rotation" or "kensa-component-rotation" or "validation-negative"))
        {
            File.Copy(Path.Combine(existingFolder, item.Image), Path.Combine(folder, item.Image));
            File.Copy(Path.Combine(existingFolder, item.Template), Path.Combine(folder, item.Template));
            cases.Add(item);
        }
        File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases, Dataset.Json));
        File.WriteAllText(Path.Combine(folder, "provenance.json"), JsonSerializer.Serialize(new
        {
            Source = "ken32.png",
            SourceSha256 = Hash(Path.Combine(root, "ken32.png")),
            Crops = crops,
            Note = "ROI-only timing and before/after equivalence; not whole-image ken32 ground truth"
        }, Dataset.Json));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HasEnoughEdges(Mat patch)
    {
        using var contiguous = patch.Clone();
        var pixels = new byte[patch.Width * patch.Height];
        Marshal.Copy(contiguous.Data, pixels, 0, pixels.Length);
        return Edges.Extract(new CpuGrayImage(patch.Width, patch.Height, pixels), 0, 0).Length >= 6;
    }
}
