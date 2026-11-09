using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>캔버스 오버레이 및 진단 표시를 위한 기준·상위보정·검출 보정마크 좌표 쌍.</summary>
public readonly record struct AlignedFiducialMark(
    Guid? ScopeId,
    PointD Reference,
    PointD ParentAligned,
    PointD Detected,
    bool IsEnabled);
