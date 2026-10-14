using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VisionImaging.Abstractions;

namespace PyramidComparison;

internal static class Measurement
{
    public static int Run(string backend, string[] args, Func<string, string, ITileSource> open,
        Func<ITileSource, object>? preparationDiagnostics = null)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: <image> <new-cache-directory> <report.json>");
            return 2;
        }
        var image = Path.GetFullPath(args[0]);
        var cache = Path.GetFullPath(args[1]);
        var report = new Dictionary<string, object?> { ["Backend"] = backend, ["Image"] = image, ["Cache"] = cache };
        var process = Process.GetCurrentProcess();
        long peakPrivate = 0;
        long peakWorking = 0;
        var gate = new object();
        void Sample()
        {
            lock (gate)
            {
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
            }
        }
        Sample();
        report["InitialPrivateBytes"] = peakPrivate;
        var allocations = GC.GetTotalAllocatedBytes(true);
        var cpuStart = process.TotalProcessorTime;
        using var timer = new Timer(_ => Sample(), null, 0, 20);
        var exit = 1;
        var watch = Stopwatch.StartNew();
        try
        {
            if (Directory.Exists(cache))
            {
                throw new InvalidOperationException("Use a new cache directory for every measured import.");
            }
            using (var source = open(image, cache))
            {
                report["ImportMilliseconds"] = watch.Elapsed.TotalMilliseconds;
                report["Info"] = source.Info;
                report["Preparation"] = preparationDiagnostics?.Invoke(source);
                Sample();
            }
            watch.Restart();
            using var reopened = open(image, cache);
            report["ReopenMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            var keys = Keys(reopened.Info);
            var passes = new List<object>();
            for (var pass = 0; pass < 3; pass++)
            {
                var latencies = new List<double>();
                ulong checksum = 0;
                long bytes = 0;
                var total = Stopwatch.StartNew();
                foreach (var key in keys)
                {
                    var start = Stopwatch.GetTimestamp();
                    using var pixels = reopened.Read(key, CancellationToken.None);
                    latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    bytes += pixels.Length;
                    // 읽은 버퍼 사용을 확인하되 픽셀 비교 비용을 타일 지연에 넣지 않는다.
                    checksum += pixels.Bytes[0] + (ulong)pixels.Bytes[pixels.Length - 1];
                }
                var elapsed = total.Elapsed.TotalMilliseconds;
                latencies.Sort();
                passes.Add(new
                {
                    Pass = pass,
                    Count = keys.Length,
                    Bytes = bytes,
                    Milliseconds = elapsed,
                    P50Milliseconds = Percentile(latencies, .5),
                    P95Milliseconds = Percentile(latencies, .95),
                    P99Milliseconds = Percentile(latencies, .99),
                    Checksum = checksum
                });
            }
            report["TilePasses"] = passes;
            report["CacheBytes"] = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            report["Status"] = "PASS";
            exit = 0;
        }
        catch (Exception error)
        {
            report["Status"] = "FAIL";
            report["Error"] = error.ToString();
            report["FailureMilliseconds"] = watch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            Sample();
            report["PeakPrivateBytes"] = peakPrivate;
            report["PeakWorkingSetBytes"] = peakWorking;
            report["ManagedAllocatedBytes"] = GC.GetTotalAllocatedBytes(true) - allocations;
            report["CpuMilliseconds"] = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            report["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            report["OS"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            report["LogicalProcessors"] = Environment.ProcessorCount;
            report["BinaryHashes"] = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("VisionImaging", StringComparison.Ordinal))
                .ToDictionary(a => a.GetName().Name!, a => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))));
            report["OptimizationEnabled"] = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("VisionImaging", StringComparison.Ordinal))
                .ToDictionary(a => a.GetName().Name!, a => !a.GetCustomAttributes(typeof(DebuggableAttribute), false).Cast<DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled));
            report["LoadedAssemblies"] = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Order().ToArray();
            report["NativeModules"] = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).Order().ToArray();
            report["NativeBinaryHashes"] = process.Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Contains("VisionImageCodecs", StringComparison.Ordinal) || module.ModuleName.Contains("vips", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("SkiaSharp", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(module => module.ModuleName, module => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName))));
            using (var input = File.OpenRead(image))
            {
                report["InputSha256"] = Convert.ToHexString(SHA256.HashData(input));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
            File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return exit;
    }

    public static TileKey[] Keys(ImageInfo info)
    {
        var random = new Random(20261013);
        var keys = new List<TileKey>();
        var width = info.Width;
        var height = info.Height;
        for (var level = 0; level < info.Levels; level++)
        {
            var columns = (width + 511) / 512;
            var rows = (height + 511) / 512;
            keys.Add(new(level, 0, 0));
            keys.Add(new(level, columns - 1, rows - 1));
            for (var index = 0; index < Math.Min(128, (long)columns * rows); index++)
            {
                keys.Add(new(level, random.Next(columns), random.Next(rows)));
            }
            width = (width + 1) / 2;
            height = (height + 1) / 2;
        }
        return keys.ToArray();
    }

    private static double Percentile(List<double> values, double fraction) => values[(int)Math.Ceiling(fraction * values.Count) - 1];
}
