using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>
/// 컴포넌트 또는 모듈 하위에 소속된 초대용량(1억~수십억 개 이상) 파라메트릭 ROI 격자 기술자.
/// 개별 ROI 객체를 힙에 할당하지 않고 64바이트 수준의 기술자와 계층 변환만으로 O(1) 보정 좌표를 계산한다.
/// </summary>
public sealed record ComponentRoiGrid
{
    public Guid ScopeId { get; }

    public string Name { get; }

    public PointD Origin { get; }

    public int Columns { get; }

    public int Rows { get; }

    public double PitchX { get; }

    public double PitchY { get; }

    public double CellWidth { get; }

    public double CellHeight { get; }

    public double CellAngle { get; }

    public long TotalCount => (long)Columns * Rows;

    public ComponentRoiGrid(
        Guid scopeId,
        string name,
        PointD origin,
        int columns,
        int rows,
        double pitchX,
        double pitchY,
        double cellWidth,
        double cellHeight,
        double cellAngle = 0)
    {
        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("소속 범위 ID가 비어 있습니다.", nameof(scopeId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("격자 이름이 비어 있습니다.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        if (!origin.IsFinite || !double.IsFinite(pitchX) || pitchX <= 0 || !double.IsFinite(pitchY) || pitchY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(origin), "원점과 피치는 유한한 양수여야 합니다.");
        }

        if (!double.IsFinite(cellWidth) || cellWidth < 0.01 || !double.IsFinite(cellHeight) || cellHeight < 0.01 || !double.IsFinite(cellAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(cellWidth), "셀 크기와 각도가 유효하지 않습니다.");
        }

        ScopeId = scopeId;
        Name = name;
        Origin = origin;
        Columns = columns;
        Rows = rows;
        PitchX = pitchX;
        PitchY = pitchY;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        CellAngle = cellAngle;
    }

    /// <summary>보정 전 티칭 기준에서 (row, column) 셀의 기하를 O(1)에 계산한다.</summary>
    public RoiGeometry GetTeachingCell(int row, int column)
    {
        if ((uint)row >= (uint)Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if ((uint)column >= (uint)Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        var halfColumns = (Columns - 1) / 2d;
        var halfRows = (Rows - 1) / 2d;
        var localOffset = new PointD((column - halfColumns) * PitchX, (row - halfRows) * PitchY);
        var center = Origin + localOffset.Rotate(CellAngle);
        return new RoiGeometry(center, CellWidth, CellHeight, CellAngle);
    }

    /// <summary>전체 → 모듈 → 컴포넌트 합성 보정을 한 번만 적용한 (row, column) 셀의 최종 검사 기하를 O(1)에 반환한다.</summary>
    public RoiGeometry GetAlignedCell(RoiAlignmentMap alignment, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        var teaching = GetTeachingCell(row, column);
        var transform = alignment.GetScopeTransform(ScopeId);
        return transform.IsIdentity
            ? teaching
            : teaching.WithFrame(transform.Apply(teaching.Center), teaching.Width, teaching.Height, teaching.Angle + transform.AngleDegrees);
    }
}
