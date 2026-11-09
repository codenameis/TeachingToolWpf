using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>한 검사 영상의 보정 스냅샷. 전체 → 모듈 → ROI 그룹(컴포넌트)을 실제 ParentId로 연결한다.</summary>
public sealed class RoiAlignmentMap
{
    private readonly DocumentState _reference;
    private readonly ImmutableDictionary<Guid, FiducialAlignment> _groups;

    public FiducialAlignment? Overall { get; }

    public ImmutableArray<AlignedFiducialMark> Marks { get; }

    public ImmutableDictionary<Guid, ComponentRoiGrid> Grids { get; }

    public bool HasGroupAlignments => !_groups.IsEmpty;

    public bool HasAnyAlignment => (Overall is { Transform.IsIdentity: false }) || _groups.Values.Any(g => !g.Transform.IsIdentity);

    public RigidTransform2D OverallTransform => Overall?.Transform ?? RigidTransform2D.Identity;

    public RoiAlignmentMap(DocumentState reference)
        : this(reference, null, ImmutableDictionary<Guid, FiducialAlignment>.Empty, [], ImmutableDictionary<Guid, ComponentRoiGrid>.Empty)
    {
        ArgumentNullException.ThrowIfNull(reference);
    }

    private RoiAlignmentMap(
        DocumentState reference,
        FiducialAlignment? overall,
        ImmutableDictionary<Guid, FiducialAlignment> groups,
        ImmutableArray<AlignedFiducialMark> marks,
        ImmutableDictionary<Guid, ComponentRoiGrid> grids)
    {
        _reference = reference;
        Overall = overall;
        _groups = groups;
        Marks = marks;
        Grids = grids;
    }

    /// <summary>
    /// scopeId가 null이면 전체 보정, 그 외에는 해당 그룹 보정이다. 상위부터 등록한다.
    /// 기준 마크는 원래 티칭 좌표, 검출 마크는 현재 원본 영상 좌표다. 실패 시 result는 현재 맵이다.
    /// </summary>
    public bool TryAlign(Guid? scopeId, ReadOnlySpan<PointD> referenceMarks, ReadOnlySpan<PointD> detectedMarks,
        out RoiAlignmentMap result, out string error, double maximumErrorPixels = 1)
    {
        result = this;
        error = string.Empty;
        FiducialAlignment? parent = null;
        if (scopeId is { } id)
        {
            if (!TryResolve(id, out _, out error))
            {
                return false;
            }
            if (!_reference.IsGroupRoi(id))
            {
                error = "보정 범위는 모듈 또는 ROI 그룹이어야 합니다. 검사 리프 ROI는 부모 보정을 사용합니다.";
                return false;
            }
            if (!TryResolve(_reference.Rois[id].ParentId, out parent, out error))
            {
                return false;
            }
        }
        if (HasAlignedDescendant(scopeId))
        {
            error = "이미 하위 그룹 보정이 있습니다. 새 RoiAlignmentMap에서 전체 → 모듈 → ROI 그룹 순서로 다시 보정하세요.";
            return false;
        }
        if (!FiducialAlignment.TryCreate(referenceMarks, detectedMarks, out var alignment, out error, maximumErrorPixels, parent))
        {
            return false;
        }

        result = scopeId is { } groupId
            ? new(_reference, Overall, _groups.SetItem(groupId, alignment), Marks, Grids)
            : new(_reference, alignment, _groups, Marks, Grids);
        return true;
    }

    /// <summary>원래 ROI의 부모·ID·마스크·설정을 보존하고 보정된 형상만 반환한다. 문서와 Undo 이력은 변경하지 않는다.</summary>
    public Roi Map(Guid roiId)
    {
        if (!TryResolve(roiId, out var alignment, out var error))
        {
            throw new InvalidOperationException(error);
        }
        var roi = _reference.Rois[roiId];
        return alignment is null || alignment.Transform.IsIdentity ? roi : roi with
        {
            Geometry = alignment.Apply(roi.Geometry)
        };
    }

