using System.Diagnostics;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Rendering;

/// <summary>Render-owned full-frame overview, refreshed at most twice per second.</summary>
internal sealed class GpuLiveOverview : IDisposable
{
    private readonly GpuTexture?[] textures = new GpuTexture?[2];
    private long sequence = -1, refreshed;
    public GpuTexture? Texture { get; private set; }
    public long Bytes => textures.Sum(t => t?.Bytes ?? 0);
    public void Update(VideoFrame frame, SpritePipeline pipeline, Dx12Surface surface, IFramePreparer preparer)
    {
        if (Texture is not null && (sequence == frame.Sequence || Stopwatch.GetElapsedTime(refreshed).TotalSeconds < .5)) return;
        var source = frame.Pixels;
        using var prepared = preparer.Prepare(source, ViewportTransform.Fit(source.Width, source.Height, 256, 160), 256, 160, 1);
        if (prepared is null) return;
        var pixels = prepared.Pixels; var index = surface.FrameIndex; var texture = textures[index];
        if (texture is null || texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
        {
            if (texture is not null) surface.Retire(texture);
            textures[index] = texture = pipeline.CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
        }
        pipeline.Update(texture, pixels, surface);
        Texture = texture; sequence = frame.Sequence; refreshed = Stopwatch.GetTimestamp();
    }
    public void Clear(Dx12Surface surface)
    {
        for (var i = 0; i < textures.Length; i++) { if (textures[i] is { } texture) surface.Retire(texture); textures[i] = null; }
        Texture = null; sequence = -1; refreshed = 0;
    }
    public void Dispose() { foreach (var texture in textures) texture?.Dispose(); }
}
