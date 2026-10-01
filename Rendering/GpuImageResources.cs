using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>Owns image textures and their byte-budget eviction. Retired resources remain fence protected.</summary>
internal sealed class GpuImageResources(Dx12Device gpu, SpritePipeline pipeline, TileWorker source, IFramePreparer preparer) : IDisposable
{
    private readonly Dictionary<TileKey, GpuTexture> tiles = [];
    private readonly Dictionary<TileKey, long> lastUsed = [];
    private readonly GpuTexture?[] video = new GpuTexture?[2];
    private long generation = -1;
    private VideoFrame? preparedIdentity;
    private (ViewportTransform View, double Width, double Height, double Dpi) preparedView;
    public BoundsD LiveBounds { get; private set; }
    public IReadOnlyDictionary<TileKey, GpuTexture> Tiles => tiles;
    public GpuTexture? Live { get; private set; }
    public long Bytes => tiles.Values.Sum(t => t.Bytes) + video.Where(t => t is not null).Sum(t => t!.Bytes);

    public bool Update(RenderScene scene, VideoFrame? frame, Dx12Surface surface, long frameNumber)
    {
        if (generation != scene.Generation)
        {
            foreach (var texture in tiles.Values) surface.Retire(texture);
            tiles.Clear(); lastUsed.Clear(); generation = scene.Generation;
        }
        var count = 0;
        while (count < 16 && source.TryTake(out var ready))
        {
            using var pixels = ready!.Pixels;
            if (ready.Generation == generation && !tiles.ContainsKey(ready.Key))
            { tiles.Add(ready.Key, pipeline.Upload(pixels, surface)); lastUsed[ready.Key] = frameNumber; }
            count++;
        }
        UpdateVideo(scene, frame, surface);
        if (!scene.IsLive && scene.Image is { } image) UpdateTiles(image, scene, surface, frameNumber);
        return count == 16;
    }
    private void UpdateVideo(RenderScene scene, VideoFrame? frame, Dx12Surface surface)
    {
        if (scene.IsLive && frame is not null)
        {
            var key = (scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi);
            if (ReferenceEquals(frame, preparedIdentity) && key == preparedView) return;
            using var prepared = preparer.Prepare(frame.Pixels, key.Viewport, key.Width, key.Height, key.Dpi);
            preparedIdentity = frame; preparedView = key;
            if (prepared is null) { Live = null; return; }
            var index = surface.FrameIndex; var pixels = prepared.Pixels;
            var texture = video[index];
            if (texture is null || texture.Width != pixels.Width || texture.Height != pixels.Height || texture.Kind != pixels.Kind)
            {
                if (texture is not null) surface.Retire(texture);
                video[index] = texture = pipeline.CreateTexture(pixels.Width, pixels.Height, pixels.Kind);
            }
            pipeline.Update(texture, pixels, surface); Live = texture; LiveBounds = prepared.ImageBounds;
        }
        else if (!scene.IsLive && preparedIdentity is not null)
        {
            for (var index = 0; index < 2; index++) { if (video[index] is { } texture) surface.Retire(texture); video[index] = null; }
            Live = null; preparedIdentity = null;
        }
    }
    private void UpdateTiles(ImageInfo image, RenderScene scene, Dx12Surface surface, long frameNumber)
    {
        var visible = TilePlanner.Visible(image, scene.Document.Viewport, scene.Width, scene.Height, scene.Dpi).Prepend(new TileKey(image.Levels - 1, 0, 0)).Distinct().ToArray();
        foreach (var key in visible) if (tiles.ContainsKey(key)) lastUsed[key] = frameNumber;
        var bytes = tiles.Values.Sum(t => t.Bytes);
        if (bytes > gpu.TileBudgetBytes)
        {
            foreach (var entry in lastUsed.OrderBy(p => p.Value).ToArray())
            {
                if (bytes <= gpu.TileBudgetBytes) break;
                if (entry.Value == frameNumber) continue;
                var texture = tiles[entry.Key]; bytes -= texture.Bytes;
                tiles.Remove(entry.Key); lastUsed.Remove(entry.Key); surface.Retire(texture);
            }
        }
        source.RequestTiles(generation, visible.Where(key => !tiles.ContainsKey(key)).ToArray());
    }
    public void Dispose()
    {
        foreach (var texture in tiles.Values) texture.Dispose();
        foreach (var texture in video) texture?.Dispose();
    }
}
