using VisionCanvas.Core.Geometry;

namespace VisionCanvas.Core.Editing;

/// <summary>동일한 보정 범위(전체 null, 모듈 ID, 컴포넌트 ID)를 공유하는 불변 공간 인덱스 파티션.</summary>
public sealed record RoiScopePartition(
    Guid? ScopeId,
    RoiSpatialIndex Spatial,
    RoiSpatialIndex SearchSpatial,
    bool HasSearchRanges);
