using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Core.Geometry;

/// <summary>Immutable bounding-volume tree for broad-phase lookup. Exact geometry remains authoritative.</summary>
public sealed class RoiSpatialIndex
{
    private sealed record Entry(Roi Roi, BoundsD Bounds);
    private sealed record Node(BoundsD Bounds, int Start, int Count, Node? Left, Node? Right);
    private readonly Entry[] entries;
    private readonly Node? root;
    public RoiSpatialIndex(IEnumerable<Roi> rois)
    {
        entries = rois.Select(roi => new Entry(roi, roi.Geometry.Bounds)).ToArray();
        root = entries.Length == 0 ? null : Build(0, entries.Length);
    }
    private Node Build(int start, int count)
    {
        var bounds = entries[start].Bounds;
        for (var i = start + 1; i < start + count; i++)
        {
            var next = entries[i].Bounds;
            bounds = new(Math.Min(bounds.Left, next.Left), Math.Min(bounds.Top, next.Top), Math.Max(bounds.Right, next.Right), Math.Max(bounds.Bottom, next.Bottom));
        }
        if (count <= 8) return new(bounds, start, count, null, null);
        var horizontal = bounds.Width >= bounds.Height;
        Array.Sort(entries, start, count, Comparer<Entry>.Create((a, b) => horizontal ? a.Bounds.Center.X.CompareTo(b.Bounds.Center.X) : a.Bounds.Center.Y.CompareTo(b.Bounds.Center.Y)));
        var middle = count / 2;
        return new(bounds, start, count, Build(start, middle), Build(start + middle, count - middle));
    }
    public IEnumerable<Roi> At(PointD point) => Intersecting(new(point.X, point.Y, point.X, point.Y));
    public IEnumerable<Roi> Intersecting(BoundsD bounds)
    {
        if (root is null) yield break;
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (!node.Bounds.Intersects(bounds)) continue;
            if (node.Left is not null && node.Right is not null) { pending.Push(node.Right); pending.Push(node.Left); continue; }
            for (var i = node.Start; i < node.Start + node.Count; i++) if (entries[i].Bounds.Intersects(bounds)) yield return entries[i].Roi;
        }
    }
}
