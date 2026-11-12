namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Recovers true object centers suppressed along occlusion boundary ridges in coarse anisotropic angle buckets
/// and realigns major-axis / subpixel centers at pyramid level 0 under partial occlusion.
/// </summary>
internal static class OccludedCandidateRecovery
{
    private const double OctantCosThreshold = 0.38268; // [Math Why]: cos(67.5°)quantizes unit axis vector to 8-connected (-1, 0, +1) grid step.
    private const double BucketProbeReprobeThreshold = 0.72;
    private const double BucketImprovementMargin = 0.01;
    private const double MinimumRecoveredVerificationScore = 0.45;
    private const double MinorRidgeNeighborMargin = 0.04;
    private const double MajorRidgeNeighborMargin = 0.01;
    private const double MinVisibleFraction = 0.50;
    private const double MaxVisibleFraction = 0.85;
    private const double MaxRecoveredScoreCap = 0.98;
    private const double OcclusionPriorWeight = 0.35;

    private const double MajorSlideMinScore = 0.45;
    private const double MajorSlideMaxScore = 0.82;
    private const double MajorSlideSpanRatio = 0.045;
    private const int MinMajorSlideSpan = 2;
    private const int MaxMajorSlideSpan = 12;
    private const double SubpixelStep = 0.25;
    private const double SubpixelOcclusionTolerance = 0.002;
    private const double SubpixelRigidImprovementMargin = 1e-4;

    internal readonly record struct RecoveredCellResult(
        bool Succeeded,
        (double Score, int Group) Cell,
        double EffectiveScore);

