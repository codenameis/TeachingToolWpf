using VisionCanvas.Imaging;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>Owns image textures and their byte-budget eviction. Retired resources remain fence protected.</summary>
internal sealed class GpuImageResources(Dx12Device gpu, SpritePipeline pipeline, TileWorker source, IFramePreparer preparer) : IDisposable
{
    private readonly Dictionary<TileKey, GpuTexture> _tiles = [];
    private readonly Dictionary<TileKey, long> _lastUsed = [];
    private readonly GpuTexture?[] _video = new GpuTexture?[2];
    private readonly GpuLiveOverview _overview = new();
    private long _generation = -1;
    private VideoFrame? _preparedIdentity;
    private (ViewportTransform View, double Width, double Height, double Dpi) _preparedView;

    public BoundsD LiveBounds
    {
        get;
        private set;
    }

    public IReadOnlyDictionary<TileKey, GpuTexture> Tiles => _tiles;

    public GpuTexture? Live
    {
        get;
        private set;
    }

    public GpuTexture? LiveOverview => _overview.Texture;

    public long Bytes => _tiles.Values.Sum(t => t.Bytes) + _video.Where(t => t is not null).Sum(t => t!.Bytes) + _overview.Bytes;

    public bool Update(RenderScene scene, VideoFrame? frame, Dx12Surface surface, long frameNumber)
    {
        if (_generation != scene.Generation)
        {
            foreach (var texture in _tiles.Values)
            {
                surface.Retire(texture);
            }

            _tiles.Clear();
            _lastUsed.Clear();
            _generation = scene.Generation;
            _overview.Clear(surface);
        }
        var count = 0;
        while (count < 16 && source.TryTake(out var ready))
        {
            using var pixels = ready!.Pixels;
            if (ready.Generation == _generation && !_tiles.ContainsKey(ready.Key))
            {
                _tiles.Add(ready.Key, pipeline.Upload(pixels, surface));
                _lastUsed[ready.Key] = frameNumber;
            }
            count++;
        }
        UpdateVideo(scene, frame, surface);
        if (scene.IsLive && frame is not null)
        {
            _overview.Update(frame, pipeline, surface, preparer);
        }
        else if (!scene.IsLive && _overview.Texture is not null)
        {
            _overview.Clear(surface);
        }

        if (!scene.IsLive && scene.Image is { } image)
        {
            UpdateTiles(image, scene, surface, frameNumber);
        }

        return count == 16;
    }

    private void UpdateVideo(RenderScene scene, VideoFrame? frame, Dx12Surface surface)
    {
        if (scene.IsLive && frame is not null)
        {
            var key = (scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi);
            if (ReferenceEquals(frame, _preparedIdentity) && key == _preparedView)
            {
                return;
            }

            using var prepared = preparer.Prepare(frame.Pixels, key.Viewport, key.Width, key.Height, key.Dpi);
            _preparedIdentity = frame;
            _preparedView = key;
            if (prepared is null)
            {
                Live = null;
                return;
            }
            var index = surface.FrameIndex;
            var pixels = prepared.Pixels;
            var texture = _video[index];
            if (texture is null || texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
            {
                if (texture is not null)
                {
                    surface.Retire(texture);
                }

                _video[index] = texture = pipeline.CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
            }
            pipeline.Update(texture, pixels, surface);
            Live = texture;
            LiveBounds = prepared.ImageBounds;
        }
        else if (!scene.IsLive && _preparedIdentity is not null)
        {
            for (var index = 0;
index < 2;
index++)
            {
                if (_video[index] is { } texture)
                {
                    surface.Retire(texture);
                }

                _video[index] = null;
            }
            Live = null;
            _preparedIdentity = null;
        }
    }

    private void UpdateTiles(ImageInfo image, RenderScene scene, Dx12Surface surface, long frameNumber)
    {
        var visible = TilePlanner.Visible(image, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi).Prepend(new TileKey(image.Levels - 1, 0, 0)).Distinct().ToArray();
        foreach (var key in visible)
        {
            if (_tiles.ContainsKey(key))
            {
                _lastUsed[key] = frameNumber;
            }
        }

        var bytes = _tiles.Values.Sum(t => t.Bytes);
        if (bytes > gpu.TileBudgetBytes)
        {
            foreach (var entry in _lastUsed.OrderBy(p => p.Value).ToArray())
            {
                if (bytes <= gpu.TileBudgetBytes)
                {
                    break;
                }

                if (entry.Value == frameNumber)
                {
                    continue;
                }

                var texture = _tiles[entry.Key];
                bytes -= texture.Bytes;
                _tiles.Remove(entry.Key);
                _lastUsed.Remove(entry.Key);
                surface.Retire(texture);
            }
        }
        source.RequestTiles(_generation, visible.Where(key => !_tiles.ContainsKey(key)).ToArray());
    }

    public void Dispose()
    {
        _overview.Dispose();
        foreach (var texture in _tiles.Values)
        {
            texture.Dispose();
        }

        foreach (var texture in _video)
        {
            texture?.Dispose();
        }
    }
}
