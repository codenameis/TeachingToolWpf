namespace TeachingToolWpf.Rendering;

internal sealed class GpuLabelCache(ITextRasterizer rasterizer) : IDisposable
{
    private readonly Dictionary<(string Text, double Dpi), GpuTexture> entries = [];
    private readonly HashSet<(string Text, double Dpi)> used = [];
    public void Begin() => used.Clear();
    public GpuTexture Get(string text, double dpi, SpritePipeline pipeline, Dx12Surface surface)
    {
        var key = (text, dpi); used.Add(key);
        if (entries.TryGetValue(key, out var existing)) return existing;
        using var pixels = rasterizer.Rasterize(text, dpi);
        var texture = pipeline.Upload(pixels, surface); entries.Add(key, texture); return texture;
    }
    public void End(Dx12Surface surface)
    {
        foreach (var key in entries.Keys.Where(key => !used.Contains(key)).ToArray())
        {
            if (entries.Count <= 128) break;
            surface.Retire(entries[key]); entries.Remove(key);
        }
    }
    public void Dispose() { foreach (var texture in entries.Values) texture.Dispose(); entries.Clear(); }
}
