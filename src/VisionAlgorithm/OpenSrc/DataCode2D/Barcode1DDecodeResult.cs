using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Result of multi-scanline 1D barcode (Code 128 / EAN-13) decoding, oriented 2D world pose (center, size, rotation angle),
/// and ISO/IEC 15416 print quality grading.
/// </summary>
public readonly record struct Barcode1DDecodeResult(
    bool IsDecoded,
    string Symbology,
    string Payload,
    int ChecksumValue,
    bool IsChecksumValid,
    int DecodedScanlineCount,
    double SymbolContrast,
    double Modulation,
    double Decodability,
    double OverallGradeScore,
    string IsoGradeLetter,
    double StartX,
    double EndX,
    double ScanlineY,
    double TopY = 0.0,
    double BottomY = 0.0,
    double AngleRad = 0.0,
    double OrientedCenterX = double.NaN,
    double OrientedCenterY = double.NaN,
    double OrientedWidth = double.NaN,
    double OrientedHeight = double.NaN) : IVisionToolResult
{
    private const double RadiansToDegreesFactor = 180.0 / Math.PI;
    private const double DefaultHalfBarSpan = 24.0;
    private const double HalfFactor = 0.5;
    private const double MinimumBarHeight = 1.0;
    private const float MaxIsoGradeNumeric = 4.0f;
    private const double PassMinimumIsoGrade = 1.5;

    public double EffectiveTopY => TopY > 0.0 || BottomY > 0.0 ? TopY : Math.Max(0.0, ScanlineY - DefaultHalfBarSpan);

    public double EffectiveBottomY => BottomY > TopY ? BottomY : ScanlineY + DefaultHalfBarSpan;

    public double CenterX => !double.IsNaN(OrientedCenterX) ? OrientedCenterX : (StartX + EndX) * HalfFactor;

    public double CenterY => !double.IsNaN(OrientedCenterY) ? OrientedCenterY : (EffectiveTopY + EffectiveBottomY) * HalfFactor;

    public double WidthPixels => !double.IsNaN(OrientedWidth) ? Math.Max(0.0, OrientedWidth) : Math.Max(0.0, EndX - StartX);

    public double HeightPixels => !double.IsNaN(OrientedHeight) ? Math.Max(MinimumBarHeight, OrientedHeight) : Math.Max(MinimumBarHeight, EffectiveBottomY - EffectiveTopY);

    public double AngleDeg => AngleRad * RadiansToDegreesFactor;

    public VisionResultHeader Header => IsDecoded
        ? VisionResultHeader.SucceededJudge(
            category: VisionToolCategory.OcrVerification,
            isPass: IsChecksumValid && OverallGradeScore >= PassMinimumIsoGrade,
            qualityScore: Math.Clamp((float)OverallGradeScore / MaxIsoGradeNumeric, 0.0f, 1.0f),
            primaryMetric: (float)OverallGradeScore)
        : VisionResultHeader.NotFound(VisionToolCategory.OcrVerification);

    public FixturePose2D WorldPose => IsDecoded
        ? new FixturePose2D((float)CenterX, (float)CenterY, (float)AngleRad, 1.0f)
        : FixturePose2D.Identity;

    public Vector2 ReferencePointWorld => IsDecoded
        ? new Vector2((float)CenterX, (float)CenterY)
        : Vector2.Zero;

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float primaryGrade = (float)OverallGradeScore;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(primaryGrade, specLsl, specUsl);

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: (float)CenterX,
            WorldY: (float)CenterY,
            WorldAngleDeg: (float)AngleDeg,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: primaryGrade,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: (float)SymbolContrast,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: (float)Modulation,
            AuxMetric2: (float)Decodability,
            AuxMetric3: ChecksumValue,
            HitCount: DecodedScanlineCount,
            TotalSampleCount: DecodedScanlineCount,
            DecodedText: Payload ?? string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!IsDecoded)
        {
            return VisionReplayPayload.Empty;
        }

        float cx = (float)CenterX;
        float cy = (float)CenterY;
        float w = (float)WidthPixels;
        float h = (float)HeightPixels;
        float angleRad = (float)AngleRad;
        (float sinA, float cosA) = MathF.SinCos(angleRad);
        Vector2 halfU = new(w * (float)HalfFactor * cosA, w * (float)HalfFactor * sinA);
        Vector2 halfV = new(-h * (float)HalfFactor * sinA, h * (float)HalfFactor * cosA);

        Vector2 center = new(cx, cy);
        Vector2 bottomLeft = center - halfU + halfV;
        Vector2 bottomRight = center + halfU + halfV;
        Vector2 topLeft = center - halfU - halfV;

        string label = $"{Symbology} \"{Payload}\" | Pos=({CenterX:F1}, {CenterY:F1}) | θ={AngleDeg:F2}° | ISO={IsoGradeLetter}";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: w,
            HeightOrSpan: h,
            SegmentAStart: bottomLeft,
            SegmentAEnd: bottomRight,
            SegmentBStart: bottomLeft,
            SegmentBEnd: topLeft,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
