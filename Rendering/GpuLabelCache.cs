using System.Diagnostics;

namespace TeachingToolWpf.Rendering;

internal sealed class GpuLabelCache(ITextRasterizer rasterizer, Dx12Device gpu) : IDisposable
{
    private sealed record Entry(GpuTexture Texture, long Bytes)
    {
        public long LastUsed
        {
            get;
            set;
        }
    }
    private readonly Dictionary<(string Text, double Dpi), Entry> _entries = [];
    private readonly HashSet<(string Text, double Dpi)> _used = [];
    private long _frame;

    private long _bytes;

    public int Created
    {
        get;
        private set;
    }

    public double RasterizeMilliseconds
    {
        get;
        private set;
    }

    public bool HasPending
    {
        get;
        private set;
    }

    public int RetainedCount => _entries.Count;

    public long RetainedBytes => _bytes;

    public long Evicted
    {
        get;
        private set;
    }

    public void Begin()
    {
        _frame++;
        _used.Clear();
        Created = 0;
        RasterizeMilliseconds = 0;
        HasPending = false;
    }

    public GpuTexture? TryGet(string text, double dpi, SpritePipeline pipeline, Dx12Surface surface)
    {
        if (!_entries.ContainsKey((text, dpi)) && (Created >= 2 || RasterizeMilliseconds >= 2))
        {
            HasPending = true;
            return null;
        }
        return Get(text, dpi, pipeline, surface);
    }

    public GpuTexture Get(string text, double dpi, SpritePipeline pipeline, Dx12Surface surface)
    {
        var key = (text, dpi);
        _used.Add(key);
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = _frame;
            return existing.Texture;
        }
        var start = Stopwatch.GetTimestamp();
        using var pixels = rasterizer.Rasterize(text, dpi);
        var texture = pipeline.Upload(pixels, surface);
        var size = checked((long)gpu.Device.GetResourceAllocationInfo(0, texture.Resource.Description).SizeInBytes);
        _entries.Add(key, new(texture, size)
        {
            LastUsed = _frame
        });
        _bytes += size;
        Created++;
        RasterizeMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return texture;
    }

    public void End(Dx12Surface surface)
    {
        if (_entries.Count <= 512 && _bytes <= 32L * 1024 * 1024)
        {
            return;
        }

        foreach (var pair in _entries.Where(pair => !_used.Contains(pair.Key)).OrderBy(pair => pair.Value.LastUsed).ToArray())
        {
            if (_entries.Count <= 512 && _bytes <= 32L * 1024 * 1024)
            {
                break;
            }

            surface.Retire(pair.Value.Texture);
            _entries.Remove(pair.Key);
            _bytes -= pair.Value.Bytes;
            Evicted++;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Texture.Dispose();
        }

        _entries.Clear();
    }
}
