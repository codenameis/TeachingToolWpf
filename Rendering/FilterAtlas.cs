using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Filters;

namespace TeachingToolWpf.Rendering;

/// <summary>Packs preview pixels independently of GPU lifetime; draw order is retained by Index.</summary>
internal static class FilterAtlas
{
    internal sealed record Placement(int Index, FilteredRoi Region, int X, int Y);
    internal sealed record Page(int Width, int Height, IReadOnlyList<Placement> Regions)
    {
        public BoundsD Uv(Placement p) => new(p.X / (double)Width, p.Y / (double)Height,
            (p.X + p.Region.Width) / (double)Width, (p.Y + p.Region.Height) / (double)Height);
        public PixelBuffer Pixels()
        {
            var pixels = new PixelBuffer(Width, Height, PixelKind.Gray8);
            try
            {
                pixels.Span.Clear();
                foreach (var p in Regions)
                    for (var y = 0; y < p.Region.Height; y++)
                        p.Region.Gray8.AsSpan(y * p.Region.Width, p.Region.Width)
                            .CopyTo(pixels.Span.Slice((p.Y + y) * pixels.Stride + p.X, p.Region.Width));
                return pixels;
            }
            catch { pixels.Dispose(); throw; }
        }
    }
    public static IReadOnlyList<Page> Pack(IReadOnlyList<FilteredRoi> regions)
    {
        const int edge = 2048;
        var pages = new List<Page>(); var placed = new List<Placement>();
        var x = 0; var y = 0; var rowHeight = 0; var width = 0;
        foreach (var item in regions.Select((region, index) => (region, index)).OrderByDescending(p => p.region.Height).ThenByDescending(p => p.region.Width))
        {
            var r = item.region;
            if (r.Width <= 0 || r.Height <= 0 || r.Width > edge - 2 || r.Height > edge - 2 || r.Gray8.Length != checked(r.Width * r.Height))
                throw new ArgumentException("필터 atlas 입력 크기가 유효하지 않습니다.", nameof(regions));
            if (x + r.Width + 2 > edge) { x = 0; y += rowHeight; rowHeight = 0; }
            if (y + r.Height + 2 > edge)
            {
                pages.Add(new(width, y + rowHeight, placed));
                placed = []; x = y = rowHeight = width = 0;
            }
            placed.Add(new(item.index, r, x + 1, y + 1));
            x += r.Width + 2; rowHeight = Math.Max(rowHeight, r.Height + 2); width = Math.Max(width, x);
        }
        if (placed.Count > 0) pages.Add(new(width, y + rowHeight, placed));
        return pages;
    }
}
