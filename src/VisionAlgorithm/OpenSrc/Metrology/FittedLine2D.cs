// File: src/VisionAlgorithm/OpenSrc/Metrology/FittedLine2D.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a 2D line in normal Hesse form $n_x x + n_y y + d = 0$ with $\|n\|_2 = 1$ implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct FittedLine2D(
    bool IsValid,
    float NormalX,
    float NormalY,
    float DistanceFromOrigin,
    Vector2 Centroid,
    Vector2 Direction,
    int InlierCount,
    float RmsResidualPixels) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float DefaultHalfLineSpan = 30.0f;

    public Vector2 Normal => new(NormalX, NormalY);

    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.SubpixelMetrology,
            qualityScore: Math.Clamp(1.0f - (RmsResidualPixels * 0.5f), 0.0f, 1.0f),
            primaryMetric: RmsResidualPixels)
        : VisionResultHeader.Degenerate(VisionToolCategory.SubpixelMetrology);

    public FixturePose2D WorldPose => IsValid
        ? new FixturePose2D(Centroid.X, Centroid.Y, MathF.Atan2(Direction.Y, Direction.X), 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => Centroid;

    /// <summary>Evaluates the signed orthogonal distance from <paramref name="point"/> to the line.</summary>
    public float SignedDistance(Vector2 point)
        => (NormalX * point.X) + (NormalY * point.Y) + DistanceFromOrigin;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float angleDeg = MathF.Atan2(Direction.Y, Direction.X) * RadiansToDegrees;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(angleDeg, specLsl, specUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: Centroid.X,
            WorldY: Centroid.Y,
            WorldAngleDeg: angleDeg,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: angleDeg,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: RmsResidualPixels,
            AuxMetric1: DistanceFromOrigin,
            AuxMetric2: NormalX,
            AuxMetric3: NormalY,
            HitCount: InlierCount,
            TotalSampleCount: InlierCount,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsValid)
        {
            return VisionReplayPayload.Empty;
        }

        Vector2 p1 = Centroid - (Direction * DefaultHalfLineSpan);
        Vector2 p2 = Centroid + (Direction * DefaultHalfLineSpan);
        string label = $"FittedLine: Angle={MathF.Atan2(Direction.Y, Direction.X) * RadiansToDegrees:F2}° Inliers={InlierCount} RMS={RmsResidualPixels:F3}px";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.LineSegment,
            WorldPose: WorldPose,
            WidthOrRadius: DefaultHalfLineSpan * 2.0f,
            HeightOrSpan: RmsResidualPixels,
            SegmentAStart: p1,
            SegmentAEnd: p2,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
