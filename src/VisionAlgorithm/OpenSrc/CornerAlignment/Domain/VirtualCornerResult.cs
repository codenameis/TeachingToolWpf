// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/VirtualCornerResult.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 두 직선의 연립 교차로 산출된 서브픽셀 가상 코너(Virtual Corner) 검출 및 정합 결과 (<see cref="IVisionToolResult"/> 구현).
/// </summary>
public readonly record struct VirtualCornerResult(
    float CornerX,
    float CornerY,
    float OrientationRad,
    float IncludedAngleDeg,
    HessianLine2D Line1,
    HessianLine2D Line2,
    float InlierRatio,
    bool IsValid) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float DefaultRayLength = 42.0f;

    /// <summary>
    /// 검출 실패 시 반환되는 무효 결과.
    /// </summary>
    public static VirtualCornerResult Invalid => new(
        CornerX: float.NaN,
        CornerY: float.NaN,
        OrientationRad: 0f,
        IncludedAngleDeg: 0f,
        Line1: HessianLine2D.Invalid,
        Line2: HessianLine2D.Invalid,
        InlierRatio: 0f,
        IsValid: false);

    /// <summary>
    /// 검출된 가상 코너의 2D 좌표 벡터.
    /// </summary>
    public Vector2 CornerPoint => new(CornerX, CornerY);

    public VisionResultHeader Header => IsValid
        ? VisionResultHeader.SucceededMeasurement(
            category: VisionToolCategory.CornerAlignment,
            qualityScore: Math.Clamp(InlierRatio, 0.0f, 1.0f),
            primaryMetric: IncludedAngleDeg)
        : VisionResultHeader.Degenerate(VisionToolCategory.CornerAlignment, InlierRatio);

    public FixturePose2D WorldPose => IsValid && float.IsFinite(CornerX) && float.IsFinite(CornerY)
        ? new FixturePose2D(CornerX, CornerY, OrientationRad, 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => CornerPoint;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(IncludedAngleDeg, specLsl, specUsl);
        float avgRms = (Line1.RmsResidual + Line2.RmsResidual) * 0.5f;
        int totalInliers = Line1.InlierCount + Line2.InlierCount;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: CornerX,
            WorldY: CornerY,
            WorldAngleDeg: OrientationRad * RadiansToDegrees,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: IncludedAngleDeg,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: avgRms,
            AuxMetric1: InlierRatio,
            AuxMetric2: Line1.RmsResidual,
            AuxMetric3: Line2.RmsResidual,
            HitCount: totalInliers,
            TotalSampleCount: totalInliers,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsValid)
        {
            return VisionReplayPayload.Empty;
        }

        Vector2 c = CornerPoint;
        Vector2 ray1 = c + (Line1.Tangent * DefaultRayLength);
        Vector2 ray2 = c + (Line2.Tangent * DefaultRayLength);
        string label = $"Corner: ({CornerX:F1},{CornerY:F1}) θ={OrientationRad * RadiansToDegrees:F2}° Included={IncludedAngleDeg:F1}° Inlier={InlierRatio:P0}";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.DualLineCorner,
            WorldPose: WorldPose,
            WidthOrRadius: DefaultRayLength,
            HeightOrSpan: IncludedAngleDeg,
            SegmentAStart: c,
            SegmentAEnd: ray1,
            SegmentBStart: c,
            SegmentBEnd: ray2,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
