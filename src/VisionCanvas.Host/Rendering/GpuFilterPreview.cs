using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>Render-thread texture owner; superseded previews retire after the GPU fence.</summary>
internal sealed class GpuFilterPreview : IDisposable
{
    private FilterPreviewResult? _identity;
    private readonly List<IRenderTexture> _textures = [];
    private (FilteredRoi Region, IRenderTexture Texture, BoundsD Uv)[] _regions = [];

    public IReadOnlyList<(FilteredRoi Region, IRenderTexture Texture, BoundsD Uv)> Regions => _regions;

    public void Update(FilterPreviewResult? preview, ITexturePipeline pipeline, IRenderSurface surface)
    {
        if (ReferenceEquals(_identity, preview))
        {
            return;
        }

        foreach (var texture in _textures)
        {
            surface.Retire(texture);
        }

        _textures.Clear();
        _regions = [];
        _identity = preview;
        if (preview is null)
        {
            return;
        }

        _regions = new (FilteredRoi, IRenderTexture, BoundsD)[preview.Regions.Count];
        foreach (var page in FilterAtlas.Pack(preview.Regions))
        {
            using var pixels = page.Pixels();
            var texture = pipeline.Upload(pixels, surface);
            _textures.Add(texture);
            foreach (var p in page.Regions)
            {
                _regions[p.Index] = (p.Region, texture, page.Uv(p));
            }
        }
    }

    public void Dispose()
    {
        foreach (var texture in _textures)
        {
            texture.Dispose();
        }

        _textures.Clear();
        _regions = [];
    }
}
