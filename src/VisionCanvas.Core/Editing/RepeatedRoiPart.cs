using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>A subset of stable cell IDs sharing one pose. Partial rigid edits split parts, not cells.</summary>
public sealed class RepeatedRoiPart
{
    private readonly Lazy<RoiSpatialIndex> _constraints;
    private readonly Lazy<ImmutableArray<CellBlock>> _blocks;
    public RepeatedRoiGrid Grid
    {
        get;
    }
    public RoiCellSet Cells
    {
        get;
    }
    public RoiSpatialIndex Constraints => _constraints.Value;
    public BoundsD Bounds => Constraints.Bounds;
    public ImmutableArray<CellBlock> Blocks => _blocks.Value;
    internal long EstimatedBytes => 128L + (Cells.Ranges.Length * 8L)
        + ((_blocks.Value.Length + (long)Grid.Overrides.Count) * 1024)
        + Grid.Overrides.Values.Sum(geometry => 256L + (geometry.Shape.Vertices.Length * 16L));

    public RepeatedRoiPart(RepeatedRoiGrid grid, RoiCellSet cells)
    {
        if (cells.IsEmpty || cells.Ranges[^1].End > grid.Count)
        {
            throw new ArgumentException("격자 안의 셀 범위가 필요합니다.", nameof(cells));
        }

        Grid = grid;
        Cells = cells;
        _blocks = new(() => CreateBlocks().ToImmutableArray());
        _constraints = new(() => new(ConstraintGeometries().Select((geometry, index) =>
            new Roi(new Guid(index, 0, 0, new byte[8]), "", geometry, null, false, index))));
    }

    public RepeatedRoiPart Transform(RigidTransform2D transform) => transform.IsIdentity ? this : new(Grid.Transform(transform), Cells);

    public RoiCellSet Contained(BoundsD bounds, CancellationToken token = default)
    {
        var ranges = new List<RoiCellSet.Range>();
        var (sin, cos) = Math.SinCos(Grid.Angle * Math.PI / 180);
        var halfX = ((Math.Abs(cos) * Grid.CellWidth) + (Math.Abs(sin) * Grid.CellHeight)) / 2;
        var halfY = ((Math.Abs(sin) * Grid.CellWidth) + (Math.Abs(cos) * Grid.CellHeight)) / 2;
        var left = bounds.Left + halfX;
        var right = bounds.Right - halfX;
        var top = bounds.Top + halfY;
        var bottom = bounds.Bottom - halfY;
        if (left <= right && top <= bottom)
        {
            for (var row = 0; row < Grid.Rows; row++)
            {
                token.ThrowIfCancellationRequested();
                var y = (row - ((Grid.Rows - 1) / 2d)) * Grid.Pitch;
                var low = -((Grid.Columns - 1) / 2d) * Grid.Pitch;
                var high = -low;
                Clip(cos, Grid.Center.X - (sin * y), left, right, ref low, ref high);
                Clip(sin, Grid.Center.Y + (cos * y), top, bottom, ref low, ref high);
                if (low > high)
                {
                    continue;
                }

                var first = (int)Math.Clamp(Math.Ceiling((low / Grid.Pitch) + ((Grid.Columns - 1) / 2d) - 1e-10), 0, Grid.Columns);
                var end = (int)Math.Clamp(Math.Floor((high / Grid.Pitch) + ((Grid.Columns - 1) / 2d) + 1e-10) + 1, 0, Grid.Columns);
                if (end > first)
                {
                    ranges.Add(new((row * Grid.Columns) + first, (row * Grid.Columns) + end));
                }
            }
        }
        var selected = RoiCellSet.Create(ranges).Intersect(Cells);
        if (Grid.Overrides.IsEmpty)
        {
            return selected;
        }

        var overrides = RoiCellSet.Create(Grid.Overrides.Keys.Select(index => new RoiCellSet.Range(index, index + 1)));
        var included = Grid.Overrides.Keys.Where(index => Cells.Contains(index)).Where(index =>
        {
            token.ThrowIfCancellationRequested();
            var actual = Grid.Geometry(index).Bounds;
            return bounds.Contains(new(actual.Left, actual.Top)) && bounds.Contains(new(actual.Right, actual.Bottom));
        });
        return selected.Except(overrides).Union(RoiCellSet.Create(included.Select(index => new RoiCellSet.Range(index, index + 1))));
    }

