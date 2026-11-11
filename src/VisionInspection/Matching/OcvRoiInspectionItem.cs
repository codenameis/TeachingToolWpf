using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// ROI-level taught-font OCV verification output separating borderline Review warnings from product NG.
/// </summary>
public sealed record OcvRoiInspectionItem(
    Guid RoiId,
    string RoiName,
    string ExpectedText,
    string FormatMask,
    string DecodedText,
    OcvVerdictKind Verdict,
    string VerdictBadge,
    string VerdictAccent,
    float MeanScore,
    float MinScore,
    string CauseSummary,
    string RecommendedAction,
    ImmutableArray<OcvCharacterInspectionItem> Characters,
    PointD ReferencePoint,
    double AngleRadians,
    ImmutableArray<PointD> Bounds,
    ImmutableArray<ImmutableArray<PointD>> CharContours,
    ImmutableArray<PointD> OkCharCenters,
    ImmutableArray<PointD> ReviewCharCenters,
    ImmutableArray<PointD> NgCharCenters,
    double ElapsedMicroseconds)
{
    public bool IsOk => Verdict == OcvVerdictKind.Ok;

    public bool IsReview => Verdict == OcvVerdictKind.Review;

    public bool IsNg => Verdict == OcvVerdictKind.Ng;
}
