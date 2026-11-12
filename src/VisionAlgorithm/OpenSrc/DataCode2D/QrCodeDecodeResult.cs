using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Represents the decoded payload, full 360-degree pose, 4-corner perspective quadrangle, Reed-Solomon ECC statistics,
/// and ISO/IEC 15415 print quality metrics of an industrial 2D QR Code symbol (ISO/IEC 18004,
/// HALCON <c>find_data_code_2d('QR Code')</c> / Cognex <c>CogIDTool</c> parity).
/// </summary>
public readonly record struct QrCodeDecodeResult(
    bool IsDecoded,
    string DecodedText,
    int Version,
    int Dimension,
    QrErrorCorrectionLevel ErrorCorrectionLevel,
    int MaskPattern,
    Vector2 CenterWorld,
    float AngleRad,
    float WidthPixels,
    float HeightPixels,
    Vector2 TopLeftCorner,
    Vector2 TopRightCorner,
    Vector2 BottomRightCorner,
    Vector2 BottomLeftCorner,
    Vector2 TopLeftFinderCenter,
    Vector2 TopRightFinderCenter,
    Vector2 BottomLeftFinderCenter,
    float SymbolContrast,
    float ModulationScore,
    float AxialNonUniformity,
    float FixedPatternDamageScore,
    char OverallIsoGrade,
    int CorrectedByteErrors) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float QualityMetricCount = 3.0f;
    private const float GradeANumeric = 4.0f;
    private const float GradeBNumeric = 3.0f;
    private const float GradeCNumeric = 2.0f;
    private const float GradeDNumeric = 1.0f;

    public float AngleDegrees => AngleRad * RadiansToDegrees;

    public VisionResultHeader Header => IsDecoded
        ? VisionResultHeader.SucceededJudge(
            category: VisionToolCategory.OcrVerification,
            isPass: OverallIsoGrade is 'A' or 'B' or 'C',
            qualityScore: Math.Clamp((SymbolContrast + ModulationScore + FixedPatternDamageScore) / QualityMetricCount, 0.0f, 1.0f),
            primaryMetric: ModulationScore)
        : VisionResultHeader.NotFound(VisionToolCategory.OcrVerification);

    public FixturePose2D WorldPose => IsDecoded
        ? new FixturePose2D(CenterWorld.X, CenterWorld.Y, AngleRad, 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => CenterWorld;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float qualityNumeric = IsoGradeToNumeric(OverallIsoGrade);
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(qualityNumeric, specLsl, specUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: CenterWorld.X,
            WorldY: CenterWorld.Y,
            WorldAngleDeg: AngleDegrees,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: qualityNumeric,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: SymbolContrast,
            RmsFitErrorPixels: AxialNonUniformity,
            AuxMetric1: ModulationScore,
            AuxMetric2: FixedPatternDamageScore,
            AuxMetric3: CorrectedByteErrors,
            HitCount: Dimension * Dimension,
            TotalSampleCount: Dimension * Dimension,
            DecodedText: DecodedText ?? string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsDecoded)
        {
            return VisionReplayPayload.Empty;
        }

        string label = $"QR V{Version} ({Dimension}x{Dimension}, ECC-{ErrorCorrectionLevel}) \"{DecodedText}\" | Pos=({CenterWorld.X:F1}, {CenterWorld.Y:F1}) | θ={AngleDegrees:F2}° | ISO={OverallIsoGrade}";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: WidthPixels,
            HeightOrSpan: HeightPixels,
            SegmentAStart: BottomLeftCorner,
            SegmentAEnd: BottomRightCorner,
            SegmentBStart: BottomLeftCorner,
            SegmentBEnd: TopLeftCorner,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }

    private static float IsoGradeToNumeric(char grade) => grade switch
    {
        'A' => GradeANumeric,
        'B' => GradeBNumeric,
        'C' => GradeCNumeric,
        'D' => GradeDNumeric,
        _ => 0.0f
    };
}
