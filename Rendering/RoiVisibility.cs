using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>Render-thread cache. Subpixel clusters are summaries; hit testing keeps exact geometry.</summary>
internal sealed class RoiVisibility
{
    private RoiSpatialIndex? index;
    private ViewportTransform view;
    private double width, height;
    public List<Roi> Rois { get; } = [];
    public List<BoundsD> Summaries { get; } = [];
    public void Update(RoiSpatialIndex spatial, ViewportTransform transform, double w, double h)
    {
        if (ReferenceEquals(index, spatial) && view == transform && width == w && height == h) return;
        index = spatial; view = transform; width = w; height = h;
        Rois.Clear(); Summaries.Clear();
        var a = view.ScreenToImage(new(-50, -50)); var b = view.ScreenToImage(new(w + 50, h + 50));
        foreach (var item in index.Visible(new(a.X, a.Y, b.X, b.Y), 6 / view.Scale))
        {
            if (item.Roi is { } roi) Rois.Add(roi);
            else Summaries.Add(item.Bounds);
        }
        Rois.Sort((a, b) => a.Order.CompareTo(b.Order));
    }
}