    public static RecoveredCellResult TryRecoverCoarseCell(
        PreparedSearchImage image,
        OrientationMaps map,
        ShapeModel model,
        List<List<int>> groups,
        int top,
        int gridWidth,
        int gridHeight,
        ReadOnlySpan<(double Score, int Group)> cellBest,
        int x,
        int y,
        (double Score, int Group) cell,
        int bucketIndex,
        bool stride2,
        double maxOcclusionRatio)
    {
        var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
        var template = model.Templates[groups[cell.Group][0]];
        var majorAngle = (template.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
        var ux = Math.Cos(majorAngle);
        var uy = Math.Sin(majorAngle);
        var vx = -uy;
        var vy = ux;
        var dux = Math.Abs(ux) >= OctantCosThreshold ? Math.Sign(ux) : 0;
        var duy = Math.Abs(uy) >= OctantCosThreshold ? Math.Sign(uy) : 0;
        var dvx = Math.Abs(vx) >= OctantCosThreshold ? Math.Sign(vx) : 0;
        var dvy = Math.Abs(vy) >= OctantCosThreshold ? Math.Sign(vy) : 0;

        var m1x = x - dvx;
        var m1y = y - dvy;
        var p1x = x + dvx;
        var p1y = y + dvy;
        var m1Score = m1x >= 0 && m1x < gridWidth && m1y >= 0 && m1y < gridHeight
            ? cellBest[(m1y * gridWidth) + m1x].Score
            : double.NegativeInfinity;
        var p1Score = p1x >= 0 && p1x < gridWidth && p1y >= 0 && p1y < gridHeight
            ? cellBest[(p1y * gridWidth) + p1x].Score
            : double.NegativeInfinity;
        var hasHigherMinorNeighbor = m1Score > cell.Score || p1Score > cell.Score;
        var isMinorRidgePeak = cell.Score > m1Score && cell.Score > p1Score;

        var maj1x = x - dux;
        var maj1y = y - duy;
        var maj2x = x + dux;
        var maj2y = y + duy;
        var maj1Score = maj1x >= 0 && maj1x < gridWidth && maj1y >= 0 && maj1y < gridHeight
            ? cellBest[(maj1y * gridWidth) + maj1x].Score
            : double.NegativeInfinity;
        var maj2Score = maj2x >= 0 && maj2x < gridWidth && maj2y >= 0 && maj2y < gridHeight
            ? cellBest[(maj2y * gridWidth) + maj2x].Score
            : double.NegativeInfinity;
        var hasHigherMajorNeighbor = maj1Score > cell.Score || maj2Score > cell.Score;
        var isMajorValley = cell.Score < maj1Score && cell.Score < maj2Score;

        if (!((hasHigherMinorNeighbor && !isMajorValley) || (isMinorRidgePeak && hasHigherMajorNeighbor && !isMajorValley)))
        {
            return default;
        }

        var recoveredCell = stride2
            ? CoarseLevelScanner.RestoreOddAngles(map, model, groups, top, x, y, cell, bucketIndex)
            : cell;

        var bestGroup = recoveredCell.Group;
        var bestTemplate = model.Templates[groups[bestGroup][0]];
        var herePose = new MatchPose((x * map.Step) + 0.5, (y * map.Step) + 0.5, bestTemplate.Angle, bestTemplate.Scale);
        var (hereVer, bestProbePose) = PyramidCandidateTracker.ProbeTemplateScore(
            image.Gradients[top],
            model,
            herePose,
            top,
            fullSubCell: true);

        if (hereVer.Score < BucketProbeReprobeThreshold)
        {
            for (var g = 0; g < groups.Count; g++)
            {
                if (g == bestGroup || PoseClusterer.GetAngleBucket(model, groups[g][0]) != bucketIndex)
                {
                    continue;
                }

                var gTemplate = model.Templates[groups[g][0]];
                var (gVer, gProbePose) = PyramidCandidateTracker.ProbeTemplateScore(
                    image.Gradients[top],
                    model,
                    herePose with { Angle = gTemplate.Angle, Scale = gTemplate.Scale },
                    top,
                    fullSubCell: true);
                if (gVer.Score > hereVer.Score + BucketImprovementMargin)
                {
                    hereVer = gVer;
                    bestGroup = g;
                    herePose = herePose with { Angle = gTemplate.Angle, Scale = gTemplate.Scale };
                    bestProbePose = gProbePose;
                }
            }

            recoveredCell = (recoveredCell.Score, bestGroup);
        }

        if (hereVer.Score < MinimumRecoveredVerificationScore)
        {
            return default;
        }

        if (!ContinuousVerifier.HasOrthogonalNormalDiversity(
                image.Gradients[top],
                model.Levels[top].Features.AsSpan(),
                default,
                0,
                bestProbePose,
                model.Options.Polarity))
        {
            return default;
        }

        var stepX = hasHigherMinorNeighbor ? dvx : dux;
        var stepY = hasHigherMinorNeighbor ? dvy : duy;
        var subOffsetX = bestProbePose.X - herePose.X;
        var subOffsetY = bestProbePose.Y - herePose.Y;
        var neighborMargin = hasHigherMinorNeighbor ? MinorRidgeNeighborMargin : MajorRidgeNeighborMargin;
        for (var s = -2; s <= 2; s++)
        {
            if (s == 0)
            {
                continue;
            }

            var nx = x + (s * stepX);
            var ny = y + (s * stepY);
            if (nx < 0 || nx >= gridWidth || ny < 0 || ny >= gridHeight)
            {
                continue;
            }

            if (cellBest[(ny * gridWidth) + nx].Score > cell.Score)
            {
                var sVer = ContinuousVerifier.Score(
                    image.Gradients[top],
                    model,
                    bestProbePose with { X = (nx * map.Step) + 0.5 + subOffsetX, Y = (ny * map.Step) + 0.5 + subOffsetY },
                    top,
                    allowElastic: false);
                if (sVer.Score > hereVer.Score + neighborMargin)
                {
                    return default;
                }
            }
        }

        var visibleFraction = Math.Clamp(1.0 - maxOcclusionRatio, MinVisibleFraction, MaxVisibleFraction);
        var occlusionAdjustedScore = Math.Min(
            MaxRecoveredScoreCap,
            (hereVer.Score / visibleFraction) + (OcclusionPriorWeight * (1.0 - visibleFraction)));
        var effectiveScore = Math.Max(recoveredCell.Score, occlusionAdjustedScore);
        return new RecoveredCellResult(true, recoveredCell, effectiveScore);
    }

    public static (MatchPose Pose, VerifiedScore Score) RefineMajorAxisAndSubpixelCenter(
        PreparedSearchImage image,
        ShapeModel model,
        (MatchPose Pose, VerifiedScore Score) currentResult,
        ShapeSearchOptions options,
        CancellationToken token)
    {
        var result = currentResult;
        if ((options.MaxOcclusionRatio > 0 || !model.FeatureAsymmetryFlags.IsDefault) &&
            model.FineDetail is null &&
            !model.FeaturePartitions.IsDefault &&
            model.Options.UseFineDetailValidation &&
            result.Score.Score is >= MajorSlideMinScore and < MajorSlideMaxScore)
        {
            var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
            var majorSpan = Math.Max(model.BoundsWidth, model.BoundsHeight) * result.Pose.Scale;
            var slideSpan = Math.Clamp((int)Math.Round(majorSpan * MajorSlideSpanRatio), MinMajorSlideSpan, MaxMajorSlideSpan);
            var majorAngle = (result.Pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
            var ux = Math.Cos(majorAngle);
            var uy = Math.Sin(majorAngle);
            var bestMajorPose = result.Pose;
            var bestMajorScore = result.Score;
            for (var du = -slideSpan; du <= slideSpan; du++)
            {
                if (du == 0)
                {
                    continue;
                }

                var trialPose = result.Pose with
                {
                    X = result.Pose.X + (du * ux),
                    Y = result.Pose.Y + (du * uy)
                };
                var trialScore = ContinuousVerifier.Score(
                    image.Gradients[0],
                    model,
                    trialPose,
                    0,
                    allowElastic: false,
                    options.MaxOcclusionRatio,
                    options.StrictContext);
                if (trialScore.Score > bestMajorScore.Score + BucketImprovementMargin)
                {
                    bestMajorScore = trialScore;
                    bestMajorPose = trialPose;
                }
            }

            if (!bestMajorPose.Equals(result.Pose))
            {
                var reoptimized = ContinuousVerifier.Optimize(image.Gradients[0], model, bestMajorPose, options, token, 0);
                if (reoptimized.Score.Score > result.Score.Score + BucketImprovementMargin)
                {
                    result = reoptimized;
                }
            }
        }

        if (options.MaxOcclusionRatio > 0 &&
            model.FineDetail is null &&
            !model.FeaturePartitions.IsDefault &&
            model.Options.UseFineDetailValidation &&
            result.Score.Score >= options.MinimumScore)
        {
            var horizontalMajor = model.BoundsWidth >= model.BoundsHeight;
            var majorAngle = (result.Pose.Angle - model.TrainingReferenceAngle) + (horizontalMajor ? 0.0 : Math.PI / 2);
            var ux = Math.Cos(majorAngle);
            var uy = Math.Sin(majorAngle);
            var vx = -uy;
            var vy = ux;
            var bestSubScore = ContinuousVerifier.Score(image.Gradients[0], model, result.Pose, 0, allowElastic: false, 0, false);
            for (var iv = -2; iv <= 2; iv++)
            {
                var dv = iv * SubpixelStep;
                for (var iu = -2; iu <= 2; iu++)
                {
                    if (iu == 0 && iv == 0)
                    {
                        continue;
                    }

                    var du = iu * SubpixelStep;
                    var subPose = result.Pose with
                    {
                        X = result.Pose.X + (du * ux) + (dv * vx),
                        Y = result.Pose.Y + (du * uy) + (dv * vy)
                    };
                    var subOcc = ContinuousVerifier.Score(
                        image.Gradients[0],
                        model,
                        subPose,
                        0,
                        allowElastic: true,
                        options.MaxOcclusionRatio,
                        options.StrictContext);
                    if (subOcc.Score < result.Score.Score - SubpixelOcclusionTolerance)
                    {
                        continue;
                    }

                    var subRigid = ContinuousVerifier.Score(image.Gradients[0], model, subPose, 0, allowElastic: false, 0, false);
                    if (subRigid.Score > bestSubScore.Score + SubpixelRigidImprovementMargin)
                    {
                        bestSubScore = subRigid;
                        result = (subPose, subOcc);
                    }
                }
            }
        }

        return result;
    }
}
