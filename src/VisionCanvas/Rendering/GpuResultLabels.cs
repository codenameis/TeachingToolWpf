using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal sealed record GpuTextRegion(GpuTexture Texture, BoundsD Pixels)
{
    public double Width => Pixels.Width;
    public double Height => Pixels.Height;
    public BoundsD Uv => new(Pixels.Left / Texture.Width, Pixels.Top / Texture.Height,
        Pixels.Right / Texture.Width, Pixels.Bottom / Texture.Height);
}

/// <summary>현재 결과의 불변 라벨을 한 번 업로드한다. 이전 atlas는 제출된 GPU 참조 종료 후 반환한다.</summary>
internal sealed class GpuResultLabels(ITextRasterizer rasterizer) : IDisposable
{
    private GpuTexture? _texture;
    private ImmutableDictionary<string, BoundsD> _regions = ImmutableDictionary<string, BoundsD>.Empty;
    private ImmutableArray<MatchMarker> _matches;
    private double _dpi;

    public void Update(RenderScene scene, SpritePipeline pipeline, Dx12Surface surface)
    {
        var result = scene.Inspection;
        var matches = result?.BatchLabels == true ? result.Matches : default;
        if (matches.IsDefaultOrEmpty)
        {
            // 검사 중/지우기 장면에는 그리지 않지만 마지막 글자 atlas 한 개는 제한된 캐시로 유지한다.
            return;
        }
        if (matches == _matches && scene.Dpi == _dpi)
        {
            return;
        }
        if (!matches.IsDefaultOrEmpty && _texture is not null && scene.Dpi == _dpi &&
            matches.All(match => _regions.ContainsKey(match.Label)))
        {
            // 위치·판정색은 매 장면에서 다시 그린다. 같은 글자 비트맵은 검사 결과 재사용이 아니다.
            _matches = matches;
            return;
        }
        if (_texture is not null)
        {
            surface.Retire(_texture);
            _texture = null;
        }
        _regions = ImmutableDictionary<string, BoundsD>.Empty;
        _matches = matches;
        _dpi = scene.Dpi;
        if (matches.IsDefaultOrEmpty || rasterizer is not ITextBatchRasterizer batchRasterizer)
        {
            return;
        }
        using var batch = batchRasterizer.RasterizeBatch(matches.Select(match => match.Label), scene.Dpi);
        if (batch is not null)
        {
            _texture = pipeline.Upload(batch.Pixels, surface);
            _regions = batch.Regions;
        }
    }

    public GpuTextRegion? Get(string text)
        => _texture is not null && _regions.TryGetValue(text, out var bounds) ? new(_texture, bounds) : null;

    public void Dispose() => _texture?.Dispose();
}
