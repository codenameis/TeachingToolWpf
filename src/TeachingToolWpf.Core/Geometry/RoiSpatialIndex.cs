using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Core.Geometry;

/// <summary>Persistent BVH: edits copy affected paths; prior render snapshots remain valid.</summary>
public sealed class RoiSpatialIndex
{
    private readonly record struct Entry(Roi Roi, BoundsD Bounds);
    private sealed record Node(BoundsD Bounds, int Count, int Height, double MaxArea, Roi? FirstHit, Roi? Representative, Entry[]? Entries, Node? Left, Node? Right)
    {
        public long ShapeBytes { get; init; }
    }
    private readonly Node root;
    private readonly ImmutableDictionary<Guid, ulong> locations;
    private readonly ImmutableDictionary<Guid, Roi>? values;
    public BoundsD Bounds => root.Bounds;
    public RoiSpatialIndex(IEnumerable<Roi> rois)
    {
        var entries = rois.Select(r => new Entry(r, r.Geometry.Bounds)).ToArray();
        var paths = ImmutableDictionary.CreateBuilder<Guid, ulong>();
        root = Build(entries, 0, entries.Length, 1, paths);
        locations = paths.ToImmutable();
    }
    private RoiSpatialIndex(Node root, ImmutableDictionary<Guid, ulong> locations, ImmutableDictionary<Guid, Roi>? values = null)
    { this.root = root; this.locations = locations; this.values = values; }
    // Metadata can change without rebuilding geometry. The value map belongs to this immutable snapshot.
    internal RoiSpatialIndex WithValues(ImmutableDictionary<Guid, Roi> rois) => new(root, locations, rois);
    private Roi Value(Entry entry) => values is null ? entry.Roi : values[entry.Roi.Id];

    /// <summary>Exact rotated rectangle union bounds, using four branch-and-bound support queries.</summary>
    public BoundsD RotatedBounds(PointD pivot, double angleDegrees)
    {
        if (!pivot.IsFinite) throw new ArgumentOutOfRangeException(nameof(pivot));
        if (!double.IsFinite(angleDegrees)) throw new ArgumentOutOfRangeException(nameof(angleDegrees));
        if (root.Count == 0) return default;
        var angle = angleDegrees % 360;
        if (angle == 0) return root.Bounds;
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        return new(pivot.X - Support(pivot, new(-cos, sin)), pivot.Y - Support(pivot, new(-sin, -cos)),
            pivot.X + Support(pivot, new(cos, -sin)), pivot.Y + Support(pivot, new(sin, cos)));
    }

