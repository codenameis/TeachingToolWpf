// File: src/VisionAlgorithm/OpenSrc/BlobRle/BlobAnalysisResult.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Standardized summary result of <see cref="RleBlobAnalyzer"/> implementing <see cref="IVisionToolResult"/>.
/// Encapsulates the primary (largest or rank-selected) blob feature, total blob count, cumulative area,
/// world coordinate pose, and <see cref="ReplaySubRegion"/> bounding boxes for 100% <c>VisionCanvas</c> replay.
/// </summary>
public readonly record struct BlobAnalysisResult(
    VisionResultHeader Header,
    FixturePose2D RoiWorldPose,
    FixturePose2D PrimaryBlobWorldPose,
    int BlobCount,
    long TotalBlobAreaPixels,
    BlobComponentFeature PrimaryBlob,
    ReadOnlyMemory<ReplaySubRegion> BlobSubRegions) : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    public FixturePose2D WorldPose => Header.IsFound ? PrimaryBlobWorldPose : RoiWorldPose;
    public Vector2 ReferencePointWorld => new(WorldPose.CenterX, WorldPose.CenterY);
    public bool IsFound => Header.IsFound;
    public bool IsPass => Header.IsPass;

    public static BlobAnalysisResult Empty(in FixturePose2D roiWorldPose) => new(
        Header: VisionResultHeader.NotFound(VisionToolCategory.BlobAnalysis),
        RoiWorldPose: roiWorldPose,
        PrimaryBlobWorldPose: roiWorldPose,
        BlobCount: 0,
        TotalBlobAreaPixels: 0,
        PrimaryBlob: default,
        BlobSubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty);

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        double area = PrimaryBlob.AreaPixels;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(area, specLsl, specUsl);
        float deltaX = Header.IsFound ? PrimaryBlobWorldPose.CenterX - RoiWorldPose.CenterX : 0.0f;
        float deltaY = Header.IsFound ? PrimaryBlobWorldPose.CenterY - RoiWorldPose.CenterY : 0.0f;
        float deltaAngleDeg = Header.IsFound ? (PrimaryBlobWorldPose.AngleRad - RoiWorldPose.AngleRad) * RadiansToDegrees : 0.0f;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: (short)Math.Clamp(BlobCount, 0, short.MaxValue),
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: WorldPose.CenterX,
            WorldY: WorldPose.CenterY,
            WorldAngleDeg: WorldPose.AngleRad * RadiansToDegrees,
            Scale: 1.0f,
            DeltaX: deltaX,
            DeltaY: deltaY,
            DeltaAngleDeg: deltaAngleDeg,
            PrimaryMetric: area,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: PrimaryBlob.Eccentricity,
            AuxMetric2: PrimaryBlob.BoundingWidth,
            AuxMetric3: PrimaryBlob.BoundingHeight,
            HitCount: BlobCount,
            TotalSampleCount: (int)Math.Min(int.MaxValue, TotalBlobAreaPixels),
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (!Header.IsFound)
        {
            return VisionReplayPayload.Empty;
        }

        string label = $"Blob #{Header.MatchedModelIndex}: Area={PrimaryBlob.AreaPixels}px² (Count={BlobCount}, Ecc={PrimaryBlob.Eccentricity:F2})";
        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.OrientedBox,
            WorldPose: PrimaryBlobWorldPose,
            WidthOrRadius: PrimaryBlob.BoundingWidth,
            HeightOrSpan: PrimaryBlob.BoundingHeight,
            SegmentAStart: Vector2.Zero,
            SegmentAEnd: Vector2.Zero,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: BlobSubRegions,
            InlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<Vector2>.Empty,
            SummaryLabel: label);
    }
}
