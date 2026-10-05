using System.Diagnostics;
using System.Globalization;
using System.IO;
using VisionCanvas.Rendering;

namespace VisionCanvas.Diagnostics;

/// <summary>Opt-in render-thread stage measurements; never enabled by normal application startup.</summary>
internal sealed class RenderProfile : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _previousFrameStart;

    private long _frameNumber;

    public bool Enabled => _writer is not null;

    public RenderProfile()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--render-profile");
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        var path = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new(path);
        _writer.WriteLine("seconds,rois,selected,prepare_ms,paint_ms,submit_ms,allocated_bytes,vertices,batches,roi_instances,roi_upload_bytes,roi_cache_bytes,labels_pending,labels_created,label_cache_count,label_cache_bytes,labels_evicted,profile_frame,frame_start_interval_ms,transform_preview,preview_angle_degrees,stopwatch_start_ticks,stopwatch_end_ticks,stopwatch_frequency,record_utc_unix_ms,roi_pack_ms,roi_create_ms,roi_upload_ms,gc_pause_counter_delta_ms");
    }

    public void Record(RenderScene scene, long start, long paint, long submit, long end, long allocated, SpriteMesh mesh, RoiPipeline rois, GpuLabelCache labels, TimeSpan gcPauseDelta)
    {
        if (_writer is null)
        {
            return;
        }

        var interval = _previousFrameStart == 0 ? 0 : Stopwatch.GetElapsedTime(_previousFrameStart, start).TotalMilliseconds;
        _previousFrameStart = start;
        _writer.WriteLine(string.Join(',', new object[] { _clock.Elapsed.TotalSeconds, scene.Document.Rois.Count, scene.Document.Selection.Count,
            Stopwatch.GetElapsedTime(start, paint).TotalMilliseconds, Stopwatch.GetElapsedTime(paint, submit).TotalMilliseconds,
            Stopwatch.GetElapsedTime(submit, end).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated, mesh.Vertices.Count, mesh.Batches.Count, rois.InstanceCount, rois.UploadedBytes, rois.CacheBytes,
            labels.HasPending ? 1 : 0, labels.Created, labels.RetainedCount, labels.RetainedBytes, labels.Evicted,
            ++_frameNumber, interval, scene.Transform is null ? 0 : 1, scene.Transform?.Transform.AngleDegrees ?? 0,
            start, end, Stopwatch.Frequency, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rois.PackMilliseconds, rois.CreateMilliseconds, rois.UploadMilliseconds,
            gcPauseDelta.TotalMilliseconds }
            .Select(v => ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture))));
    }

    public void Dispose() => _writer?.Dispose();
}
