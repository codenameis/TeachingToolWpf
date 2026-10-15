using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Evaluation;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

if (args.Length < 2)
{
    Console.WriteLine("generate | accuracy-data | accuracy-validation <new-folder> [kensa.png]; run <folder> <report.json> [case-name] [repeats=3]");
    return 1;
}
try
{
    if (args[0] == "accuracy-validation")
    {
        AccuracyDataset.CreateValidation(Path.GetFullPath(args[1]), Path.GetFullPath(args.Length > 2 ? args[2] : "kensa.png"));
        return 0;
    }
    if (args[0] == "accuracy-data")
    {
        AccuracyDataset.Create(Path.GetFullPath(args[1]), Path.GetFullPath(args.Length > 2 ? args[2] : "kensa.png"));
        return 0;
    }
    if (args[0] == "generate")
    {
        Dataset.Create(Path.GetFullPath(args[1]), args.Length > 2 ? Path.GetFullPath(args[2]) : null);
        Console.WriteLine($"테스트 데이터 생성: {Path.GetFullPath(args[1])}");
        return 0;
    }
    if (args[0] != "run" || args.Length < 3)
    {
        throw new ArgumentException("Use generate, accuracy-data, accuracy-validation or run.");
    }
    var folder = Path.GetFullPath(args[1]);
    var cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(folder, "cases.json")))!;
    var repeats = args.Length > 4 ? int.Parse(args[4]) : 3;
    if (repeats is < 1 or > 10)
    {
        throw new ArgumentOutOfRangeException(nameof(repeats));
    }
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    var rows = new List<object>();
    var accuracyFailed = false;
    OpenCvSharp.Cv2.SetNumThreads(1);
    var selected = cases.Where(item => args.Length < 4 || args[3] == "all" || item.Name == args[3]).ToArray();
    if (selected.Length == 0)
    {
        throw new ArgumentException("Unknown case name.");
    }
    foreach (var item in selected)
    {
        var image = Dataset.Read(Path.Combine(folder, item.Image));
        var template = Dataset.Read(Path.Combine(folder, item.Template));
        foreach (var method in new[] { "Line2D", "SubNcc", "DirectionalChamfer", "OpenCvFixed", "OpenCvRotated" })
        {
            var watch = Stopwatch.StartNew();
            ITemplateMatcher matcher = method switch
            {
                "Line2D" => new Line2DMatcher(template),
                "SubNcc" => new SubNccMatcher(template),
                "DirectionalChamfer" => new DirectionalChamferMatcher(template),
                "OpenCvFixed" => new OpenCvBaseline(template, false),
                _ => new OpenCvBaseline(template, true)
            };
            var trainMs = watch.Elapsed.TotalMilliseconds;
            var times = new List<double>();
            var allocatedBytes = new List<long>();
            SearchResult result = null!;
            for (var repeat = 0; repeat <= repeats; repeat++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                result = matcher.Find(image, item.Options, cancel.Token, item.Centers is not { } centers ? null :
                    (x, y) => x >= centers.X && y >= centers.Y && x < centers.X + centers.Width && y < centers.Y + centers.Height);
                times.Add(watch.Elapsed.TotalMilliseconds);
                allocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            }
            var unused = new HashSet<int>(Enumerable.Range(0, result.Matches.Count));
            var positions = new List<double>();
            var angles = new List<double>();
            var scales = new List<double>();
            foreach (var target in item.Expected)
            {
                var nearest = unused.OrderBy(index => Distance(target, result.Matches[index])).FirstOrDefault(-1);
                if (nearest >= 0 && Distance(target, result.Matches[nearest]) < Math.Min(template.Width, template.Height) * 0.5)
                {
                    unused.Remove(nearest);
                    positions.Add(Distance(target, result.Matches[nearest]));
                    angles.Add(PyramidSearch.AngleDistance(target.AngleDegrees, result.Matches[nearest].AngleDegrees));
                    scales.Add(Math.Abs(target.Scale - result.Matches[nearest].Scale));
                }
            }
            var passed = positions.Count == item.Expected.Length && unused.Count == 0 && positions.All(value => value <= 1) &&
                angles.All(value => value <= 1) && scales.All(value => value <= 0.03) && !result.CandidatesTruncated && !result.MatchLimitExceeded;
            accuracyFailed |= item.GroundTruthComplete && !passed && method is "Line2D" or "SubNcc" or "DirectionalChamfer";
            var sorted = times.Skip(1).Order().ToArray();
            var median = (sorted[(repeats - 1) / 2] + sorted[repeats / 2]) / 2;
            rows.Add(new
            {
                item.Name,
                Method = method,
                item.Labels,
                item.GroundTruthComplete,
                item.Split,
                item.Options,
                TrainMs = trainMs,
                FirstMs = times[0],
                WarmMs = times.Skip(1).ToArray(),
                AllocatedBytes = allocatedBytes,
                MedianMs = median,
                Expected = item.Expected,
                Found = result.Matches,
                Missing = item.Expected.Length - positions.Count,
                FalsePositive = unused.Count,
                PositionErrors = positions,
                AngleErrors = angles,
                ScaleErrors = scales,
                KnownTargetsPassed = passed,
                Passed = item.GroundTruthComplete && passed,
                result.CandidatesTruncated,
                result.MatchLimitExceeded,
                result.Evaluations,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, item.Image)))),
                TemplateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, item.Template))))
            });
            var status = item.GroundTruthComplete ? (passed ? "PASS" : "FAIL") : (passed ? "KNOWN_TARGET_PASS / full labels unverified" : "KNOWN_TARGET_FAIL");
            Console.WriteLine($"{item.Name}/{method}: {result.Matches.Count}/{item.Expected.Length}, {median:F1}ms, position={positions.DefaultIfEmpty().Max():F2}, angle={angles.DefaultIfEmpty().Max():F2}, {status}");
            File.WriteAllText(args[2], JsonSerializer.Serialize(new
            {
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                CpuThreads = Environment.ProcessorCount,
                Timing = "CPU Find includes ROI copy, pyramid, features, search, refinement; excludes PNG decode/UI; sequential",
                Rows = rows
            }, Dataset.Json));
        }
    }
    return accuracyFailed ? 2 : 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static double Distance(Match a, Match b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
