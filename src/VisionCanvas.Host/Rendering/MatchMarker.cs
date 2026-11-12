using System.Collections.Immutable;
using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>알고리즘 형식과 무관한 읽기 전용 표시. 문서 ROI/선택/이력에 추가하지 않는다.</summary>
public sealed record MatchMarker(ImmutableArray<PointD> Bounds, PointD ReferencePoint, double AngleRadians, string Label,
    Vector4 Color, ImmutableArray<PointD> Features)
{
    public static readonly Vector4 KeyFeatureColor = new(0f, 229 / 255f, 1f, 1f);
    public static readonly Vector4 UnmatchedFeatureColor = new(1f, 59 / 255f, 48 / 255f, 1f);

    /// <summary>이 마커를 방출한 원본 대상 ROI 식별자 (1개 ROI에서 복수 마커가 생성될 때 실시간 추적/매핑에 사용).</summary>
    public Guid SourceRoiId { get; init; }
    public ImmutableArray<ImmutableArray<PointD>> InnerContours { get; init; } = [];
    public ImmutableArray<ImmutableArray<PointD>> AdditionalContours { get; init; } = [];
    /// <summary>외곽선(Bounds) 전용 색상. null이면 기본 Color를 사용한다.</summary>
    public Vector4? BoundsColor { get; init; }
    /// <summary>외곽선(Bounds) 두께 (기본 2.0px).</summary>
    public double BoundsThickness { get; init; } = 2.0;
    /// <summary>외곽선(Bounds) 대시 길이 (0이면 실선).</summary>
    public double BoundsDashLength { get; init; }
    /// <summary>외곽선(Bounds) 대시 간격 (0이면 실선).</summary>
    public double BoundsGapLength { get; init; }
    /// <summary>내부/보조 가이드 윤곽선(InnerContours) 전용 색상. null이면 기본 Color를 사용한다.</summary>
    public Vector4? InnerContourColor { get; init; }
    /// <summary>내부/보조 가이드 윤곽선(InnerContours) 두께 (기본 2.0px).</summary>
    public double InnerContourThickness { get; init; } = 2.0;
    /// <summary>내부/보조 가이드 윤곽선(InnerContours) 대시 길이 (0이면 실선).</summary>
    public double InnerContourDashLength { get; init; }
    /// <summary>내부/보조 가이드 윤곽선(InnerContours) 대시 간격 (0이면 실선).</summary>
    public double InnerContourGapLength { get; init; }
    /// <summary>알고리즘별 확장 진단 포인트 그룹 목록 (예: 비대칭 문맥 키, 누락 특징점, 캘리퍼 히트, ZNCC 표본 등).</summary>
    public ImmutableArray<DiagnosticPointGroup> DiagnosticGroups { get; init; } = [];
    /// <summary>비대칭 구분 키(AsymX, AsymY) 또는 핵심 문맥 특징점 좌표 (하늘색 강조 표시).</summary>
    public ImmutableArray<PointD> KeyFeatures { get; init; } = [];
    /// <summary>누락·불일치로 판정된 특징점 좌표 (빨간색 표시).</summary>
    public ImmutableArray<PointD> UnmatchedFeatures { get; init; } = [];
}
