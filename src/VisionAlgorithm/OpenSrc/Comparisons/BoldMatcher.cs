using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

public sealed record BoldSearchOptions
{
    public int MinimumInliers { get; init; } = 3;
    public int MaximumSceneSegmentsPerLevel { get; init; } = 384;
    public int MaximumHypotheses { get; init; } = 50000;
    public int MaximumMatches { get; init; } = 8;
    public double DescriptorRatio { get; init; } = 0.98;
    public double MaximumDescriptorDistance { get; init; } = 1.35;
    public double PositionTolerance { get; init; } = 6;
    public double OrientationToleranceDegrees { get; init; } = 20;
    public double RelativeLengthTolerance { get; init; } = 0.45;
    public double MaximumOverlap { get; init; } = 0.3;
    public double MinimumScale { get; init; } = 0.1;
    public double MaximumScale { get; init; } = 10;

    public void Validate()
    {
        if (MinimumInliers is < 2 or > 512 || MaximumSceneSegmentsPerLevel is < 3 or > 1024 ||
            MaximumHypotheses is < 1 or > 1_000_000 || MaximumMatches is < 1 or > 100 ||
            !double.IsFinite(DescriptorRatio) || DescriptorRatio is <= 0 or > 1 ||
            !double.IsFinite(MaximumDescriptorDistance) || MaximumDescriptorDistance <= 0 ||
            !double.IsFinite(PositionTolerance) || PositionTolerance <= 0 ||
            !double.IsFinite(OrientationToleranceDegrees) || OrientationToleranceDegrees is <= 0 or > 90 ||
            !double.IsFinite(RelativeLengthTolerance) || RelativeLengthTolerance is <= 0 or > 1 ||
            !double.IsFinite(MaximumOverlap) || MaximumOverlap is < 0 or > 1 ||
            !double.IsFinite(MinimumScale) || !double.IsFinite(MaximumScale) ||
            MinimumScale <= 0 || MaximumScale < MinimumScale)
        {
            throw new ArgumentOutOfRangeException(nameof(BoldSearchOptions));
        }
    }
}

public sealed class BoldModel
{
    public int FormatVersion => 1;
    public ImagePoint TrainingReferencePoint
    {
        get;
    }
    public double TrainingReferenceAngle
    {
        get;
    }
    public int SegmentCount => Features.Length;
    public long EstimatedBytes => (Bounds.Length * 16L) +
        Features.Sum(feature => 64L + (feature.Descriptor.Length * sizeof(float)));
    internal ImmutableArray<ImagePoint> Bounds
    {
        get;
    }
    internal float MinimumGradient
    {
        get;
    }
    internal ImmutableArray<BoldFeature> Features
    {
        get;
    }

    internal BoldModel(ImagePoint referencePoint, double referenceAngle, float minimumGradient,
        ImmutableArray<ImagePoint> bounds, ImmutableArray<BoldFeature> features)
    {
        TrainingReferencePoint = referencePoint;
        TrainingReferenceAngle = referenceAngle;
        MinimumGradient = minimumGradient;
        Bounds = bounds;
        Features = features;
    }
}

public sealed record BoldPoseMatch(MatchPose Pose, double Score, int Inliers, double MeanDescriptorDistance);
public sealed record BoldSearchDiagnostics(double FeatureExtractionMilliseconds, double MatchingAndVotingMilliseconds,
    int ModelSegments, int SceneSegments, int Correspondences, int Hypotheses, int MaximumConsensusInliers,
    bool Truncated);
public sealed record BoldMatchResult(ImmutableArray<BoldPoseMatch> Matches, BoldSearchDiagnostics Diagnostics);

internal readonly record struct BoldFeature(LineSegment Segment, ImmutableArray<float> Descriptor);
internal readonly record struct BoldCorrespondence(int ModelIndex, int SceneIndex, double DescriptorDistance);

/// <summary>BOLD §3–4에서 착안한 다중-bunch 선분 descriptor pose 비교기.</summary>
public static class BoldMatcher
{
    public const int MaximumInputPixels = 262144;
    private const double MinimumLineLength = 2;

