using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>전체(null), 모듈, 컴포넌트 각 보정 단위의 기준 보정마크와 대량 셀 배치 정의.</summary>
public sealed record FiducialScopeDefinition
{
    public Guid? ScopeId { get; }

    public string Name { get; }

    public Guid? ParentScopeId { get; }

    public ImmutableArray<PointD> ReferenceMarks { get; }

    public ComponentRoiGrid? Grid { get; }

    public FiducialScopeDefinition(
        Guid? scopeId,
        string name,
        Guid? parentScopeId,
        ImmutableArray<PointD> referenceMarks,
        ComponentRoiGrid? grid = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("보정 범위 이름이 비어 있습니다.", nameof(name));
        }

        if (referenceMarks.IsDefault || referenceMarks.Length is < 1 or > 4)
        {
            throw new ArgumentException("기준 보정마크는 1~4점이어야 합니다.", nameof(referenceMarks));
        }

        ScopeId = scopeId;
        Name = name;
        ParentScopeId = parentScopeId;
        ReferenceMarks = referenceMarks;
        Grid = grid;
    }
}
