using System.Collections.Immutable;
using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>호스트가 정한 문구·색·검색 윤곽과 결과. 캔버스는 판정 규칙을 갖지 않는다.</summary>
public sealed record ResultOverlay(string Summary, Vector4 Color, ImmutableArray<PointD> SearchBoundary,
    ImmutableArray<MatchMarker> Matches, bool ShowFeatures = false)
{
    public bool BatchLabels
    {
        get; init;
    }
    public Guid PresentationId { get; init; } = Guid.NewGuid();
}
