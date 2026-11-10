// File: src/VisionAlgorithm/OpenSrc/Common/VisionReplayPayload.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Parametric geometry packet enabling 100% pixel-identical <c>VisionCanvas</c> overlay reconstruction
/// both during live execution and when reloading historical inspection records from a database.
/// </summary>
public readonly record struct VisionReplayPayload(
    ReplayShapeKind ShapeKind,
    FixturePose2D WorldPose,
    float WidthOrRadius,
    float HeightOrSpan,
    Vector2 SegmentAStart,
    Vector2 SegmentAEnd,
    Vector2 SegmentBStart,
    Vector2 SegmentBEnd,
    ReadOnlyMemory<ReplaySubRegion> SubRegions,
    ReadOnlyMemory<Vector2> InlierPointsWorld,
    ReadOnlyMemory<Vector2> OutlierPointsWorld,
    string SummaryLabel)
{
    public static VisionReplayPayload Empty => new(
        ShapeKind: ReplayShapeKind.None,
        WorldPose: FixturePose2D.Identity,
        WidthOrRadius: 0.0f,
        HeightOrSpan: 0.0f,
        SegmentAStart: Vector2.Zero,
        SegmentAEnd: Vector2.Zero,
        SegmentBStart: Vector2.Zero,
        SegmentBEnd: Vector2.Zero,
        SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
        InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
        OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
        SummaryLabel: string.Empty);
}
