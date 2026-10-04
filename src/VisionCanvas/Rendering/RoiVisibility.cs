using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>Render-thread cache. Subpixel clusters are summaries; hit testing keeps exact geometry.</summary>
internal sealed class RoiVisibility
{
    // Labels are screen-density decoration. Exact outlines and hit tests use the uncapped spatial index.
    internal const int CandidateBudget = 1024;
    private RoiSpatialIndex? _index;
    private ViewportTransform _view;
    private RigidTransform2D _rigid;
    private double _width;

    private double _height;

    public List<Roi> Rois
    {
        get;
    } = [];

    public List<BoundsD> Summaries
    {
        get;
    } = [];

    public void Update(RoiSpatialIndex spatial, ViewportTransform transform, double w, double h, RigidTransform2D preview = default)
    {
        if (ReferenceEquals(_index, spatial) && _view == transform && _width == w && _height == h && _rigid == preview)
        {
            return;
        }

        _index = spatial;
        _view = transform;
        _width = w;
        _height = h;
        _rigid = preview;
        Rois.Clear();
        Summaries.Clear();
        var a = _view.ScreenToImage(new(-50, -50));
        var b = _view.ScreenToImage(new(w + 50, h + 50));
        var query = preview.InverseTransformBounds(new(a.X, a.Y, b.X, b.Y));
        // Stop the lazy BVH iterator before materialization: overlapping large ROIs cannot be density-collapsed.
        // Spatial traversal is deterministic; candidates retain stable document order within this budget.
        foreach (var item in _index.LabelCandidates(query, 6 / _view.Scale, 120 / _view.Scale, 32 / _view.Scale).Take(CandidateBudget))
        {
            if (item.Roi is { } roi)
            {
                Rois.Add(roi);
            }
            else
            {
                Summaries.Add(item.Bounds);
            }
        }
        Rois.Sort((a, b) => a.Order.CompareTo(b.Order));
    }
}
