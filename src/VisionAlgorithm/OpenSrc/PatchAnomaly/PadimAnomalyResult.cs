// File: src/VisionAlgorithm/OpenSrc/PatchAnomaly/PadimAnomalyResult.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.PatchAnomaly;

/// <summary>
/// Result of evaluating a query ROI with <see cref="PadimHeatmapDetector"/> implementing <see cref="IVisionToolResult"/>.
/// Note: <see cref="IVisionToolResult.IsPass"/> is normalized to <c>!IsAnomalous</c> (<see langword="true"/> = defect-free).
/// </summary>
public readonly record struct PadimAnomalyResult(
    bool IsAnomalous,
    float ImageAnomalyScore,
    float AnomalyThreshold,
    Vector2 PeakAnomalyLocalPosition,
    int AnomalousPixelCount) : IVisionToolResult
{
    private const float TargetMarkerSize = 12.0f;

    public VisionResultHeader Header => VisionResultHeader.SucceededJudge(
        category: VisionToolCategory.AnomalyVariation,
        isPass: !IsAnomalous,
        qualityScore: Math.Clamp(AnomalyThreshold / MathF.Max(1e-3f, AnomalyThreshold + ImageAnomalyScore), 0.0f, 1.0f),
        primaryMetric: ImageAnomalyScore);

    public FixturePose2D WorldPose => new(PeakAnomalyLocalPosition.X, PeakAnomalyLocalPosition.Y, 0.0f, 1.0f);
    public Vector2 ReferencePointWorld => PeakAnomalyLocalPosition;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        double effectiveUsl = double.IsFinite(specUsl) ? specUsl : AnomalyThreshold;
        double effectiveLsl = double.IsFinite(specLsl) ? specLsl : 0.0;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(ImageAnomalyScore, effectiveLsl, effectiveUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: PeakAnomalyLocalPosition.X,
            WorldY: PeakAnomalyLocalPosition.Y,
            WorldAngleDeg: 0.0f,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: ImageAnomalyScore,
            SpecLsl: effectiveLsl,
            SpecUsl: effectiveUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: AnomalyThreshold,
            AuxMetric2: AnomalousPixelCount,
            AuxMetric3: ImageAnomalyScore / MathF.Max(1e-3f, AnomalyThreshold),
            HitCount: AnomalousPixelCount,
            TotalSampleCount: AnomalousPixelCount,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        string status = !IsAnomalous ? "OK" : "NG";
        string label = $"PaDiM Anomaly: Score={ImageAnomalyScore:F2} (Thresh={AnomalyThreshold:F2}, Px={AnomalousPixelCount}) [{status}]";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.PointTarget,
            WorldPose: WorldPose,
            WidthOrRadius: TargetMarkerSize,
            HeightOrSpan: TargetMarkerSize,
            SegmentAStart: PeakAnomalyLocalPosition,
            SegmentAEnd: PeakAnomalyLocalPosition,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: IsAnomalous ? new[] { PeakAnomalyLocalPosition } : ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
