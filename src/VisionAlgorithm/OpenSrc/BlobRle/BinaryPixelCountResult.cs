// File: src/VisionAlgorithm/OpenSrc/BlobRle/BinaryPixelCountResult.cs
using System.Numerics;
using System.Runtime.InteropServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Standardized result of <see cref="BinaryPixelCountInspector"/> implementing <see cref="IVisionToolResult"/>.
/// Provides foreground pixel count, area occupancy percentage, center of mass in world coordinates,
/// grayscale mean/standard deviation, and 100% <c>VisionCanvas</c> replay geometry.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct BinaryPixelCountResult(
    VisionResultHeader Header,
    FixturePose2D RoiWorldPose,
    Vector2 CenterOfMassWorld,
    int ValidPixelCount,
    int TotalPixelCount,
    float AreaRatioPercent,
    float MeanGrayValue,
    float StdDevGrayValue,
    byte MinObservedGray,
    byte MaxObservedGray,
    int RoiWidth,
    int RoiHeight) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    public FixturePose2D WorldPose => RoiWorldPose;
    public Vector2 ReferencePointWorld => CenterOfMassWorld;
    public bool IsFound => Header.IsFound;
    public bool IsPass => Header.IsPass;

    public static BinaryPixelCountResult Empty => new(
        Header: VisionResultHeader.NotFound(VisionToolCategory.BinaryPixelCount),
        RoiWorldPose: FixturePose2D.Identity,
        CenterOfMassWorld: Vector2.Zero,
        ValidPixelCount: 0,
        TotalPixelCount: 0,
        AreaRatioPercent: 0.0f,
        MeanGrayValue: 0.0f,
        StdDevGrayValue: 0.0f,
        MinObservedGray: 0,
        MaxObservedGray: 0,
        RoiWidth: 0,
        RoiHeight: 0);

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(AreaRatioPercent, specLsl, specUsl);
        float deltaX = ValidPixelCount > 0 ? CenterOfMassWorld.X - RoiWorldPose.CenterX : 0.0f;
        float deltaY = ValidPixelCount > 0 ? CenterOfMassWorld.Y - RoiWorldPose.CenterY : 0.0f;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: CenterOfMassWorld.X,
            WorldY: CenterOfMassWorld.Y,
            WorldAngleDeg: RoiWorldPose.AngleRad * RadiansToDegrees,
            Scale: RoiWorldPose.Scale,
            DeltaX: deltaX,
            DeltaY: deltaY,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: AreaRatioPercent,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: MeanGrayValue,
            ContrastOrStdDev: StdDevGrayValue,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: ValidPixelCount,
            AuxMetric2: MinObservedGray,
            AuxMetric3: MaxObservedGray,
            HitCount: ValidPixelCount,
            TotalSampleCount: TotalPixelCount,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!Header.IsFound)
        {
            return VisionReplayPayload.Empty;
        }

        string statusText = Header.IsPass ? "OK" : "NG";
        string label = $"BinaryCount #{Header.MatchedModelIndex}: {AreaRatioPercent:F1}% ({ValidPixelCount}/{TotalPixelCount} px) Mean={MeanGrayValue:F0}GV [{statusText}]";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: RoiWorldPose,
            WidthOrRadius: RoiWidth,
            HeightOrSpan: RoiHeight,
            SegmentAStart: CenterOfMassWorld,
            SegmentAEnd: new Vector2(RoiWorldPose.CenterX, RoiWorldPose.CenterY),
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
