using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public readonly record struct RepeatedRoiCell(Guid BatchId, int Index);

/// <summary>Stable batch/cell identities and immutable pose partitions of one repeated layout.</summary>
public sealed class RepeatedRoiBatch
{
    private readonly Lazy<RoiCellSet> _cells;
    private readonly Lazy<BoundsD> _bounds;
    public Guid Id
    {
        get;
    }
    public string Name
    {
        get;
    }
    public long Order
    {
        get;
    }
    public ImmutableArray<RepeatedRoiPart> Parts
    {
        get;
    }
    public int Count
    {
        get;
    }
    public RoiCellSet Cells => _cells.Value;
    public BoundsD Bounds => _bounds.Value;
    // Include the retained blocks and reserve geometry/BVH/path storage before those lazy values exist.
    public long EstimatedBytes => 256L + Parts.Sum(part => part.EstimatedBytes);

    public RepeatedRoiBatch(Guid id, string name, long order, RepeatedRoiGrid grid)
        : this(id, name, order, [new(grid, RoiCellSet.All(grid.Count))]) { }

    private RepeatedRoiBatch(Guid id, string name, long order, ImmutableArray<RepeatedRoiPart> parts)
    {
        Id = id;
        Name = name;
        Order = order;
        Parts = parts;
        Count = parts.Sum(part => part.Cells.Count);
        _cells = new(() => parts.Aggregate(RoiCellSet.Empty, (cells, part) => cells.Union(part.Cells)));
        _bounds = new(() => BoundsD.FromPoints(parts.SelectMany(part => new[]
        {
            new PointD(part.Bounds.Left, part.Bounds.Top), new PointD(part.Bounds.Right, part.Bounds.Bottom)
        })));
    }

    public RoiGeometry Geometry(int index) => Part(index).Grid.Geometry(index);

    private RepeatedRoiPart Part(int index) => Parts.FirstOrDefault(part => part.Cells.Contains(index))
        ?? throw new ArgumentOutOfRangeException(nameof(index));

    public RepeatedRoiBatch WithGeometry(int index, RoiGeometry geometry)
    {
        var part = Part(index);
        var grid = part.Grid.WithGeometry(index, geometry);
        return ReferenceEquals(grid, part.Grid) ? this : new(Id, Name, Order, Parts.Replace(part, new(grid, part.Cells)));
    }

    public RepeatedRoiBatch Transform(RoiCellSet selected, RigidTransform2D transform)
    {
        if (selected.IsEmpty || transform.IsIdentity)
        {
            return this;
        }

        var parts = ImmutableArray.CreateBuilder<RepeatedRoiPart>();
        foreach (var part in Parts)
        {
            var moving = part.Cells.Intersect(selected);
            if (moving.IsEmpty)
            {
                parts.Add(part);
            }
            else if (moving.Count == part.Cells.Count)
            {
                parts.Add(part.Transform(transform));
            }
            else
            {
                parts.Add(new(part.Grid, part.Cells.Except(moving)));
                parts.Add(new(part.Grid.Transform(transform), moving));
            }
        }
        return new(Id, Name, Order, parts.ToImmutable());
    }

    public RepeatedRoiBatch? Slice(RoiCellSet cells)
    {
        var parts = ImmutableArray.CreateBuilder<RepeatedRoiPart>();
        foreach (var part in Parts)
        {
            var included = part.Cells.Intersect(cells);
            if (!included.IsEmpty)
            {
                parts.Add(included.Count == part.Cells.Count ? part : new(part.Grid, included));
            }
        }
        return parts.Count == 0 ? null : parts.Count == Parts.Length && parts.SequenceEqual(Parts) ? this
            : new(Id, Name, Order, parts.ToImmutable());
    }

    public RoiCellSet Contained(BoundsD bounds, CancellationToken token = default)
    {
        var selected = RoiCellSet.Empty;
        foreach (var part in Parts)
        {
            token.ThrowIfCancellationRequested();
            selected = selected.Union(part.Contained(bounds, token));
        }
        return selected;
    }

    public int? HitTest(PointD point, double tolerance)
    {
        int? hit = null;
        var distance = double.PositiveInfinity;
        foreach (var part in Parts)
        {
            if (part.HitTest(point, tolerance) is not { } index)
            {
                continue;
            }

            var next = (part.Grid.Geometry(index).Center - point).Length;
            if (next < distance || (next == distance && (hit is null || index > hit)))
            {
                hit = index;
                distance = next;
            }
        }
        return hit;
    }

    internal RepeatedRoiBatch Place(Guid id, long order, RoiClipboardContent.Placement placement)
        => new(id, Name[..Math.Min(Name.Length, 253)] + " 복사", order,
            Parts.Select(part => new RepeatedRoiPart(part.Grid.Place(placement.SourceCenter, placement.TargetCenter, placement.Scale, part.Cells), part.Cells)).ToImmutableArray());
}
