// File: src/VisionAlgorithm/OpenSrc/GoldenVariation/VariationInspectionResult.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Summary result of inspecting a target ROI against a trained <see cref="VariationModel"/> implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct VariationInspectionResult(
    bool IsDefectFree,
    int DefectPixelCount,
    float MaxExcessDifference,
    float MaxNormalizedZScore) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const int DefaultRoiWidth = 48;
    private const int DefaultRoiHeight = 36;

    public FixturePose2D InspectedWorldPose { get; init; } = FixturePose2D.Identity;
    public int RoiWidth { get; init; } = DefaultRoiWidth;
    public int RoiHeight { get; init; } = DefaultRoiHeight;
    public short GoldenModelIndex { get; init; }

    public VisionResultHeader Header => VisionResultHeader.SucceededJudge(
        category: VisionToolCategory.AnomalyVariation,
        isPass: IsDefectFree,
        qualityScore: Math.Clamp(1.0f / (1.0f + (MaxNormalizedZScore * 0.2f)), 0.0f, 1.0f),
        primaryMetric: DefectPixelCount,
        matchedModelIndex: GoldenModelIndex,
        matchedModelId: GoldenModelIndex + 1);

    public FixturePose2D WorldPose => InspectedWorldPose.IsValid ? InspectedWorldPose : FixturePose2D.Identity;
    public Vector2 ReferencePointWorld => new(WorldPose.CenterX, WorldPose.CenterY);

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(DefectPixelCount, specLsl, specUsl);
        int totalPixels = Math.Max(1, RoiWidth * RoiHeight);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: WorldPose.CenterX,
            WorldY: WorldPose.CenterY,
            WorldAngleDeg: WorldPose.AngleRad * RadiansToDegrees,
            Scale: WorldPose.Scale,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: DefectPixelCount,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: MaxExcessDifference,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: MaxExcessDifference,
            AuxMetric2: MaxNormalizedZScore,
            AuxMetric3: (float)DefectPixelCount / totalPixels,
            HitCount: DefectPixelCount,
            TotalSampleCount: totalPixels,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        string status = IsDefectFree ? "OK" : "NG";
        string label = $"GoldenVar #{GoldenModelIndex}: DefectPx={DefectPixelCount} MaxDiff={MaxExcessDifference:F1}GV Z={MaxNormalizedZScore:F2} [{status}]";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: RoiWidth,
            HeightOrSpan: RoiHeight,
            SegmentAStart: Vector2.Zero,
            SegmentAEnd: Vector2.Zero,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
