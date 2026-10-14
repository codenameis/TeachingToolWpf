using System.Diagnostics;
using System.Text.Json;
using VisionImaging.Abstractions;

namespace PyramidComparison;

/// <summary>별도 진단의 단계 경계·20ms 표본 최고값. 일반 성능 실행과 구별한다.</summary>
internal static class MemoryInspection
{
    public static int Run(string backend, string file, string cache, string output,
        Func<string, string, Action<string>, ITileSource> open)
    {
        using var process = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        var checkpoints = new List<object>();
        var previous = 0d;
        var gate = new object();
        long peakPrivate = 0;
        long peakWorking = 0;
        void Sample()
        {
            process.Refresh();
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            peakWorking = Math.Max(peakWorking, process.WorkingSet64);
        }
        void Record(string stage)
        {
            lock (gate)
            {
                Sample();
                var elapsed = watch.Elapsed.TotalMilliseconds;
                checkpoints.Add(new
                {
                    Stage = stage,
                    Milliseconds = elapsed,
                    SincePreviousMilliseconds = elapsed - previous,
                    PrivateBytes = process.PrivateMemorySize64,
                    WorkingSetBytes = process.WorkingSet64,
                    ManagedBytes = GC.GetTotalMemory(false),
                    PeakPrivateSincePrevious = peakPrivate,
                    PeakWorkingSincePrevious = peakWorking
                });
                previous = elapsed;
                peakPrivate = process.PrivateMemorySize64;
                peakWorking = process.WorkingSet64;
            }
        }

        string? error = null;
        Record("Start");
        var timer = new Timer(_ => { lock (gate) { Sample(); } }, null, 20, 20);
        try
        {
            if (Directory.Exists(cache))
            {
                throw new InvalidOperationException("Use a new cache directory for memory inspection.");
            }
            using (var source = open(file, cache, Record))
            {
                Record("ImportReady");
            }
            Record("ImportSourceDisposed");
            using (var source = open(file, cache, Record))
            {
                Record("CacheReopened");
                for (var pass = 0; pass < 3; pass++)
                {
                    foreach (var key in Measurement.Keys(source.Info))
                    {
                        using var pixels = source.Read(key, CancellationToken.None);
                    }
                    Record($"TilesPass{pass}");
                }
            }
            Record("ReopenedSourceDisposed");
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Record("Failure");
        }
        finally
        {
            using var completed = new ManualResetEvent(false);
            timer.Dispose(completed);
            completed.WaitOne();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Backend = backend,
            File = file,
            Error = error,
            Checkpoints = checkpoints
        },
            new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }

    internal sealed class Progress(Action<string> record) : IProgress<string>
    {
        public void Report(string value) => record(value);
    }
}
