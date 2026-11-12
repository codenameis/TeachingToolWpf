using VisionCanvas.Imaging;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>Owns image textures and their byte-budget eviction. Retired resources remain fence protected.</summary>
internal sealed class GpuImageResources(long tileBudgetBytes, ITexturePipeline pipeline, TileWorker source, IFramePreparer preparer) : IDisposable
{
    private readonly Dictionary<TileKey, IRenderTexture> _tiles = [];
    private readonly Dictionary<TileKey, long> _lastUsed = [];
    private readonly HashSet<TileKey> _requiredKeySet = [];
    private readonly List<TileKey> _requiredKeys = [];
    private readonly List<TileKey> _missingKeys = [];
    private readonly List<KeyValuePair<TileKey, long>> _evictionCandidates = [];
    private readonly IRenderTexture?[] _video = new IRenderTexture?[2];
    private readonly GpuLiveOverview _overview = new();
    private long _tileBytes;
    private long _videoBytes;
    private long _generation = -1;
    private VideoFrame? _preparedIdentity;
    private (ViewportTransform View, double Width, double Height, double Dpi) _preparedView;

    public BoundsD LiveBounds
    {
        get;
        private set;
    }

    public IReadOnlyDictionary<TileKey, IRenderTexture> Tiles => _tiles;

    public IRenderTexture? Live
    {
        get;
        private set;
    }

    public IRenderTexture? LiveOverview => _overview.Texture;

    public long Bytes => _tileBytes + _videoBytes + _overview.Bytes;

    public bool Update(RenderScene scene, VideoFrame? frame, IRenderSurface surface, long frameNumber)
    {
        if (_generation != scene.Generation)
        {
            foreach (var texture in _tiles.Values)
            {
                surface.Retire(texture);
            }

            _tiles.Clear();
            _lastUsed.Clear();
            _tileBytes = 0;
            _generation = scene.Generation;
            _overview.Clear(surface);
        }

        var count = 0;
        while (count < 16 && source.TryTake(out var ready))
        {
            using var pixels = ready!.Pixels;
            if (ready.Generation == _generation && !_tiles.ContainsKey(ready.Key))
            {
                var uploaded = pipeline.Upload(pixels, surface);
                _tiles.Add(ready.Key, uploaded);
                _tileBytes += uploaded.Bytes;
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

    private void UpdateVideo(RenderScene scene, VideoFrame? frame, IRenderSurface surface)
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
                    _videoBytes -= texture.Bytes;
                    surface.Retire(texture);
                }

                texture = pipeline.CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
                _video[index] = texture;
                _videoBytes += texture.Bytes;
            }

            pipeline.Update(texture, pixels, surface);
            Live = texture;
            LiveBounds = prepared.ImageBounds;
        }
        else if (!scene.IsLive && _preparedIdentity is not null)
        {
            for (var index = 0; index < 2; index++)
            {
                if (_video[index] is { } texture)
                {
                    _videoBytes -= texture.Bytes;
                    surface.Retire(texture);
                }

                _video[index] = null;
            }

            Live = null;
            _preparedIdentity = null;
        }
    }

    private void UpdateTiles(ImageInfo image, RenderScene scene, IRenderSurface surface, long frameNumber)
    {
        _requiredKeySet.Clear();
        _requiredKeys.Clear();
        var fallbackKey = new TileKey(image.Levels - 1, 0, 0);
        if (_requiredKeySet.Add(fallbackKey))
        {
            _requiredKeys.Add(fallbackKey);
        }

        var visible = TilePlanner.Visible(image, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi);
        for (var i = 0; i < visible.Count; i++)
        {
            var key = visible[i];
            if (_requiredKeySet.Add(key))
            {
                _requiredKeys.Add(key);
            }
        }

        for (var i = 0; i < _requiredKeys.Count; i++)
        {
            var key = _requiredKeys[i];
            if (_tiles.ContainsKey(key))
            {
                _lastUsed[key] = frameNumber;
            }
        }

        if (_tileBytes > tileBudgetBytes)
        {
            _evictionCandidates.Clear();
            foreach (var pair in _lastUsed)
            {
                if (pair.Value != frameNumber)
                {
                    _evictionCandidates.Add(pair);
                }
            }

            _evictionCandidates.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            for (var i = 0; i < _evictionCandidates.Count; i++)
            {
                if (_tileBytes <= tileBudgetBytes)
                {
                    break;
                }

                var entry = _evictionCandidates[i];
                var texture = _tiles[entry.Key];
                _tileBytes -= texture.Bytes;
                _tiles.Remove(entry.Key);
                _lastUsed.Remove(entry.Key);
                surface.Retire(texture);
            }
        }

        _missingKeys.Clear();
        for (var i = 0; i < _requiredKeys.Count; i++)
        {
            var key = _requiredKeys[i];
            if (!_tiles.ContainsKey(key))
            {
                _missingKeys.Add(key);
            }
        }

        if (_missingKeys.Count > 0)
        {
            source.RequestTiles(_generation, _missingKeys.ToArray());
        }
    }

    public void Dispose()
    {
        _overview.Dispose();
        foreach (var texture in _tiles.Values)
        {
            texture.Dispose();
        }

        _tiles.Clear();
        _tileBytes = 0;

        foreach (var texture in _video)
        {
            texture?.Dispose();
        }

        Array.Clear(_video);
        _videoBytes = 0;
    }
}
