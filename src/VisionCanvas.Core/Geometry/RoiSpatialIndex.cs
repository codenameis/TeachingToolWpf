using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Geometry;

/// <summary>Persistent BVH: edits copy affected paths; prior render snapshots remain valid.</summary>
public sealed partial class RoiSpatialIndex
{
    private readonly record struct Entry(Roi Roi, BoundsD Bounds);
    private sealed record Node(BoundsD Bounds, int Count, int Height, double MaxArea, Roi? FirstHit, Roi? Representative, Entry[]? Entries, Node? Left, Node? Right)
    {
        public long ShapeBytes
        {
            get;
            init;
        }
    }
    private readonly Node _root;
    private readonly Lazy<ImmutableDictionary<Guid, BvhNodePath>> _locations;
    private readonly ImmutableDictionary<Guid, Roi>? _values;

    public BoundsD Bounds => _root.Bounds;

    public RoiSpatialIndex(IEnumerable<Roi> rois)
    {
        var entries = rois.Select(r => new Entry(r, r.Geometry.Bounds)).ToArray();
        var paths = ImmutableDictionary.CreateBuilder<Guid, BvhNodePath>();
        _root = Build(entries, 0, entries.Length, BvhNodePath.Root, paths);
        var locations = paths.ToImmutable();
        _locations = new(() => locations);
    }

    private RoiSpatialIndex(Node root, ImmutableDictionary<Guid, BvhNodePath> locations, ImmutableDictionary<Guid, Roi>? values = null)
        : this(root, new Lazy<ImmutableDictionary<Guid, BvhNodePath>>(() => locations), values)
    {
    }

    private RoiSpatialIndex(Node root, Lazy<ImmutableDictionary<Guid, BvhNodePath>> locations, ImmutableDictionary<Guid, Roi>? values = null)
    {
        _root = root;
        _locations = locations;
        _values = values;
    }

    // Metadata can change without rebuilding geometry. The value map belongs to this immutable snapshot.
    internal RoiSpatialIndex WithValues(ImmutableDictionary<Guid, Roi> rois) => new(_root, _locations, rois);

    // Preserve spatial partitions and unchanged block identities instead of sorting a large subset again.
    internal RoiSpatialIndex Filter(IReadOnlySet<Guid> ids, bool include)
    {
        var membership = ids;
        if (ids is ImmutableHashSet<Guid>)
        {
            var lookup = new HashSet<Guid>(ids.Count);
            foreach (var id in ids)
            {
                lookup.Add(id);
            }
            membership = lookup;
        }
        var empty = Leaf([]);
        var root = FilterNode(_root) ?? empty;
        // 선택용 부분 인덱스는 조회만 한다. 실제 수정 시에만 ID→경로 사전을 만든다.
        return new(root, new Lazy<ImmutableDictionary<Guid, BvhNodePath>>(() => IndexLocations(root)), _values);

        Node? FilterNode(Node node)
        {
            if (node.Count == 0)
            {
                return null;
            }

            if (node.Entries is { } entries)
            {
                var retainedCount = 0;
                foreach (var entry in entries)
                {
                    if (membership.Contains(entry.Roi.Id) == include)
                    {
                        retainedCount++;
                    }
                }

                if (retainedCount == 0)
                {
                    return null;
                }

                if (retainedCount == entries.Length)
                {
                    return node;
                }

                return Leaf(entries.Where(entry => membership.Contains(entry.Roi.Id) == include).ToArray());
            }

            var left = FilterNode(node.Left!);
            var right = FilterNode(node.Right!);
            if (left is null && right is null)
            {
                return null;
            }

            // Keep empty branches so retained IDs still use their original paths for later edits.
            return ReferenceEquals(left, node.Left) && ReferenceEquals(right, node.Right)
                ? node : Branch(left ?? empty, right ?? empty);
        }
    }

