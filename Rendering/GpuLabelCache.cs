using System.Diagnostics;

namespace TeachingToolWpf.Rendering;

internal sealed class GpuLabelCache(ITextRasterizer rasterizer, Dx12Device gpu) : IDisposable
{
    private sealed record Entry(GpuTexture Texture, long Bytes) { public long LastUsed { get; set; } }
    private readonly Dictionary<(string Text, double Dpi), Entry> entries = [];
    private readonly HashSet<(string Text, double Dpi)> used = [];
    private long frame, bytes;
    public int Created { get; private set; }
    public double RasterizeMilliseconds { get; private set; }
    public bool HasPending { get; private set; }
    public int RetainedCount => entries.Count;
    public long RetainedBytes => bytes;
    public long Evicted { get; private set; }
    public void Begin() { frame++; used.Clear(); Created = 0; RasterizeMilliseconds = 0; HasPending = false; }
    public GpuTexture? TryGet(string text, double dpi, SpritePipeline pipeline, Dx12Surface surface)
    {
        if (!entries.ContainsKey((text, dpi)) && (Created >= 2 || RasterizeMilliseconds >= 2))
        { HasPending = true; return null; }
        return Get(text, dpi, pipeline, surface);
    }
    public GpuTexture Get(string text, double dpi, SpritePipeline pipeline, Dx12Surface surface)
    {
        var key = (text, dpi); used.Add(key);
        if (entries.TryGetValue(key, out var existing)) { existing.LastUsed = frame; return existing.Texture; }
        var start = Stopwatch.GetTimestamp();
        using var pixels = rasterizer.Rasterize(text, dpi);
        var texture = pipeline.Upload(pixels, surface);
        var size = checked((long)gpu.Device.GetResourceAllocationInfo(0, texture.Resource.Description).SizeInBytes);
        entries.Add(key, new(texture, size) { LastUsed = frame }); bytes += size;
        Created++; RasterizeMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return texture;
    }
    public void End(Dx12Surface surface)
    {
        if (entries.Count <= 512 && bytes <= 32L * 1024 * 1024) return;
        foreach (var pair in entries.Where(pair => !used.Contains(pair.Key)).OrderBy(pair => pair.Value.LastUsed).ToArray())
        {
            if (entries.Count <= 512 && bytes <= 32L * 1024 * 1024) break;
            surface.Retire(pair.Value.Texture); entries.Remove(pair.Key); bytes -= pair.Value.Bytes;
            Evicted++;
        }
    }
    public void Dispose() { foreach (var entry in entries.Values) entry.Texture.Dispose(); entries.Clear(); }
}
