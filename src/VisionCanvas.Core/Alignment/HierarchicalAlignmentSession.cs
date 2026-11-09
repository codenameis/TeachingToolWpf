using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>
/// 전체 → 모듈 → 컴포넌트 계층별 보정마크와 로컬 오프셋(X, Y, Theta)을 관리하고,
/// 상위 보정이 변경되더라도 항상 상위 → 하위 순서로 불변 <see cref="RoiAlignmentMap"/>을 재구성한다.
/// </summary>
public sealed class HierarchicalAlignmentSession
{
    private readonly DocumentState _document;
    private readonly ImmutableArray<FiducialScopeDefinition> _orderedScopes;
    private readonly ImmutableDictionary<Guid, ComponentRoiGrid> _grids;
    private readonly Dictionary<Guid, FiducialScopeOffset> _groupOffsets = [];
    private FiducialScopeOffset _overallOffset = FiducialScopeOffset.Identity;

    public DocumentState Document => _document;

    public ImmutableArray<FiducialScopeDefinition> Scopes => _orderedScopes;

    public RoiAlignmentMap CurrentMap { get; private set; }

    public long TotalLogicalRoiCount { get; }

    public HierarchicalAlignmentSession(DocumentState document, IEnumerable<FiducialScopeDefinition> scopes)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scopes);
        _document = document;
        _orderedScopes = OrderTopologically(document, scopes.ToImmutableArray());

        var grids = ImmutableDictionary.CreateBuilder<Guid, ComponentRoiGrid>();
        long gridCellSum = 0;
        foreach (var scope in _orderedScopes)
        {
            if (scope.ScopeId is { } id && scope.Grid is { } grid)
            {
                grids[id] = grid;
                gridCellSum = checked(gridCellSum + grid.TotalCount);
            }
        }

        _grids = grids.ToImmutable();
        TotalLogicalRoiCount = checked(document.RoiCount + gridCellSum);
        CurrentMap = RebuildMap(out _);
    }

    public FiducialScopeOffset GetOffset(Guid? scopeId)
        => scopeId is { } id
            ? _groupOffsets.GetValueOrDefault(id, FiducialScopeOffset.Identity)
            : _overallOffset;

    public bool TrySetOffset(Guid? scopeId, FiducialScopeOffset offset, out string error)
    {
        var previousOverall = _overallOffset;
        var hadGroup = false;
        FiducialScopeOffset previousGroup = default;
        if (scopeId is { } id)
        {
            hadGroup = _groupOffsets.TryGetValue(id, out previousGroup);
            _groupOffsets[id] = offset;
        }
        else
        {
            _overallOffset = offset;
        }

        var rebuilt = RebuildMap(out error);
        if (!string.IsNullOrEmpty(error))
        {
            if (scopeId is { } rollbackId)
            {
                if (hadGroup)
                {
                    _groupOffsets[rollbackId] = previousGroup;
                }
                else
                {
                    _groupOffsets.Remove(rollbackId);
                }
            }
            else
            {
                _overallOffset = previousOverall;
            }

            return false;
        }

        CurrentMap = rebuilt;
        return true;
    }

    public void ResetScope(Guid? scopeId)
    {
        var current = GetOffset(scopeId);
        _ = TrySetOffset(scopeId, new(0, 0, 0, current.IsEnabled), out _);
    }

    public void ResetAll()
    {
        _overallOffset = FiducialScopeOffset.Identity;
        _groupOffsets.Clear();
        CurrentMap = RebuildMap(out _);
    }

    private RoiAlignmentMap RebuildMap(out string error)
    {
        error = string.Empty;
        var map = new RoiAlignmentMap(_document);
        var marks = ImmutableArray.CreateBuilder<AlignedFiducialMark>(_orderedScopes.Length * 2);
        Span<PointD> detectedBuffer = stackalloc PointD[4];

        foreach (var scope in _orderedScopes)
        {
            var offset = GetOffset(scope.ScopeId);
            var parentTransform = scope.ScopeId is { } groupId && _document.Rois.TryGetValue(groupId, out var groupRoi)
                ? map.GetScopeTransform(groupRoi.ParentId)
                : RigidTransform2D.Identity;

            var referenceMarks = scope.ReferenceMarks.AsSpan();
            var detectedMarks = detectedBuffer[..referenceMarks.Length];
            var parentAlignedCenter = ComputeCentroid(referenceMarks, parentTransform);
            var localTransform = offset.IsEnabled
                ? new RigidTransform2D(parentAlignedCenter, offset.DeltaThetaDegrees, new(offset.DeltaX, offset.DeltaY))
                : RigidTransform2D.Identity;

            for (var index = 0; index < referenceMarks.Length; index++)
            {
                var reference = referenceMarks[index];
                var parentAligned = parentTransform.Apply(reference);
                var detected = localTransform.Apply(parentAligned);
                detectedMarks[index] = detected;
                marks.Add(new(scope.ScopeId, reference, parentAligned, detected, offset.IsEnabled));
            }

            if (!offset.IsEnabled)
            {
                continue;
            }

            if (!map.TryAlign(scope.ScopeId, referenceMarks, detectedMarks, out map, out error))
            {
                return CurrentMap;
            }
        }

        return map.WithMetadata(marks.ToImmutable(), _grids);
    }

    private static PointD ComputeCentroid(ReadOnlySpan<PointD> marks, RigidTransform2D transform)
    {
        var center = new PointD();
        for (var index = 0; index < marks.Length; index++)
        {
            center += transform.Apply(marks[index]) / marks.Length;
        }

        return center;
    }

    private static ImmutableArray<FiducialScopeDefinition> OrderTopologically(
        DocumentState document,
        ImmutableArray<FiducialScopeDefinition> scopes)
    {
        return scopes
            .OrderBy(scope => scope.ScopeId is null ? 0 : RoiHierarchy.Depth(document.Rois, scope.ScopeId.Value))
            .ToImmutableArray();
    }
}