    private static void Clip(double coefficient, double offset, double minimum, double maximum, ref double low, ref double high)
    {
        if (Math.Abs(coefficient) < 1e-14)
        {
            if (offset < minimum || offset > maximum)
            {
                low = double.PositiveInfinity;
            }

            return;
        }
        var a = (minimum - offset) / coefficient;
        var b = (maximum - offset) / coefficient;
        low = Math.Max(low, Math.Min(a, b));
        high = Math.Min(high, Math.Max(a, b));
    }

    public int? HitTest(PointD point, double tolerance)
    {
        int? hit = null;
        var distance = double.PositiveInfinity;
        foreach (var index in HitCandidates(point, tolerance))
        {
            var candidate = (Grid.Geometry(index).Center - point).Length;
            if (candidate < distance || (candidate == distance && (hit is null || index > hit)))
            {
                hit = index;
                distance = candidate;
            }
        }
        return hit;
    }

    internal IEnumerable<int> HitCandidates(PointD point, double tolerance)
    {
        if (!point.IsFinite || !double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }

        var local = Grid.Pose.InverseApply(point);
        var column = (local.X / Grid.Pitch) + ((Grid.Columns - 1) / 2d);
        var row = (local.Y / Grid.Pitch) + ((Grid.Rows - 1) / 2d);
        foreach (var block in _blocks.Value)
        {
            var x = (int)Math.Clamp(Math.Round(column), block.Left, block.Right);
            var y = (int)Math.Clamp(Math.Round(row), block.Top, block.Bottom);
            var index = (y * Grid.Columns) + x;
            if (Grid.LocalGeometry(index).Contains(local, tolerance))
            {
                yield return index;
            }
        }
        foreach (var index in Grid.Overrides.Keys)
        {
            if (Cells.Contains(index) && Grid.LocalGeometry(index).Contains(local, tolerance))
            {
                yield return index;
            }
        }
    }

    public IEnumerable<RoiGeometry> ConstraintGeometries()
    {
        foreach (var block in _blocks.Value)
        {
            yield return Grid.Block(block.Left, block.Top, block.Right, block.Bottom);
        }

        foreach (var index in Grid.Overrides.Keys)
        {
            if (Cells.Contains(index))
            {
                yield return Grid.Geometry(index);
            }
        }
    }

    private IEnumerable<CellBlock> CreateBlocks()
    {
        var cells = Grid.Overrides.IsEmpty ? Cells : Cells.Except(RoiCellSet.Create(
            Grid.Overrides.Keys.Select(index => new RoiCellSet.Range(index, index + 1))));
        CellBlock? pending = null;
        foreach (var range in cells.Ranges)
        {
            foreach (var next in Split(range))
            {
                if (pending is { } previous && previous.Left == next.Left && previous.Right == next.Right && previous.Bottom + 1 == next.Top)
                {
                    pending = previous with
                    {
                        Bottom = next.Bottom
                    };
                }
                else
                {
                    if (pending is { } value)
                    {
                        yield return value;
                    }

                    pending = next;
                }
            }
        }
        if (pending is { } last)
        {
            yield return last;
        }
    }

    private IEnumerable<CellBlock> Split(RoiCellSet.Range range)
    {
        var start = range.Start;
        var firstColumn = start % Grid.Columns;
        if (firstColumn != 0)
        {
            var end = Math.Min(range.End, start + Grid.Columns - firstColumn);
            yield return new(firstColumn, start / Grid.Columns, (end - 1) % Grid.Columns, start / Grid.Columns);
            start = end;
        }
        var rows = (range.End - start) / Grid.Columns;
        if (rows > 0)
        {
            yield return new(0, start / Grid.Columns, Grid.Columns - 1, (start / Grid.Columns) + rows - 1);
            start += rows * Grid.Columns;
        }
        if (start < range.End)
        {
            yield return new(0, start / Grid.Columns, range.End - start - 1, start / Grid.Columns);
        }
    }

    public readonly record struct CellBlock(int Left, int Top, int Right, int Bottom);
}
