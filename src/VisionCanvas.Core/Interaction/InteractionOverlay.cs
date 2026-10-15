using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

public sealed record InteractionOverlay(Guid? Hover = null, RoiGeometry? Draft = null, BoundsD? Marquee = null, ImmutableArray<PointD> PolygonPoints = default)
{
    public double HandleScale { get; init; } = HandleGeometry.DefaultScale;

    public bool DraftIsGroup
    {
        get; init;
    }

    /// <summary>ROI 생성 중 마우스로 지정하는 임시 사각 영역(DIP). 문서 형상은 아니다.</summary>
    public BoundsD? DragArea
    {
        get; init;
    }

    public MaskRegion? MaskDraft
    {
        get; init;
    }
}
