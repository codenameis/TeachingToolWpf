using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Filters;

namespace TeachingToolWpf.Rendering;

/// <summary>Render-thread texture owner; superseded previews retire after the GPU fence.</summary>
internal sealed class GpuFilterPreview : IDisposable
{
    private FilterPreviewResult? identity;
    private readonly List<GpuTexture> textures = [];
    private (FilteredRoi Region, GpuTexture Texture, BoundsD Uv)[] regions = [];
    public IReadOnlyList<(FilteredRoi Region, GpuTexture Texture, BoundsD Uv)> Regions => regions;
    public void Update(FilterPreviewResult? preview, SpritePipeline pipeline, Dx12Surface surface)
    {
        if (ReferenceEquals(identity, preview)) return;
        foreach (var texture in textures) surface.Retire(texture);
        textures.Clear(); regions = []; identity = preview;
        if (preview is null) return;
        regions = new (FilteredRoi, GpuTexture, BoundsD)[preview.Regions.Count];
        foreach (var page in FilterAtlas.Pack(preview.Regions))
        {
            using var pixels = page.Pixels();
            var texture = pipeline.Upload(pixels, surface); textures.Add(texture);
            foreach (var p in page.Regions) regions[p.Index] = (p.Region, texture, page.Uv(p));
        }
    }
    public void Dispose() { foreach (var texture in textures) texture.Dispose(); textures.Clear(); regions = []; }
}
