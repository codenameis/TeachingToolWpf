using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace TeachingToolWpf.Diagnostics;

public sealed record RenderMetrics(double CpuMilliseconds, double? GpuMilliseconds, double PresentMilliseconds, double FrameIntervalMilliseconds,
    double? SourceToSubmitMilliseconds, long Dropped, long FrameNumber, long PrivateBytes, long TextureBytes);

internal sealed class FrameMetrics : IDisposable
{
    private readonly StreamWriter? writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Process process = Process.GetCurrentProcess();
    private double previous;
    private long count;
    private long privateBytes;
    private double memorySample;
    public FrameMetrics()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--metrics");
        if (index < 0 || index + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new StreamWriter(path);
        writer.WriteLine("seconds,frame,cpu_ms,gpu_ms,present_ms,interval_ms,source_to_submit_ms,dropped,private_bytes,texture_bytes,source_sequence");
    }
    public RenderMetrics Record(double cpu, double? gpu, double present, long? sourceTimestamp, long dropped, long textureBytes, long? sourceSequence)
    {
        var now = clock.Elapsed.TotalMilliseconds; var interval = previous == 0 ? 0 : now - previous; previous = now;
        if (now - memorySample >= 1000 || privateBytes == 0) { process.Refresh(); privateBytes = process.PrivateMemorySize64; memorySample = now; }
        var latency = sourceTimestamp is { } timestamp ? Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds : (double?)null;
        var result = new RenderMetrics(cpu, gpu, present, interval, latency, dropped, ++count, privateBytes, textureBytes);
        if (writer is not null)
        {
            writer.WriteLine(string.Join(',', new object?[] { now / 1000, count, cpu, gpu, present, interval, latency, dropped, result.PrivateBytes, textureBytes, sourceSequence }.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "")));
            if (count % 120 == 0) writer.Flush();
        }
        return result;
    }
    public void Dispose() { writer?.Dispose(); process.Dispose(); }
}