    public static BoldMatchResult Search(GrayImage image, BoldModel model, BoldSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        BoldInputValidation.ValidateInputSize(image, MaximumInputPixels);
        cancellationToken.ThrowIfCancellationRequested();

        var extractionTimer = Stopwatch.StartNew();
        var scene = ExtractSceneFeatures(image, model, options, cancellationToken, out var sceneTruncated);
        var featureExtractionMilliseconds = extractionTimer.Elapsed.TotalMilliseconds;
        var matchingTimer = Stopwatch.StartNew();
        var correspondences = MatchDescriptors(model.Features, scene, options, cancellationToken);
        var poseSearch = BoldPoseEstimator.FindMatches(model, scene, correspondences, options, cancellationToken);
        var matchingAndVotingMilliseconds = matchingTimer.Elapsed.TotalMilliseconds;
        var diagnostics = new BoldSearchDiagnostics(featureExtractionMilliseconds, matchingAndVotingMilliseconds,
            model.Features.Length, scene.Count, correspondences.Count, poseSearch.Hypotheses,
            poseSearch.MaximumConsensusInliers, sceneTruncated || poseSearch.Truncated);
        return new(poseSearch.Matches, diagnostics);
    }

    private static List<BoldFeature> ExtractSceneFeatures(GrayImage image, BoldModel model, BoldSearchOptions options,
        CancellationToken cancellationToken, out bool truncated)
    {
        var scene = new List<BoldFeature>();
        var current = image;
        var factor = 1;
        truncated = false;
        for (var level = 0; level < 3; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gradients = GradientImage.Create(current, model.MinimumGradient, cancellationToken);
            var lines = LineSegments.Extract(gradients, cancellationToken)
                .Where(line => line.Length >= MinimumLineLength)
                .Select(line => BoldLineGeometry.TryCanonicalize(line, gradients, out var canonical)
                    ? canonical
                    : (LineSegment?)null)
                .Where(line => line.HasValue)
                .Select(line => line!.Value)
                .OrderByDescending(line => line.Length)
                .ToArray();
            if (lines.Length > options.MaximumSceneSegmentsPerLevel)
            {
                truncated = true;
                lines = lines.Take(options.MaximumSceneSegmentsPerLevel).ToArray();
            }

            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = BoldDescriptor.Compute(lines, index);
                if (descriptor.IsEmpty)
                {
                    continue;
                }

                var line = lines[index];
                var original = new LineSegment(
                    new(line.Start.X * factor, line.Start.Y * factor),
                    new(line.End.X * factor, line.End.Y * factor));
                scene.Add(new(original, descriptor));
            }

            current = GradientImage.Reduce(current);
            factor *= 2;
        }

        return scene;
    }

    private static List<BoldCorrespondence> MatchDescriptors(ImmutableArray<BoldFeature> model,
        IReadOnlyList<BoldFeature> scene, BoldSearchOptions options, CancellationToken cancellationToken)
    {
        var matches = new List<BoldCorrespondence>();
        for (var sceneIndex = 0; sceneIndex < scene.Count; sceneIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bestIndex = -1;
            var bestDistance = double.PositiveInfinity;
            var secondDistance = double.PositiveInfinity;
            for (var modelIndex = 0; modelIndex < model.Length; modelIndex++)
            {
                if ((modelIndex & 15) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var distance = DescriptorDistance(scene[sceneIndex].Descriptor, model[modelIndex].Descriptor);
                if (distance < bestDistance)
                {
                    secondDistance = bestDistance;
                    bestDistance = distance;
                    bestIndex = modelIndex;
                }
                else if (distance < secondDistance)
                {
                    secondDistance = distance;
                }
            }

            if (bestIndex < 0 || bestDistance > options.MaximumDescriptorDistance ||
                (double.IsFinite(secondDistance) && bestDistance >= options.DescriptorRatio * secondDistance))
            {
                continue;
            }

            matches.Add(new(bestIndex, sceneIndex, bestDistance));
        }

        return matches;
    }

    private static double DescriptorDistance(ImmutableArray<float> first, ImmutableArray<float> second)
    {
        var sum = 0d;
        for (var index = 0; index < first.Length; index++)
        {
            var difference = first[index] - second[index];
            sum += difference * difference;
        }

        return Math.Sqrt(sum);
    }
}
