using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

public sealed record PrimitiveDetectionResult(ImmutableArray<PrimitiveShapeDetection> Shapes,
    ImmutableArray<ImagePoint> ClippedCenters, bool LimitExceeded);
