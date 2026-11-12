using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a subpixel fitted circle $(C_x, C_y, R)$ and its radial fit quality implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct FittedCircle2D(
    bool IsValid,
    Vector2 Center,
    float Radius,
    float Diameter,
    float RmsRadialErrorPixels,
    int InlierCount) : IVisionToolResult
{
    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.SubpixelMetrology,
            qualityScore: Math.Clamp(1.0f - (RmsRadialErrorPixels / MathF.Max(1.0f, Radius)), 0.0f, 1.0f),
            primaryMetric: Radius)
        : VisionResultHeader.Degenerate(VisionToolCategory.SubpixelMetrology);

    public FixturePose2D WorldPose => IsValid
        ? new FixturePose2D(Center.X, Center.Y, 0.0f, 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => Center;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(Radius, specLsl, specUsl);
        float circularity = Radius > 1e-4f ? Math.Clamp(1.0f - (RmsRadialErrorPixels / Radius), 0.0f, 1.0f) : 0.0f;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: Center.X,
            WorldY: Center.Y,
            WorldAngleDeg: 0.0f,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: Radius,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: RmsRadialErrorPixels,
            AuxMetric1: Diameter,
            AuxMetric2: circularity,
            AuxMetric3: RmsRadialErrorPixels,
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

        string label = $"FittedCircle: R={Radius:F2}px (D={Diameter:F2}px, Inliers={InlierCount}, RMS={RmsRadialErrorPixels:F3}px)";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.Circle,
            WorldPose: WorldPose,
            WidthOrRadius: Radius,
            HeightOrSpan: Diameter,
            SegmentAStart: Center,
            SegmentAEnd: new Vector2(Center.X + Radius, Center.Y),
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
