// File: src/VisionAlgorithm/OpenSrc/DataCode2D/DataMatrixDecodeResult.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Represents the decoded payload, geometric bounding quadrangle, Reed-Solomon ECC statistics, and
/// ISO/IEC 15415 print quality metrics of an industrial 2D DataMatrix (ECC 200) symbol
/// (HALCON <c>find_data_code_2d</c> / Cognex <c>CogIDTool</c> parity).
/// </summary>
public readonly record struct DataMatrixDecodeResult(
    bool IsDecoded,
    string DecodedText,
    int SymbolRows,
    int SymbolCols,
    Vector2 CenterWorld,
    float AngleRad,
    float WidthPixels,
    float HeightPixels,
    float SymbolContrast,
    float ModulationScore,
    float AxialNonUniformity,
    float FixedPatternDamageScore,
    char OverallIsoGrade,
    int CorrectedByteErrors) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const float HalfFactor = 0.5f;

    public VisionResultHeader Header => IsDecoded
        ? VisionResultHeader.SucceededJudge(
            category: VisionToolCategory.OcrVerification,
            isPass: OverallIsoGrade is 'A' or 'B' or 'C',
            qualityScore: Math.Clamp((SymbolContrast + ModulationScore + FixedPatternDamageScore) / 3.0f, 0.0f, 1.0f),
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
            WorldAngleDeg: AngleRad * RadiansToDegrees,
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
            HitCount: SymbolRows * SymbolCols,
            TotalSampleCount: SymbolRows * SymbolCols,
            DecodedText: DecodedText ?? string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsDecoded)
        {
            return VisionReplayPayload.Empty;
        }

        (float sinA, float cosA) = MathF.SinCos(AngleRad);
        Vector2 halfU = new(WidthPixels * HalfFactor * cosA, WidthPixels * HalfFactor * sinA);
        Vector2 halfV = new(-HeightPixels * HalfFactor * sinA, HeightPixels * HalfFactor * cosA);

        Vector2 bottomLeft = CenterWorld - halfU + halfV;
        Vector2 bottomRight = CenterWorld + halfU + halfV;
        Vector2 topLeft = CenterWorld - halfU - halfV;

        string label = $"DataMatrix [{SymbolRows}x{SymbolCols}] \"{DecodedText}\" | θ={AngleRad * RadiansToDegrees:F2}° | ISO={OverallIsoGrade} (Contrast={SymbolContrast:P0}, Mod={ModulationScore:P0}, ECC={CorrectedByteErrors})";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: WidthPixels,
            HeightOrSpan: HeightPixels,
            SegmentAStart: bottomLeft,
            SegmentAEnd: bottomRight,
            SegmentBStart: bottomLeft,
            SegmentBEnd: topLeft,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }

    private static float IsoGradeToNumeric(char grade) => grade switch
    {
        'A' => 4.0f,
        'B' => 3.0f,
        'C' => 2.0f,
        'D' => 1.0f,
        _ => 0.0f
    };
}
