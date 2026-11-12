using System.Diagnostics;
using System.Text.Json;
using CpuMatching.Cuda;
using TeachingToolWpf.Imaging.Matching;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

internal static class CpuWindowedBenchmark
{
    public static async Task RunAsync(string outputPath, string path, string methodName, bool useCuda, int profileBatches = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(profileBatches);
        var loader = new ImageLoader(Path.GetFullPath("artifacts/matching-benchmark-cache"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var profile = new CpuSearchProfile(profileBatches, cancel.Cancel);
        using var cuda = new CudaTemplateScoreSession();
        var image = new ImageReference(Path.GetFullPath(path), Path.GetFileName(path), 17_280, 30_720);
        using (var source = await loader.LoadAsync(image, new Progress<string>(), cancel.Token))
        {
            image = image with
            {
                Width = source.Info.Width,
                Height = source.Info.Height
            };
        }
        var service = new CpuTemplateMatchingService(loader, () => null, cuda.Rent);
        var method = Enum.Parse<CpuTemplateMethod>(methodName);
        var roi = new RoiGeometry(new(14_256, 5808), 96, 96, 0);
        var timer = Stopwatch.StartNew();
        var model = await service.TrainAsync(image, false, roi, method, cancel.Token);
        var trainingMs = timer.Elapsed.TotalMilliseconds;
        var options = new CpuMatching.Core.CpuSearchOptions { MinimumAngle = -30, MaximumAngle = 30, MaximumMatches = 256 };
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var collectionsBefore = GC.CollectionCount(2);
        timer.Restart();
        CpuSearchOutput output;
        try
        {
            output = await service.SearchAsync(image, false, model, null, options, cancel.Token, useCuda: useCuda);
        }
        catch (OperationCanceledException) when (profile.LimitReached)
        {
            var partial = new
            {
                Scope = "PARTIAL_PROFILE_NOT_WHOLE_IMAGE",
                ProfileBatches = profileBatches,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                Image = image,
                Teaching = roi,
                Options = options,
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                Gen2Collections = GC.CollectionCount(2) - collectionsBefore,
                Profile = profile.Report()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(partial, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Partial profile only: {partial.ElapsedMilliseconds:F1} ms; whole-image result NOT_RUN");
            return;
        }
        var knownTarget = output.Result.Matches.Any(match => Math.Abs(match.X - roi.Center.X) <= 1 &&
            Math.Abs(match.Y - roi.Center.Y) <= 1 && Math.Abs(match.AngleDegrees) <= 1);
        var report = new
        {
            Image = image,
            Method = methodName,
            UseCuda = useCuda,
            Teaching = roi,
            Options = options,
            TrainingMilliseconds = trainingMs,
            Output = output,
            KnownTargetFound = knownTarget,
            WholeImageGroundTruth = "NOT_AVAILABLE",
            Display = "NOT_RUN",
            Profile = profile.Report(),
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            Gen2Collections = GC.CollectionCount(2) - collectionsBefore,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancel.Token);
        Console.WriteLine($"{methodName}: {output.TotalMilliseconds:F1} ms, {output.Result.Matches.Count} matches, {output.Result.CandidateCount} candidates, known target={knownTarget}");
    }
}
