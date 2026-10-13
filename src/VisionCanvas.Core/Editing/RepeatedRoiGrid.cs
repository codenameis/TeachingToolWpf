using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>반복 사각 ROI의 손실 없는 배치. 행·열 ID와 개별 수정값을 보존한다.</summary>
public sealed record RepeatedRoiGrid
{
    public int Columns
    {
        get;
    }
    public int Rows
    {
        get;
    }
    public int Count
    {
        get;
    }
    public double Pitch
    {
        get; private init;
    }
    public double CellWidth => Pitch * .6;
    public double CellHeight => Pitch * .4;
    public PointD Center
    {
        get; private init;
    }
    public double Angle
    {
        get; private init;
    }
    public ImmutableDictionary<int, RoiGeometry> Overrides { get; private init; } = ImmutableDictionary<int, RoiGeometry>.Empty;
    public BoundsD LocalBounds
    {
        get; private init;
    }
    public RigidTransform2D Pose => new(default, Angle, Center);
    public RoiGeometry Frame => new(Pose.Apply(LocalBounds.Center), LocalBounds.Width, LocalBounds.Height, Angle);

    public RepeatedRoiGrid(int columns, int rows, double pitch, PointD center)
    {
        if (columns < 1 || rows < 1 || (long)columns * rows > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }
        if (!double.IsFinite(pitch) || pitch < .025 || !center.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch));
        }
        Columns = columns;
        Rows = rows;
        Count = checked(columns * rows);
        Pitch = pitch;
        Center = center;
        LocalBounds = BaseBounds;
    }

    public BoundsD BaseBounds => new(-(((Columns - 1) * Pitch) + CellWidth) / 2,
        -(((Rows - 1) * Pitch) + CellHeight) / 2,
        (((Columns - 1) * Pitch) + CellWidth) / 2, (((Rows - 1) * Pitch) + CellHeight) / 2);

    public RoiGeometry LocalGeometry(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return Overrides.TryGetValue(index, out var geometry) ? geometry : BaseGeometry(index);
    }

    private RoiGeometry BaseGeometry(int index) => new(new(
        ((index % Columns) - ((Columns - 1) / 2d)) * Pitch,
        ((index / Columns) - ((Rows - 1) / 2d)) * Pitch), CellWidth, CellHeight, 0);

    public RoiGeometry Geometry(int index)
    {
        var local = LocalGeometry(index);
        return local.WithFrame(Pose.Apply(local.Center), local.Width, local.Height, local.Angle + Angle);
    }

    public RepeatedRoiGrid WithGeometry(int index, RoiGeometry geometry)
    {
        var old = Geometry(index);
        if (old == geometry)
        {
            return this;
        }
        var local = geometry.WithFrame(Pose.InverseApply(geometry.Center), geometry.Width, geometry.Height, geometry.Angle - Angle);
        // A scaled clipboard part may contain only overrides; its unused base cell can be below
        // the minimum ROI size. Do not construct that virtual geometry while editing a valid cell.
        var matchesBase = CellWidth >= .01 && CellHeight >= .01 && local == BaseGeometry(index);
        var edits = matchesBase ? Overrides.Remove(index) : Overrides.SetItem(index, local);
        var bounds = BaseBounds;
        foreach (var item in edits.Values)
        {
            var other = item.Bounds;
            bounds = new(Math.Min(bounds.Left, other.Left), Math.Min(bounds.Top, other.Top),
                Math.Max(bounds.Right, other.Right), Math.Max(bounds.Bottom, other.Bottom));
        }
        return this with
        {
            Overrides = edits,
            LocalBounds = bounds
        };
    }

    public RepeatedRoiGrid Transform(RigidTransform2D transform) => transform.IsIdentity ? this : this with
    {
        Center = transform.Apply(Center),
        Angle = RoiGeometry.NormalizeAngle(Angle + transform.AngleDegrees)
    };

    internal RepeatedRoiGrid Place(PointD sourceCenter, PointD targetCenter, double scale, RoiCellSet cells)
    {
        if (scale == 1)
        {
            return Transform(new(default, 0, targetCenter - sourceCenter));
        }
        // Match ordinary clipboard placement, including minimum geometry size validation.
        var included = Overrides.Where(pair => cells.Contains(pair.Key)).ToArray();
        if (cells.Count > included.Length && CellHeight * scale < .01)
        {
            throw new ArgumentException("붙여넣은 ROI 크기가 최소 0.01px보다 작습니다.", nameof(scale));
        }

        var overrides = included.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.WithFrame(
            pair.Value.Center * scale, pair.Value.Width * scale, pair.Value.Height * scale, pair.Value.Angle));
        return this with
        {
            Pitch = Pitch * scale,
            Center = targetCenter + ((Center - sourceCenter) * scale),
            Overrides = overrides,
            LocalBounds = new(LocalBounds.Left * scale, LocalBounds.Top * scale, LocalBounds.Right * scale, LocalBounds.Bottom * scale)
        };
    }

    internal IEnumerable<RoiGeometry> ConstraintGeometries()
    {
        // 수정된 셀을 제외한 직사각 격자 구간의 convex hull은 그 구간 사각형과 같다.
        // 빈 모서리를 포함한 전체 AABB로 회전을 제한하지 않고 실제 남은 구간을 사용한다.
        var nextRow = 0;
        foreach (var row in Overrides.Keys.Order().GroupBy(index => index / Columns))
        {
            if (row.Key > nextRow)
            {
                yield return Block(0, nextRow, Columns - 1, row.Key - 1);
            }
            var nextColumn = 0;
            foreach (var index in row)
            {
                var column = index % Columns;
                if (column > nextColumn)
                {
                    yield return Block(nextColumn, row.Key, column - 1, row.Key);
                }
                nextColumn = column + 1;
                yield return Geometry(index);
            }
            if (nextColumn < Columns)
            {
                yield return Block(nextColumn, row.Key, Columns - 1, row.Key);
            }
            nextRow = row.Key + 1;
        }
        if (nextRow < Rows)
        {
            yield return Block(0, nextRow, Columns - 1, Rows - 1);
        }
    }

    internal RoiGeometry Block(int left, int top, int right, int bottom)
    {
        var first = BaseGeometry((top * Columns) + left);
        var last = BaseGeometry((bottom * Columns) + right);
        return new(Pose.Apply((first.Center + last.Center) / 2),
            ((right - left) * Pitch) + CellWidth, ((bottom - top) * Pitch) + CellHeight, Angle);
    }

    public int? HitTest(PointD imagePoint, double tolerance)
    {
        if (!imagePoint.IsFinite || !double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imagePoint));
        }
        var point = Pose.InverseApply(imagePoint);
        int? hit = null;
        var distance = double.PositiveInfinity;
        // 밀집 격자는 포인터에 가장 가까운 중심을 고른다. 수정된 ROI도 실제 윤곽으로 판정한다.
        foreach (var (index, geometry) in Overrides)
        {
            if (geometry.Contains(point, tolerance))
            {
                var nextDistance = (geometry.Center - point).Length;
                if (nextDistance < distance || (nextDistance == distance && index > hit))
                {
                    hit = index;
                    distance = nextDistance;
                }
            }
        }
        var column = (int)Math.Clamp(Math.Round((point.X / Pitch) + ((Columns - 1) / 2d)), 0, Columns - 1);
        var row = (int)Math.Clamp(Math.Round((point.Y / Pitch) + ((Rows - 1) / 2d)), 0, Rows - 1);
        var toleranceRadius = double.Hypot((CellWidth / 2) + tolerance, (CellHeight / 2) + tolerance);
        for (var radius = 0; radius < Math.Max(Columns, Rows); radius++)
        {
            for (var y = Math.Max(0, row - radius); y <= Math.Min(Rows - 1, row + radius); y++)
            {
                for (var x = Math.Max(0, column - radius); x <= Math.Min(Columns - 1, column + radius); x++)
                {
                    if (radius > 0 && Math.Abs(x - column) < radius && Math.Abs(y - row) < radius)
                    {
                        continue;
                    }
                    var index = (y * Columns) + x;
                    if (Overrides.ContainsKey(index))
                    {
                        continue;
                    }
                    var geometry = BaseGeometry(index);
                    var nextDistance = (geometry.Center - point).Length;
                    if (geometry.Contains(point, tolerance) &&
                        (nextDistance < distance || (nextDistance == distance && (hit is null || index > hit))))
                    {
                        hit = index;
                        distance = nextDistance;
                    }
                }
            }
            // 아직 방문하지 않은 네 띠에서 가능한 최소 중심 거리가 현재 답보다 멀면 끝낸다.
            // 인접 셀이 수정되어 사라져도 고정 3×3 후보 제한으로 선택을 누락하지 않는다.
            var unseen = double.PositiveInfinity;
            Include(column - radius - 1, row);
            Include(column + radius + 1, row);
            Include(column, row - radius - 1);
            Include(column, row + radius + 1);
            if (unseen > Math.Min(distance, toleranceRadius) || double.IsPositiveInfinity(unseen))
            {
                break;
            }

            void Include(int x, int y)
            {
                if (x >= 0 && x < Columns && y >= 0 && y < Rows)
                {
                    unseen = Math.Min(unseen, (BaseGeometry((y * Columns) + x).Center - point).Length);
                }
            }
        }
        return hit;
    }
}
