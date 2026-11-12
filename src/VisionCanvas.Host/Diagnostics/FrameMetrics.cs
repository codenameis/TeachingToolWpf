using System.Diagnostics;
using System.Globalization;
using System.IO;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Diagnostics;

internal sealed class FrameMetrics : IDisposable
{
    private const double MemorySampleIntervalMilliseconds = 1000.0;
    private const int WriterFlushIntervalFrames = 120;
    private const double MillisecondsPerSecond = 1000.0;

    private readonly StreamWriter? _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();
    private double _previous;
    private long _count;
    private long _privateBytes;
    private double _memorySample;

    public bool Enabled => _writer is not null;

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
        _writer.WriteLine("seconds,frame,cpu_ms,gpu_ms,present_ms,interval_ms,source_to_submit_ms,dropped,private_bytes,texture_bytes,source_sequence,stopwatch_record_ticks,stopwatch_frequency,fence_wait_ms,scene_age_ms,display_present_count,display_refresh_count,display_sync_refresh_count,display_sync_qpc,pacing_wait_ms");
    }

    public RenderMetrics Record(
        double cpu,
        double? gpu,
        double present,
        long? sourceTimestamp,
        long dropped,
        long textureBytes,
        long? sourceSequence,
        double fenceWait,
        double sceneAge,
        DisplayPresentStatistics? statistics,
        double pacingWait = 0)
    {
        var recordTicks = Stopwatch.GetTimestamp();
        var now = _clock.Elapsed.TotalMilliseconds;
        var interval = _previous == 0 ? 0 : now - _previous;
        _previous = now;
        if (now - _memorySample >= MemorySampleIntervalMilliseconds || _privateBytes == 0)
        {
            _process.Refresh();
            _privateBytes = _process.PrivateMemorySize64;
            _memorySample = now;
        }

        double? latency = null;
        if (sourceTimestamp is { } timestamp)
        {
            latency = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        }

        var result = new RenderMetrics(cpu, gpu, present, interval, latency, dropped, ++_count, _privateBytes, textureBytes);
        if (_writer is not null)
        {
            _writer.WriteLine(string.Join(',', new object?[]
            {
                now / MillisecondsPerSecond,
                _count,
                cpu,
                gpu,
                present,
                interval,
                latency,
                dropped,
                result.PrivateBytes,
                textureBytes,
                sourceSequence,
                recordTicks,
                Stopwatch.Frequency,
                fenceWait,
                sceneAge,
                statistics?.PresentCount,
                statistics?.PresentRefreshCount,
                statistics?.SyncRefreshCount,
                statistics?.SyncQpcTime,
                pacingWait
            }.Select(FormatMetricValue)));
            if (_count % WriterFlushIntervalFrames == 0)
            {
                _writer.Flush();
            }
        }

        return result;
    }

    private static string FormatMetricValue(object? value)
    {
        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return value?.ToString() ?? string.Empty;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _process.Dispose();
    }
}
