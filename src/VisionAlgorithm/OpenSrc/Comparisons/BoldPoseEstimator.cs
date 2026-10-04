using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

internal sealed record BoldPoseSearchResult(ImmutableArray<BoldPoseMatch> Matches, int Hypotheses,
    int MaximumConsensusInliers, bool Truncated);

internal static class BoldPoseEstimator
{
    public static BoldPoseSearchResult FindMatches(BoldModel model, IReadOnlyList<BoldFeature> scene,
        IReadOnlyList<BoldCorrespondence> correspondences, BoldSearchOptions options,
        CancellationToken cancellationToken)
    {
        var candidates = new List<BoldPoseMatch>();
        var available = correspondences.ToList();
        var hypotheses = 0;
        var maximumConsensusInliers = 0;
        var truncated = false;
        while (available.Count >= options.MinimumInliers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bestPose = default(MatchPose);
            List<BoldCorrespondence>? bestInliers = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var correspondence in available)
            {
                if (hypotheses == options.MaximumHypotheses)
                {
                    truncated = true;
                    break;
                }

                hypotheses++;
                if (!TryCreatePose(model.Features[correspondence.ModelIndex].Segment,
                    scene[correspondence.SceneIndex].Segment, options, out var pose))
                {
                    continue;
                }

                var inliers = FindInliers(pose, available, model.Features, scene, options, cancellationToken);
                maximumConsensusInliers = Math.Max(maximumConsensusInliers, inliers.Count);
                var distance = inliers.Sum(inlier => inlier.DescriptorDistance);
                if (inliers.Count > (bestInliers?.Count ?? 0) ||
                    (inliers.Count == bestInliers?.Count && distance < bestDistance))
                {
                    bestPose = pose;
                    bestInliers = inliers;
                    bestDistance = distance;
                }
            }

            if (bestInliers is null || bestInliers.Count < options.MinimumInliers)
            {
                break;
            }

            for (var iteration = 0; iteration < 3; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryFitPose(bestInliers, model.Features, scene, out var fittedPose))
                {
                    break;
                }

                var fittedInliers = FindInliers(fittedPose, available, model.Features, scene, options, cancellationToken);
                if (fittedInliers.Count < options.MinimumInliers || fittedInliers.Count < bestInliers.Count)
                {
                    break;
                }

                bestPose = fittedPose;
                bestInliers = fittedInliers;
            }

            var uniqueModels = bestInliers.Select(inlier => inlier.ModelIndex).Distinct().Count();
            var meanDistance = bestInliers.Average(inlier => inlier.DescriptorDistance);
            candidates.Add(new(bestPose, uniqueModels / (double)model.Features.Length, bestInliers.Count, meanDistance));