    private double Support(PointD pivot, PointD direction)
    {
        var best = double.NegativeInfinity;
        Search(root, ProjectionUpperBound(root.Bounds, pivot, direction));
        return best;

        void Search(Node node, double upperBound)
        {
            if (node.Count == 0 || upperBound <= best) return;
            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    var rectangle = entry.Roi.Geometry;
                    var projection = rectangle.Support(direction) - pivot.X * direction.X - pivot.Y * direction.Y;
                    best = Math.Max(best, projection);
                }
                return;
            }
            var left = node.Left!; var right = node.Right!;
            var leftBound = ProjectionUpperBound(left.Bounds, pivot, direction);
            var rightBound = ProjectionUpperBound(right.Bounds, pivot, direction);
            if (leftBound >= rightBound) { Search(left, leftBound); Search(right, rightBound); }
            else { Search(right, rightBound); Search(left, leftBound); }
        }
    }

    private static double ProjectionUpperBound(BoundsD bounds, PointD pivot, PointD direction)
    {
        var x = direction.X >= 0 ? bounds.Right : bounds.Left;
        var y = direction.Y >= 0 ? bounds.Bottom : bounds.Top;
        var projection = (x - pivot.X) * direction.X + (y - pivot.Y) * direction.Y;
        // Only the pruning bound is expanded; the returned support still uses the actual rectangle.
        return projection + CoordinateRoundoff(bounds, pivot);
    }

    private static double CoordinateRoundoff(BoundsD bounds, PointD pivot)
        => 32 * 2.2204460492503131e-16 * Math.Max(1,
            Math.Max(Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right)),
                Math.Max(Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom)), Math.Max(Math.Abs(pivot.X), Math.Abs(pivot.Y)))));

    /// <summary>
    /// First image-boundary exit in each direction. Compute once per immutable selection/pivot;
    /// later drag samples can clamp without visiting ROI. Worst-case preparation remains O(N).
    /// </summary>
    public RotationRange RotationLimits(PointD pivot, ImageReference image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!pivot.IsFinite) throw new ArgumentOutOfRangeException(nameof(pivot));
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentOutOfRangeException(nameof(image));
        if (root.Count == 0) return RotationRange.Unbounded;
        var limits = new BoundsD(-1e-7 - pivot.X, -1e-7 - pivot.Y,
            image.Width + 1e-7 - pivot.X, image.Height + 1e-7 - pivot.Y);
        var best = RotationExits.Unbounded;
        Search(root, BoundsExits(root.Bounds));
        return new(SafeEndpoint(best.Counterclockwise, -1), SafeEndpoint(best.Clockwise, 1));

        double SafeEndpoint(double radians, int direction)
        {
            if (double.IsPositiveInfinity(radians)) return direction * double.PositiveInfinity;
            // The analytic root is an equality. Keep the published rectangle on its inside even when
            // angle normalization, SinCos, and final world-coordinate additions round differently.
            var degrees = Math.Max(0, radians * 180 / Math.PI - 1e-10);
            if (degrees == 0 || FitsWithRoundoff(degrees * direction)) return degrees * direction;
            var inside = 0d; var outside = degrees;
            for (var iteration = 0; iteration < 48; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = (inside + outside) / 2;
                if (FitsWithRoundoff(middle * direction)) inside = middle; else outside = middle;
            }
            return inside * direction;
        }

        bool FitsWithRoundoff(double degrees)
        {
            var bounds = RotatedBounds(pivot, degrees);
            var error = CoordinateRoundoff(bounds, pivot);
            return bounds.Left >= -1e-7 + error && bounds.Top >= -1e-7 + error
                && bounds.Right <= image.Width + 1e-7 - error && bounds.Bottom <= image.Height + 1e-7 - error;
        }

        RotationExits BoundsExits(BoundsD bounds)
        {
            // An outward-rounded AABB contains every descendant corner, so its first exit is a lower bound.
            var margin = CoordinateRoundoff(bounds, pivot);
            var left = bounds.Left - pivot.X - margin; var right = bounds.Right - pivot.X + margin;
            var top = bounds.Top - pivot.Y - margin; var bottom = bounds.Bottom - pivot.Y + margin;
            var exits = RotationExits.Unbounded;
            IncludePoint(ref exits, new(left, top), limits);
            IncludePoint(ref exits, new(right, top), limits);
            IncludePoint(ref exits, new(right, bottom), limits);
            IncludePoint(ref exits, new(left, bottom), limits);
            return exits;
        }

        void Search(Node node, RotationExits lowerBound)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Count == 0 || lowerBound.Clockwise >= best.Clockwise && lowerBound.Counterclockwise >= best.Counterclockwise) return;
            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rectangle = entry.Roi.Geometry;
                    var center = rectangle.Center - pivot;
                    if (rectangle.Radius > 0)
                    {
                        var radius = rectangle.Radius;
                        IncludePoint(ref best, center, new(limits.Left + radius, limits.Top + radius, limits.Right - radius, limits.Bottom - radius));
                        continue;
                    }
                    if (rectangle.Shape.Kind != RoiShapeKind.Rectangle)
                    {
                        foreach (var point in rectangle.Outline) IncludePoint(ref best, point - pivot, limits);
                        continue;
                    }
                    var (sin, cos) = Math.SinCos(rectangle.Angle * Math.PI / 180);
                    var horizontal = new PointD(cos * (rectangle.Width / 2), sin * (rectangle.Width / 2));
                    var vertical = new PointD(-sin * (rectangle.Height / 2), cos * (rectangle.Height / 2));
                    IncludePoint(ref best, center - horizontal - vertical, limits);
                    IncludePoint(ref best, center + horizontal - vertical, limits);
                    IncludePoint(ref best, center + horizontal + vertical, limits);
                    IncludePoint(ref best, center - horizontal + vertical, limits);
                }
                return;
            }
            var left = node.Left!; var right = node.Right!;
            var leftBound = BoundsExits(left.Bounds); var rightBound = BoundsExits(right.Bounds);
            if (leftBound.Earliest <= rightBound.Earliest) { Search(left, leftBound); Search(right, rightBound); }
            else { Search(right, rightBound); Search(left, leftBound); }
        }
    }

    private readonly record struct RotationExits(double Clockwise, double Counterclockwise)
    {
        public static RotationExits Unbounded => new(double.PositiveInfinity, double.PositiveInfinity);
        public double Earliest => Math.Min(Clockwise, Counterclockwise);
    }

    private static void IncludePoint(ref RotationExits exits, PointD point, BoundsD limits)
    {
        IncludeEdge(ref exits, point.X, -point.Y, limits.Right);
        IncludeEdge(ref exits, -point.X, point.Y, -limits.Left);
        IncludeEdge(ref exits, point.Y, point.X, limits.Bottom);
        IncludeEdge(ref exits, -point.Y, -point.X, -limits.Top);
    }

    private static void IncludeEdge(ref RotationExits exits, double a, double b, double limit)
    {
        // A corner follows a*cos(theta)+b*sin(theta). The forbidden arc starts at phase-acos(limit/radius).
        var radius = double.Hypot(a, b);
        if (radius <= limit) return; // Tangency alone never leaves the image.
        if (a > limit || limit <= -radius) { exits = new(0, 0); return; }
        var phase = Math.Atan2(b, a);
        var halfArc = Math.Acos(Math.Clamp(limit / radius, -1, 1));
        var clockwise = a == limit && b >= 0 ? 0 : PositiveAngle(phase - halfArc);
        var counterclockwise = a == limit && b <= 0 ? 0 : PositiveAngle(-phase - halfArc);
        exits = new(Math.Min(exits.Clockwise, clockwise), Math.Min(exits.Counterclockwise, counterclockwise));
    }

    private static double PositiveAngle(double radians)
    {
        var result = radians % (2 * Math.PI);
        return result < 0 ? result + 2 * Math.PI : result;
    }

    private static BoundsD Union(BoundsD a, BoundsD b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    private static Node Leaf(Entry[] entries)
    {
        var bounds = entries.Length == 0 ? default : entries[0].Bounds;
        var maxArea = 0d; Roi? firstHit = null; Roi? representative = null;
        var shapeBytes = 0L;
        foreach (var entry in entries)
        {
            bounds = Union(bounds, entry.Bounds);
            var roi = entry.Roi; var area = roi.Geometry.Area;
            if (roi.Geometry.Shape.Kind == RoiShapeKind.Polygon) shapeBytes += roi.Geometry.Shape.Vertices.Length * 16L;
            if (CompareHit(roi, firstHit) < 0) firstHit = roi;
            if (representative is null || area > maxArea || area == maxArea && roi.Order < representative.Order)
                representative = roi;
            maxArea = Math.Max(maxArea, area);
        }
        return new(bounds, entries.Length, 0, maxArea, firstHit, representative, entries, null, null) { ShapeBytes = shapeBytes };
    }
    private static Node Branch(Node left, Node right) => new(left.Count == 0 ? right.Bounds : right.Count == 0 ? left.Bounds : Union(left.Bounds, right.Bounds),
        left.Count + right.Count, 1 + Math.Max(left.Height, right.Height), Math.Max(left.MaxArea, right.MaxArea),
        CompareHit(left.FirstHit, right.FirstHit) <= 0 ? left.FirstHit : right.FirstHit,
        left.MaxArea > right.MaxArea || left.MaxArea == right.MaxArea && (left.Representative?.Order ?? long.MaxValue) <= (right.Representative?.Order ?? long.MaxValue)
            ? left.Representative : right.Representative, null, left, right) { ShapeBytes = left.ShapeBytes + right.ShapeBytes };
    private static int CompareHit(Roi? a, Roi? b)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        var area = a.Geometry.Area.CompareTo(b.Geometry.Area);
        return area != 0 ? area : b.Order.CompareTo(a.Order);
    }
    private static Node Build(Entry[] entries, int start, int count, ulong path, ImmutableDictionary<Guid, ulong>.Builder paths)
    {
        if (count <= 8)
        {
            var leaf = entries.AsSpan(start, count).ToArray();
            foreach (var entry in leaf) paths[entry.Roi.Id] = path;
            return Leaf(leaf);
        }
        var bounds = entries[start].Bounds;
        for (var i = start + 1; i < start + count; i++) bounds = Union(bounds, entries[i].Bounds);
        var horizontal = bounds.Width >= bounds.Height;
        Array.Sort(entries, start, count, Comparer<Entry>.Create((a, b) => horizontal ? a.Bounds.Center.X.CompareTo(b.Bounds.Center.X) : a.Bounds.Center.Y.CompareTo(b.Bounds.Center.Y)));
        var middle = count / 2;
        return Branch(Build(entries, start, middle, path << 1, paths), Build(entries, start + middle, count - middle, (path << 1) | 1, paths));
    }
    /// <summary>
    /// Replaces every ROI value while preserving the spatial partition and ID paths. The caller chooses
    /// this for a coherent whole-set transform; arbitrary rearrangement can reduce later query efficiency.
    /// </summary>
    public RoiSpatialIndex RefitGeometry(ImmutableDictionary<Guid, Roi> rois, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rois);
        cancellationToken.ThrowIfCancellationRequested();
        if (rois.Count != locations.Count) throw new ArgumentException("Refit requires exactly the indexed ROI IDs.", nameof(rois));
        return new(Refit(root), locations);

        Node Refit(Node node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Entries is not { } entries) return Branch(Refit(node.Left!), Refit(node.Right!));
            var updated = new Entry[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                var id = entries[index].Roi.Id;
                if (!rois.TryGetValue(id, out var roi) || roi is null || roi.Id != id)
                    throw new ArgumentException("Refit requires matching indexed keys and ROI IDs.", nameof(rois));
                updated[index] = new(roi, roi.Geometry.Bounds);
            }
            return Leaf(updated);
        }
    }

    public RoiSpatialIndex WithChanges(IEnumerable<(Guid Id, Roi? Value)> changes)
    {
        var next = root; var paths = locations.ToBuilder(); var currentValues = values?.ToBuilder();
        foreach (var (id, value) in changes)
        {
            if (currentValues is not null) { if (value is null) currentValues.Remove(id); else currentValues[id] = value; }
            // Rebalance before a long insertion sequence can exhaust the path representation.
            if (next.Height >= 48)
            {
                var balanced = new RoiSpatialIndex(Enumerate(next));
                next = balanced.root; paths = balanced.locations.ToBuilder();
            }
            if (paths.TryGetValue(id, out var path))
            {
                var depth = BitOperations.Log2(path);
                next = Replace(next, path, depth == 0 ? 0 : 1UL << (depth - 1), id, value);
                if (value is null) paths.Remove(id);
            }
            else if (value is not null) next = Insert(next, new(value, value.Geometry.Bounds), 1, paths);
        }
        return new(next, paths.ToImmutable(), currentValues?.ToImmutable());
    }
    private static IEnumerable<Roi> Enumerate(Node node)
    {
        if (node.Entries is { } entries) { foreach (var e in entries) yield return e.Roi; }
        else { foreach (var r in Enumerate(node.Left!)) yield return r; foreach (var r in Enumerate(node.Right!)) yield return r; }
    }
    private static Node Replace(Node node, ulong path, ulong bit, Guid id, Roi? value)
    {
        if (node.Entries is { } entries)
            return Leaf(entries.Where(e => e.Roi.Id != id).Concat(value is null ? [] : new[] { new Entry(value, value.Geometry.Bounds) }).ToArray());
        return (path & bit) == 0 ? Branch(Replace(node.Left!, path, bit >> 1, id, value), node.Right!)
            : Branch(node.Left!, Replace(node.Right!, path, bit >> 1, id, value));
    }
    private static Node Insert(Node node, Entry entry, ulong path, ImmutableDictionary<Guid, ulong>.Builder paths)
    {
        if (node.Entries is { } entries)
        {
            var expanded = entries.Append(entry).ToArray();
            if (expanded.Length <= 8) { paths[entry.Roi.Id] = path; return Leaf(expanded); }
            return Build(expanded, 0, expanded.Length, path, paths);
        }
        var left = node.Left!; var right = node.Right!;
        static double Area(BoundsD bounds) => bounds.Width * bounds.Height;
        var leftCost = left.Count == 0 ? 0 : Area(Union(left.Bounds, entry.Bounds)) - Area(left.Bounds);
        var rightCost = right.Count == 0 ? 0 : Area(Union(right.Bounds, entry.Bounds)) - Area(right.Bounds);
        return leftCost < rightCost || leftCost == rightCost && left.Count <= right.Count
            ? Branch(Insert(left, entry, path << 1, paths), right) : Branch(left, Insert(right, entry, (path << 1) | 1, paths));
    }
    public IEnumerable<Roi> At(PointD point, double minimumArea = 0) => Intersecting(new(point.X, point.Y, point.X, point.Y), minimumArea);
    /// <summary>Exact nearest editing priority without materializing/sorting every overlapping hit.</summary>
    public Roi? HitTest(PointD point, double tolerance)
    {
        if (!point.IsFinite || !double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        var margin = tolerance * Math.Sqrt(2);
        var bounds = new BoundsD(point.X - margin, point.Y - margin, point.X + margin, point.Y + margin);
        Roi? best = null;
        Visit(root);
        return best;
        void Visit(Node node)
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds) || best is not null && CompareHit(node.FirstHit, best) >= 0) return;
            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    if (!entry.Bounds.Intersects(bounds) || !entry.Roi.Geometry.Contains(point, tolerance)) continue;
                    var candidate = Value(entry);
                    if (CompareHit(candidate, best) < 0) best = candidate;
                }
            }
            else if (CompareHit(node.Left!.FirstHit, node.Right!.FirstHit) <= 0) { Visit(node.Left); Visit(node.Right); }
            else { Visit(node.Right); Visit(node.Left); }
        }
    }
    public IEnumerable<Roi> Intersecting(BoundsD bounds, double minimumArea = 0)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || node.MaxArea <= minimumArea || !node.Bounds.Intersects(bounds)) continue;
            if (node.Entries is not { } entries) { pending.Push(node.Right!); pending.Push(node.Left!); continue; }
            foreach (var entry in entries) if (entry.Roi.Geometry.Area > minimumArea && entry.Bounds.Intersects(bounds)) yield return Value(entry);
        }
    }
    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Visible(BoundsD bounds, double aggregateSize)
        => Visible(root, bounds, aggregateSize).Select(item => item.Roi is { } roi && values is not null ? (values[roi.Id], item.Bounds, item.Count) : item);
    /// <summary>Screen-density label candidates; exact geometry queries and editing remain independent.</summary>
    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> LabelCandidates(BoundsD bounds, double aggregateSize, double spacingX, double spacingY)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds)) continue;
            if (node.Count > 1 && node.Bounds.Width <= aggregateSize && node.Bounds.Height <= aggregateSize)
            { yield return (null, node.Bounds, node.Count); continue; }
            // The largest ROI represents a dense cluster, so a containing parent wins over its children.
            if (node.Count > 1 && node.Bounds.Width <= spacingX && node.Bounds.Height <= spacingY &&
                node.Representative is { } representative && representative.Geometry.Bounds.Intersects(bounds))
            { yield return (values is null ? representative : values[representative.Id], representative.Geometry.Bounds, 1); continue; }
            if (node.Entries is not { } entries) { pending.Push(node.Right!); pending.Push(node.Left!); continue; }
            foreach (var entry in entries) if (entry.Bounds.Intersects(bounds)) yield return (Value(entry), entry.Bounds, 1);
        }
    }
    private static IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Visible(Node root, BoundsD bounds, double aggregateSize)
    {
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds)) continue;
            if (node.Count > 1 && node.Bounds.Width <= aggregateSize && node.Bounds.Height <= aggregateSize)
            { yield return (null, node.Bounds, node.Count); continue; }
            if (node.Entries is not { } entries) { pending.Push(node.Right!); pending.Push(node.Left!); continue; }
            foreach (var entry in entries) if (entry.Bounds.Intersects(bounds)) yield return (entry.Roi, entry.Bounds, 1);
        }
    }

    /// <summary>Immutable spatial blocks retain identity across edits outside their subtree.</summary>
    public IEnumerable<RoiSpatialBlock> Blocks(BoundsD bounds, int maximumCount = 2048)
    {
        if (maximumCount < 8) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds)) continue;
            if (node.Count <= maximumCount)
            {
                yield return new(node, node.Bounds, node.Count, size => Visible(node, node.Bounds, size), node.ShapeBytes);
                continue;
            }
            pending.Push(node.Right!); pending.Push(node.Left!);
        }
    }
}

/// <summary>Geometry-only immutable block; metadata is read from the current document by ID.</summary>
public sealed class RoiSpatialBlock(object identity, BoundsD bounds, int count, Func<double, IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)>> enumerate, long shapeBytes = 0)
{
    public long ShapeBytes { get; } = shapeBytes;
    public object Identity { get; } = identity;
    public BoundsD Bounds { get; } = bounds;
    public int Count { get; } = count;
    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Detail(double aggregateSize) => enumerate(aggregateSize);
}
