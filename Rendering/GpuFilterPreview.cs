using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Filters;

namespace TeachingToolWpf.Rendering;

/// <summary>Render-thread texture owner; superseded previews retire after the GPU fence.</summary>
internal sealed class GpuFilterPreview : IDisposable
{
    private FilterPreviewResult? identity;
    private readonly List<(FilteredRoi Region, GpuTexture Texture)> regions = [];
    public IReadOnlyList<(FilteredRoi Region, GpuTexture Texture)> Regions => regions;
    public void Update(FilterPreviewResult? preview, SpritePipeline pipeline, Dx12Surface surface)
    {
        if (ReferenceEquals(identity, preview)) return;
        foreach (var item in regions) surface.Retire(item.Texture);
        regions.Clear(); identity = preview;
        if (preview is null) return;
        foreach (var region in preview.Regions)
        {
            using var pixels = new PixelBuffer(region.Width, region.Height, PixelKind.Gray8);
            region.Gray8.CopyTo(pixels.Span);
            regions.Add((region, pipeline.Upload(pixels, surface)));
        }
    }
    public void Dispose() { foreach (var item in regions) item.Texture.Dispose(); regions.Clear(); }
}