            var usedSceneIndices = bestInliers.Select(inlier => inlier.SceneIndex).ToHashSet();
            available.RemoveAll(correspondence => usedSceneIndices.Contains(correspondence.SceneIndex));
        }

        var matches = SuppressDuplicates(candidates, model, options);
        return new(matches, hypotheses, maximumConsensusInliers, truncated);
    }

    private static List<BoldCorrespondence> FindInliers(MatchPose pose,
        IReadOnlyList<BoldCorrespondence> correspondences, ImmutableArray<BoldFeature> model,
        IReadOnlyList<BoldFeature> scene, BoldSearchOptions options, CancellationToken cancellationToken)
    {
        var inliers = new List<BoldCorrespondence>();
        var inspected = 0;
        foreach (var correspondence in correspondences)
        {
            if ((inspected++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (IsInlier(pose, model[correspondence.ModelIndex].Segment, scene[correspondence.SceneIndex].Segment, options))
            {
                inliers.Add(correspondence);
            }
        }

        return inliers;
    }

    private static bool IsInlier(MatchPose pose, LineSegment model, LineSegment scene, BoldSearchOptions options)
    {
        var predictedCenter = Transform(model.Middle, pose);
        var dx = predictedCenter.X - scene.Middle.X;
        var dy = predictedCenter.Y - scene.Middle.Y;
        if ((dx * dx) + (dy * dy) > options.PositionTolerance * options.PositionTolerance)
        {
            return false;
        }

        var angleError = Math.Abs(NormalizeSignedAngle(pose.Angle + model.Angle - scene.Angle));
        if (angleError > options.OrientationToleranceDegrees * Math.PI / 180)
        {
            return false;
        }

        var expectedLength = pose.Scale * model.Length;
        var lengthError = Math.Abs(expectedLength - scene.Length) / Math.Max(expectedLength, scene.Length);
        return lengthError <= options.RelativeLengthTolerance;
    }

    private static bool TryCreatePose(LineSegment model, LineSegment scene, BoldSearchOptions options,
        out MatchPose pose)
    {
        var scale = scene.Length / model.Length;
        if (scale < options.MinimumScale || scale > options.MaximumScale)
        {
            pose = default;
            return false;
        }

        var angle = scene.Angle - model.Angle;
        var center = Transform(model.Middle, new(0, 0, angle, scale));
        pose = new(scene.Middle.X - center.X, scene.Middle.Y - center.Y, angle, scale);
        return true;
    }

    private static bool TryFitPose(IReadOnlyList<BoldCorrespondence> inliers,
        ImmutableArray<BoldFeature> model, IReadOnlyList<BoldFeature> scene, out MatchPose pose)
    {
        var modelCenterX = inliers.Average(inlier => model[inlier.ModelIndex].Segment.Middle.X);
        var modelCenterY = inliers.Average(inlier => model[inlier.ModelIndex].Segment.Middle.Y);
        var sceneCenterX = inliers.Average(inlier => scene[inlier.SceneIndex].Segment.Middle.X);
        var sceneCenterY = inliers.Average(inlier => scene[inlier.SceneIndex].Segment.Middle.Y);
        var dot = 0d;
        var cross = 0d;
        var energy = 0d;
        foreach (var inlier in inliers)
        {
            var modelX = model[inlier.ModelIndex].Segment.Middle.X - modelCenterX;
            var modelY = model[inlier.ModelIndex].Segment.Middle.Y - modelCenterY;
            var sceneX = scene[inlier.SceneIndex].Segment.Middle.X - sceneCenterX;
            var sceneY = scene[inlier.SceneIndex].Segment.Middle.Y - sceneCenterY;
            dot += (modelX * sceneX) + (modelY * sceneY);
            cross += (modelX * sceneY) - (modelY * sceneX);
            energy += (modelX * modelX) + (modelY * modelY);
        }

        if (energy <= 1e-9 || (Math.Abs(dot) + Math.Abs(cross)) <= 1e-9)
        {
            pose = default;
            return false;
        }

        var angle = Math.Atan2(cross, dot);
        var scale = Math.Sqrt((dot * dot) + (cross * cross)) / energy;
        var transformedCenter = Transform(new(modelCenterX, modelCenterY), new(0, 0, angle, scale));
        pose = new(sceneCenterX - transformedCenter.X, sceneCenterY - transformedCenter.Y, angle, scale);
        return double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Angle) &&
            double.IsFinite(pose.Scale);
    }

    private static ImmutableArray<BoldPoseMatch> SuppressDuplicates(List<BoldPoseMatch> candidates,
        BoldModel model, BoldSearchOptions options)
    {
        var results = ImmutableArray.CreateBuilder<BoldPoseMatch>();
        foreach (var candidate in candidates.OrderByDescending(match => match.Inliers)
            .ThenByDescending(match => match.Score).ThenBy(match => match.MeanDescriptorDistance))
        {
            if (results.All(existing => MatchSuppression.Overlap(model.Bounds, candidate.Pose, existing.Pose) <=
                options.MaximumOverlap))
            {
                results.Add(candidate);
                if (results.Count == options.MaximumMatches)
                {
                    break;
                }
            }
        }

        return results.ToImmutable();
    }

    private static ImagePoint Transform(ImagePoint point, MatchPose pose)
    {
        var cos = Math.Cos(pose.Angle) * pose.Scale;
        var sin = Math.Sin(pose.Angle) * pose.Scale;
        return new(pose.X + (cos * point.X) - (sin * point.Y), pose.Y + (sin * point.X) + (cos * point.Y));
    }

    private static double NormalizeSignedAngle(double angle)
        => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
}
