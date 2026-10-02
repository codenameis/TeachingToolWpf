using System.Diagnostics;
using System.Globalization;
using System.IO;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Opt-in render-thread stage measurements; never enabled by normal application startup.</summary>
internal sealed class RenderProfile : IDisposable
{
    private readonly StreamWriter? writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public bool Enabled => writer is not null;
    public RenderProfile()
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--render-profile");
        if (index < 0 || index + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new(path);
        writer.WriteLine("seconds,rois,selected,prepare_ms,paint_ms,submit_ms,allocated_bytes,vertices,batches,roi_instances,roi_upload_bytes,roi_cache_bytes,labels_pending,labels_created,label_cache_count,label_cache_bytes,labels_evicted");
    }
    public void Record(RenderScene scene, long start, long paint, long submit, long end, long allocated, SpriteMesh mesh, RoiPipeline rois, GpuLabelCache labels)
    {
        if (writer is null) return;
        writer.WriteLine(string.Join(',', new object[] { clock.Elapsed.TotalSeconds, scene.Document.Rois.Count, scene.Document.Selection.Count,
            Stopwatch.GetElapsedTime(start, paint).TotalMilliseconds, Stopwatch.GetElapsedTime(paint, submit).TotalMilliseconds,
            Stopwatch.GetElapsedTime(submit, end).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated, mesh.Vertices.Count, mesh.Batches.Count, rois.InstanceCount, rois.UploadedBytes, rois.CacheBytes,
            labels.HasPending ? 1 : 0, labels.Created, labels.RetainedCount, labels.RetainedBytes, labels.Evicted }
            .Select(v => ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture))));
    }
    public void Dispose() => writer?.Dispose();
}
