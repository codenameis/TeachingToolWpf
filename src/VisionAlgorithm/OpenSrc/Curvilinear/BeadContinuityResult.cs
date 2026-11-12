using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;

namespace VisionAlgorithm.OpenSrc.Curvilinear;

/// <summary>
/// Summary report of a curvilinear continuity and width profile inspection implementing <see cref="IVisionToolResult"/>.
/// </summary>
public readonly record struct BeadContinuityResult(
    bool IsContinuousAndWithinSpec,
    int TotalStations,
    int ValidStations,
    int GapStationCount,
    int NeckingStationCount,
    int BulgeStationCount,
    float MinWidthPixels,
    float MaxWidthPixels,
    float MeanWidthPixels,
    IReadOnlyList<BeadCrossSectionSample> Samples) : IVisionToolResult
{
    public VisionResultHeader Header => TotalStations <= 0
        ? VisionResultHeader.NotFound(VisionToolCategory.CurvilinearBead)
        : VisionResultHeader.SucceededJudge(
            category: VisionToolCategory.CurvilinearBead,
            isPass: IsContinuousAndWithinSpec,
            qualityScore: Math.Clamp((float)ValidStations / Math.Max(1, TotalStations), 0.0f, 1.0f),
            primaryMetric: MeanWidthPixels);

    public FixturePose2D WorldPose
    {
        get
        {
            if (Samples is null || Samples.Count == 0)
            {
                return FixturePose2D.Identity;
            }

            Vector2 mid = Samples[Samples.Count / 2].MeasuredCenterWorld;
            return new FixturePose2D(mid.X, mid.Y, 0.0f, 1.0f);
        }
    }

    public Vector2 ReferencePointWorld => new(WorldPose.CenterX, WorldPose.CenterY);

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(MeanWidthPixels, specLsl, specUsl);
        float validRatio = TotalStations > 0 ? (float)ValidStations / TotalStations : 0.0f;

        return new VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: 0.0f,
            WorldX: WorldPose.CenterX,
            WorldY: WorldPose.CenterY,
            WorldAngleDeg: 0.0f,
            Scale: 1.0f,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: MeanWidthPixels,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: 0.0f,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: MinWidthPixels,
            AuxMetric2: MaxWidthPixels,
            AuxMetric3: validRatio,
            HitCount: ValidStations,
            TotalSampleCount: TotalStations,
            DecodedText: string.Empty);
    }

    public VisionReplayPayload ToReplayPayload()
    {
        if (TotalStations <= 0 || Samples is null || Samples.Count == 0)
        {
            return VisionReplayPayload.Empty;
        }

        var inliers = new List<Vector2>(ValidStations);
        var outliers = new List<Vector2>(Math.Max(0, TotalStations - ValidStations));
        for (int i = 0; i < Samples.Count; i++)
        {
            BeadCrossSectionSample s = Samples[i];
            if (s.IsPresent)
            {
                inliers.Add(s.MeasuredCenterWorld);
            }
            else
            {
                outliers.Add(s.NominalCenterWorld);
            }
        }

        string status = IsContinuousAndWithinSpec ? "OK" : "NG";
        string label = $"Bead: MeanW={MeanWidthPixels:F2}px (Min={MinWidthPixels:F1}, Max={MaxWidthPixels:F1}, Gaps={GapStationCount}) [{status}]";

        return new VisionReplayPayload(
            ShapeKind: ReplayShapeKind.Polyline,
            WorldPose: WorldPose,
            WidthOrRadius: MeanWidthPixels,
            HeightOrSpan: TotalStations,
            SegmentAStart: Samples[0].MeasuredCenterWorld,
            SegmentAEnd: Samples[^1].MeasuredCenterWorld,
            SegmentBStart: Vector2.Zero,
            SegmentBEnd: Vector2.Zero,
            SubRegions: ReadOnlyMemory<ReplaySubRegion>.Empty,
            InlierPointsWorld: inliers.ToArray(),
            OutlierPointsWorld: outliers.ToArray(),
            SummaryLabel: label);
    }
}
