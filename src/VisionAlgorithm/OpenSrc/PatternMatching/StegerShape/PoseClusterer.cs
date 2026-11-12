using VisionKernels;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Clusters coarse anisotropic candidates prior to pyramid refinement and deduplicates refined poses after verification.
/// </summary>
internal static class PoseClusterer
{
    private const double ClusterAngleStepMultiplier = 2.01;
    private const double ClusterAngleSlack = 1e-9;
    private const double MinClusterRadiusPixels = 2.0;
    private const double WidthRadiusDivisor = 3.2;
    private const double HeightRadiusDivisor = 4.0;
    private const double MaxScaleDelta = 0.15;
    private const double DeduplicationDistanceSquared = 4.0;
    private const double DeduplicationAngleDeltaRadians = 0.15;

    public static PoseCandidate[] ClusterCoarsePoses(ShapeModel model, PoseCandidate[] ascending)
    {
        var angleLimit = (model.Options.AngleStepDegrees * Math.PI / 180 * ClusterAngleStepMultiplier) + ClusterAngleSlack;
        var kept = new List<PoseCandidate>();
        for (var index = ascending.Length - 1; index >= 0; index--)
        {
            var candidate = ascending[index];
            var theta = candidate.Pose.Angle - model.TrainingReferenceAngle;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var rx = Math.Max(MinClusterRadiusPixels, model.BoundsWidth * candidate.Pose.Scale / WidthRadiusDivisor);
            var ry = Math.Max(MinClusterRadiusPixels, model.BoundsHeight * candidate.Pose.Scale / HeightRadiusDivisor);
            var duplicate = false;
            foreach (var other in kept)
            {
                var dx = other.Pose.X - candidate.Pose.X;
                var dy = other.Pose.Y - candidate.Pose.Y;
                var localX = Math.Abs((dx * cos) + (dy * sin));
                var localY = Math.Abs((-dx * sin) + (dy * cos));
                if (localX <= rx &&
                    localY <= ry &&
                    AngleDistance(other.Pose.Angle, candidate.Pose.Angle) <= angleLimit &&
                    Math.Abs(other.Pose.Scale - candidate.Pose.Scale) <= MaxScaleDelta)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                kept.Add(candidate);
            }
        }

        kept.Reverse();
        return [.. kept];
    }

    public static List<PoseCandidate> SortAndDeduplicateRefined(
        ShapeModel model,
        PoseCandidate[] ordered,
        ShapeSearchOptions options,
        CancellationToken token)
    {
        var refined = new List<PoseCandidate>(ordered.Length);
        foreach (var candidate in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Verification is { } verification &&
                (verification.Score < options.MinimumScore || verification.Support < options.MinimumSupport))
            {
                continue;
            }

            if (options.Verify || candidate.Score >= options.CandidateThreshold)
            {
                refined.Add(candidate);
            }
        }

        refined.Sort((a, b) =>
            (b.Verification?.Score ?? b.Score) != (a.Verification?.Score ?? a.Score)
                ? (b.Verification?.Score ?? b.Score).CompareTo(a.Verification?.Score ?? a.Score)
                : a.Pose.Y != b.Pose.Y
                    ? a.Pose.Y.CompareTo(b.Pose.Y)
                    : a.Pose.X != b.Pose.X
                        ? a.Pose.X.CompareTo(b.Pose.X)
                        : a.Template.CompareTo(b.Template));

        if (model.FineDetail is not null || (!model.FeaturePartitions.IsDefault && model.Options.UseFineDetailValidation))
        {
            var deduplicated = new List<PoseCandidate>(refined.Count);
            foreach (var candidate in refined)
            {
                var duplicate = false;
                for (var i = 0; i < deduplicated.Count; i++)
                {
                    var dx = candidate.Pose.X - deduplicated[i].Pose.X;
                    var dy = candidate.Pose.Y - deduplicated[i].Pose.Y;
                    if ((dx * dx) + (dy * dy) <= DeduplicationDistanceSquared &&
                        (model.FineDetail is not null || AngleDistance(candidate.Pose.Angle, deduplicated[i].Pose.Angle) <= DeduplicationAngleDeltaRadians))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    deduplicated.Add(candidate);
                }
            }

            return deduplicated;
        }

        return refined.DistinctBy(static candidate => candidate.Pose).ToList();
    }

    public static double AngleDistance(double a, double b)
        => AngleMath.DistanceRadians(a, b);

    public static int GetAngleBucket(ShapeModel model, int template)
    {
        var diff = (model.Templates[template].Angle - model.TrainingReferenceAngle) % (2 * Math.PI);
        if (diff < 0)
        {
            diff += 2 * Math.PI;
        }

        return ((int)Math.Floor((diff + (Math.PI / 8)) / (Math.PI / 4))) & 7;
    }
}
