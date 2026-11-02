using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>한 검사 영상의 보정 스냅샷. 전체 → 모듈 → ROI 그룹을 실제 ParentId로 연결한다.</summary>
public sealed class RoiAlignmentMap
{
    private readonly DocumentState _reference;
    private readonly ImmutableDictionary<Guid, FiducialAlignment> _groups;

    public FiducialAlignment? Overall { get; }

    public RoiAlignmentMap(DocumentState reference)
        : this(reference, null, ImmutableDictionary<Guid, FiducialAlignment>.Empty)
    {
        ArgumentNullException.ThrowIfNull(reference);
    }

    private RoiAlignmentMap(DocumentState reference, FiducialAlignment? overall, ImmutableDictionary<Guid, FiducialAlignment> groups)
    {
        _reference = reference;
        Overall = overall;
        _groups = groups;
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
            ? new(_reference, Overall, _groups.SetItem(groupId, alignment))
            : new(_reference, alignment, _groups);
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
