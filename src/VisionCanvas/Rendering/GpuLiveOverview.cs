using System.Diagnostics;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>Render-owned full-frame overview, refreshed at most twice per second.</summary>
internal sealed class GpuLiveOverview : IDisposable
{
    private readonly GpuTexture?[] _textures = new GpuTexture?[2];
    private long _sequence = -1;

    private long _refreshed;

    public GpuTexture? Texture
    {
        get;
        private set;
    }

    public long Bytes => _textures.Sum(t => t?.Bytes ?? 0);

    public void Update(VideoFrame frame, SpritePipeline pipeline, Dx12Surface surface, IFramePreparer preparer)
    {
        if (Texture is not null && (_sequence == frame.Sequence || Stopwatch.GetElapsedTime(_refreshed).TotalSeconds < .5))
        {
            return;
        }

        var source = frame.Pixels;
        using var prepared = preparer.Prepare(source, ViewportTransform.Fit(source.Width, source.Height, 256, 160), 256, 160, 1);
        if (prepared is null)
        {
            return;
        }

        var pixels = prepared.Pixels;
        var index = surface.FrameIndex;
        var texture = _textures[index];
        if (texture is null || texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
        {
            if (texture is not null)
            {
                surface.Retire(texture);
            }

            _textures[index] = texture = pipeline.CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
        }
        pipeline.Update(texture, pixels, surface);
        Texture = texture;
        _sequence = frame.Sequence;
        _refreshed = Stopwatch.GetTimestamp();
    }

    public void Clear(Dx12Surface surface)
    {
        for (var i = 0;
i < _textures.Length;
i++)
        {
            if (_textures[i] is { } texture)
            {
                surface.Retire(texture);
            }

            _textures[i] = null;
        }
        Texture = null;
        _sequence = -1;
        _refreshed = 0;
    }

    public void Dispose()
    {
        foreach (var texture in _textures)
        {
            texture?.Dispose();
        }
    }
}