    private static ImmutableDictionary<Guid, BvhNodePath> IndexLocations(Node root)
    {
        var paths = ImmutableDictionary.CreateBuilder<Guid, BvhNodePath>();
        Visit(root, BvhNodePath.Root);
        return paths.ToImmutable();

        void Visit(Node node, BvhNodePath path)
        {
            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    paths.Add(entry.Roi.Id, path);
                }
                return;
            }
            Visit(node.Left!, path.Left);
            Visit(node.Right!, path.Right);
        }
    }

    private Roi Value(Entry entry) => _values is null ? entry.Roi : _values[entry.Roi.Id];

    private static BoundsD Union(BoundsD a, BoundsD b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static Node Leaf(Entry[] entries)
    {
        var bounds = entries.Length == 0 ? default : entries[0].Bounds;
        var maxArea = 0d;
        Roi? firstHit = null;
        Roi? representative = null;
        var shapeBytes = 0L;
        foreach (var entry in entries)
        {
            bounds = Union(bounds, entry.Bounds);
            var roi = entry.Roi;
            var area = roi.Geometry.Area;
            if (roi.Geometry.Shape.Kind == RoiShapeKind.Polygon)
            {
                shapeBytes += roi.Geometry.Shape.Vertices.Length * 16L;
            }

            if (CompareHit(roi, firstHit) < 0)
            {
                firstHit = roi;
            }

            if (representative is null || area > maxArea || (area == maxArea && roi.Order < representative.Order))
            {
                representative = roi;
            }

            maxArea = Math.Max(maxArea, area);
        }
        return new(bounds, entries.Length, 0, maxArea, firstHit, representative, entries, null, null)
        {
            ShapeBytes = shapeBytes
        };
    }

    private static Node Branch(Node left, Node right)
    {
        var bounds = MergeBranchBounds(left, right);
        var count = left.Count + right.Count;
        var height = 1 + Math.Max(left.Height, right.Height);
        var maxArea = Math.Max(left.MaxArea, right.MaxArea);
        var firstHit = CompareHit(left.FirstHit, right.FirstHit) <= 0 ? left.FirstHit : right.FirstHit;
        var representative = SelectRepresentative(left, right);
        return new(bounds, count, height, maxArea, firstHit, representative, null, left, right)
        {
            ShapeBytes = left.ShapeBytes + right.ShapeBytes
        };
    }

    private static BoundsD MergeBranchBounds(Node left, Node right)
    {
        if (left.Count == 0)
        {
            return right.Bounds;
        }

        if (right.Count == 0)
        {
            return left.Bounds;
        }

        return Union(left.Bounds, right.Bounds);
    }

    private static Roi? SelectRepresentative(Node left, Node right)
    {
        if (left.MaxArea > right.MaxArea)
        {
            return left.Representative;
        }

        if (left.MaxArea < right.MaxArea)
        {
            return right.Representative;
        }

        var leftOrder = left.Representative?.Order ?? long.MaxValue;
        var rightOrder = right.Representative?.Order ?? long.MaxValue;
        return leftOrder <= rightOrder ? left.Representative : right.Representative;
    }

    private static int CompareHit(Roi? a, Roi? b)
    {
        if (a is null)
        {
            return b is null ? 0 : 1;
        }

        if (b is null)
        {
            return -1;
        }

        var area = a.Geometry.Area.CompareTo(b.Geometry.Area);
        return area != 0 ? area : b.Order.CompareTo(a.Order);
    }

    private static Node Build(Entry[] entries, int start, int count, BvhNodePath path, ImmutableDictionary<Guid, BvhNodePath>.Builder paths)
    {
        if (count <= 8)
        {
            var leaf = entries.AsSpan(start, count).ToArray();
            foreach (var entry in leaf)
            {
                paths[entry.Roi.Id] = path;
            }

            return Leaf(leaf);
        }
        var bounds = entries[start].Bounds;
        for (var i = start + 1; i < start + count; i++)
        {
            bounds = Union(bounds, entries[i].Bounds);
        }

        var horizontal = bounds.Width >= bounds.Height;
        Array.Sort(entries, start, count, Comparer<Entry>.Create((a, b) => horizontal ? a.Bounds.Center.X.CompareTo(b.Bounds.Center.X) : a.Bounds.Center.Y.CompareTo(b.Bounds.Center.Y)));
        var middle = count / 2;
        return Branch(Build(entries, start, middle, path.Left, paths), Build(entries, start + middle, count - middle, path.Right, paths));
    }

    /// <summary>
    /// Replaces every ROI value while preserving the spatial partition and ID paths. The caller chooses
    /// this for a coherent whole-set transform; arbitrary rearrangement can reduce later query efficiency.
    /// </summary>
    public RoiSpatialIndex RefitGeometry(ImmutableDictionary<Guid, Roi> rois, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rois);
        cancellationToken.ThrowIfCancellationRequested();
        if (rois.Count != _root.Count)
        {
            throw new ArgumentException("Refit requires exactly the indexed ROI IDs.", nameof(rois));
        }

        return new(Refit(_root), _locations);

        Node Refit(Node node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Entries is not { } entries)
            {
                return Branch(Refit(node.Left!), Refit(node.Right!));
            }

            var updated = new Entry[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                var id = entries[index].Roi.Id;
                if (!rois.TryGetValue(id, out var roi) || roi is null || roi.Id != id)
                {
                    throw new ArgumentException("Refit requires matching indexed keys and ROI IDs.", nameof(rois));
                }

                updated[index] = new(roi, roi.Geometry.Bounds);
            }
            return Leaf(updated);
        }
    }

    public RoiSpatialIndex WithChanges(IEnumerable<(Guid Id, Roi? Value)> changes)
    {
        var next = _root;
        var paths = _locations.Value.ToBuilder();
        var currentValues = _values?.ToBuilder();
        foreach (var (id, value) in changes)
        {
            if (currentValues is not null)
            {
                if (value is null)
                {
                    currentValues.Remove(id);
                }
                else
                {
                    currentValues[id] = value;
                }
            }
            // Rebalance before a long insertion sequence can exhaust the path representation.
            if (next.Height >= BvhNodePath.MaxSupportedHeight)
            {
                var balanced = new RoiSpatialIndex(Enumerate(next));
                next = balanced._root;
                paths = balanced._locations.Value.ToBuilder();
            }
            if (paths.TryGetValue(id, out var path))
            {
                next = Replace(next, path, path.FirstStepBit, id, value);
                if (value is null)
                {
                    paths.Remove(id);
                }
            }
            else if (value is not null)
            {
                next = Insert(next, new(value, value.Geometry.Bounds), BvhNodePath.Root, paths);
            }
        }
        return new(next, paths.ToImmutable(), currentValues?.ToImmutable());
    }

    private static IEnumerable<Roi> Enumerate(Node node)
    {
        if (node.Entries is { } entries)
        {
            foreach (var e in entries)
            {
                yield return e.Roi;
            }
        }
        else
        {
            foreach (var r in Enumerate(node.Left!))
            {
                yield return r;
            }

            foreach (var r in Enumerate(node.Right!))
            {
                yield return r;
            }
        }
    }

    private static Node Replace(Node node, BvhNodePath path, ulong stepBit, Guid id, Roi? value)
    {
        if (node.Entries is { } entries)
        {
            return Leaf(entries.Where(e => e.Roi.Id != id).Concat(value is null ? [] : new[] { new Entry(value, value.Geometry.Bounds) }).ToArray());
        }

        if (path.IsRightBranch(stepBit))
        {
            return Branch(node.Left!, Replace(node.Right!, path, stepBit >> 1, id, value));
        }

        return Branch(Replace(node.Left!, path, stepBit >> 1, id, value), node.Right!);
    }

    private static Node Insert(Node node, Entry entry, BvhNodePath path, ImmutableDictionary<Guid, BvhNodePath>.Builder paths)
    {
        if (node.Entries is { } entries)
        {
            var expanded = entries.Append(entry).ToArray();
            if (expanded.Length <= 8)
            {
                paths[entry.Roi.Id] = path;
                return Leaf(expanded);
            }
            return Build(expanded, 0, expanded.Length, path, paths);
        }
        var left = node.Left!;
        var right = node.Right!;
        static double Area(BoundsD bounds) => bounds.Width * bounds.Height;
        var leftCost = left.Count == 0 ? 0 : Area(Union(left.Bounds, entry.Bounds)) - Area(left.Bounds);
        var rightCost = right.Count == 0 ? 0 : Area(Union(right.Bounds, entry.Bounds)) - Area(right.Bounds);
        if (leftCost < rightCost || (leftCost == rightCost && left.Count <= right.Count))
        {
            return Branch(Insert(left, entry, path.Left, paths), right);
        }

        return Branch(left, Insert(right, entry, path.Right, paths));
    }

    public IEnumerable<Roi> At(PointD point, double minimumArea = 0) => Intersecting(new(point.X, point.Y, point.X, point.Y), minimumArea);

    /// <summary>Exact nearest editing priority without materializing/sorting every overlapping hit.</summary>
    public Roi? HitTest(PointD point, double tolerance)
    {
        if (!point.IsFinite || !double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        var margin = tolerance * Math.Sqrt(2);
        var bounds = new BoundsD(point.X - margin, point.Y - margin, point.X + margin, point.Y + margin);
        Roi? best = null;
        Visit(_root);
        return best;
        void Visit(Node node)
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds) || (best is not null && CompareHit(node.FirstHit, best) >= 0))
            {
                return;
            }

            if (node.Entries is { } entries)
            {
                foreach (var entry in entries)
                {
                    if (!entry.Bounds.Intersects(bounds) || !entry.Roi.Geometry.Contains(point, tolerance))
                    {
                        continue;
                    }

                    var candidate = Value(entry);
                    if (CompareHit(candidate, best) < 0)
                    {
                        best = candidate;
                    }
                }
            }
            else if (CompareHit(node.Left!.FirstHit, node.Right!.FirstHit) <= 0)
            {
                Visit(node.Left);
                Visit(node.Right);
            }
            else
            {
                Visit(node.Right);
                Visit(node.Left);
            }
        }
    }

    public IEnumerable<Roi> Intersecting(BoundsD bounds, double minimumArea = 0)
    {
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || node.MaxArea <= minimumArea || !node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.Entries is not { } entries)
            {
                pending.Push(node.Right!);
                pending.Push(node.Left!);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Roi.Geometry.Area > minimumArea && entry.Bounds.Intersects(bounds))
                {
                    yield return Value(entry);
                }
            }
        }
    }

    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Visible(BoundsD bounds, double aggregateSize)
        => Visible(_root, bounds, aggregateSize).Select(item => item.Roi is { } roi && _values is not null ? (_values[roi.Id], item.Bounds, item.Count) : item);

    /// <summary>Screen-density label candidates; exact geometry queries and editing remain independent.</summary>
    public IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> LabelCandidates(BoundsD bounds, double aggregateSize, double spacingX, double spacingY)
    {
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.Count > 1 && node.Bounds.Width <= aggregateSize && node.Bounds.Height <= aggregateSize)
            {
                yield return (null, node.Bounds, node.Count);
                continue;
            }
            // The largest ROI represents a dense cluster, so a containing parent wins over its children.
            if (node.Count > 1 && node.Bounds.Width <= spacingX && node.Bounds.Height <= spacingY &&
                node.Representative is { } representative && representative.Geometry.Bounds.Intersects(bounds))
            {
                yield return (_values is null ? representative : _values[representative.Id], representative.Geometry.Bounds, 1);
                continue;
            }
            if (node.Entries is not { } entries)
            {
                pending.Push(node.Right!);
                pending.Push(node.Left!);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Bounds.Intersects(bounds))
                {
                    yield return (Value(entry), entry.Bounds, 1);
                }
            }
        }
    }

    private static IEnumerable<(Roi? Roi, BoundsD Bounds, int Count)> Visible(Node root, BoundsD bounds, double aggregateSize)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.Count > 1 && node.Bounds.Width <= aggregateSize && node.Bounds.Height <= aggregateSize)
            {
                yield return (null, node.Bounds, node.Count);
                continue;
            }
            if (node.Entries is not { } entries)
            {
                pending.Push(node.Right!);
                pending.Push(node.Left!);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Bounds.Intersects(bounds))
                {
                    yield return (entry.Roi, entry.Bounds, 1);
                }
            }
        }
    }

    /// <summary>Immutable spatial blocks retain identity across edits outside their subtree.</summary>
    public IEnumerable<RoiSpatialBlock> Blocks(BoundsD bounds, int maximumCount = 2048)
    {
        if (maximumCount < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            if (node.Count == 0 || !node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.Count <= maximumCount)
            {
                yield return new(node, node.Bounds, node.Count, size => Visible(node, node.Bounds, size), node.ShapeBytes);
                continue;
            }
            pending.Push(node.Right!);
            pending.Push(node.Left!);
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
