using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using VisionImaging.Codecs;

namespace PyramidComparison;

internal static class DecoderProbe
{
    public static int Run(string file, long budget, int cancelMilliseconds, string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        var scratch = Path.Combine(directory, "scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("TMP", scratch);
        Environment.SetEnvironmentVariable("TMPDIR", scratch);
        using var process = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        long requested = 0, peakPrivate = 0, peakWorking = 0;
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
        using var timer = new Timer(_ => Sample(), null, 0, 20);
        using var cancel = new Timer(_ =>
        {
            Interlocked.Exchange(ref requested, Stopwatch.GetTimestamp());
            cancellation.Cancel();
        }, null, cancelMilliseconds > 0 ? cancelMilliseconds : Timeout.Infinite, Timeout.Infinite);
        var watch = Stopwatch.StartNew();
        var status = "PASS";
        string? error = null;
        int rows = 0;
        double? cancellationLatency = null;
        try
        {
            using var reader = new NativeRasterDecoder(budget).Open(file, cancellation.Token);
            var stride = reader.Width * (reader.Kind == VisionImaging.Abstractions.PixelKind.Gray8 ? 1 : reader.Kind == VisionImaging.Abstractions.PixelKind.Gray16 ? 2 : 4);
            var count = Math.Max(1, 8 * 1024 * 1024 / stride);
            var buffer = ArrayPool<byte>.Shared.Rent(stride * count);
            try
            {
                while (rows < reader.Height)
                {
                    var height = Math.Min(count, reader.Height - rows);
                    reader.ReadRows(rows, height, buffer, cancellation.Token);
                    rows += height;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            status = "CANCELLED";
            cancellationLatency = Stopwatch.GetElapsedTime(Interlocked.Read(ref requested)).TotalMilliseconds;
        }
        catch (InvalidOperationException exception)
        {
            status = "BUDGET_REJECTED";
            error = exception.Message;
        }
        catch (Exception exception)
        {
            status = "FAIL";
            error = exception.ToString();
        }
        watch.Stop();
        Sample();
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            File = Path.GetFullPath(file), Status = status, Error = error, BudgetBytes = budget, RowsCompleted = rows,
            Milliseconds = watch.Elapsed.TotalMilliseconds, CancellationObservedMilliseconds = cancellationLatency,
            PeakPrivateBytes = peakPrivate, PeakWorkingSetBytes = peakWorking, FileReleased = true,
            ScratchFilesAfterDispose = Directory.GetFiles(scratch).Length
        }, new JsonSerializerOptions { WriteIndented = true }));
        return status == "FAIL" ? 1 : 0;
    }
}
