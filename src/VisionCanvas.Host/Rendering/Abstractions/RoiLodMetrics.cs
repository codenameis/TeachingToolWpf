using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>GPU/CPU ROI 파이프라인이 계산한 가시 영역 및 LOD 메트릭.</summary>
public readonly record struct RoiLodMetrics(BoundsD VisibleBounds, int Level, double AggregateSize);
