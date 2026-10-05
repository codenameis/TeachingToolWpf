using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace VisionCanvas.Diagnostics;

public sealed record RenderMetrics(double CpuMilliseconds, double? GpuMilliseconds, double PresentMilliseconds, double FrameIntervalMilliseconds,
    double? SourceToSubmitMilliseconds, long Dropped, long FrameNumber, long PrivateBytes, long TextureBytes);

internal sealed class FrameMetrics : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private double _previous;
    private long _count;
    private long _privateBytes;
    private double _memorySample;

    public FrameMetrics()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--metrics");
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        var path = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path);
        _writer.WriteLine("seconds,frame,cpu_ms,gpu_ms,present_ms,interval_ms,source_to_submit_ms,dropped,private_bytes,texture_bytes,source_sequence");
    }

    public RenderMetrics Record(double cpu, double? gpu, double present, long? sourceTimestamp, long dropped, long textureBytes, long? sourceSequence)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var interval = _previous == 0 ? 0 : now - _previous;
        _previous = now;
        if (now - _memorySample >= 1000 || _privateBytes == 0)
        {
            _process.Refresh();
            _privateBytes = _process.PrivateMemorySize64;
            _memorySample = now;
        }
        var latency = sourceTimestamp is { } timestamp ? Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds : (double?)null;
        var result = new RenderMetrics(cpu, gpu, present, interval, latency, dropped, ++_count, _privateBytes, textureBytes);
        if (_writer is not null)
        {
            _writer.WriteLine(string.Join(',', new object?[] { now / 1000, _count, cpu, gpu, present, interval, latency, dropped, result.PrivateBytes, textureBytes, sourceSequence }.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString() ?? "")));
            if (_count % 120 == 0)
            {
                _writer.Flush();
            }
        }
        return result;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _process.Dispose();
    }
}
