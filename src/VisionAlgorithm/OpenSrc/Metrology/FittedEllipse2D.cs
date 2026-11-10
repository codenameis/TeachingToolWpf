// File: src/VisionAlgorithm/OpenSrc/Metrology/FittedEllipse2D.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Represents a subpixel fitted ellipse $(C_x, C_y, A, B, \theta)$ with $A \ge B &gt; 0$ and its geometric fit quality
/// implementing <see cref="IVisionToolResult"/> (HALCON <c>fit_ellipse_contour_xld</c> / Cognex <c>CogFindEllipseTool</c> parity).
/// </summary>
public readonly record struct FittedEllipse2D(
    bool IsValid,
    Vector2 Center,
    float SemiMajorAxis,
    float SemiMinorAxis,
    float AngleRad,
    float RmsGeometricErrorPixels,
    int InlierCount) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float MinAxisFloor = 1.0f;

    public float MajorDiameter => SemiMajorAxis * 2.0f;
    public float MinorDiameter => SemiMinorAxis * 2.0f;
    public float AspectRatio => SemiMajorAxis > 0.0f ? SemiMinorAxis / SemiMajorAxis : 0.0f;
    public float Eccentricity => SemiMajorAxis > 0.0f
        ? MathF.Sqrt(Math.Clamp(1.0f - ((SemiMinorAxis * SemiMinorAxis) / (SemiMajorAxis * SemiMajorAxis)), 0.0f, 1.0f))
        : 0.0f;

    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.SubpixelMetrology,
            qualityScore: Math.Clamp(1.0f - (RmsGeometricErrorPixels / MathF.Max(MinAxisFloor, SemiMinorAxis)), 0.0f, 1.0f),
            primaryMetric: SemiMajorAxis)
        : VisionResultHeader.Degenerate(VisionToolCategory.SubpixelMetrology);

    public FixturePose2D WorldPose => IsValid
        ? new FixturePose2D(Center.X, Center.Y, AngleRad, 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => Center;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(SemiMajorAxis, specLsl, specUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: Center.X,
            WorldY: Center.Y,
            WorldAngleDeg: AngleRad * RadiansToDegrees,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: SemiMajorAxis,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: RmsGeometricErrorPixels,
            AuxMetric1: SemiMinorAxis,
            AuxMetric2: AspectRatio,
            AuxMetric3: AngleRad * RadiansToDegrees,
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

        (float sinTheta, float cosTheta) = MathF.SinCos(AngleRad);
        Vector2 majorEnd = new(Center.X + (SemiMajorAxis * cosTheta), Center.Y + (SemiMajorAxis * sinTheta));
        Vector2 minorEnd = new(Center.X - (SemiMinorAxis * sinTheta), Center.Y + (SemiMinorAxis * cosTheta));
        string label = $"FittedEllipse: A={SemiMajorAxis:F2}px, B={SemiMinorAxis:F2}px, θ={AngleRad * RadiansToDegrees:F1}°, RMS={RmsGeometricErrorPixels:F3}px";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: MajorDiameter,
            HeightOrSpan: MinorDiameter,
            SegmentAStart: Center,
            SegmentAEnd: majorEnd,
            SegmentBStart: Center,
            SegmentBEnd: minorEnd,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
