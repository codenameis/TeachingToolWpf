using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace CpuMatching.Evaluation;

internal static class SplitBenchmark
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("split <folder> <report.json> [case|all] [repeats=3] [method|all] [workers=4] [overlap|aligned]");
        }
        var folder = Path.GetFullPath(args[1]);
        var report = Path.GetFullPath(args[2]);
        var cases = JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(folder, "cases.json")))!;
        var selected = cases.Where(item => args.Length < 4 || args[3] == "all" || item.Name == args[3]).ToArray();
        var repeats = args.Length > 4 ? int.Parse(args[4]) : 3;
        var methods = new[] { "Line2D", "SubNcc", "DirectionalChamfer" }
            .Where(method => args.Length < 6 || args[5] == "all" || method == args[5]).ToArray();
        var workers = args.Length > 6 ? int.Parse(args[6]) : 4;
        var padding = args.Length > 7 ? args[7] : "overlap";
        if (selected.Length == 0 || methods.Length == 0 || repeats is < 1 or > 10 || workers is < 1 or > 64 ||
            padding is not ("overlap" or "aligned"))
        {
            throw new ArgumentException("Unknown case/method or invalid repeat/worker count.");
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var rows = new List<object>();
            var passed = true;
            using var process = Process.GetCurrentProcess();
            OpenCvSharp.Cv2.SetNumThreads(1);
            foreach (var item in selected)
            {
                if (item.Centers is not null || !item.GroundTruthComplete)
                {
                    throw new ArgumentException("Split benchmark requires complete full-image labels.");
                }
                var imagePath = Path.Combine(folder, item.Image);
                var templatePath = Path.Combine(folder, item.Template);
                var image = Dataset.Read(imagePath);
                var template = Dataset.Read(templatePath);
                var imageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)));
                var templateHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(templatePath)));
                foreach (var method in methods)
                {
                    ITemplateMatcher Create() => method switch
                    {
                        "Line2D" => new Line2DMatcher(template),
                        "SubNcc" => new SubNccMatcher(template),
                        _ => new DirectionalChamferMatcher(template)
                    };
                    var matchers = Enumerable.Range(0, 16).Select(_ => Create()).ToArray();
                    Match[]? reference = null;
                    for (var repeat = 0; repeat < repeats; repeat++)
                    {
                        // 반복마다 순서를 순환해 항상 마지막 분할만 예열 이득을 얻지 않게 한다.
                        var counts = new[] { 1, 8, 16 };
                        foreach (var count in counts.Skip(repeat % 3).Concat(counts.Take(repeat % 3)))
                        {
                            var options = item.Options with
                            {
                                MaximumDegreeOfParallelism = workers
                            };
                            var allocated = GC.GetTotalAllocatedBytes(true);
                            var cpu = process.TotalProcessorTime;
                            var watch = Stopwatch.StartNew();
                            TiledInspection.RunResult? split = null;
                            SearchResult result;
                            if (count == 1)
                            {
                                result = matchers[0].Find(image, options, cancellation.Token);
                            }
                            else
                            {
                                split = TiledInspection.Find(image, template, matchers.Take(count).ToArray(), options, workers, padding == "overlap", cancellation.Token);
                                result = split.Search;
                            }
                            var elapsed = watch.Elapsed.TotalMilliseconds;
                            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                            reference ??= result.Matches.ToArray();
                            var accuracy = Measure(item.Expected, result);
                            var agreement = Measure(reference, result);
                            passed &= accuracy.Passed && agreement.Passed;
                            rows.Add(new
                            {
                                item.Name,
                                Method = method,
                                Tiles = count,
                                Workers = workers,
                                Padding = padding,
                                Repeat = repeat,
                                image.Width,
                                image.Height,
                                ImageSha256 = imageHash,
                                TemplateSha256 = templateHash,
                                Options = options,
                                Milliseconds = elapsed,
                                CpuMilliseconds = cpuMs,
                                AllocatedBytes = bytes,
                                Accuracy = accuracy,
                                BaselineAgreement = agreement,
                                Result = result,
                                Partitions = split?.Tiles,
                                MergeMilliseconds = split?.MergeMilliseconds,
                                ReadPixels = split?.Tiles.Sum(tile => (long)tile.Tile.Read.Width * tile.Tile.Read.Height) ?? ((long)image.Width * image.Height)
                            });
                            Save(complete: false);
                            Console.WriteLine($"{item.Name} {method} tiles={count} workers={workers} run={repeat + 1}: {elapsed:F1}ms; found={result.Matches.Count}/{item.Expected.Length}; pos={accuracy.MaximumPositionError:F4}px angle={accuracy.MaximumAngleError:F4}; accuracy={accuracy.Passed} baseline={agreement.Passed}");
                        }
                    }
                }
            }
            Save(complete: true);
            return passed ? 0 : 2;

            void Save(bool complete)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    Scope = "CPU; already aligned image. Crop, preparation, search and merge included; alignment, decoding, training and WPF display excluded. No result reuse.",
                    Complete = complete,
                    Passed = passed,
                    Rows = rows
                }, Dataset.Json));
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private sealed record Accuracy(bool Passed, int Missing, int Extra, double MaximumPositionError, double MaximumAngleError, double MaximumScaleError);

    private static Accuracy Measure(IReadOnlyList<Match> expected, SearchResult result)
    {
        var remaining = new HashSet<int>(Enumerable.Range(0, result.Matches.Count));
        var positions = new List<double>();
        var angles = new List<double>();
        var scales = new List<double>();
        foreach (var target in expected)
        {
            var nearest = remaining.OrderBy(index => Distance(target, result.Matches[index])).FirstOrDefault(-1);
            if (nearest < 0 || Distance(target, result.Matches[nearest]) > 1)
            {
                continue;
            }
            remaining.Remove(nearest);
            var match = result.Matches[nearest];
            positions.Add(Distance(target, match));
            angles.Add(PyramidSearch.AngleDistance(target.AngleDegrees, match.AngleDegrees));
            scales.Add(Math.Abs(target.Scale - match.Scale));
        }
        var missing = expected.Count - positions.Count;
        var angle = angles.DefaultIfEmpty(0).Max();
        var scale = scales.DefaultIfEmpty(0).Max();
        return new(missing == 0 && remaining.Count == 0 && angle <= 1 && scale <= .03 &&
            !result.CandidatesTruncated && !result.MatchLimitExceeded, missing, remaining.Count,
            positions.DefaultIfEmpty(0).Max(), angle, scale);
    }

    private static double Distance(Match left, Match right)
        => Math.Sqrt(((left.X - right.X) * (left.X - right.X)) + ((left.Y - right.Y) * (left.Y - right.Y)));
}
