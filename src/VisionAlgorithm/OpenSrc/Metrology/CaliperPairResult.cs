using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents the result of a 1D edge-pair width measurement implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct CaliperPairResult(
    bool IsFound,
    CaliperEdgeHit FirstEdge,
    CaliperEdgeHit SecondEdge,
    SubpixelWidthMeasurement Value) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float ContrastNormScale = 1.0f / 128.0f;

    public float SubpixelWidth => Value.WidthPixels;
    public Vector2 MidpointWorld => Value.MidpointWorld;

    public VisionResultHeader Header => IsFound
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.SubpixelMetrology,
            qualityScore: Math.Clamp((FirstEdge.GradientAmplitude + SecondEdge.GradientAmplitude) * 0.5f * ContrastNormScale, 0.0f, 1.0f),
            primaryMetric: Value.WidthPixels)
        : VisionResultHeader.NotFound(VisionToolCategory.SubpixelMetrology);

    public FixturePose2D WorldPose
    {
        get
        {
            if (!IsFound)
            {
                return FixturePose2D.Identity;
            }

            Vector2 dir = SecondEdge.WorldPoint - FirstEdge.WorldPoint;
            float angle = dir.LengthSquared() > 1e-6f ? MathF.Atan2(dir.Y, dir.X) : 0.0f;
            return new FixturePose2D(Value.MidpointWorld.X, Value.MidpointWorld.Y, angle, 1.0f);
        }
    }

    public Vector2 ReferencePointWorld => Value.MidpointWorld;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(Value.WidthPixels, specLsl, specUsl);
        float meanContrast = (FirstEdge.GradientAmplitude + SecondEdge.GradientAmplitude) * 0.5f;
        FixturePose2D pose = WorldPose;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: Value.MidpointWorld.X,
            WorldY: Value.MidpointWorld.Y,
            WorldAngleDeg: pose.AngleRad * RadiansToDegrees,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: Value.WidthPixels,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: meanContrast,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: FirstEdge.GradientAmplitude,
            AuxMetric2: SecondEdge.GradientAmplitude,
            AuxMetric3: FirstEdge.SubpixelPosition,
            HitCount: IsFound ? 2 : 0,
            TotalSampleCount: 2,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsFound)
        {
            return VisionReplayPayload.Empty;
        }

        string label = $"Caliper Width: {Value.WidthPixels:F3} px (Contrast={FirstEdge.GradientAmplitude:F0}/{SecondEdge.GradientAmplitude:F0})";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.EdgePair,
            WorldPose: WorldPose,
            WidthOrRadius: Value.WidthPixels,
            HeightOrSpan: 12.0f,
            SegmentAStart: FirstEdge.WorldPoint,
            SegmentAEnd: SecondEdge.WorldPoint,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: new[] { FirstEdge.WorldPoint, SecondEdge.WorldPoint },
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
