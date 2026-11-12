using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>단일 선택 ROI의 활성 마스크 렌더 대상 해석 결과.</summary>
public readonly record struct ActiveMaskTarget(
    Roi Roi,
    RoiGeometry Geometry,
    MaskRegion? Draft,
    int MaskCount,
    int VertexCount);