    /// <summary>ROI가 사용하는 합성 보정. 보정이 전혀 없으면 null이며 원래 좌표를 사용한다.</summary>
    public FiducialAlignment? GetAlignment(Guid roiId)
    {
        if (!TryResolve(roiId, out var alignment, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return alignment;
    }

    /// <summary>보정 범위(null=전체, 그 외=모듈/컴포넌트 그룹 ID)의 유효 보정을 O(1) 계층 깊이로 조회한다.</summary>
    public FiducialAlignment? GetScopeAlignment(Guid? scopeId)
    {
        if (scopeId is null)
        {
            return Overall;
        }

        return TryResolve(scopeId, out var alignment, out _) ? alignment : Overall;
    }

    /// <summary>보정 범위의 합성 강체 변환을 반환한다.</summary>
    public RigidTransform2D GetScopeTransform(Guid? scopeId)
        => GetScopeAlignment(scopeId)?.Transform ?? RigidTransform2D.Identity;

    /// <summary>개별 ROI 객체를 복제하지 않고 해당 ROI가 속한 그룹 범위의 합성 변환을 반환한다.</summary>
    public RigidTransform2D GetEffectiveTransform(Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var governingScopeId = _reference.Rois.ContainsKey(roi.Id) && _reference.IsGroupRoi(roi.Id)
            ? roi.Id
            : roi.ParentId;
        return GetScopeTransform(governingScopeId);
    }

    /// <summary>개별 ROI 레코드 할당 없이 보정된 기하만 계산한다.</summary>
    public RoiGeometry ApplyGeometry(Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var transform = GetEffectiveTransform(roi);
        return transform.IsIdentity
            ? roi.Geometry
            : roi.Geometry.WithFrame(transform.Apply(roi.Geometry.Center), roi.Geometry.Width, roi.Geometry.Height, roi.Geometry.Angle + transform.AngleDegrees);
    }

    /// <summary>컴포넌트에 연결된 파라메트릭 격자(1억~수십억 셀)의 (row, column) 보정 후 기하를 O(1)에 반환한다.</summary>
    public RoiGeometry MapGridCell(Guid scopeId, int row, int column)
    {
        if (!Grids.TryGetValue(scopeId, out var grid))
        {
            throw new KeyNotFoundException($"범위 '{scopeId}'에 등록된 반복 격자가 없습니다.");
        }

        return grid.GetAlignedCell(this, row, column);
    }

    public RoiAlignmentMap WithMetadata(
        ImmutableArray<AlignedFiducialMark> marks,
        ImmutableDictionary<Guid, ComponentRoiGrid> grids)
    {
        ArgumentNullException.ThrowIfNull(grids);
        return new(_reference, Overall, _groups, marks.IsDefault ? [] : marks, grids);
    }

    private bool TryResolve(Guid? roiId, out FiducialAlignment? alignment, out string error)
    {
        alignment = null;
        error = string.Empty;
        var depth = 0;
        var current = roiId;
        while (current is { } id)
        {
            if (++depth > RoiHierarchy.MaximumDepth || !_reference.Rois.TryGetValue(id, out var roi))
            {
                error = "ROI의 부모 연결이 없거나 순환/최대 4단계 계층 제한을 위반했습니다.";
                return false;
            }
            if (alignment is null && _groups.TryGetValue(id, out var found))
            {
                alignment = found;
            }
            current = roi.ParentId;
        }
        alignment ??= Overall;
        return true;
    }

    private bool HasAlignedDescendant(Guid? scopeId)
    {
        if (scopeId is not { } id)
        {
            return _groups.Count > 0;
        }
        foreach (var groupId in _groups.Keys)
        {
            if (RoiHierarchy.IsDescendant(_reference, groupId, id))
            {
                return true;
            }
        }
        return false;
    }
}
