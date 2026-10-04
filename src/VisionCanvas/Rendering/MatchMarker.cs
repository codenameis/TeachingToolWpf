using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>알고리즘 형식과 무관한 읽기 전용 표시. 문서 ROI/선택/이력에 추가하지 않는다.</summary>
public sealed record MatchMarker(ImmutableArray<PointD> Bounds, PointD ReferencePoint, double AngleRadians, string Label,
    Vector4 Color, ImmutableArray<PointD> Features);

/// <summary>호스트가 정한 문구·색·검색 윤곽과 결과. 캔버스는 판정 규칙을 갖지 않는다.</summary>
public sealed record ResultOverlay(string Summary, Vector4 Color, ImmutableArray<PointD> SearchBoundary,
    ImmutableArray<MatchMarker> Matches, bool ShowFeatures = false);
